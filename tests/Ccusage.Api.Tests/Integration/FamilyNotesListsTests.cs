using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Ccusage.Api.Tests.Integration;

/// <summary>
/// Family Hub F1 — shared NOTES and LISTS (/api/family/notes, /api/family/lists). Covers the privacy
/// rules: every endpoint is gated by family.use (no permission → 403); a household member sees their
/// household's notes/lists; a non-member / non-shared user does NOT (404 on the item, and sees none in
/// their GET); sharing a note/list to a contact (canEdit true/false) grants view/edit appropriately;
/// a canEdit=false share cannot PUT/PATCH (403); toggling a list item's done stamps the caller; every
/// person field carries userId+name with NO email ("@") anywhere; and cross-household isolation holds.
/// Each test provisions fresh users so they're order-independent.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class FamilyNotesListsTests(WebAppFactory factory)
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
        var email = $"famnl-{Guid.NewGuid():N}@test.local";
        var res = await Admin().PostAsJsonAsync("/api/users", new { email, isEnabled = true, permissions });
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await Json(res)).GetProperty("id").GetInt32();
        return (email, Client(email), id);
    }

    /// <summary>Make two users mutual chat contacts (the source for the share people-picker).</summary>
    private async Task MakeContacts(int aId, int bId)
    {
        var res = await Admin().PostAsJsonAsync($"/api/chat/contacts/user/{aId}", new { contactUserId = bId });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        await resp.Content.ReadFromJsonAsync<JsonElement>();

    private static bool HasProperty(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out _);

    private static List<long> NoteIds(JsonElement arr) =>
        arr.EnumerateArray().Select(n => n.GetProperty("id").GetInt64()).ToList();

    private static List<long> ListIds(JsonElement arr) =>
        arr.EnumerateArray().Select(l => l.GetProperty("id").GetInt64()).ToList();

    // =====================================================================================
    // GATING
    // =====================================================================================

    [Fact]
    public async Task Notes_and_lists_require_family_use()
    {
        var (_, plain, _) = await ProvisionUser("dashboard.view");

        (await plain.GetAsync("/api/family/notes")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await plain.PostAsJsonAsync("/api/family/notes", new { title = "X", body = "Y", pinned = false }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await plain.GetAsync("/api/family/lists")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await plain.PostAsJsonAsync("/api/family/lists", new { name = "X", kind = "todo" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Notes_require_authentication()
    {
        var anon = factory.CreateClient();
        (await anon.GetAsync("/api/family/notes")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anon.GetAsync("/api/family/lists")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // =====================================================================================
    // NOTES — household visibility, mine/canEdit, no email
    // =====================================================================================

    [Fact]
    public async Task Household_member_sees_household_notes_no_email_on_the_wire()
    {
        var (_, owner, ownerId) = await ProvisionUser("family.use");
        var (_, bob, bobId) = await ProvisionUser("family.use");
        await owner.GetAsync("/api/family/household"); // provision
        await owner.PostAsJsonAsync("/api/family/household/members", new { userId = bobId });

        var created = await owner.PostAsJsonAsync("/api/family/notes",
            new { title = "Groceries plan", body = "Buy **milk**", pinned = true });
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        var note = await Json(created);
        var noteId = note.GetProperty("id").GetInt64();
        note.GetProperty("createdByUserId").GetInt32().Should().Be(ownerId);
        note.GetProperty("createdByName").GetString().Should().NotBeNullOrWhiteSpace();
        note.GetProperty("isMine").GetBoolean().Should().BeTrue();
        note.GetProperty("canEdit").GetBoolean().Should().BeTrue();
        note.GetProperty("pinned").GetBoolean().Should().BeTrue();
        note.GetRawText().Should().NotContain("@");

        // Bob (a member) sees the same note; it's not "his" but he can edit it.
        var bobList = await Json(await bob.GetAsync("/api/family/notes"));
        NoteIds(bobList).Should().Contain(noteId);
        var bobView = bobList.EnumerateArray().Single(n => n.GetProperty("id").GetInt64() == noteId);
        bobView.GetProperty("isMine").GetBoolean().Should().BeFalse();
        bobView.GetProperty("canEdit").GetBoolean().Should().BeTrue();
        bobList.GetRawText().Should().NotContain("@");
    }

    // =====================================================================================
    // NOTES — a non-member / non-shared user sees nothing and gets 404
    // =====================================================================================

    [Fact]
    public async Task A_non_member_non_shared_user_cannot_see_or_touch_a_note()
    {
        var (_, owner, _) = await ProvisionUser("family.use");
        var (_, stranger, _) = await ProvisionUser("family.use"); // owns their OWN (different) household
        await owner.GetAsync("/api/family/household");
        var noteId = (await Json(await owner.PostAsJsonAsync("/api/family/notes",
            new { title = "Private", body = "secret", pinned = false }))).GetProperty("id").GetInt64();

        // The stranger's own GET shows none of the owner's notes.
        NoteIds(await Json(await stranger.GetAsync("/api/family/notes"))).Should().NotContain(noteId);

        // Existence is never leaked: every targeted op is 404 (not 403).
        (await stranger.PutAsJsonAsync($"/api/family/notes/{noteId}",
            new { title = "Hacked", body = "x", pinned = false })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await stranger.DeleteAsync($"/api/family/notes/{noteId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await stranger.PostAsJsonAsync($"/api/family/notes/{noteId}/share", new { userId = 1, canEdit = false }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // =====================================================================================
    // NOTES — sharing to a contact grants view; canEdit governs edit
    // =====================================================================================

    [Fact]
    public async Task Sharing_a_note_view_only_grants_view_but_not_edit()
    {
        var (_, owner, ownerId) = await ProvisionUser("family.use");
        var (_, friend, friendId) = await ProvisionUser("family.use");
        await owner.GetAsync("/api/family/household");
        await friend.GetAsync("/api/family/household");
        await MakeContacts(ownerId, friendId);

        var noteId = (await Json(await owner.PostAsJsonAsync("/api/family/notes",
            new { title = "Shared", body = "hi", pinned = false }))).GetProperty("id").GetInt64();

        // Share view-only.
        var shared = await owner.PostAsJsonAsync($"/api/family/notes/{noteId}/share",
            new { userId = friendId, canEdit = false });
        shared.StatusCode.Should().Be(HttpStatusCode.OK);
        var afterShare = await Json(shared);
        // The owner (a manager) sees sharedWith populated with userId+name+canEdit, no email.
        var sw = afterShare.GetProperty("sharedWith").EnumerateArray().Single();
        sw.GetProperty("userId").GetInt32().Should().Be(friendId);
        sw.GetProperty("name").GetString().Should().NotBeNullOrWhiteSpace();
        sw.GetProperty("canEdit").GetBoolean().Should().BeFalse();
        HasProperty(sw, "email").Should().BeFalse();
        afterShare.GetRawText().Should().NotContain("@");

        // The friend now sees the note (view), canEdit=false, and sharedWith is NOT populated for them.
        var friendList = await Json(await friend.GetAsync("/api/family/notes"));
        NoteIds(friendList).Should().Contain(noteId);
        var friendView = friendList.EnumerateArray().Single(n => n.GetProperty("id").GetInt64() == noteId);
        friendView.GetProperty("canEdit").GetBoolean().Should().BeFalse();
        friendView.GetProperty("isMine").GetBoolean().Should().BeFalse();
        friendView.GetProperty("sharedWith").EnumerateArray().Should().BeEmpty();
        friendList.GetRawText().Should().NotContain("@");

        // A view-only share cannot edit (403, not 404 — they CAN see it).
        (await friend.PutAsJsonAsync($"/api/family/notes/{noteId}",
            new { title = "Edited", body = "x", pinned = false })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // ...and cannot re-share or delete (manage is members-only).
        (await friend.DeleteAsync($"/api/family/notes/{noteId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await friend.PostAsJsonAsync($"/api/family/notes/{noteId}/share", new { userId = ownerId, canEdit = true }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Sharing_a_note_with_edit_lets_the_contact_edit()
    {
        var (_, owner, ownerId) = await ProvisionUser("family.use");
        var (_, friend, friendId) = await ProvisionUser("family.use");
        await owner.GetAsync("/api/family/household");
        await friend.GetAsync("/api/family/household");
        await MakeContacts(ownerId, friendId);

        var noteId = (await Json(await owner.PostAsJsonAsync("/api/family/notes",
            new { title = "Collab", body = "draft", pinned = false }))).GetProperty("id").GetInt64();
        await owner.PostAsJsonAsync($"/api/family/notes/{noteId}/share", new { userId = friendId, canEdit = true });

        var edit = await friend.PutAsJsonAsync($"/api/family/notes/{noteId}",
            new { title = "Collab v2", body = "done", pinned = false });
        edit.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(edit)).GetProperty("title").GetString().Should().Be("Collab v2");

        // The owner sees the edit.
        var ownerView = (await Json(await owner.GetAsync("/api/family/notes")))
            .EnumerateArray().Single(n => n.GetProperty("id").GetInt64() == noteId);
        ownerView.GetProperty("body").GetString().Should().Be("done");
    }

    [Fact]
    public async Task Sharing_to_a_non_contact_is_rejected()
    {
        var (_, owner, _) = await ProvisionUser("family.use");
        var (_, _, strangerId) = await ProvisionUser("family.use");
        await owner.GetAsync("/api/family/household");
        var noteId = (await Json(await owner.PostAsJsonAsync("/api/family/notes",
            new { title = "N", body = "B", pinned = false }))).GetProperty("id").GetInt64();

        // Not a contact → rejected (400).
        (await owner.PostAsJsonAsync($"/api/family/notes/{noteId}/share", new { userId = strangerId, canEdit = false }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // =====================================================================================
    // LISTS — items, toggling done stamps the caller, assignee, no email
    // =====================================================================================

    [Fact]
    public async Task List_items_carry_people_by_id_and_name_and_toggling_done_stamps_the_caller()
    {
        var (_, owner, ownerId) = await ProvisionUser("family.use");
        var (_, bob, bobId) = await ProvisionUser("family.use");
        await owner.GetAsync("/api/family/household");
        await owner.PostAsJsonAsync("/api/family/household/members", new { userId = bobId });

        var listId = (await Json(await owner.PostAsJsonAsync("/api/family/lists",
            new { name = "Shopping", kind = "shopping" }))).GetProperty("id").GetInt64();

        // Add an item assigned to Bob (a household member).
        var added = await owner.PostAsJsonAsync($"/api/family/lists/{listId}/items",
            new { text = "Milk", assignedToUserId = bobId });
        added.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await Json(added);
        var item = list.GetProperty("items").EnumerateArray().Single();
        var itemId = item.GetProperty("id").GetInt64();
        item.GetProperty("assignedToUserId").GetInt32().Should().Be(bobId);
        item.GetProperty("assignedToName").GetString().Should().NotBeNullOrWhiteSpace();
        item.GetProperty("done").GetBoolean().Should().BeFalse();
        list.GetRawText().Should().NotContain("@");

        // Bob toggles it done → DoneByUserId is stamped as Bob.
        var toggled = await bob.PatchAsJsonAsync($"/api/family/lists/{listId}/items/{itemId}", new { done = true });
        toggled.StatusCode.Should().Be(HttpStatusCode.OK);
        var afterToggle = (await Json(toggled)).GetProperty("items").EnumerateArray().Single();
        afterToggle.GetProperty("done").GetBoolean().Should().BeTrue();
        afterToggle.GetProperty("doneByUserId").GetInt32().Should().Be(bobId);
        afterToggle.GetProperty("doneByName").GetString().Should().NotBeNullOrWhiteSpace();

        // Un-toggle clears the stamp.
        var cleared = await owner.PatchAsJsonAsync($"/api/family/lists/{listId}/items/{itemId}", new { done = false });
        var afterClear = (await Json(cleared)).GetProperty("items").EnumerateArray().Single();
        afterClear.GetProperty("done").GetBoolean().Should().BeFalse();
        afterClear.GetProperty("doneByUserId").ValueKind.Should().Be(JsonValueKind.Null);

        // createdBy on the list carries id+name, never email.
        list.GetProperty("createdByUserId").GetInt32().Should().Be(ownerId);
        list.GetProperty("createdByName").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Sharing_a_list_view_only_blocks_writes_with_403()
    {
        var (_, owner, ownerId) = await ProvisionUser("family.use");
        var (_, friend, friendId) = await ProvisionUser("family.use");
        await owner.GetAsync("/api/family/household");
        await friend.GetAsync("/api/family/household");
        await MakeContacts(ownerId, friendId);

        var listId = (await Json(await owner.PostAsJsonAsync("/api/family/lists",
            new { name = "Trip", kind = "todo" }))).GetProperty("id").GetInt64();
        var itemId = (await Json(await owner.PostAsJsonAsync($"/api/family/lists/{listId}/items",
            new { text = "Pack" }))).GetProperty("items").EnumerateArray().Single().GetProperty("id").GetInt64();

        await owner.PostAsJsonAsync($"/api/family/lists/{listId}/share", new { userId = friendId, canEdit = false });

        // The friend sees the list (view) but cannot mutate it.
        ListIds(await Json(await friend.GetAsync("/api/family/lists"))).Should().Contain(listId);
        (await friend.PutAsJsonAsync($"/api/family/lists/{listId}", new { name = "Hijack" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await friend.PatchAsJsonAsync($"/api/family/lists/{listId}/items/{itemId}", new { done = true }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await friend.PostAsJsonAsync($"/api/family/lists/{listId}/items", new { text = "Sneak" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Sharing_a_list_with_edit_lets_the_contact_add_and_toggle_items()
    {
        var (_, owner, ownerId) = await ProvisionUser("family.use");
        var (_, friend, friendId) = await ProvisionUser("family.use");
        await owner.GetAsync("/api/family/household");
        await friend.GetAsync("/api/family/household");
        await MakeContacts(ownerId, friendId);

        var listId = (await Json(await owner.PostAsJsonAsync("/api/family/lists",
            new { name = "Party", kind = "todo" }))).GetProperty("id").GetInt64();
        await owner.PostAsJsonAsync($"/api/family/lists/{listId}/share", new { userId = friendId, canEdit = true });

        // The friend can add an item, and assign it to themselves (a shared-in person is a valid assignee).
        var added = await friend.PostAsJsonAsync($"/api/family/lists/{listId}/items",
            new { text = "Bring cake", assignedToUserId = friendId });
        added.StatusCode.Should().Be(HttpStatusCode.OK);
        var itemId = (await Json(added)).GetProperty("items").EnumerateArray().Single().GetProperty("id").GetInt64();

        var toggled = await friend.PatchAsJsonAsync($"/api/family/lists/{listId}/items/{itemId}", new { done = true });
        toggled.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(toggled)).GetProperty("items").EnumerateArray().Single()
            .GetProperty("doneByUserId").GetInt32().Should().Be(friendId);
    }

    // =====================================================================================
    // CROSS-HOUSEHOLD ISOLATION
    // =====================================================================================

    [Fact]
    public async Task Cross_household_isolation_each_family_sees_only_its_own_items()
    {
        var (_, alice, _) = await ProvisionUser("family.use");
        var (_, bob, _) = await ProvisionUser("family.use");
        await alice.GetAsync("/api/family/household");
        await bob.GetAsync("/api/family/household");

        var aliceNote = (await Json(await alice.PostAsJsonAsync("/api/family/notes",
            new { title = "Alice", body = "a", pinned = false }))).GetProperty("id").GetInt64();
        var bobNote = (await Json(await bob.PostAsJsonAsync("/api/family/notes",
            new { title = "Bob", body = "b", pinned = false }))).GetProperty("id").GetInt64();
        var aliceList = (await Json(await alice.PostAsJsonAsync("/api/family/lists",
            new { name = "AList", kind = "todo" }))).GetProperty("id").GetInt64();
        var bobList = (await Json(await bob.PostAsJsonAsync("/api/family/lists",
            new { name = "BList", kind = "todo" }))).GetProperty("id").GetInt64();

        var aliceNotes = NoteIds(await Json(await alice.GetAsync("/api/family/notes")));
        aliceNotes.Should().Contain(aliceNote);
        aliceNotes.Should().NotContain(bobNote);

        var bobLists = ListIds(await Json(await bob.GetAsync("/api/family/lists")));
        bobLists.Should().Contain(bobList);
        bobLists.Should().NotContain(aliceList);

        // And Bob can't reach into Alice's list (404).
        (await bob.PutAsJsonAsync($"/api/family/lists/{aliceList}", new { name = "x" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // =====================================================================================
    // ASSIGNEE VALIDATION
    // =====================================================================================

    [Fact]
    public async Task Assigning_an_item_to_an_outsider_is_rejected()
    {
        var (_, owner, _) = await ProvisionUser("family.use");
        var (_, _, outsiderId) = await ProvisionUser("family.use");
        await owner.GetAsync("/api/family/household");
        var listId = (await Json(await owner.PostAsJsonAsync("/api/family/lists",
            new { name = "L", kind = "todo" }))).GetProperty("id").GetInt64();

        // The outsider is neither a member nor shared the list → can't be an assignee.
        (await owner.PostAsJsonAsync($"/api/family/lists/{listId}/items",
            new { text = "T", assignedToUserId = outsiderId })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // =====================================================================================
    // AI-ASSIST (slice 2) — lists quick-add, notes draft/rewrite, notes summarize.
    // Each is gated by family.use (403) + auth (401); empty input → 400; unconfigured Gemini
    // → graceful 503 (never 500). The test host configures NO Gemini key, so the unconfigured
    // branch returns before any real Gemini/HTTP call, and none of the three writes anything.
    // =====================================================================================

    [Fact]
    public async Task ListItemsAi_requires_family_use_and_auth()
    {
        var (_, plain, _) = await ProvisionUser("dashboard.view");
        (await plain.PostAsJsonAsync("/api/family/lists/ai/parse-items", new { text = "milk, eggs" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var anon = factory.CreateClient();
        (await anon.PostAsJsonAsync("/api/family/lists/ai/parse-items", new { text = "milk, eggs" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task NoteDraftAi_requires_family_use_and_auth()
    {
        var (_, plain, _) = await ProvisionUser("dashboard.view");
        (await plain.PostAsJsonAsync("/api/family/notes/ai/draft", new { prompt = "a packing list" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var anon = factory.CreateClient();
        (await anon.PostAsJsonAsync("/api/family/notes/ai/draft", new { prompt = "a packing list" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task NoteSummarizeAi_requires_family_use_and_auth()
    {
        var (_, plain, _) = await ProvisionUser("dashboard.view");
        (await plain.PostAsJsonAsync("/api/family/notes/1/ai/summarize", new { }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var anon = factory.CreateClient();
        (await anon.PostAsJsonAsync("/api/family/notes/1/ai/summarize", new { }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ListItemsAi_returns_400_for_empty_text()
    {
        var (_, owner, _) = await ProvisionUser("family.use");
        await owner.GetAsync("/api/family/household");
        (await owner.PostAsJsonAsync("/api/family/lists/ai/parse-items", new { text = "   " }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task NoteDraftAi_returns_400_for_empty_prompt()
    {
        var (_, owner, _) = await ProvisionUser("family.use");
        await owner.GetAsync("/api/family/household");
        (await owner.PostAsJsonAsync("/api/family/notes/ai/draft", new { prompt = "   " }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ListItemsAi_is_unavailable_503_when_gemini_is_unconfigured_never_500()
    {
        var (_, owner, _) = await ProvisionUser("family.use");
        await owner.GetAsync("/api/family/household");

        // No Gemini key in tests → graceful 503 (never 500), and a real upstream call is never made.
        var res = await owner.PostAsJsonAsync("/api/family/lists/ai/parse-items",
            new { text = "milk, eggs, bread, bananas", kind = "shopping" });
        res.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task NoteDraftAi_is_unavailable_503_when_gemini_is_unconfigured_never_500()
    {
        var (_, owner, _) = await ProvisionUser("family.use");
        await owner.GetAsync("/api/family/household");

        var res = await owner.PostAsJsonAsync("/api/family/notes/ai/draft",
            new { prompt = "draft a weekend chore checklist" });
        res.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task NoteSummarizeAi_is_unavailable_503_when_gemini_is_unconfigured_never_500()
    {
        var (_, owner, _) = await ProvisionUser("family.use");
        await owner.GetAsync("/api/family/household");
        var noteId = (await Json(await owner.PostAsJsonAsync("/api/family/notes",
            new { title = "Trip", body = "Pack bags. Book hotel by Friday.", pinned = false })))
            .GetProperty("id").GetInt64();

        var res = await owner.PostAsJsonAsync($"/api/family/notes/{noteId}/ai/summarize", new { });
        res.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task NoteSummarizeAi_returns_404_when_caller_cannot_view_the_note()
    {
        var (_, owner, _) = await ProvisionUser("family.use");
        var (_, stranger, _) = await ProvisionUser("family.use"); // their OWN (different) household
        await owner.GetAsync("/api/family/household");
        var noteId = (await Json(await owner.PostAsJsonAsync("/api/family/notes",
            new { title = "Private", body = "secret", pinned = false }))).GetProperty("id").GetInt64();

        // The access check runs BEFORE the Gemini config check, so a non-viewer gets 404 (existence not leaked),
        // never a 503.
        (await stranger.PostAsJsonAsync($"/api/family/notes/{noteId}/ai/summarize", new { }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_three_ai_endpoints_write_nothing()
    {
        var (_, owner, _) = await ProvisionUser("family.use");
        await owner.GetAsync("/api/family/household");

        // A list with one item, and a note — the baseline we'll prove is untouched.
        var listId = (await Json(await owner.PostAsJsonAsync("/api/family/lists",
            new { name = "Groceries", kind = "shopping" }))).GetProperty("id").GetInt64();
        await owner.PostAsJsonAsync($"/api/family/lists/{listId}/items", new { text = "Milk" });
        var noteId = (await Json(await owner.PostAsJsonAsync("/api/family/notes",
            new { title = "Trip", body = "Pack. Book hotel.", pinned = false }))).GetProperty("id").GetInt64();

        var notesBefore = NoteIds(await Json(await owner.GetAsync("/api/family/notes")));
        var listsBefore = await Json(await owner.GetAsync("/api/family/lists"));
        var itemsBefore = listsBefore.EnumerateArray().Single(l => l.GetProperty("id").GetInt64() == listId)
            .GetProperty("items").EnumerateArray().Count();

        // Fire all three AI calls (each 503s with no key) — none may mutate stored state.
        await owner.PostAsJsonAsync("/api/family/lists/ai/parse-items",
            new { text = "eggs, bread, bananas", kind = "shopping" });
        await owner.PostAsJsonAsync("/api/family/notes/ai/draft", new { prompt = "make it a checklist" });
        await owner.PostAsJsonAsync($"/api/family/notes/{noteId}/ai/summarize", new { });

        // Notes unchanged (same set), list item count unchanged.
        NoteIds(await Json(await owner.GetAsync("/api/family/notes"))).Should().BeEquivalentTo(notesBefore);
        var itemsAfter = (await Json(await owner.GetAsync("/api/family/lists"))).EnumerateArray()
            .Single(l => l.GetProperty("id").GetInt64() == listId)
            .GetProperty("items").EnumerateArray().Count();
        itemsAfter.Should().Be(itemsBefore);
    }
}
