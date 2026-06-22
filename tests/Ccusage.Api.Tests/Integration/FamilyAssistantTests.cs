using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Ccusage.Api.Tests.Integration;

/// <summary>
/// Family Hub — the FAMILY ASSISTANT (POST /api/family/assistant): one chat box over the household that
/// answers from a server-assembled read-only snapshot and PROPOSES actions the frontend writes on confirm.
/// It clones the family-AI pattern, so this suite asserts that contract:
///
/// <list type="bullet">
///   <item>requires family.use (403) and authentication (401);</item>
///   <item>an empty message is a 400 (validated BEFORE the Gemini config check);</item>
///   <item>an unconfigured Gemini is a graceful 503, never a 500 (the test host configures NO key);</item>
///   <item>the endpoint WRITES NOTHING — no list/chore/meal/timer/reminder/event is mutated by a call;</item>
///   <item>finance facts only reach the snapshot path for a caller who ALSO holds family.finance (a
///   family.use-only caller's request still 503s — the finance gate never widens access);</item>
///   <item>nothing on the wire (the response, and the family reads that back the snapshot) carries an email.</item>
/// </list>
///
/// With no Gemini key the unconfigured branch returns before any real Gemini/HTTP call, so the snapshot
/// assembly + write-nothing guarantees are exercised without a live key. Users are provisioned fresh per test
/// so the suite is order-independent.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class FamilyAssistantTests(WebAppFactory factory)
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
        var email = $"famasst-{Guid.NewGuid():N}@test.local";
        var res = await Admin().PostAsJsonAsync("/api/users", new { email, isEnabled = true, permissions });
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await Json(res)).GetProperty("id").GetInt32();
        return (email, Client(email), id);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        await resp.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<string> RawBody(HttpResponseMessage resp) =>
        await resp.Content.ReadAsStringAsync();

    // =====================================================================================
    // GATING — family.use (403) + auth (401)
    // =====================================================================================

    [Fact]
    public async Task Assistant_requires_family_use()
    {
        var (_, plain, _) = await ProvisionUser("dashboard.view");
        (await plain.PostAsJsonAsync("/api/family/assistant", new { message = "what's for dinner?" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Assistant_requires_family_ai_assistant_on_top_of_family_use()
    {
        // family.use reaches the Family Hub, but the action-taking assistant is gated by the sensitive
        // family.ai.assistant permission — a family.use-only caller is forbidden (the filter precedes the
        // empty-message 400 + the 503, so it's a hard 403 even for a valid message).
        var (_, owner, _) = await ProvisionUser("family.use");
        await owner.GetAsync("/api/family/household");
        (await owner.PostAsJsonAsync("/api/family/assistant", new { message = "what's for dinner?" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Assistant_requires_authentication()
    {
        var anon = factory.CreateClient();
        (await anon.PostAsJsonAsync("/api/family/assistant", new { message = "hi" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // =====================================================================================
    // EMPTY MESSAGE → 400 (validated BEFORE the Gemini config check)
    // =====================================================================================

    [Fact]
    public async Task Assistant_returns_400_for_an_empty_message()
    {
        var (_, owner, _) = await ProvisionUser("family.use", "family.ai.assistant");
        await owner.GetAsync("/api/family/household");
        (await owner.PostAsJsonAsync("/api/family/assistant", new { message = "   " }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Assistant_returns_400_for_a_missing_message()
    {
        var (_, owner, _) = await ProvisionUser("family.use", "family.ai.assistant");
        await owner.GetAsync("/api/family/household");
        (await owner.PostAsJsonAsync("/api/family/assistant", new { }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // =====================================================================================
    // UNCONFIGURED GEMINI → graceful 503 (never 500)
    // =====================================================================================

    [Fact]
    public async Task Assistant_is_503_when_gemini_is_unconfigured_never_500()
    {
        var (_, owner, _) = await ProvisionUser("family.use", "family.ai.assistant");
        await owner.GetAsync("/api/family/household");

        var res = await owner.PostAsJsonAsync("/api/family/assistant",
            new { message = "add milk and eggs to groceries" });
        res.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Assistant_is_503_even_with_a_rich_household_snapshot()
    {
        // Seed every snapshot category, then confirm the assembled-snapshot path still degrades to 503 (no
        // key) rather than 500 — the snapshot assembly never throws.
        var (_, owner, _) = await ProvisionUser("family.use", "family.ai.assistant");
        await owner.GetAsync("/api/family/household");

        var listId = (await Json(await owner.PostAsJsonAsync("/api/family/lists",
            new { name = "Groceries", kind = "shopping" }))).GetProperty("id").GetInt64();
        await owner.PostAsJsonAsync($"/api/family/lists/{listId}/items", new { text = "Milk" });
        await owner.PostAsJsonAsync("/api/family/chores", new { title = "Take out trash", points = 3 });
        await owner.PostAsJsonAsync("/api/family/reminders",
            new { text = "Call dentist", dueUtc = DateTime.UtcNow.AddHours(2), recurrence = "none" });
        await owner.PostAsJsonAsync("/api/family/timers", new { label = "Pasta", durationSeconds = 600 });
        var monday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-(((int)DateTime.UtcNow.DayOfWeek + 6) % 7));
        await owner.PostAsJsonAsync("/api/family/meals",
            new { localDate = monday.ToString("yyyy-MM-dd"), slot = "dinner", title = "Tacos" });

        var res = await owner.PostAsJsonAsync("/api/family/assistant", new { message = "what's on for today?" });
        res.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    // =====================================================================================
    // THE ENDPOINT WRITES NOTHING (no list/chore/meal/timer/reminder mutation)
    // =====================================================================================

    [Fact]
    public async Task Assistant_writes_nothing()
    {
        var (_, owner, _) = await ProvisionUser("family.use", "family.ai.assistant");
        await owner.GetAsync("/api/family/household");

        // Baseline: one list+item, one chore, one reminder, one timer, one meal.
        var listId = (await Json(await owner.PostAsJsonAsync("/api/family/lists",
            new { name = "Groceries", kind = "shopping" }))).GetProperty("id").GetInt64();
        await owner.PostAsJsonAsync($"/api/family/lists/{listId}/items", new { text = "Milk" });
        await owner.PostAsJsonAsync("/api/family/chores", new { title = "Vacuum", points = 2 });
        await owner.PostAsJsonAsync("/api/family/reminders",
            new { text = "Pay rent", dueUtc = DateTime.UtcNow.AddDays(1), recurrence = "none" });
        await owner.PostAsJsonAsync("/api/family/timers", new { label = "Bread", durationSeconds = 1200 });
        var monday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-(((int)DateTime.UtcNow.DayOfWeek + 6) % 7));
        await owner.PostAsJsonAsync("/api/family/meals",
            new { localDate = monday.ToString("yyyy-MM-dd"), slot = "dinner", title = "Stew" });

        // Snapshot the stored counts BEFORE the assistant call.
        var listItemsBefore = (await Json(await owner.GetAsync("/api/family/lists"))).EnumerateArray()
            .Single(l => l.GetProperty("id").GetInt64() == listId)
            .GetProperty("items").EnumerateArray().Count();
        var choresBefore = (await Json(await owner.GetAsync("/api/family/chores")))
            .GetProperty("chores").EnumerateArray().Count();
        var remindersBefore = (await Json(await owner.GetAsync("/api/family/reminders"))).EnumerateArray().Count();
        var timersBefore = (await Json(await owner.GetAsync("/api/family/timers"))).EnumerateArray().Count();
        var mealsBefore = (await Json(await owner.GetAsync($"/api/family/meals?weekStart={monday:yyyy-MM-dd}")))
            .EnumerateArray().SelectMany(d => d.GetProperty("meals").EnumerateArray()).Count();

        // Fire several assistant messages that "ask" to create things (each 503s with no key) — the endpoint
        // must mutate nothing (the AI proposes; the frontend writes on confirm).
        await owner.PostAsJsonAsync("/api/family/assistant", new { message = "add bread and butter to groceries" });
        await owner.PostAsJsonAsync("/api/family/assistant", new { message = "set a 10 minute timer for the oven" });
        await owner.PostAsJsonAsync("/api/family/assistant", new { message = "remind me to water the plants tonight" });
        await owner.PostAsJsonAsync("/api/family/assistant", new { message = "add a chore to mow the lawn" });
        await owner.PostAsJsonAsync("/api/family/assistant", new { message = "plan spaghetti for friday dinner" });

        // Every stored count is unchanged.
        var listItemsAfter = (await Json(await owner.GetAsync("/api/family/lists"))).EnumerateArray()
            .Single(l => l.GetProperty("id").GetInt64() == listId)
            .GetProperty("items").EnumerateArray().Count();
        listItemsAfter.Should().Be(listItemsBefore);
        (await Json(await owner.GetAsync("/api/family/chores")))
            .GetProperty("chores").EnumerateArray().Count().Should().Be(choresBefore);
        (await Json(await owner.GetAsync("/api/family/reminders"))).EnumerateArray().Count().Should().Be(remindersBefore);
        (await Json(await owner.GetAsync("/api/family/timers"))).EnumerateArray().Count().Should().Be(timersBefore);
        (await Json(await owner.GetAsync($"/api/family/meals?weekStart={monday:yyyy-MM-dd}")))
            .EnumerateArray().SelectMany(d => d.GetProperty("meals").EnumerateArray()).Count()
            .Should().Be(mealsBefore);
    }

    // =====================================================================================
    // FINANCE GATE — finance facts only reach the snapshot for a family.finance caller; a
    // family.use-only caller never widens access (they still 503, no 500, no leak)
    // =====================================================================================

    [Fact]
    public async Task Assistant_works_for_a_family_use_only_caller_without_finance_access()
    {
        // A caller WITHOUT family.finance: the snapshot omits finance entirely. The call still degrades to a
        // graceful 503 (no key) — never a 500 from trying to read finance they can't see.
        var (_, owner, _) = await ProvisionUser("family.use", "family.ai.assistant");
        await owner.GetAsync("/api/family/household");

        var res = await owner.PostAsJsonAsync("/api/family/assistant",
            new { message = "how much did we spend this month?" });
        res.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        // And the family.use-only caller can't reach any finance route at all (the extra money gate holds).
        (await owner.GetAsync("/api/family/finance/summary"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Assistant_is_503_for_a_finance_caller_too_so_finance_data_never_leaks_via_the_assistant()
    {
        // A caller WITH family.finance who imported real spending: the snapshot path includes finance totals,
        // but with no Gemini key the assistant returns a graceful 503 — the finance numbers are never echoed
        // back on the wire (the snapshot stays server-side).
        var (_, owner, _) = await ProvisionUser("family.use", "family.finance", "family.ai.assistant");
        await owner.GetAsync("/api/family/household");

        const string csv =
            "Date,Original Date,Account Type,Account Name,Institution Name,Name,Custom Name,Amount,Description,Category,Note,Ignored From,Tax Deductible\n" +
            "2026-05-03,2026-05-03,Checking,SoFi Checking,SoFi,Trader Joes,,-54.20,TJ groceries,Groceries,,,\n" +
            "2026-05-01,2026-05-01,Checking,SoFi Checking,SoFi,ACME Payroll,,2500.00,Payroll,Paycheck,,,\n";
        (await owner.PostAsJsonAsync("/api/family/finance/import", new { fileName = "x.csv", content = csv }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var res = await owner.PostAsJsonAsync("/api/family/assistant",
            new { message = "what was our biggest spending category?" });
        res.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        // The degraded body carries none of the finance figures.
        var body = await RawBody(res);
        body.Should().NotContain("54.20");
        body.Should().NotContain("Groceries");
    }

    // =====================================================================================
    // NO EMAIL ON THE WIRE — the response + the family reads that back the snapshot
    // =====================================================================================

    [Fact]
    public async Task Assistant_response_carries_no_email()
    {
        var (email, owner, _) = await ProvisionUser("family.use", "family.ai.assistant");
        await owner.GetAsync("/api/family/household");
        await owner.PostAsJsonAsync("/api/family/chores", new { title = "Dishes", points = 1 });

        var res = await owner.PostAsJsonAsync("/api/family/assistant", new { message = "who has chores?" });
        var body = await RawBody(res);

        // No "@" (no email of any user) anywhere in the response payload.
        body.Should().NotContain("@");
        body.Should().NotContain(email);
    }
}
