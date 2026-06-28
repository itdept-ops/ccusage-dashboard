using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ccusage.Api.Data;
using Ccusage.Api.Data.Entities;
using Ccusage.Api.Services;
using Ccusage.Api.Services.Health;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ccusage.Api.Tests.Integration;

/// <summary>
/// PROGRAM-2 #1 — Wearable / Health sync (Fitbit). The OAuth exchange + live Fitbit API are NOT hit (no real
/// Google/Fitbit HTTP in tests); instead these cover the parts we own end-to-end:
///
/// <list type="bullet">
///   <item>GATING: without health.sync every /api/health endpoint is 403; unauthenticated is 401.</item>
///   <item>STATUS: configured=false when no Fitbit:ClientSecret is set (tests set none); connected=false
///   with no stored connection — a graceful body, never a 500.</item>
///   <item>DE-DUP: mapping the SAME day's signals twice does NOT double-write (the HealthImportLog guard).</item>
///   <item>NO-CLOBBER-MANUAL: a Watch sync never overwrites a Manual tracker row.</item>
///   <item>ROTATED-TOKEN: a refresh persists the NEW (rotated) refresh token — proven via a captured Fitbit
///   token response routed through an in-memory handler.</item>
///   <item>TZ-MAPPING: the scheduler anchors "today-local" + the wake-date in the OWNER's timezone, so a
///   non-UTC user's day boundary is correct.</item>
///   <item>OWNER-ONLY: synced sleep/HR rows are written only under the owner's email.</item>
/// </list>
/// The sync mapper + scheduler are driven directly (resolved services) with a seeded connection so no real
/// Fitbit HTTP is needed; a FakeHealthProvider stands in for the scheduler path. Each test provisions fresh
/// users so they're order-independent.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class HealthSyncTests(WebAppFactory factory)
{
    private HttpClient Admin()
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestJwt.For(WebAppFactory.AdminEmail));
        return c;
    }

    private HttpClient Client(string email)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestJwt.For(email));
        return c;
    }

    private async Task<(string email, HttpClient client, int id)> ProvisionUser(params string[] permissions)
    {
        var email = $"health-{Guid.NewGuid():N}@test.local";
        var res = await Admin().PostAsJsonAsync("/api/users", new { email, isEnabled = true, permissions });
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        return (email.ToLowerInvariant(), Client(email), id);
    }

    private async Task<T> WithDb<T>(Func<UsageDbContext, Task<T>> work)
    {
        using var scope = factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<UsageDbContext>());
    }

    /// <summary>Seed a Fitbit connection for a user, storing the refresh token ENCRYPTED via the app's
    /// TokenProtector (the same way the production connect path does). Returns the connection id.</summary>
    private async Task<int> SeedConnection(int userId, string email, string plaintextRefreshToken,
        DateOnly? cursor = null, bool syncSleep = true, bool syncHr = true)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<TokenProtector>();
        var conn = new HealthConnection
        {
            UserId = userId,
            UserEmail = email.ToLowerInvariant(),
            Provider = HealthProvider.Fitbit,
            EncryptedRefreshToken = protector.Protect(plaintextRefreshToken),
            Scope = FitbitHealthProvider.FitbitScopes,
            AutoSyncEnabled = true,
            SyncSteps = true, SyncSleep = syncSleep, SyncHeartRate = syncHr, SyncWorkouts = true,
            LastSyncCursorDate = cursor,
            ConnectedUtc = DateTime.UtcNow,
        };
        db.HealthConnections.Add(conn);
        await db.SaveChangesAsync();
        return conn.Id;
    }

    private static HealthDaySignals Signals(DateOnly date,
        (int steps, int dist, int cal, int hr)? activity = null,
        (string logId, decimal hours)? sleep = null,
        (string logId, string name, int min, int cal)? workout = null)
    {
        DailySummary? a = activity is { } v ? new DailySummary(v.steps, v.dist, v.cal, v.hr) : null;
        var sleeps = sleep is { } s
            ? new[] { new SleepRecord(s.logId, s.hours, new TimeOnly(23, 0), new TimeOnly(7, 0), 4) }
            : Array.Empty<SleepRecord>();
        var workouts = workout is { } w
            ? new[] { new WorkoutRecord(w.logId, w.name, w.min, w.cal) }
            : Array.Empty<WorkoutRecord>();
        return new HealthDaySignals(date, a, sleeps, workouts);
    }

    // =====================================================================================
    // GATING
    // =====================================================================================

    [Fact]
    public async Task Health_endpoints_require_health_sync_permission()
    {
        var (_, plain, _) = await ProvisionUser("dashboard.view");

        (await plain.GetAsync("/api/health/status")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await plain.PostAsJsonAsync("/api/health/connect", new { code = "x", redirectUri = "y" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await plain.DeleteAsync("/api/health/disconnect")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await plain.PostAsync("/api/health/sync-now", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Health_endpoints_require_authentication()
    {
        var anon = factory.CreateClient();
        (await anon.GetAsync("/api/health/status")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Status_reports_not_configured_and_not_connected_gracefully()
    {
        var (_, client, _) = await ProvisionUser("health.sync");
        var res = await client.GetAsync("/api/health/status");
        res.StatusCode.Should().Be(HttpStatusCode.OK); // never a 500
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("configured").GetBoolean().Should().BeFalse(); // no Fitbit secret in tests
        body.GetProperty("connected").GetBoolean().Should().BeFalse();
        body.GetProperty("provider").GetString().Should().Be("Fitbit");
        body.GetProperty("scopes").GetString().Should().Contain("sleep");
    }

    [Fact]
    public async Task Connect_is_503_when_provider_not_configured()
    {
        var (_, client, _) = await ProvisionUser("health.sync");
        var res = await client.PostAsJsonAsync("/api/health/connect",
            new { code = "abc", redirectUri = "https://app/callback", codeVerifier = "v" });
        res.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Disconnect_is_idempotent()
    {
        var (_, client, _) = await ProvisionUser("health.sync");
        // No connection yet — still a 200 with connected:false (idempotent).
        var res = await client.DeleteAsync("/api/health/disconnect");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("connected").GetBoolean().Should().BeFalse();
    }

    // =====================================================================================
    // DE-DUP — re-mapping the same day does NOT double-write
    // =====================================================================================

    [Fact]
    public async Task Re_mapping_the_same_day_does_not_double_write()
    {
        var (email, _, id) = await ProvisionUser("health.sync");
        var connId = await SeedConnection(id, email, "refresh-1");
        var date = new DateOnly(2026, 6, 20);

        var signals = Signals(date,
            activity: (8000, 5000, 400, 58),
            sleep: ("sleep-aaa", 7.5m),
            workout: ("wk-bbb", "Run", 30, 250));

        await MapDay(connId, signals);
        await MapDay(connId, signals); // a second identical pull

        var activityCount = await WithDb(db => db.DailyActivities.CountAsync(x => x.UserEmail == email && x.LocalDate == date));
        var sleepCount = await WithDb(db => db.SleepEntries.CountAsync(x => x.UserEmail == email && x.LocalDate == date));
        var workoutCount = await WithDb(db => db.ExerciseEntries.CountAsync(x => x.UserEmail == email && x.LocalDate == date));

        activityCount.Should().Be(1, "the day-keyed DailyActivity upserts, never duplicates");
        sleepCount.Should().Be(1, "the sleep logId is de-duped via HealthImportLog");
        workoutCount.Should().Be(1, "the workout logId is de-duped via HealthImportLog");

        // The import log holds one row per signal kind (steps, hr, sleep, workout) — never doubled.
        var logCount = await WithDb(db => db.HealthImportLogs.CountAsync(l => l.UserEmail == email));
        logCount.Should().Be(4);
    }

    // =====================================================================================
    // NO-CLOBBER-MANUAL — a Watch sync never overwrites a Manual row
    // =====================================================================================

    [Fact]
    public async Task Watch_sync_never_overwrites_a_manual_activity_row()
    {
        var (email, _, id) = await ProvisionUser("health.sync");
        var connId = await SeedConnection(id, email, "refresh-1");
        var date = new DateOnly(2026, 6, 21);

        // The owner TYPED their steps for the day (a Manual row).
        await WithDb<object?>(async db =>
        {
            db.DailyActivities.Add(new DailyActivity
            {
                UserEmail = email, LocalDate = date, Steps = 12345, Source = SourceKind.Manual,
                CalorieMode = ActivityCalorieMode.Add, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            return null;
        });

        // The watch reports different steps — must NOT clobber the manual row.
        await MapDay(connId, Signals(date, activity: (8000, 5000, 400, 58)));

        var rows = await WithDb(db => db.DailyActivities
            .Where(x => x.UserEmail == email && x.LocalDate == date).ToListAsync());
        rows.Should().HaveCount(1);
        rows[0].Source.Should().Be(SourceKind.Manual);
        rows[0].Steps.Should().Be(12345, "the owner-typed value stands; the watch never overwrites a Manual row");
        rows[0].RestingHeartRate.Should().BeNull("HR was not written onto the protected manual row");
    }

    [Fact]
    public async Task Watch_sync_never_overwrites_a_manual_sleep_row()
    {
        var (email, _, id) = await ProvisionUser("health.sync");
        var connId = await SeedConnection(id, email, "refresh-1");
        var date = new DateOnly(2026, 6, 22);

        await WithDb<object?>(async db =>
        {
            db.SleepEntries.Add(new SleepEntry
            {
                UserEmail = email, LocalDate = date, Hours = 6.0m, Quality = 3, Source = SourceKind.Manual,
                CreatedUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            return null;
        });

        await MapDay(connId, Signals(date, sleep: ("sleep-zzz", 8.0m)));

        var rows = await WithDb(db => db.SleepEntries
            .Where(x => x.UserEmail == email && x.LocalDate == date).ToListAsync());
        rows.Should().HaveCount(1, "no competing Watch row is added when a Manual sleep row exists for the day");
        rows[0].Source.Should().Be(SourceKind.Manual);
        rows[0].Hours.Should().Be(6.0m);
    }

    // =====================================================================================
    // ROTATED-TOKEN — a refresh persists the NEW refresh token (the #1 Fitbit correctness rule)
    // =====================================================================================

    [Fact]
    public async Task A_refresh_persists_the_rotated_refresh_token()
    {
        var (email, _, id) = await ProvisionUser("health.sync");
        var connId = await SeedConnection(id, email, "old-refresh-token");

        // Build a provider whose Fitbit HttpClient is routed to an in-memory handler that returns a token
        // response carrying a NEW refresh token + a minimal activity body — proving the rotated token is
        // re-stored (encrypted) after the refresh.
        var handler = new FakeFitbitHandler("rotated-refresh-token-NEW");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<TokenProtector>();
        var provider = new FitbitHealthProvider(
            new SingleClientFactory(handler), db, protector,
            Microsoft.Extensions.Options.Options.Create(new FitbitOptions
            {
                ClientId = "test-fitbit-id", ClientSecret = "test-fitbit-secret",
            }),
            scope.ServiceProvider.GetRequiredService<ILogger<FitbitHealthProvider>>());

        var conn = await db.HealthConnections.FirstAsync(c => c.Id == connId);
        var result = await provider.PullDayAsync(conn, new DateOnly(2026, 6, 23), TimeZoneInfo.Utc);
        result.Ok.Should().BeTrue();

        // The stored refresh token must now decrypt to the ROTATED value — never the old one.
        var stored = await WithDb(d => d.HealthConnections.AsNoTracking()
            .Where(c => c.Id == connId).Select(c => c.EncryptedRefreshToken).FirstAsync());
        protector.Unprotect(stored).Should().Be("rotated-refresh-token-NEW");
        stored.Should().NotContain("rotated-refresh-token-NEW", "the refresh token is stored ENCRYPTED, never plaintext");
    }

    // =====================================================================================
    // TZ-MAPPING — the scheduler anchors today-local in the OWNER's timezone
    // =====================================================================================

    [Fact]
    public async Task Scheduler_anchors_today_local_in_the_owner_timezone()
    {
        var (email, _, id) = await ProvisionUser("health.sync");
        // Seed an agent row carrying the user's timezone (the scheduler resolves the owner's tz from it).
        await WithDb<object?>(async db =>
        {
            db.ScheduledAgents.Add(new ScheduledAgent
            {
                UserEmail = email, Kind = ScheduledAgentKind.MorningBriefing, Enabled = false,
                TimeZone = "Pacific/Kiritimati", // UTC+14 — the day boundary is far from UTC
                CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            return null;
        });
        var connId = await SeedConnection(id, email, "refresh-1");

        // 2026-06-23 10:00 UTC is already 2026-06-24 00:00 in UTC+14. A fake provider records WHICH local
        // dates the scheduler asked it to pull; the newest must be the OWNER-LOCAL today (the 24th), not UTC's 23rd.
        var fake = new RecordingHealthProvider();
        var mapper = await ResolveMapper();
        var sched = new HealthSyncScheduler(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            factory.Services.GetRequiredService<ILogger<HealthSyncScheduler>>());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
            var conn = await db.HealthConnections.FirstAsync(c => c.Id == connId);
            await sched.SyncConnectionAsync(db, fake, mapper, conn,
                new DateTime(2026, 6, 23, 10, 0, 0, DateTimeKind.Utc),
                TimeZoneInfo.FindSystemTimeZoneById("Pacific/Kiritimati"));
        }

        fake.PulledDates.Should().NotBeEmpty();
        fake.PulledDates.Max().Should().Be(new DateOnly(2026, 6, 24),
            "today-local in UTC+14 is the 24th even though it's still the 23rd in UTC");
    }

    // =====================================================================================
    // OWNER-ONLY — synced rows are written only under the owner's email
    // =====================================================================================

    [Fact]
    public async Task Synced_rows_are_written_only_under_the_owner_email()
    {
        var (ownerEmail, _, ownerId) = await ProvisionUser("health.sync");
        var (otherEmail, _, _) = await ProvisionUser("health.sync");
        var connId = await SeedConnection(ownerId, ownerEmail, "refresh-1");
        var date = new DateOnly(2026, 6, 24);

        await MapDay(connId, Signals(date,
            activity: (5000, 3000, 200, 60), sleep: ("s-1", 7.0m), workout: ("w-1", "Walk", 20, 80)));

        // The OTHER user has nothing — every synced row is keyed to the owner only.
        (await WithDb(db => db.SleepEntries.CountAsync(x => x.UserEmail == otherEmail))).Should().Be(0);
        (await WithDb(db => db.DailyActivities.CountAsync(x => x.UserEmail == otherEmail))).Should().Be(0);
        (await WithDb(db => db.ExerciseEntries.CountAsync(x => x.UserEmail == otherEmail))).Should().Be(0);

        // And the owner's own sensitive rows ARE present (sleep + resting HR), owner-scoped.
        var sleep = await WithDb(db => db.SleepEntries.FirstAsync(x => x.UserEmail == ownerEmail && x.LocalDate == date));
        sleep.Source.Should().Be(SourceKind.Watch);
        var activity = await WithDb(db => db.DailyActivities.FirstAsync(x => x.UserEmail == ownerEmail && x.LocalDate == date));
        activity.RestingHeartRate.Should().Be(60);
    }

    // =====================================================================================
    // Helpers
    // =====================================================================================

    private async Task MapDay(int connId, HealthDaySignals signals)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
        var mapper = new HealthSyncMapper(db);
        var conn = await db.HealthConnections.FirstAsync(c => c.Id == connId);
        await mapper.MapDayAsync(conn, signals);
    }

    private Task<HealthSyncMapper> ResolveMapper()
    {
        // A mapper bound to a long-lived scope's DbContext for the scheduler test (the scheduler opens its
        // own writes via the same context instance passed to SyncConnectionAsync).
        var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
        return Task.FromResult(new HealthSyncMapper(db));
    }

    /// <summary>An IHealthProvider that records WHICH local dates it was asked to pull (and returns empty
    /// signals), for the timezone-boundary test.</summary>
    private sealed class RecordingHealthProvider : IHealthProvider
    {
        public List<DateOnly> PulledDates { get; } = new();
        public HealthProvider Provider => HealthProvider.Fitbit;
        public bool IsConfigured => true;
        public string Scopes => "activity sleep heartrate";
        public Task<bool> ConnectAsync(int u, string e, string c, string r, string? v, CancellationToken ct = default)
            => Task.FromResult(true);
        public Task<HealthDayResult> PullDayAsync(HealthConnection conn, DateOnly localDate, TimeZoneInfo tz, CancellationToken ct = default)
        {
            PulledDates.Add(localDate);
            return Task.FromResult(HealthDayResult.Value(new HealthDaySignals(localDate, null,
                Array.Empty<SleepRecord>(), Array.Empty<WorkoutRecord>())));
        }
    }

    /// <summary>An IHttpClientFactory that always hands back a client wrapping the given handler (Fitbit base).</summary>
    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.fitbit.com") };
    }

    /// <summary>An in-memory Fitbit handler: the token endpoint returns an access token + the given ROTATED
    /// refresh token; every API GET returns a minimal valid body so the pull succeeds.</summary>
    private sealed class FakeFitbitHandler(string rotatedRefreshToken) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            string json;
            if (url.Contains("/oauth2/token"))
                json = $$"""{"access_token":"access-xyz","refresh_token":"{{rotatedRefreshToken}}","expires_in":28800,"user_id":"FBUSER"}""";
            else if (url.Contains("/activities/heart/"))
                json = """{"activities-heart":[{"value":{"restingHeartRate":55}}]}""";
            else if (url.Contains("/activities/date/"))
                json = """{"summary":{"steps":4321,"activityCalories":300,"distances":[{"activity":"total","distance":3.2}]}}""";
            else if (url.Contains("/sleep/date/"))
                json = """{"sleep":[]}""";
            else if (url.Contains("/activities/list"))
                json = """{"activities":[]}""";
            else
                json = "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
                RequestMessage = request,
            });
        }
    }
}
