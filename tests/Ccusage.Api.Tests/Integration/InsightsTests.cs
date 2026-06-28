using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ccusage.Api.Data;
using Ccusage.Api.Data.Entities;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Ccusage.Api.Tests.Integration;

/// <summary>
/// The Insight Engine (/api/insights + /api/insights/narrate): auth (401) + tracker.self (403) gating; the
/// DETERMINISTIC always-200 floor; STRICT owner-scoping (another user's rows NEVER alter the caller's cards);
/// the n&gt;=10 correlation floor (9 paired days ⇒ no correlation card); /narrate floors to fellBackToPlain
/// with AI off; the window is clamped + respected; and the engine WRITES NOTHING. Every test provisions fresh
/// users so they're order-independent.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class InsightsTests(WebAppFactory factory)
{
    private HttpClient Admin() => Client(WebAppFactory.AdminEmail);

    private HttpClient Client(string email)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestJwt.For(email));
        return c;
    }

    private async Task<(string email, HttpClient client)> ProvisionUser(params string[] permissions)
    {
        var email = $"insights-{Guid.NewGuid():N}@test.local";
        var res = await Admin().PostAsJsonAsync("/api/users", new { email, isEnabled = true, permissions });
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        return (email.ToLowerInvariant(), Client(email));
    }

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        await resp.Content.ReadFromJsonAsync<JsonElement>();

    private async Task Seed(string email, Action<UsageDbContext, string> seed)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
        seed(db, email);
        await db.SaveChangesAsync();
    }

    // Anchor off the display tz (mirrors WrappedTests) so seeded "recent" rows land inside the window.
    private static readonly DateOnly Today = DisplayTzToday();
    private static DateOnly DisplayTzToday()
    {
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); } catch { tz = TimeZoneInfo.Utc; }
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz));
    }

    // ---- Gating ----

    [Fact]
    public async Task Insights_requires_authentication()
    {
        var anon = factory.CreateClient();
        (await anon.GetAsync("/api/insights")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Insights_requires_tracker_self()
    {
        var (_, noTracker) = await ProvisionUser("dashboard.view");
        (await noTracker.GetAsync("/api/insights?window=90")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- Window clamping ----

    [Fact]
    public async Task Window_is_clamped_to_thirty_ninety_or_threesixtyfive()
    {
        var (_, user) = await ProvisionUser("tracker.self");
        (await Json(await user.GetAsync("/api/insights?window=banana")))
            .GetProperty("window").GetInt32().Should().Be(30);
        (await Json(await user.GetAsync("/api/insights?window=90")))
            .GetProperty("window").GetInt32().Should().Be(90);
        (await Json(await user.GetAsync("/api/insights?window=365")))
            .GetProperty("window").GetInt32().Should().Be(365);
    }

    // ---- Empty/insufficient-data state ----

    [Fact]
    public async Task Fresh_user_gets_the_empty_state_always_200()
    {
        var (_, user) = await ProvisionUser("tracker.self");
        var root = await Json(await user.GetAsync("/api/insights?window=90"));
        root.GetProperty("hasData").GetBoolean().Should().BeFalse();
        root.GetProperty("cards").EnumerateArray().Should().BeEmpty();
        // No email anywhere on the wire.
        root.GetRawText().Should().NotContain("@");
    }

    // ---- Deterministic engine: a strong correlation surfaces at n>=10 ----

    [Fact]
    public async Task Sleep_and_recovery_correlation_surfaces_at_ten_paired_days()
    {
        var (email, user) = await ProvisionUser("tracker.self");
        // Seed 14 days of sleep with rising hours; recovery is derived from sleep, so sleep[D] correlates with
        // recovery[D+1] (the next-day-recovery pairing). 14 sleep days ⇒ >=10 paired (sleep, next-day-recovery).
        await Seed(email, (db, e) =>
        {
            for (var i = 0; i < 14; i++)
            {
                var d = Today.AddDays(-i);
                db.SleepEntries.Add(new SleepEntry
                {
                    UserEmail = e, LocalDate = d, Hours = (decimal)(5.0 + i * 0.2), Quality = 1 + (i % 5),
                });
            }
        });

        var root = await Json(await user.GetAsync("/api/insights?window=30"));
        root.GetProperty("hasData").GetBoolean().Should().BeTrue();
        var cards = root.GetProperty("cards").EnumerateArray().ToList();
        // At least one correlation card carries the association-not-causation honesty microcopy.
        cards.Where(c => c.GetProperty("kind").GetString() == "correlation")
            .Should().NotBeEmpty();
        cards.First(c => c.GetProperty("kind").GetString() == "correlation")
            .GetProperty("detail").GetString().Should().Contain("Association, not causation");
    }

    // ---- The n>=10 floor: 9 paired days yields NO correlation card ----

    [Fact]
    public async Task Correlation_with_fewer_than_ten_paired_days_is_dropped()
    {
        var (email, user) = await ProvisionUser("tracker.self");
        // Only 9 sleep days ⇒ at most 8 paired (sleep, next-day recovery) ⇒ under the n>=10 floor.
        await Seed(email, (db, e) =>
        {
            for (var i = 0; i < 9; i++)
                db.SleepEntries.Add(new SleepEntry
                {
                    UserEmail = e, LocalDate = Today.AddDays(-i), Hours = (decimal)(6.0 + i * 0.1), Quality = 3,
                });
        });

        var root = await Json(await user.GetAsync("/api/insights?window=30"));
        var cards = root.GetProperty("cards").EnumerateArray().ToList();
        cards.Where(c => c.GetProperty("kind").GetString() == "correlation"
                && c.GetProperty("title").GetString()!.Contains("recovery"))
            .Should().BeEmpty("9 sleep days cannot reach the 10-paired-day correlation floor");
    }

    // ---- Owner-scoping: another user's rows NEVER appear in the caller's insights ----

    [Fact]
    public async Task Another_users_data_never_appears_in_the_callers_insights()
    {
        var (mineEmail, me) = await ProvisionUser("tracker.self");
        var (otherEmail, _) = await ProvisionUser("tracker.self");

        // I log nothing. The OTHER user logs a rich, correlated dataset.
        await Seed(otherEmail, (db, e) =>
        {
            for (var i = 0; i < 20; i++)
            {
                var d = Today.AddDays(-i);
                db.SleepEntries.Add(new SleepEntry
                { UserEmail = e, LocalDate = d, Hours = (decimal)(5.0 + i * 0.2), Quality = 1 + (i % 5) });
                db.WeightEntries.Add(new WeightEntry { UserEmail = e, LocalDate = d, WeightKg = 80 - i * 0.1 });
                db.HydrationEntries.Add(new HydrationEntry { UserEmail = e, LocalDate = d, AmountMl = 2500 });
            }
        });

        // My insights remain the EMPTY state — the other user's rows are invisible to me.
        var root = await Json(await me.GetAsync("/api/insights?window=90"));
        root.GetProperty("hasData").GetBoolean().Should().BeFalse();
        root.GetProperty("cards").EnumerateArray().Should().BeEmpty();
        root.GetRawText().Should().NotContain(otherEmail);
    }

    // ---- /narrate floors to the plain path when AI is off (never 503), and writes nothing ----

    [Fact]
    public async Task Narrate_falls_back_to_plain_never_503()
    {
        // tracker.self alone — the deterministic floor needs no AI.
        var (email, user) = await ProvisionUser("tracker.self");
        await Seed(email, (db, e) =>
        {
            for (var i = 0; i < 12; i++)
                db.HydrationEntries.Add(new HydrationEntry
                { UserEmail = e, LocalDate = Today.AddDays(-i), AmountMl = 2500 });
        });

        var res = await user.GetAsync("/api/insights/narrate?window=30");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await Json(res);
        dto.GetProperty("fellBackToPlain").GetBoolean().Should().BeTrue();
        dto.GetRawText().Should().NotContain("@");
    }

    [Fact]
    public async Task Narrate_with_tracker_ai_still_floors_200_when_unconfigured()
    {
        // tracker.ai is the LLM upgrade, but with Gemini OFF in the test host /narrate still 200-floors. The
        // AI-perm branch is exercised here (it just can't reach a configured Gemini).
        var (_, user) = await ProvisionUser("tracker.self", "tracker.ai");
        var res = await user.GetAsync("/api/insights/narrate?window=90");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(res)).GetProperty("fellBackToPlain").GetBoolean().Should().BeTrue();
    }

    // ---- The engine writes nothing ----

    [Fact]
    public async Task Insights_read_writes_nothing()
    {
        var (email, user) = await ProvisionUser("tracker.self");
        await Seed(email, (db, e) =>
        {
            for (var i = 0; i < 12; i++)
                db.SleepEntries.Add(new SleepEntry
                { UserEmail = e, LocalDate = Today.AddDays(-i), Hours = 7m, Quality = 4 });
        });

        int CountSleep()
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
            return db.SleepEntries.Count(s => s.UserEmail == email);
        }

        var before = CountSleep();
        (await user.GetAsync("/api/insights?window=30")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await user.GetAsync("/api/insights/narrate?window=30")).StatusCode.Should().Be(HttpStatusCode.OK);
        CountSleep().Should().Be(before, "the engine is read-only");
    }
}
