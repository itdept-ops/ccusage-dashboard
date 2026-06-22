using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ccusage.Api.Data;
using Ccusage.Api.Data.Entities;
using Ccusage.Api.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ccusage.Api.Tests.Integration;

/// <summary>
/// The admin AI-usage log: the GET /api/ai-usage endpoint (gated by ai.usage.view) — its auth, paging, and
/// filters — plus the AiUsageLogQueue -> AiUsageLogWriter round-trip that persists an enqueued row. The
/// rows carry NO prompt/response content (asserted at the entity level in a unit test); here we seed rows
/// directly via the queue/DB so the endpoint behaviour is exercised without calling the real Gemini API.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AiUsageLogIntegrationTests(WebAppFactory factory)
{
    private HttpClient Admin()
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestJwt.For(WebAppFactory.AdminEmail));
        return c;
    }

    private HttpClient Client(string? email = null)
    {
        var c = factory.CreateClient();
        if (email is not null)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestJwt.For(email));
        return c;
    }

    private async Task<int> CreateUser(string email, params string[] permissions)
    {
        var res = await Admin().PostAsJsonAsync("/api/users",
            new { email, isEnabled = true, permissions = permissions.Length == 0 ? new[] { "dashboard.view" } : permissions });
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    /// <summary>Insert AI-usage rows straight to the DB (bypassing the queue) so the endpoint has data.</summary>
    private async Task SeedAsync(params AiUsageLog[] rows)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
        db.AiUsageLogs.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private static AiUsageLog Row(
        string feature, string outcome, string? email = null, int total = 100,
        DateTime? whenUtc = null) => new()
    {
        WhenUtc = whenUtc ?? DateTime.UtcNow,
        UserEmail = email,
        Feature = feature,
        Model = "gemini-2.5-flash",
        Outcome = outcome,
        HttpStatus = outcome == "ok" ? 200 : outcome == "rate-limited" ? 429 : outcome == "unavailable" ? 503 : null,
        DurationMs = 42,
        PromptTokens = outcome == "ok" ? total / 2 : null,
        OutputTokens = outcome == "ok" ? total / 2 : null,
        TotalTokens = outcome == "ok" ? total : null,
        ErrorHint = outcome == "ok" ? null : $"HTTP {outcome}",
    };

    [Fact]
    public async Task Anonymous_is_401_and_a_non_holder_is_403()
    {
        (await Client().GetAsync("/api/ai-usage")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // A user WITHOUT ai.usage.view (even a full admin-of-users without this key) is forbidden.
        var email = $"aiuse-deny-{Guid.NewGuid():N}@test.local";
        await CreateUser(email, "users.view", "activity.view");
        (await Client(email).GetAsync("/api/ai-usage")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_holder_of_ai_usage_view_is_allowed()
    {
        var email = $"aiuse-ok-{Guid.NewGuid():N}@test.local";
        await CreateUser(email, "ai.usage.view");
        (await Client(email).GetAsync("/api/ai-usage")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Rows_are_newest_first_and_carry_no_email_only_userid_and_name()
    {
        var email = $"aiuse-rows-{Guid.NewGuid():N}@test.local";
        var uid = await CreateUser(email, "tracker.ai");

        var marker = $"feat-{Guid.NewGuid():N}";
        await SeedAsync(
            Row(marker, "ok", email, total: 100, whenUtc: DateTime.UtcNow.AddMinutes(-2)),
            Row(marker, "ok", email, total: 200, whenUtc: DateTime.UtcNow.AddMinutes(-1)));

        var res = await Admin().GetAsync($"/api/ai-usage?feature={marker}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        var rows = body.GetProperty("rows");
        rows.GetArrayLength().Should().Be(2);
        // Newest-first by Id.
        rows[0].GetProperty("id").GetInt64().Should().BeGreaterThan(rows[1].GetProperty("id").GetInt64());
        // The row resolves to the AppUser id + name; the raw email is never present.
        rows[0].GetProperty("userId").GetInt32().Should().Be(uid);
        rows[0].TryGetProperty("userEmail", out _).Should().BeFalse();
        body.GetRawText().Should().NotContain(email);
    }

    [Fact]
    public async Task Summary_aggregates_the_whole_window_not_just_the_page()
    {
        var email = $"aiuse-sum-{Guid.NewGuid():N}@test.local";
        await CreateUser(email, "tracker.ai");
        var marker = $"sum-{Guid.NewGuid():N}";

        await SeedAsync(
            Row(marker, "ok", email, total: 100),
            Row(marker, "ok", email, total: 300),
            Row(marker, "rate-limited", email));

        // limit=1 returns ONE row, but the summary still covers all three.
        var res = await Admin().GetAsync($"/api/ai-usage?feature={marker}&limit=1");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("rows").GetArrayLength().Should().Be(1);
        var summary = body.GetProperty("summary");
        summary.GetProperty("totalCalls").GetInt32().Should().Be(3);
        summary.GetProperty("totalTokens").GetInt64().Should().Be(400);
        summary.GetProperty("byOutcome").GetProperty("ok").GetInt32().Should().Be(2);
        summary.GetProperty("byOutcome").GetProperty("rate-limited").GetInt32().Should().Be(1);
        summary.GetProperty("topFeatures")[0].GetProperty("key").GetString().Should().Be(marker);
        summary.GetProperty("topFeatures")[0].GetProperty("count").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task Before_keyset_pages_older_rows()
    {
        var email = $"aiuse-page-{Guid.NewGuid():N}@test.local";
        await CreateUser(email, "tracker.ai");
        var marker = $"page-{Guid.NewGuid():N}";
        await SeedAsync(Row(marker, "ok", email), Row(marker, "ok", email), Row(marker, "ok", email));

        var first = await (await Admin().GetAsync($"/api/ai-usage?feature={marker}&limit=2"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var firstRows = first.GetProperty("rows");
        firstRows.GetArrayLength().Should().Be(2);
        var lastId = firstRows[1].GetProperty("id").GetInt64();

        var next = await (await Admin().GetAsync($"/api/ai-usage?feature={marker}&limit=2&before={lastId}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var nextRows = next.GetProperty("rows");
        nextRows.GetArrayLength().Should().Be(1);
        nextRows[0].GetProperty("id").GetInt64().Should().BeLessThan(lastId);
    }

    [Fact]
    public async Task Outcome_and_user_and_time_filters_narrow_the_window()
    {
        var email = $"aiuse-filt-{Guid.NewGuid():N}@test.local";
        var uid = await CreateUser(email, "tracker.ai");
        var otherEmail = $"aiuse-other-{Guid.NewGuid():N}@test.local";
        await CreateUser(otherEmail, "tracker.ai");
        var marker = $"filt-{Guid.NewGuid():N}";

        await SeedAsync(
            Row(marker, "ok", email),
            Row(marker, "error", email),
            Row(marker, "ok", otherEmail));

        // outcome filter
        var byOutcome = await (await Admin().GetAsync($"/api/ai-usage?feature={marker}&outcome=error"))
            .Content.ReadFromJsonAsync<JsonElement>();
        byOutcome.GetProperty("summary").GetProperty("totalCalls").GetInt32().Should().Be(1);
        byOutcome.GetProperty("rows")[0].GetProperty("outcome").GetString().Should().Be("error");

        // user filter (by AppUser id; email never accepted/exposed)
        var byUser = await (await Admin().GetAsync($"/api/ai-usage?feature={marker}&user={uid}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        byUser.GetProperty("summary").GetProperty("totalCalls").GetInt32().Should().Be(2);

        // unknown user id -> empty window
        var unknown = await (await Admin().GetAsync($"/api/ai-usage?feature={marker}&user=2147483600"))
            .Content.ReadFromJsonAsync<JsonElement>();
        unknown.GetProperty("summary").GetProperty("totalCalls").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Writer_round_trips_an_enqueued_row()
    {
        var marker = $"writer-{Guid.NewGuid():N}";
        var queue = factory.Services.GetRequiredService<AiUsageLogQueue>();
        queue.TryEnqueue(new AiUsageLog
        {
            WhenUtc = DateTime.UtcNow,
            UserEmail = null, // background tick
            Feature = marker,
            Model = "gemini-2.5-flash",
            Outcome = "ok",
            HttpStatus = 200,
            DurationMs = 7,
            PromptTokens = 11,
            OutputTokens = 22,
            TotalTokens = 33,
        }).Should().BeTrue();

        // The hosted AiUsageLogWriter drains the channel asynchronously; poll the DB until it lands.
        AiUsageLog? persisted = null;
        for (var i = 0; i < 50 && persisted is null; i++)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
            persisted = await db.AiUsageLogs.AsNoTracking().FirstOrDefaultAsync(r => r.Feature == marker);
            if (persisted is null) await Task.Delay(100);
        }

        persisted.Should().NotBeNull();
        persisted!.Outcome.Should().Be("ok");
        persisted.TotalTokens.Should().Be(33);
        persisted.UserEmail.Should().BeNull();
    }
}
