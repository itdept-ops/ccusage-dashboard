using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Ccusage.Api.Tests.Integration;

/// <summary>
/// Admin-managed, MUTUAL chat contacts ("circles"). Covers: the manage endpoints are gated by
/// chat.contacts.manage (a non-manager gets 403); a mutual add writes BOTH directions and a mutual
/// remove deletes both; /contacts/me returns only the caller's own contacts; a self-add is ignored;
/// adds are idempotent; the directory excludes the caller; and unknown/disabled users are rejected.
/// Every test provisions fresh users so they're order-independent.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class ContactsIntegrationTests(WebAppFactory factory)
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

    private async Task<(string email, HttpClient client)> ProvisionUser(params string[] permissions)
    {
        var email = $"contact-{Guid.NewGuid():N}@test.local";
        var res = await Admin().PostAsJsonAsync("/api/users", new { email, isEnabled = true, permissions });
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        return (email, Client(email));
    }

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        await resp.Content.ReadFromJsonAsync<JsonElement>();

    private static List<string> Emails(JsonElement arr) =>
        arr.EnumerateArray().Select(c => c.GetProperty("email").GetString()!).ToList();

    // ---- Permission gating: a non-manager gets 403 on the manage endpoints ----

    [Fact]
    public async Task Manage_endpoints_require_chat_contacts_manage()
    {
        // chat.read + chat.send is NOT enough to manage contacts.
        var (someoneEmail, _) = await ProvisionUser("chat.read");
        var (_, plain) = await ProvisionUser("chat.read", "chat.send");

        (await plain.GetAsync("/api/chat/directory")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await plain.GetAsync($"/api/chat/contacts/user/{someoneEmail}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await plain.PostAsJsonAsync($"/api/chat/contacts/user/{someoneEmail}", new { contactEmail = someoneEmail }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await plain.DeleteAsync($"/api/chat/contacts/user/{someoneEmail}/{someoneEmail}"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // But /contacts/me only needs chat.read.
        (await plain.GetAsync("/api/chat/contacts/me")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Contacts_endpoints_require_authentication()
    {
        var anon = factory.CreateClient();
        (await anon.GetAsync("/api/chat/contacts/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anon.GetAsync("/api/chat/directory")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- Mutual add: writes BOTH rows ----

    [Fact]
    public async Task Adding_a_contact_is_mutual_both_users_see_each_other()
    {
        var (_, admin) = await ProvisionUser("chat.read", "chat.contacts.manage");
        var (aliceEmail, alice) = await ProvisionUser("chat.read");
        var (bobEmail, bob) = await ProvisionUser("chat.read");

        var added = await admin.PostAsJsonAsync($"/api/chat/contacts/user/{aliceEmail}", new { contactEmail = bobEmail });
        added.StatusCode.Should().Be(HttpStatusCode.OK);
        Emails(await Json(added)).Should().ContainSingle().Which.Should().Be(bobEmail);

        // Alice's own contacts now include Bob...
        Emails(await Json(await alice.GetAsync("/api/chat/contacts/me"))).Should().Contain(bobEmail);
        // ...and Bob's own contacts include Alice (the mutual / reverse row).
        Emails(await Json(await bob.GetAsync("/api/chat/contacts/me"))).Should().Contain(aliceEmail);

        // The admin view of each user's contacts agrees.
        Emails(await Json(await admin.GetAsync($"/api/chat/contacts/user/{bobEmail}"))).Should().Contain(aliceEmail);
    }

    // ---- Mutual remove: deletes BOTH rows ----

    [Fact]
    public async Task Removing_a_contact_is_mutual_both_directions_disappear()
    {
        var (_, admin) = await ProvisionUser("chat.read", "chat.contacts.manage");
        var (aliceEmail, alice) = await ProvisionUser("chat.read");
        var (bobEmail, bob) = await ProvisionUser("chat.read");

        await admin.PostAsJsonAsync($"/api/chat/contacts/user/{aliceEmail}", new { contactEmail = bobEmail });

        // Remove from Alice's side; Bob must lose Alice too.
        var removed = await admin.DeleteAsync($"/api/chat/contacts/user/{aliceEmail}/{bobEmail}");
        removed.StatusCode.Should().Be(HttpStatusCode.OK);
        Emails(await Json(removed)).Should().NotContain(bobEmail);

        Emails(await Json(await alice.GetAsync("/api/chat/contacts/me"))).Should().NotContain(bobEmail);
        Emails(await Json(await bob.GetAsync("/api/chat/contacts/me"))).Should().NotContain(aliceEmail);

        // Removing again is a harmless no-op (still 200).
        (await admin.DeleteAsync($"/api/chat/contacts/user/{aliceEmail}/{bobEmail}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- /me returns only the caller's own contacts ----

    [Fact]
    public async Task Me_returns_only_the_callers_own_contacts()
    {
        var (_, admin) = await ProvisionUser("chat.read", "chat.contacts.manage");
        var (aliceEmail, alice) = await ProvisionUser("chat.read");
        var (bobEmail, _) = await ProvisionUser("chat.read");
        var (carolEmail, carol) = await ProvisionUser("chat.read");

        // Alice <-> Bob are contacts; Carol is unrelated.
        await admin.PostAsJsonAsync($"/api/chat/contacts/user/{aliceEmail}", new { contactEmail = bobEmail });

        var aliceContacts = Emails(await Json(await alice.GetAsync("/api/chat/contacts/me")));
        aliceContacts.Should().Contain(bobEmail);
        aliceContacts.Should().NotContain(carolEmail);

        // Carol, who has no contacts, sees an empty circle (not Alice's or Bob's).
        Emails(await Json(await carol.GetAsync("/api/chat/contacts/me"))).Should().BeEmpty();
    }

    // ---- Self-add ignored ----

    [Fact]
    public async Task Adding_yourself_is_ignored_no_self_contact()
    {
        var (_, admin) = await ProvisionUser("chat.read", "chat.contacts.manage");
        var (aliceEmail, alice) = await ProvisionUser("chat.read");

        var resp = await admin.PostAsJsonAsync($"/api/chat/contacts/user/{aliceEmail}", new { contactEmail = aliceEmail });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        Emails(await Json(resp)).Should().BeEmpty(); // self-contact never written

        Emails(await Json(await alice.GetAsync("/api/chat/contacts/me"))).Should().NotContain(aliceEmail);
    }

    // ---- Idempotent add: re-adding an existing pair is a no-op ----

    [Fact]
    public async Task Adding_the_same_pair_twice_is_idempotent()
    {
        var (_, admin) = await ProvisionUser("chat.read", "chat.contacts.manage");
        var (aliceEmail, _) = await ProvisionUser("chat.read");
        var (bobEmail, _) = await ProvisionUser("chat.read");

        await admin.PostAsJsonAsync($"/api/chat/contacts/user/{aliceEmail}", new { contactEmail = bobEmail });
        var again = await admin.PostAsJsonAsync($"/api/chat/contacts/user/{aliceEmail}", new { contactEmail = bobEmail });
        again.StatusCode.Should().Be(HttpStatusCode.OK);

        // Exactly one entry — no duplicate row created.
        Emails(await Json(again)).Where(e => e == bobEmail).Should().ContainSingle();
    }

    // ---- Directory: all enabled users except the caller ----

    [Fact]
    public async Task Directory_lists_enabled_users_except_the_caller()
    {
        var (adminEmail, admin) = await ProvisionUser("chat.read", "chat.contacts.manage");
        var (otherEmail, _) = await ProvisionUser("chat.read");

        var dir = Emails(await Json(await admin.GetAsync("/api/chat/directory")));
        dir.Should().Contain(otherEmail);
        dir.Should().NotContain(adminEmail); // never include the caller
    }

    // ---- Unknown / disabled users rejected ----

    [Fact]
    public async Task Adding_to_or_for_an_unknown_user_is_rejected()
    {
        var (_, admin) = await ProvisionUser("chat.read", "chat.contacts.manage");
        var (aliceEmail, _) = await ProvisionUser("chat.read");

        // Unknown OWNER → 404.
        (await admin.PostAsJsonAsync("/api/chat/contacts/user/ghost@nowhere.local", new { contactEmail = aliceEmail }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.GetAsync("/api/chat/contacts/user/ghost@nowhere.local"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Unknown CONTACT → 400.
        (await admin.PostAsJsonAsync($"/api/chat/contacts/user/{aliceEmail}", new { contactEmail = "ghost@nowhere.local" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- Email casing is normalized ----

    [Fact]
    public async Task Emails_are_matched_case_insensitively()
    {
        var (_, admin) = await ProvisionUser("chat.read", "chat.contacts.manage");
        var (aliceEmail, alice) = await ProvisionUser("chat.read");
        var (bobEmail, _) = await ProvisionUser("chat.read");

        // Add using an UPPER-cased contact email; it should resolve to the lower-cased user.
        var resp = await admin.PostAsJsonAsync(
            $"/api/chat/contacts/user/{aliceEmail.ToUpperInvariant()}", new { contactEmail = bobEmail.ToUpperInvariant() });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        Emails(await Json(await alice.GetAsync("/api/chat/contacts/me"))).Should().Contain(bobEmail);
    }
}
