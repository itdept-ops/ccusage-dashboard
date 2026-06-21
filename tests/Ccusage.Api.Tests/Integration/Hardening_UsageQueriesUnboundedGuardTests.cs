using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Ccusage.Api.Tests.Integration;

/// <summary>
/// Hardening guard for <c>UsageQueries</c>: the calendar / heatmap / stats paths materialize one in-memory
/// row PER usage record, so an unbounded (no from/to) request previously pulled the whole table. The fix
/// adds a defensive row-materialization cap on the open-ended path. The CRITICAL contract is that the guard
/// is transparent for legitimate ranges: an open-ended request and a wide bounded request that cover the
/// same data must return byte-identical results. These tests pin that equivalence so the cap can never
/// silently change a real caller's numbers.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class Hardening_UsageQueriesUnboundedGuardTests(WebAppFactory factory)
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

    private HttpClient WithKey(string key)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Ingest-Key", key);
        return c;
    }

    private async Task<(string email, HttpClient client)> ProvisionUser(params string[] permissions)
    {
        var email = $"guard-{Guid.NewGuid():N}@test.local";
        var res = await Admin().PostAsJsonAsync("/api/users", new { email, isEnabled = true, permissions });
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        return (email, Client(email));
    }

    private static async Task<string> CreateKeyAs(HttpClient client, string name)
    {
        var resp = await client.PostAsJsonAsync("/api/ingest-keys", new { name });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var j = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return j.GetProperty("key").GetString()!;
    }

    private static object Row(string dedupKey, string timestampUtc, string machine) => new
    {
        dedupKey,
        timestampUtc,
        model = "claude-opus-4-8",
        input = 1000L,
        output = 500L,
        cacheRead = 200L,
        cache5m = 0L,
        cache1h = 0L,
        sessionId = "sess-" + dedupKey,
        cwd = @"C:\work\guard-repo",
        gitBranch = "main",
        isSidechain = false,
        agentId = (string?)null,
        version = "1.0.0",
    };

    /// <summary>
    /// Ingests a small spread of rows across distinct days + hours under a unique machine, then returns a
    /// (machine) filter value that isolates exactly those rows from everything else in the shared database.
    /// </summary>
    private async Task<string> SeedSpread()
    {
        var machine = "guard-" + Guid.NewGuid().ToString("N")[..10];
        var (_, owner) = await ProvisionUser("reporter.self");
        var key = await CreateKeyAs(owner, "guard-" + Guid.NewGuid().ToString("N")[..6]);

        // A handful of messages spread across three days and several local hours: enough to exercise
        // sessionization (gaps), the weekday/hour heatmap grid, and per-day calendar aggregation.
        var stamps = new[]
        {
            "2026-03-02T09:00:00Z", "2026-03-02T09:05:00Z", "2026-03-02T14:30:00Z",
            "2026-03-03T08:15:00Z", "2026-03-03T08:40:00Z",
            "2026-03-05T22:10:00Z",
        };
        var rows = stamps.Select(t => Row(Guid.NewGuid().ToString("N"), t, machine)).ToArray();
        var resp = await WithKey(key).PostAsJsonAsync("/api/ingest", new { source = "claude", machine, rows });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return machine;
    }

    private static async Task<string> Body(HttpResponseMessage r)
    {
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        return await r.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task Open_ended_calendar_heatmap_stats_match_a_wide_bounded_range_byte_for_byte()
    {
        var machine = await SeedSpread();
        var (_, viewer) = await ProvisionUser("calendar.view");

        // A bounded window that fully contains the seeded data. Filtering by the unique machine isolates
        // these rows so the open-ended request (no from/to) sees the same set the bounded one does.
        const string wide = "from=2026-01-01&to=2026-12-31";
        var mf = $"machine={machine}";

        // Open-ended (no from/to → hits the new cap+order path) vs. wide bounded (untouched legacy path).
        var calOpen = await Body(await viewer.GetAsync($"/api/usage/calendar?{mf}"));
        var calBounded = await Body(await viewer.GetAsync($"/api/usage/calendar?{mf}&{wide}"));
        calOpen.Should().Be(calBounded);

        var heatOpen = await Body(await viewer.GetAsync($"/api/usage/heatmap?{mf}"));
        var heatBounded = await Body(await viewer.GetAsync($"/api/usage/heatmap?{mf}&{wide}"));
        heatOpen.Should().Be(heatBounded);

        var statsOpen = await Body(await viewer.GetAsync($"/api/usage/stats?{mf}"));
        var statsBounded = await Body(await viewer.GetAsync($"/api/usage/stats?{mf}&{wide}"));
        statsOpen.Should().Be(statsBounded);

        // Sanity: the responses actually reflect the seeded spread (not two identical empties).
        var cal = JsonDocument.Parse(calOpen).RootElement;
        cal.GetArrayLength().Should().Be(3); // three distinct local days
        var heat = JsonDocument.Parse(heatOpen).RootElement;
        heat.GetArrayLength().Should().BeGreaterThan(0);
        var stats = JsonDocument.Parse(statsOpen).RootElement;
        stats.GetProperty("activeDays").GetInt32().Should().Be(3);
    }
}
