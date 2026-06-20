using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ccusage.Api.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ccusage.Api.Tests.Integration;

/// <summary>
/// Family Hub F7 — QUICK-ADD (<c>POST /api/family/quick-add</c>). Covers the dual auth (a normal user JWT
/// OR the desktop agent's <c>X-Ingest-Key</c>), the family.use gate, the kind routing (list / reminder /
/// note / auto), the light natural-time parse into a future DueUtc with the phrase stripped, and the
/// privacy rule that NO email is ever on the wire. The ingest-key path is narrow: it only ever creates
/// items in the KEY OWNER's household, and a key whose owner lacks family.use is rejected. Each test
/// provisions fresh users so they're order-independent.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class FamilyQuickAddTests(WebAppFactory factory)
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

    private async Task<(string email, HttpClient client, int id)> ProvisionUser(params string[] permissions)
    {
        var email = $"famqa-{Guid.NewGuid():N}@test.local";
        var res = await Admin().PostAsJsonAsync("/api/users", new { email, isEnabled = true, permissions });
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await Json(res)).GetProperty("id").GetInt32();
        return (email, Client(email), id);
    }

    /// <summary>Mint an ingest key owned by the given (self-service) user; returns the raw key.</summary>
    private static async Task<string> CreateKeyAs(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync("/api/ingest-keys", new { name = "qa-agent" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await Json(resp)).GetProperty("key").GetString()!;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        await resp.Content.ReadFromJsonAsync<JsonElement>();

    // =====================================================================================
    // GATING
    // =====================================================================================

    [Fact]
    public async Task Quick_add_requires_family_use_on_the_jwt_path()
    {
        var (_, plain, _) = await ProvisionUser("dashboard.view");
        var resp = await plain.PostAsJsonAsync("/api/family/quick-add", new { text = "milk" });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Quick_add_without_any_credential_is_unauthorized()
    {
        var anon = factory.CreateClient();
        (await anon.PostAsJsonAsync("/api/family/quick-add", new { text = "milk" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Empty_text_is_rejected()
    {
        var (_, user, _) = await ProvisionUser("family.use");
        (await user.PostAsJsonAsync("/api/family/quick-add", new { text = "   " }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // =====================================================================================
    // KIND: LIST
    // =====================================================================================

    [Fact]
    public async Task Kind_list_appends_to_the_default_quick_capture_list_no_email()
    {
        var (_, user, _) = await ProvisionUser("family.use");

        var resp = await user.PostAsJsonAsync("/api/family/quick-add",
            new { text = "eggs", kind = "list" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var r = await Json(resp);
        r.GetProperty("kind").GetString().Should().Be("list");
        r.GetProperty("createdId").GetInt64().Should().BeGreaterThan(0);
        r.GetRawText().Should().NotContain("@");

        // It landed on a household list named "Quick Capture".
        var lists = await Json(await user.GetAsync("/api/family/lists"));
        var qc = lists.EnumerateArray().Single(l => l.GetProperty("name").GetString() == "Quick Capture");
        qc.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("text").GetString())
            .Should().Contain("eggs");
    }

    [Fact]
    public async Task Kind_list_with_a_name_finds_or_creates_that_named_list()
    {
        var (_, user, _) = await ProvisionUser("family.use");

        await user.PostAsJsonAsync("/api/family/quick-add", new { text = "milk", kind = "list", listName = "Groceries" });
        await user.PostAsJsonAsync("/api/family/quick-add", new { text = "bread", kind = "list", listName = "groceries" }); // case-insensitive

        var lists = await Json(await user.GetAsync("/api/family/lists"));
        var groceries = lists.EnumerateArray().Where(l =>
            string.Equals(l.GetProperty("name").GetString(), "Groceries", StringComparison.OrdinalIgnoreCase)).ToList();
        groceries.Should().HaveCount(1); // find-or-create did NOT make a second list
        groceries[0].GetProperty("items").EnumerateArray().Select(i => i.GetProperty("text").GetString())
            .Should().Contain(new[] { "milk", "bread" });
    }

    // =====================================================================================
    // KIND: REMINDER — light natural-time parse, phrase stripped, targets the caller
    // =====================================================================================

    [Fact]
    public async Task Kind_reminder_parses_tomorrow_9am_into_a_future_due_targeting_the_caller_phrase_stripped()
    {
        var (email, user, userId) = await ProvisionUser("family.use");

        var resp = await user.PostAsJsonAsync("/api/family/quick-add",
            new { text = "tomorrow 9am call dentist", kind = "reminder" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var r = await Json(resp);
        r.GetProperty("kind").GetString().Should().Be("reminder");
        r.GetRawText().Should().NotContain("@");
        var reminderId = r.GetProperty("createdId").GetInt64();

        // The reminder reads through the normal F2 endpoint with the time phrase removed and DueUtc future.
        var reminders = await Json(await user.GetAsync("/api/family/reminders"));
        var made = reminders.EnumerateArray().Single(x => x.GetProperty("id").GetInt64() == reminderId);
        made.GetProperty("text").GetString()!.ToLowerInvariant().Should().Contain("call dentist");
        made.GetProperty("text").GetString()!.ToLowerInvariant().Should().NotContain("tomorrow");
        made.GetProperty("text").GetString()!.ToLowerInvariant().Should().NotContain("9am");
        made.GetProperty("targetUserId").GetInt32().Should().Be(userId);
        made.GetProperty("dueUtc").GetDateTime().ToUniversalTime().Should().BeAfter(DateTime.UtcNow);
        reminders.GetRawText().Should().NotContain("@");

        // And DueUtc is roughly a day out (tomorrow 9am local), well beyond the +1h no-parse fallback.
        made.GetProperty("dueUtc").GetDateTime().ToUniversalTime()
            .Should().BeAfter(DateTime.UtcNow.AddHours(6));
    }

    [Fact]
    public async Task Reminder_with_no_recognizable_time_defaults_to_about_one_hour_out()
    {
        var (_, user, _) = await ProvisionUser("family.use");

        var resp = await user.PostAsJsonAsync("/api/family/quick-add",
            new { text = "water the plants", kind = "reminder" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var reminderId = (await Json(resp)).GetProperty("createdId").GetInt64();

        var reminders = await Json(await user.GetAsync("/api/family/reminders"));
        var made = reminders.EnumerateArray().Single(x => x.GetProperty("id").GetInt64() == reminderId);
        var due = made.GetProperty("dueUtc").GetDateTime().ToUniversalTime();
        due.Should().BeCloseTo(DateTime.UtcNow.AddHours(1), TimeSpan.FromMinutes(5));
        made.GetProperty("text").GetString().Should().Be("water the plants");
    }

    // =====================================================================================
    // KIND: NOTE — first line -> Title, rest -> Body
    // =====================================================================================

    [Fact]
    public async Task Kind_note_creates_a_note_first_line_is_the_title()
    {
        var (_, user, _) = await ProvisionUser("family.use");

        var resp = await user.PostAsJsonAsync("/api/family/quick-add",
            new { text = "Wifi password\nhunter2 on the fridge", kind = "note" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var r = await Json(resp);
        r.GetProperty("kind").GetString().Should().Be("note");
        var noteId = r.GetProperty("createdId").GetInt64();

        var notes = await Json(await user.GetAsync("/api/family/notes"));
        var made = notes.EnumerateArray().Single(x => x.GetProperty("id").GetInt64() == noteId);
        made.GetProperty("title").GetString().Should().Be("Wifi password");
        made.GetProperty("body").GetString().Should().Be("hunter2 on the fridge");
        notes.GetRawText().Should().NotContain("@");
    }

    // =====================================================================================
    // KIND: AUTO routing
    // =====================================================================================

    [Fact]
    public async Task Auto_routes_remind_me_to_a_reminder()
    {
        var (_, user, userId) = await ProvisionUser("family.use");

        var resp = await user.PostAsJsonAsync("/api/family/quick-add",
            new { text = "remind me to take out the trash tonight" }); // kind omitted → auto
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var r = await Json(resp);
        r.GetProperty("kind").GetString().Should().Be("reminder");

        var made = (await Json(await user.GetAsync("/api/family/reminders")))
            .EnumerateArray().Single(x => x.GetProperty("id").GetInt64() == r.GetProperty("createdId").GetInt64());
        // The "remind me to" lead-in and the "tonight" time phrase are both stripped from the saved text.
        var t = made.GetProperty("text").GetString()!.ToLowerInvariant();
        t.Should().Contain("take out the trash");
        t.Should().NotContain("remind");
        t.Should().NotContain("tonight");
        made.GetProperty("targetUserId").GetInt32().Should().Be(userId);
    }

    [Fact]
    public async Task Auto_routes_note_prefix_to_a_note()
    {
        var (_, user, _) = await ProvisionUser("family.use");

        var resp = await user.PostAsJsonAsync("/api/family/quick-add",
            new { text = "note: garage code is 4815" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var r = await Json(resp);
        r.GetProperty("kind").GetString().Should().Be("note");

        var made = (await Json(await user.GetAsync("/api/family/notes")))
            .EnumerateArray().Single(x => x.GetProperty("id").GetInt64() == r.GetProperty("createdId").GetInt64());
        made.GetProperty("title").GetString().Should().Be("garage code is 4815"); // "note:" marker stripped
    }

    [Fact]
    public async Task Auto_routes_a_plain_item_to_the_list()
    {
        var (_, user, _) = await ProvisionUser("family.use");

        var resp = await user.PostAsJsonAsync("/api/family/quick-add", new { text = "paper towels" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(resp)).GetProperty("kind").GetString().Should().Be("list");
    }

    // =====================================================================================
    // INGEST-KEY AUTH — resolves to the key owner, creates in THEIR household
    // =====================================================================================

    [Fact]
    public async Task A_valid_ingest_key_resolves_to_its_owner_and_creates_in_their_household()
    {
        // The owner self-mints a key (reporter.self) and ALSO holds family.use.
        var (ownerEmail, owner, ownerId) = await ProvisionUser("family.use", "reporter.self");
        var key = await CreateKeyAs(owner);

        // The agent posts with ONLY the key (no JWT) — no owner identity is sent by the client.
        var resp = await WithKey(key).PostAsJsonAsync("/api/family/quick-add",
            new { text = "agent-captured milk", kind = "list" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var r = await Json(resp);
        r.GetProperty("kind").GetString().Should().Be("list");
        r.GetRawText().Should().NotContain("@");
        r.GetRawText().Should().NotContain(ownerEmail);

        // The item lives in the OWNER's household: the owner's own JWT GET sees it.
        var lists = await Json(await owner.GetAsync("/api/family/lists"));
        var allItems = lists.EnumerateArray()
            .SelectMany(l => l.GetProperty("items").EnumerateArray())
            .Select(i => i.GetProperty("text").GetString());
        allItems.Should().Contain("agent-captured milk");

        // And the created list was created-by the key owner (server-derived attribution).
        var qc = lists.EnumerateArray().Single(l => l.GetProperty("name").GetString() == "Quick Capture");
        qc.GetProperty("createdByUserId").GetInt32().Should().Be(ownerId);
    }

    [Fact]
    public async Task An_ingest_key_whose_owner_lacks_family_use_is_rejected()
    {
        // Owner can mint keys but has NO family.use → the agent quick-add is forbidden.
        var (_, owner, _) = await ProvisionUser("reporter.self");
        var key = await CreateKeyAs(owner);

        var resp = await WithKey(key).PostAsJsonAsync("/api/family/quick-add",
            new { text = "should be blocked", kind = "list" });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_bogus_ingest_key_is_unauthorized()
    {
        var resp = await WithKey("uiq_not-a-real-key").PostAsJsonAsync("/api/family/quick-add",
            new { text = "x", kind = "list" });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_revoked_ingest_key_is_unauthorized()
    {
        var (_, owner, _) = await ProvisionUser("family.use", "reporter.self");
        var created = await owner.PostAsJsonAsync("/api/ingest-keys", new { name = "to-revoke" });
        var j = await Json(created);
        var keyId = j.GetProperty("id").GetInt32();
        var key = j.GetProperty("key").GetString()!;

        (await owner.DeleteAsync($"/api/ingest-keys/{keyId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await WithKey(key).PostAsJsonAsync("/api/family/quick-add", new { text = "x", kind = "list" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // =====================================================================================
    // PRIVACY — adversarial no-email check across kinds, plus DB-side DueUtc verification
    // =====================================================================================

    [Fact]
    public async Task Reminder_due_is_persisted_in_the_future_in_the_database()
    {
        var (_, user, _) = await ProvisionUser("family.use");
        var resp = await user.PostAsJsonAsync("/api/family/quick-add",
            new { text = "in 30 min stretch", kind = "reminder" });
        var reminderId = (await Json(resp)).GetProperty("createdId").GetInt64();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
        var reminder = await db.FamilyReminders.AsNoTracking().FirstAsync(x => x.Id == reminderId);
        reminder.DueUtc.Should().BeAfter(DateTime.UtcNow.AddMinutes(20));
        reminder.DueUtc.Should().BeBefore(DateTime.UtcNow.AddMinutes(40));
        reminder.Text.Should().Be("stretch"); // "in 30 min" stripped
    }
}
