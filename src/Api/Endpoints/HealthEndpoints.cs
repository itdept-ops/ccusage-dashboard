using Ccusage.Api.Auth;
using Ccusage.Api.Data;
using Ccusage.Api.Data.Entities;
using Ccusage.Api.Services.Health;
using Microsoft.EntityFrameworkCore;

namespace Ccusage.Api.Endpoints;

/// <summary>
/// PROGRAM-2 #1 — Wearable / Health sync (/api/health), gated <see cref="Permissions.HealthSync"/> on top
/// of <c>.RequireAuthorization()</c> and OWNER-SCOPED end to end (a caller only ever connects/syncs/reads
/// their OWN wearable; the synced rows are written under the caller's email). Fitbit v1 via the OAuth 2.0
/// authorization-code + PKCE flow (offline access), mirroring <c>FamilyCalendarEndpoints</c>.
///
/// GRACEFUL (never a 500): when the provider isn't configured on this server, /status reports
/// configured:false and the page shows a "not configured on this server" state; when configured but not
/// connected, an empty "connect a wearable" state. PRIVACY: the client secret + the user refresh token NEVER
/// appear in any response (the refresh token is stored AES-GCM-encrypted). Sleep + resting HR are sensitive
/// and the sync only writes the owner's own rows — they are never surfaced to coach/family overlays.
/// </summary>
public static class HealthEndpoints
{
    // ---- Request DTOs ----
    /// <summary>The PKCE auth-code exchange: the one-time <see cref="Code"/>, the SAME
    /// <see cref="RedirectUri"/> the SPA used in the authorize URL, and the PKCE <see cref="CodeVerifier"/>.</summary>
    public sealed record ConnectRequest(string? Code, string? RedirectUri, string? CodeVerifier);

    /// <summary>The settings PATCH: each toggle is optional (null = leave unchanged).</summary>
    public sealed record SettingsRequest(
        bool? AutoSyncEnabled, bool? SyncSteps, bool? SyncSleep, bool? SyncHeartRate, bool? SyncWorkouts);

    // ---- Response DTOs ----
    /// <summary>
    /// The connection status the frontend renders. <see cref="Configured"/> false ⇒ "not configured on this
    /// server"; <see cref="Connected"/> false ⇒ "connect a wearable" empty state. <see cref="Provider"/> /
    /// toggles / last-sync are only meaningful when connected. <see cref="LastSyncStatus"/> is one of
    /// "Ok" | "AuthExpired" | "RateLimited" | "Error" (AuthExpired ⇒ prompt reconnect). <see cref="Scopes"/>
    /// + <see cref="ClientId"/> let the frontend build the Fitbit authorize URL when not connected.
    /// </summary>
    public sealed record HealthStatus(
        bool Configured,
        bool Connected,
        string Provider,
        string? ClientId,
        string Scopes,
        bool AutoSyncEnabled,
        bool SyncSteps,
        bool SyncSleep,
        bool SyncHeartRate,
        bool SyncWorkouts,
        DateTime? LastSyncUtc,
        string LastSyncStatus,
        string? LastSyncCursorDate);

    /// <summary>One signal's import counts for the sync-now summary.</summary>
    public sealed record SignalSummary(int Imported, int Updated, int Skipped)
    {
        public static readonly SignalSummary Zero = new(0, 0, 0);
    }

    /// <summary>The sync-now result: per-signal {imported, updated, skipped} + the window covered.</summary>
    public sealed record SyncNowResult(
        bool Connected, string? FromDate, string? ToDate, string Status,
        SignalSummary Steps, SignalSummary Sleep, SignalSummary HeartRate, SignalSummary Workouts);

    /// <summary>The most days a manual sync-now backfills (matches the scheduler's cap).</summary>
    private const int ManualBackfillDays = HealthSyncScheduler.MaxBackfillDays;

    public static void MapHealthEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/health")
            .RequireAuthorization()
            .RequirePermission(Permissions.HealthSync);

        // ---- GET /status : configured + connected + provider/toggles/last-sync (never 500) ----
        g.MapGet("/status", async (
            CurrentUserAccessor me, FitbitHealthProvider fitbit, UsageDbContext db, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;
            var conn = await db.HealthConnections.AsNoTracking()
                .FirstOrDefaultAsync(c => c.UserId == caller.Id && c.Provider == HealthProvider.Fitbit, ct);

            var status = conn is null
                ? new HealthStatus(
                    fitbit.IsConfigured, Connected: false, Provider: HealthProvider.Fitbit.ToString(),
                    ClientId: ClientIdForClient(fitbit), Scopes: fitbit.Scopes,
                    AutoSyncEnabled: true, SyncSteps: true, SyncSleep: true, SyncHeartRate: true, SyncWorkouts: true,
                    LastSyncUtc: null, LastSyncStatus: HealthSyncStatus.Ok.ToString(), LastSyncCursorDate: null)
                : new HealthStatus(
                    fitbit.IsConfigured, Connected: fitbit.IsConfigured, Provider: conn.Provider.ToString(),
                    ClientId: ClientIdForClient(fitbit), Scopes: fitbit.Scopes,
                    conn.AutoSyncEnabled, conn.SyncSteps, conn.SyncSleep, conn.SyncHeartRate, conn.SyncWorkouts,
                    conn.LastSyncUtc, conn.LastSyncStatus.ToString(),
                    conn.LastSyncCursorDate?.ToString("yyyy-MM-dd"));
            return Results.Ok(status);
        });

        // ---- POST /connect : PKCE auth-code exchange; store the encrypted (rotating) refresh token ----
        g.MapPost("/connect", async (
            ConnectRequest req, CurrentUserAccessor me, FitbitHealthProvider fitbit, CancellationToken ct) =>
        {
            if (!fitbit.IsConfigured)
                return Results.Json(new { message = "A wearable isn't configured on this server." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            if (string.IsNullOrWhiteSpace(req.Code))
                return Results.BadRequest(new { message = "An authorization code is required." });
            if (string.IsNullOrWhiteSpace(req.RedirectUri))
                return Results.BadRequest(new { message = "The redirect URI used to authorize is required." });

            var caller = (await me.GetUserAsync(ct))!;
            var ok = await fitbit.ConnectAsync(
                caller.Id, caller.Email, req.Code!, req.RedirectUri!, req.CodeVerifier, ct);
            if (!ok)
                return Results.BadRequest(new
                {
                    message = "Couldn't connect your wearable. Please try connecting again and grant access.",
                });
            return Results.Ok(new { connected = true });
        });

        // ---- DELETE /disconnect : remove the caller's Fitbit connection (idempotent) ----
        g.MapDelete("/disconnect", async (
            CurrentUserAccessor me, UsageDbContext db, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;
            await db.HealthConnections
                .Where(c => c.UserId == caller.Id && c.Provider == HealthProvider.Fitbit)
                .ExecuteDeleteAsync(ct);
            return Results.Ok(new { connected = false });
        });

        // ---- PATCH /settings : per-signal + auto-sync toggles (owner-scoped) ----
        g.MapPatch("/settings", async (
            SettingsRequest req, CurrentUserAccessor me, FitbitHealthProvider fitbit, UsageDbContext db,
            CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;
            var conn = await db.HealthConnections
                .FirstOrDefaultAsync(c => c.UserId == caller.Id && c.Provider == HealthProvider.Fitbit, ct);
            if (conn is null)
                return Results.NotFound(new { message = "Connect a wearable first." });

            if (req.AutoSyncEnabled is { } a) conn.AutoSyncEnabled = a;
            if (req.SyncSteps is { } s) conn.SyncSteps = s;
            if (req.SyncSleep is { } sl) conn.SyncSleep = sl;
            if (req.SyncHeartRate is { } hr) conn.SyncHeartRate = hr;
            if (req.SyncWorkouts is { } w) conn.SyncWorkouts = w;
            await db.SaveChangesAsync(ct);

            return Results.Ok(new HealthStatus(
                fitbit.IsConfigured, Connected: fitbit.IsConfigured, Provider: conn.Provider.ToString(),
                ClientId: ClientIdForClient(fitbit), Scopes: fitbit.Scopes,
                conn.AutoSyncEnabled, conn.SyncSteps, conn.SyncSleep, conn.SyncHeartRate, conn.SyncWorkouts,
                conn.LastSyncUtc, conn.LastSyncStatus.ToString(),
                conn.LastSyncCursorDate?.ToString("yyyy-MM-dd")));
        });

        // ---- POST /sync-now : manual bounded backfill; returns per-signal {imported,updated,skipped} ----
        g.MapPost("/sync-now", async (
            CurrentUserAccessor me, FitbitHealthProvider fitbit, HealthSyncMapper mapper, UsageDbContext db,
            CancellationToken ct) =>
        {
            if (!fitbit.IsConfigured)
                return Results.Json(new { message = "A wearable isn't configured on this server." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            var caller = (await me.GetUserAsync(ct))!;
            var conn = await db.HealthConnections
                .FirstOrDefaultAsync(c => c.UserId == caller.Id && c.Provider == HealthProvider.Fitbit, ct);
            if (conn is null)
                return Results.Ok(new SyncNowResult(
                    Connected: false, FromDate: null, ToDate: null, Status: "NotConnected",
                    SignalSummary.Zero, SignalSummary.Zero, SignalSummary.Zero, SignalSummary.Zero));

            var tz = await ResolveTzAsync(db, caller.Email, ct);
            var todayLocal = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz));
            var from = todayLocal.AddDays(-(ManualBackfillDays - 1));

            var roll = HealthSyncMapper.DayResult.Empty;
            var status = HealthSyncStatus.Ok;
            DateOnly? lastOk = null; // the most-recent day actually pulled Ok
            for (var day = from; day <= todayLocal; day = day.AddDays(1))
            {
                var pull = await fitbit.PullDayAsync(conn, day, tz, ct);
                if (!pull.Ok) { status = pull.ToSyncStatus(); break; }
                roll = roll.Add(await mapper.MapDayAsync(conn, pull.Signals!, ct));
                lastOk = day;
            }

            // Advance the cursor ONLY to the last day actually pulled Ok — NEVER jump to todayLocal on a
            // mid-window break (RateLimited/AuthExpired), or the background scheduler would resume PAST the
            // skipped days and never auto-backfill them. If nothing pulled Ok, leave the cursor untouched.
            var newCursor = lastOk ?? conn.LastSyncCursorDate;
            await db.HealthConnections.Where(c => c.Id == conn.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.LastSyncCursorDate, newCursor)
                    .SetProperty(c => c.LastSyncUtc, DateTime.UtcNow)
                    .SetProperty(c => c.LastSyncStatus, status), ct);

            return Results.Ok(new SyncNowResult(
                Connected: true, FromDate: from.ToString("yyyy-MM-dd"), ToDate: todayLocal.ToString("yyyy-MM-dd"),
                Status: status.ToString(),
                Steps: Summarize(roll.Steps), Sleep: Summarize(roll.Sleep),
                HeartRate: Summarize(roll.HeartRate), Workouts: Summarize(roll.Workouts)));
        });
    }

    // =====================================================================================
    // Helpers
    // =====================================================================================

    /// <summary>The OAuth client id is NOT a secret (the browser needs it to build the authorize URL); only
    /// the client SECRET is. Surface the client id only when the provider is configured.</summary>
    private static string? ClientIdForClient(FitbitHealthProvider fitbit) =>
        fitbit.IsConfigured ? fitbit.ClientIdForAuthorize : null;

    private static SignalSummary Summarize(HealthSyncMapper.SignalResult r) =>
        new(r.Imported, r.Updated, r.Skipped);

    /// <summary>Resolve the caller's timezone for "today-local" — their scheduled-agent / household zone, else
    /// the app display zone. Mirrors the scheduler's per-user resolution.</summary>
    private static async Task<TimeZoneInfo> ResolveTzAsync(UsageDbContext db, string email, CancellationToken ct)
    {
        var e = email.Trim().ToLowerInvariant();
        var agentTz = await db.ScheduledAgents.AsNoTracking()
            .Where(a => a.UserEmail == e).Select(a => a.TimeZone).FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(agentTz)) return Services.AgentScheduler.ResolveTimeZone(agentTz);

        var householdTz = await db.HouseholdMembers.AsNoTracking()
            .Where(m => db.Users.Any(u => u.Id == m.UserId && u.Email == e))
            .Join(db.Households.AsNoTracking(), m => m.HouseholdId, h => h.Id, (m, h) => h.TimeZone)
            .FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(householdTz)) return Services.AgentScheduler.ResolveTimeZone(householdTz);

        var displayTz = await db.AppConfigs.AsNoTracking().Select(c => c.DisplayTimeZone).FirstOrDefaultAsync(ct);
        return Services.AgentScheduler.ResolveTimeZone(displayTz);
    }
}
