using Ccusage.Api.Data;
using Ccusage.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Ccusage.Api.Services.Health;

/// <summary>
/// PROGRAM-2 #1 — the wearable auto-sync background tick, a SIBLING of <see cref="AgentScheduler"/>. On a
/// cadence it scans the BOUNDED set of auto-sync-enabled <see cref="HealthConnection"/> rows whose last sync
/// is older than <see cref="SyncCadence"/>, and for each due one pulls the days from its cursor → today-local
/// (capped at a backfill window), maps them via <see cref="IHealthProvider"/> + <see cref="HealthSyncMapper"/>,
/// and advances the cursor.
///
/// INVARIANTS (mirroring AgentScheduler):
/// <list type="bullet">
///   <item>STAMP-CURSOR-FIRST — <see cref="HealthConnection.LastSyncCursorDate"/> is advanced to the day
///   BEING pulled before that day is mapped, so a crash mid-day never re-pulls the whole window (the de-dup
///   log is the second line of defence).</item>
///   <item>BOUNDED PER-TICK — the candidate read is filtered (AutoSyncEnabled, last-sync older than the
///   cadence) and PAGED (<see cref="MaxConnectionsPerTick"/>); it never loads every connection every tick.</item>
///   <item>PER-USER TIMEZONE — "today-local" is computed in the connection owner's own timezone (reusing
///   <see cref="AgentScheduler.ResolveTimeZone"/>), resolved per user via the owner's profile / household /
///   scheduled-agent timezone, falling back to the app display zone.</item>
///   <item>BACKFILL CAP — at most <see cref="MaxBackfillDays"/> days are pulled in one tick, so a long-dormant
///   connection catches up over several ticks rather than hammering the provider.</item>
///   <item>GRACEFUL BACK-OFF — AuthExpired stamps the connection (UI prompts a reconnect) and stops;
///   RateLimited records the status and retries next tick; the row is never deleted on a transient error.</item>
/// </list>
/// The tick body is a public instance method (<see cref="TickAsync"/>) taking the clock so tests can drive a
/// single deterministic cycle.
/// </summary>
public sealed class HealthSyncScheduler(
    IServiceScopeFactory scopeFactory, ILogger<HealthSyncScheduler> logger) : BackgroundService
{
    /// <summary>How often the loop wakes; the per-connection cadence is the real gate.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    /// <summary>A connection is only re-synced when its last sync is older than this.</summary>
    public static readonly TimeSpan SyncCadence = TimeSpan.FromHours(6);

    /// <summary>Hard upper bound on connections handled per tick (bounded scan).</summary>
    public const int MaxConnectionsPerTick = 100;

    /// <summary>The most days a single tick will pull for one connection (catch-up is spread over ticks).</summary>
    public const int MaxBackfillDays = 30;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var sp = scope.ServiceProvider;
                await TickAsync(
                    sp.GetRequiredService<UsageDbContext>(),
                    sp.GetRequiredService<IEnumerable<IHealthProvider>>(),
                    sp.GetRequiredService<HealthSyncMapper>(),
                    DateTime.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Health sync scheduler tick failed."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// One deterministic tick as of <paramref name="nowUtc"/>: sync every DUE connection (auto-sync enabled,
    /// last sync older than the cadence) for a configured provider. Public + parameterized so tests can drive
    /// a single cycle with a fixed clock. Returns the number of connections that actually synced this tick.
    /// </summary>
    public async Task<int> TickAsync(
        UsageDbContext db, IEnumerable<IHealthProvider> providers, HealthSyncMapper mapper,
        DateTime nowUtc, CancellationToken ct = default)
    {
        var utc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        var byProvider = providers.Where(p => p.IsConfigured).ToDictionary(p => p.Provider);
        if (byProvider.Count == 0) return 0; // no configured provider → nothing to do (graceful)

        var dueBefore = utc - SyncCadence;
        var candidates = await db.HealthConnections
            .Where(c => c.AutoSyncEnabled && (c.LastSyncUtc == null || c.LastSyncUtc < dueBefore))
            .OrderBy(c => c.Id)
            .Take(MaxConnectionsPerTick)
            .ToListAsync(ct);
        if (candidates.Count == 0) return 0;

        // Resolve a default timezone for the deployment once (the per-user fallback).
        var defaultTz = await ResolveDisplayTzAsync(db, ct);

        var synced = 0;
        foreach (var conn in candidates)
        {
            if (!byProvider.TryGetValue(conn.Provider, out var provider)) continue; // provider not configured
            try
            {
                var tz = await ResolveUserTzAsync(db, conn.UserEmail, defaultTz, ct);
                var ok = await SyncConnectionAsync(db, provider, mapper, conn, utc, tz, ct);
                if (ok) synced++;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Health sync for a connection failed: {Reason}", ex.Message);
            }
        }
        return synced;
    }

    /// <summary>
    /// Sync ONE connection over [cursor+1 .. today-local], capped at <see cref="MaxBackfillDays"/>. Stamps the
    /// cursor to each day BEFORE mapping it (crash-safe), records the per-attempt status, and backs off on
    /// AuthExpired / RateLimited. Returns true when at least one day was pulled Ok.
    /// </summary>
    public async Task<bool> SyncConnectionAsync(
        UsageDbContext db, IHealthProvider provider, HealthSyncMapper mapper, HealthConnection conn,
        DateTime utcNow, TimeZoneInfo tz, CancellationToken ct = default)
    {
        var todayLocal = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz));

        // First day to pull: the day AFTER the cursor, or a bounded backfill start when there's no cursor.
        var start = conn.LastSyncCursorDate is { } cursor
            ? cursor.AddDays(1)
            : todayLocal.AddDays(-(MaxBackfillDays - 1));
        // Never pull into the future; cap the window length at the backfill cap.
        if (start > todayLocal) start = todayLocal;
        var earliest = todayLocal.AddDays(-(MaxBackfillDays - 1));
        if (start < earliest) start = earliest;

        var anyOk = false;
        var status = HealthSyncStatus.Ok;

        for (var day = start; day <= todayLocal; day = day.AddDays(1))
        {
            // STAMP CURSOR FIRST: advance to the day we're about to pull, so a crash here never re-pulls the
            // whole window — the de-dup log makes a partial re-pull harmless anyway.
            conn.LastSyncCursorDate = day;
            await db.HealthConnections.Where(c => c.Id == conn.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastSyncCursorDate, day), ct);

            var pull = await provider.PullDayAsync(conn, day, tz, ct);
            if (!pull.Ok)
            {
                // Back off: stop the window, keep the cursor where it is (we'll resume here next tick).
                status = pull.ToSyncStatus();
                if (pull.Status is HealthPullStatus.AuthExpired or HealthPullStatus.NotConnected)
                    logger.LogInformation("Health sync: connection {Id} needs reconnect (auth expired).", conn.Id);
                break;
            }

            await mapper.MapDayAsync(conn, pull.Signals!, ct);
            anyOk = true;
            status = HealthSyncStatus.Ok;
        }

        await db.HealthConnections.Where(c => c.Id == conn.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.LastSyncUtc, utcNow)
                .SetProperty(c => c.LastSyncStatus, status), ct);

        return anyOk;
    }

    /// <summary>
    /// Resolve the owner's timezone for "today-local": prefer an explicit per-user signal (their scheduled-
    /// agent timezone or their household timezone), else the deployment default. Mirrors how AgentScheduler
    /// anchors local-date math per user.
    /// </summary>
    private static async Task<TimeZoneInfo> ResolveUserTzAsync(
        UsageDbContext db, string userEmail, TimeZoneInfo fallback, CancellationToken ct)
    {
        var email = userEmail.Trim().ToLowerInvariant();

        // A scheduled-agent row (if any) carries the user's chosen IANA timezone.
        var agentTz = await db.ScheduledAgents.AsNoTracking()
            .Where(a => a.UserEmail == email).Select(a => a.TimeZone).FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(agentTz)) return AgentScheduler.ResolveTimeZone(agentTz);

        // Otherwise the user's household timezone, if they belong to one.
        var householdTz = await db.HouseholdMembers.AsNoTracking()
            .Where(m => db.Users.Any(u => u.Id == m.UserId && u.Email == email))
            .Join(db.Households.AsNoTracking(), m => m.HouseholdId, h => h.Id, (m, h) => h.TimeZone)
            .FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(householdTz)) return AgentScheduler.ResolveTimeZone(householdTz);

        return fallback;
    }

    /// <summary>The app's display timezone (the deployment default), resolved to a TimeZoneInfo.</summary>
    private static async Task<TimeZoneInfo> ResolveDisplayTzAsync(UsageDbContext db, CancellationToken ct)
    {
        var id = await db.AppConfigs.AsNoTracking().Select(c => c.DisplayTimeZone).FirstOrDefaultAsync(ct);
        return AgentScheduler.ResolveTimeZone(id);
    }
}
