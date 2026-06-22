using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Ccusage.Api.Tests.Integration;

/// <summary>
/// Family Hub round-2 AI assists (the deeper notes/lists/meals/timers helpers):
///   POST /api/family/notes/ai/ask       — read-only Q&amp;A over the household's notes
///   POST /api/family/notes/ai/transform — transform in-editor markdown (continue/checklist/shorten/translate)
///   POST /api/family/lists/{id}/ai/suggest      — "what am I missing" additional items
///   POST /api/family/meals/ai/what-can-i-make   — dinner ideas from on-hand ingredients
///   POST /api/family/timers/ai/parse            — natural-language timer
///
/// Each clones the family-AI pattern: gated by family.use (403) + auth (401); empty input → 400;
/// unconfigured Gemini → graceful 503 (never 500); ask/suggest honour household/list view-access (404 when
/// the list isn't viewable); and NONE write anything (the test host configures NO Gemini key, so the
/// unconfigured branch returns before any real Gemini/HTTP call). Each test provisions fresh users so they're
/// order-independent.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class FamilyAiRound2Tests(WebAppFactory factory)
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
        var email = $"famai2-{Guid.NewGuid():N}@test.local";
        var res = await Admin().PostAsJsonAsync("/api/users", new { email, isEnabled = true, permissions });
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await Json(res)).GetProperty("id").GetInt32();
        return (email, Client(email), id);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        await resp.Content.ReadFromJsonAsync<JsonElement>();

    private static List<long> NoteIds(JsonElement arr) =>
        arr.EnumerateArray().Select(n => n.GetProperty("id").GetInt64()).ToList();

    // =====================================================================================
    // GATING — every round-2 endpoint requires family.use (403) and auth (401)
    // =====================================================================================

    [Fact]
    public async Task Round2_endpoints_require_family_use()
    {
        var (_, plain, _) = await ProvisionUser("dashboard.view");

        (await plain.PostAsJsonAsync("/api/family/notes/ai/ask", new { question = "what's the wifi password?" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await plain.PostAsJsonAsync("/api/family/notes/ai/transform", new { body = "hi", action = "shorten" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await plain.PostAsJsonAsync("/api/family/lists/1/ai/suggest", new { goal = "taco night" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await plain.PostAsJsonAsync("/api/family/meals/ai/what-can-i-make", new { ingredients = "eggs, rice" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await plain.PostAsJsonAsync("/api/family/timers/ai/parse", new { text = "20 minute pasta timer" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Round2_endpoints_require_family_ai_on_top_of_family_use()
    {
        // family.use reaches the Family Hub, but every generative round-2 AI assist is gated by family.ai — a
        // family.use-only caller is forbidden (the AI filter precedes the handler's 400/503/404 paths).
        var (_, owner, _) = await ProvisionUser("family.use"); // no family.ai
        await owner.GetAsync("/api/family/household");
        var listId = (await Json(await owner.PostAsJsonAsync("/api/family/lists",
            new { name = "Party", kind = "shopping" }))).GetProperty("id").GetInt64();

        (await owner.PostAsJsonAsync("/api/family/notes/ai/ask", new { question = "what's the wifi password?" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await owner.PostAsJsonAsync("/api/family/notes/ai/transform", new { body = "hi", action = "shorten" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await owner.PostAsJsonAsync($"/api/family/lists/{listId}/ai/suggest", new { goal = "taco night" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await owner.PostAsJsonAsync("/api/family/meals/ai/what-can-i-make", new { ingredients = "eggs, rice" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await owner.PostAsJsonAsync("/api/family/timers/ai/parse", new { text = "20 minute pasta timer" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Round2_endpoints_require_authentication()
    {
        var anon = factory.CreateClient();

        (await anon.PostAsJsonAsync("/api/family/notes/ai/ask", new { question = "x" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anon.PostAsJsonAsync("/api/family/notes/ai/transform", new { body = "x", action = "shorten" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anon.PostAsJsonAsync("/api/family/lists/1/ai/suggest", new { goal = "x" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anon.PostAsJsonAsync("/api/family/meals/ai/what-can-i-make", new { ingredients = "x" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anon.PostAsJsonAsync("/api/family/timers/ai/parse", new { text = "x" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // =====================================================================================
    // EMPTY INPUT → 400 (validated BEFORE the Gemini config check, so 400 not 503)
    // =====================================================================================

    [Fact]
    public async Task AskNotes_returns_400_for_empty_question()
    {
        var (_, owner, _) = await ProvisionUser("family.use", "family.ai");
        await owner.GetAsync("/api/family/household");
        (await owner.PostAsJsonAsync("/api/family/notes/ai/ask", new { question = "   " }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Transform_returns_400_for_empty_body()
    {
        var (_, owner, _) = await ProvisionUser("family.use", "family.ai");
        await owner.GetAsync("/api/family/household");
        (await owner.PostAsJsonAsync("/api/family/notes/ai/transform", new { body = "   ", action = "shorten" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Transform_rejects_an_unknown_action_with_400()
    {
        var (_, owner, _) = await ProvisionUser("family.use", "family.ai");
        await owner.GetAsync("/api/family/household");
        (await owner.PostAsJsonAsync("/api/family/notes/ai/transform",
            new { body = "Some real note content here.", action = "explode" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task WhatCanIMake_returns_400_for_empty_ingredients()
    {
        var (_, owner, _) = await ProvisionUser("family.use", "family.ai");
        await owner.GetAsync("/api/family/household");
        (await owner.PostAsJsonAsync("/api/family/meals/ai/what-can-i-make", new { ingredients = "   " }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ParseTimer_returns_400_for_empty_text()
    {
        var (_, owner, _) = await ProvisionUser("family.use", "family.ai");
        await owner.GetAsync("/api/family/household");
        (await owner.PostAsJsonAsync("/api/family/timers/ai/parse", new { text = "   " }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ListSuggest_returns_400_for_empty_goal()
    {
        var (_, owner, _) = await ProvisionUser("family.use", "family.ai");
        await owner.GetAsync("/api/family/household");
        var listId = (await Json(await owner.PostAsJsonAsync("/api/family/lists",
            new { name = "Party", kind = "shopping" }))).GetProperty("id").GetInt64();

        // The empty-goal check runs BEFORE the Gemini config check → 400 (not 503).
        (await owner.PostAsJsonAsync($"/api/family/lists/{listId}/ai/suggest", new { goal = "   " }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // =====================================================================================
    // UNCONFIGURED GEMINI → graceful 503 (never 500)
    // =====================================================================================

    [Fact]
    public async Task AskNotes_is_503_when_gemini_is_unconfigured_never_500()
    {
        var (_, owner, _) = await ProvisionUser("family.use", "family.ai");
        await owner.GetAsync("/api/family/household");
        await owner.PostAsJsonAsync("/api/family/notes",
            new { title = "Wifi", body = "Password is hunter2", pinned = false });

        var res = await owner.PostAsJsonAsync("/api/family/notes/ai/ask",
            new { question = "what's the wifi password?" });
        res.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Transform_is_503_when_gemini_is_unconfigured_never_500()
    {
        var (_, owner, _) = await ProvisionUser("family.use", "family.ai");
        await owner.GetAsync("/api/family/household");

        var res = await owner.PostAsJsonAsync("/api/family/notes/ai/transform",
            new { body = "- milk\n- eggs", action = "checklist" });
        res.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task ListSuggest_is_503_when_gemini_is_unconfigured_never_500()
    {
        var (_, owner, _) = await ProvisionUser("family.use", "family.ai");
        await owner.GetAsync("/api/family/household");
        var listId = (await Json(await owner.PostAsJsonAsync("/api/family/lists",
            new { name = "Party", kind = "shopping" }))).GetProperty("id").GetInt64();

        var res = await owner.PostAsJsonAsync($"/api/family/lists/{listId}/ai/suggest",
            new { goal = "a kids birthday party" });
        res.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task WhatCanIMake_is_503_when_gemini_is_unconfigured_never_500()
    {
        var (_, owner, _) = await ProvisionUser("family.use", "family.ai");
        await owner.GetAsync("/api/family/household");

        var res = await owner.PostAsJsonAsync("/api/family/meals/ai/what-can-i-make",
            new { ingredients = "chicken, rice, broccoli", constraints = "kid-friendly" });
        res.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task ParseTimer_is_503_when_gemini_is_unconfigured_never_500()
    {
        var (_, owner, _) = await ProvisionUser("family.use", "family.ai");
        await owner.GetAsync("/api/family/household");

        var res = await owner.PostAsJsonAsync("/api/family/timers/ai/parse",
            new { text = "set a 5 min timeout for Lily" });
        res.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    // =====================================================================================
    // VIEW-ACCESS — ask honours household, suggest honours list access (404 when not viewable)
    // =====================================================================================

    [Fact]
    public async Task ListSuggest_returns_404_when_the_list_is_not_viewable()
    {
        var (_, owner, _) = await ProvisionUser("family.use", "family.ai");
        var (_, stranger, _) = await ProvisionUser("family.use", "family.ai"); // their OWN (different) household
        await owner.GetAsync("/api/family/household");
        var listId = (await Json(await owner.PostAsJsonAsync("/api/family/lists",
            new { name = "Private", kind = "shopping" }))).GetProperty("id").GetInt64();

        // The access check runs BEFORE the Gemini config check, so a non-viewer gets 404 (existence not leaked),
        // never a 503.
        (await stranger.PostAsJsonAsync($"/api/family/lists/{listId}/ai/suggest", new { goal = "anything" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AskNotes_only_ever_sees_the_callers_own_household_notes()
    {
        // Alice has a note; Bob (a different household) asks — Bob's household has no notes, so the call still
        // 503s with no key, but it never reaches across households. (We can't observe the corpus directly with
        // no key, so we assert the cross-household ISOLATION on the notes GET that backs the ask.)
        var (_, alice, _) = await ProvisionUser("family.use", "family.ai");
        var (_, bob, _) = await ProvisionUser("family.use", "family.ai");
        await alice.GetAsync("/api/family/household");
        await bob.GetAsync("/api/family/household");

        var aliceNote = (await Json(await alice.PostAsJsonAsync("/api/family/notes",
            new { title = "Alice secret", body = "alice only", pinned = false }))).GetProperty("id").GetInt64();

        NoteIds(await Json(await bob.GetAsync("/api/family/notes"))).Should().NotContain(aliceNote);

        // Bob's ask still degrades gracefully (no key) rather than leaking or erroring.
        (await bob.PostAsJsonAsync("/api/family/notes/ai/ask", new { question = "what's alice's secret?" }))
            .StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    // =====================================================================================
    // NONE OF THE ROUND-2 ENDPOINTS WRITE ANYTHING
    // =====================================================================================

    [Fact]
    public async Task The_round2_ai_endpoints_write_nothing()
    {
        var (_, owner, _) = await ProvisionUser("family.use", "family.ai");
        await owner.GetAsync("/api/family/household");

        // Baseline: a list with one item, a note, a timer.
        var listId = (await Json(await owner.PostAsJsonAsync("/api/family/lists",
            new { name = "Groceries", kind = "shopping" }))).GetProperty("id").GetInt64();
        await owner.PostAsJsonAsync($"/api/family/lists/{listId}/items", new { text = "Milk" });
        var noteId = (await Json(await owner.PostAsJsonAsync("/api/family/notes",
            new { title = "Trip", body = "Pack bags. Book hotel.", pinned = false }))).GetProperty("id").GetInt64();
        await owner.PostAsJsonAsync("/api/family/timers", new { label = "Existing", durationSeconds = 60 });

        var notesBefore = NoteIds(await Json(await owner.GetAsync("/api/family/notes")));
        var itemsBefore = (await Json(await owner.GetAsync("/api/family/lists"))).EnumerateArray()
            .Single(l => l.GetProperty("id").GetInt64() == listId)
            .GetProperty("items").EnumerateArray().Count();
        var timersBefore = (await Json(await owner.GetAsync("/api/family/timers"))).EnumerateArray().Count();

        // Fire every round-2 AI call (each 503s with no key) — none may mutate stored state.
        await owner.PostAsJsonAsync("/api/family/notes/ai/ask", new { question = "where's the trip?" });
        await owner.PostAsJsonAsync("/api/family/notes/ai/transform", new { body = "Pack bags.", action = "checklist" });
        await owner.PostAsJsonAsync($"/api/family/lists/{listId}/ai/suggest", new { goal = "taco night" });
        await owner.PostAsJsonAsync("/api/family/meals/ai/what-can-i-make", new { ingredients = "eggs, rice" });
        await owner.PostAsJsonAsync("/api/family/timers/ai/parse", new { text = "20 minute pasta timer" });

        // Notes unchanged (same set); list item count unchanged; timer count unchanged.
        NoteIds(await Json(await owner.GetAsync("/api/family/notes"))).Should().BeEquivalentTo(notesBefore);
        var itemsAfter = (await Json(await owner.GetAsync("/api/family/lists"))).EnumerateArray()
            .Single(l => l.GetProperty("id").GetInt64() == listId)
            .GetProperty("items").EnumerateArray().Count();
        itemsAfter.Should().Be(itemsBefore);
        (await Json(await owner.GetAsync("/api/family/timers"))).EnumerateArray().Count().Should().Be(timersBefore);

        // The note is byte-for-byte intact (transform/ask never edited it).
        var note = (await Json(await owner.GetAsync("/api/family/notes")))
            .EnumerateArray().Single(n => n.GetProperty("id").GetInt64() == noteId);
        note.GetProperty("body").GetString().Should().Be("Pack bags. Book hotel.");
    }
}
