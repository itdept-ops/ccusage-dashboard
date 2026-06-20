using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Ccusage.Api.Tests.Integration;

/// <summary>
/// Family Hub foundation (/api/family). Covers: every endpoint is gated by family.use (a user without
/// it gets 403); GET /household auto-provisions a household with the caller as the sole OWNER; the
/// member shape carries userId+name+role and NEVER an email; the owner can add a second family.use user
/// as an "adult" (and a non-owner member can't); the owner can rename; remove works and can't remove the
/// owner; and adding a user who lacks family.use or already belongs to a household is rejected.
/// Every test provisions fresh users so they're order-independent.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class FamilyIntegrationTests(WebAppFactory factory)
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
        var email = $"family-{Guid.NewGuid():N}@test.local";
        var res = await Admin().PostAsJsonAsync("/api/users", new { email, isEnabled = true, permissions });
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await Json(res)).GetProperty("id").GetInt32();
        return (email, Client(email), id);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        await resp.Content.ReadFromJsonAsync<JsonElement>();

    /// <summary>The member userIds in a household payload (email-privacy: the wire carries userId, never email).</summary>
    private static List<int> MemberIds(JsonElement household) =>
        household.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("userId").GetInt32()).ToList();

    private static JsonElement Member(JsonElement household, int userId) =>
        household.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("userId").GetInt32() == userId);

    /// <summary>True when the JSON object has a property with the given name (for asserting NO email leaks).</summary>
    private static bool HasProperty(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out _);

    // ---- Gating: a user WITHOUT family.use is 403 on every /api/family/* route ----

    [Fact]
    public async Task Family_endpoints_require_family_use()
    {
        var (_, plain, _) = await ProvisionUser("dashboard.view");

        (await plain.GetAsync("/api/family/household")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await plain.GetAsync("/api/family/household/candidates")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await plain.PatchAsJsonAsync("/api/family/household", new { name = "X" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await plain.PostAsJsonAsync("/api/family/household/members", new { userId = 1 }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await plain.DeleteAsync("/api/family/household/members/1")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Family_endpoints_require_authentication()
    {
        var anon = factory.CreateClient();
        (await anon.GetAsync("/api/family/household")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- Auto-provision: GET /household creates a household with the caller as sole OWNER ----

    [Fact]
    public async Task Get_household_auto_provisions_with_caller_as_sole_owner()
    {
        var (_, alice, aliceId) = await ProvisionUser("family.use");

        var resp = await alice.GetAsync("/api/family/household");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var hh = await Json(resp);

        hh.GetProperty("id").GetInt32().Should().BePositive();
        hh.GetProperty("name").GetString().Should().NotBeNullOrWhiteSpace();

        var members = hh.GetProperty("members").EnumerateArray().ToList();
        members.Should().ContainSingle();
        var me = members[0];
        me.GetProperty("userId").GetInt32().Should().Be(aliceId);
        me.GetProperty("role").GetString().Should().Be("owner");
        me.GetProperty("isSelf").GetBoolean().Should().BeTrue();
        me.TryGetProperty("name", out var name).Should().BeTrue();
        name.GetString().Should().NotBeNullOrWhiteSpace();

        // No email anywhere on the wire (email-privacy).
        me.TryGetProperty("email", out _).Should().BeFalse();
        hh.GetRawText().Should().NotContain("@");

        // Idempotent: a second GET returns the SAME household (no second one provisioned).
        var again = await Json(await alice.GetAsync("/api/family/household"));
        again.GetProperty("id").GetInt32().Should().Be(hh.GetProperty("id").GetInt32());
        MemberIds(again).Should().ContainSingle().Which.Should().Be(aliceId);
    }

    // ---- Owner can add a second family.use user as a member ----

    [Fact]
    public async Task Owner_can_add_a_family_use_user_as_an_adult_member()
    {
        var (_, owner, ownerId) = await ProvisionUser("family.use");
        var (_, _, bobId) = await ProvisionUser("family.use");
        await owner.GetAsync("/api/family/household"); // provision

        var resp = await owner.PostAsJsonAsync("/api/family/household/members", new { userId = bobId });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var hh = await Json(resp);

        MemberIds(hh).Should().BeEquivalentTo(new[] { ownerId, bobId });
        Member(hh, ownerId).GetProperty("role").GetString().Should().Be("owner");
        Member(hh, bobId).GetProperty("role").GetString().Should().Be("adult");
        // Still no email on the wire.
        hh.GetRawText().Should().NotContain("@");
    }

    // ---- A non-owner member can't add ----

    [Fact]
    public async Task A_non_owner_member_cannot_add_members()
    {
        var (_, owner, _) = await ProvisionUser("family.use");
        var (_, bob, bobId) = await ProvisionUser("family.use");
        var (_, _, carolId) = await ProvisionUser("family.use");
        await owner.GetAsync("/api/family/household");
        await owner.PostAsJsonAsync("/api/family/household/members", new { userId = bobId });

        // Bob is now an "adult" member — he sees the household, but can't add Carol.
        (await bob.GetAsync("/api/family/household")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await bob.PostAsJsonAsync("/api/family/household/members", new { userId = carolId }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- Owner can rename; a non-owner can't ----

    [Fact]
    public async Task Owner_can_rename_the_household()
    {
        var (_, owner, _) = await ProvisionUser("family.use");
        await owner.GetAsync("/api/family/household");

        var resp = await owner.PatchAsJsonAsync("/api/family/household", new { name = "The Testersons" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(resp)).GetProperty("name").GetString().Should().Be("The Testersons");

        // Persisted.
        (await Json(await owner.GetAsync("/api/family/household"))).GetProperty("name").GetString()
            .Should().Be("The Testersons");

        // Empty name is rejected.
        (await owner.PatchAsJsonAsync("/api/family/household", new { name = "   " }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_non_owner_member_cannot_rename()
    {
        var (_, owner, _) = await ProvisionUser("family.use");
        var (_, bob, bobId) = await ProvisionUser("family.use");
        await owner.GetAsync("/api/family/household");
        await owner.PostAsJsonAsync("/api/family/household/members", new { userId = bobId });

        (await bob.PatchAsJsonAsync("/api/family/household", new { name = "Bob's Coup" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- Remove a member works; can't remove the owner ----

    [Fact]
    public async Task Owner_can_remove_a_member_but_not_the_owner()
    {
        var (_, owner, ownerId) = await ProvisionUser("family.use");
        var (_, _, bobId) = await ProvisionUser("family.use");
        await owner.GetAsync("/api/family/household");
        await owner.PostAsJsonAsync("/api/family/household/members", new { userId = bobId });

        // Remove Bob.
        var removed = await owner.DeleteAsync($"/api/family/household/members/{bobId}");
        removed.StatusCode.Should().Be(HttpStatusCode.OK);
        MemberIds(await Json(removed)).Should().ContainSingle().Which.Should().Be(ownerId);

        // The owner can't be removed (even by themselves).
        (await owner.DeleteAsync($"/api/family/household/members/{ownerId}"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // Owner is still there.
        MemberIds(await Json(await owner.GetAsync("/api/family/household")))
            .Should().Contain(ownerId);
    }

    // ---- Adding a user who lacks family.use is rejected ----

    [Fact]
    public async Task Adding_a_user_without_family_use_is_rejected()
    {
        var (_, owner, _) = await ProvisionUser("family.use");
        var (_, _, noFamilyId) = await ProvisionUser("dashboard.view");
        await owner.GetAsync("/api/family/household");

        (await owner.PostAsJsonAsync("/api/family/household/members", new { userId = noFamilyId }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // An unknown user id → 404.
        (await owner.PostAsJsonAsync("/api/family/household/members", new { userId = 99999999 }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- Adding a user already in a household is rejected ----

    [Fact]
    public async Task Adding_a_user_already_in_a_household_is_rejected()
    {
        var (_, ownerA, _) = await ProvisionUser("family.use");
        var (_, ownerB, _) = await ProvisionUser("family.use");
        var (_, _, bobId) = await ProvisionUser("family.use");

        await ownerA.GetAsync("/api/family/household");
        await ownerB.GetAsync("/api/family/household"); // ownerB now owns their OWN household
        await ownerA.PostAsJsonAsync("/api/family/household/members", new { userId = bobId });

        // Bob is in A's household; B can't add him.
        (await ownerB.PostAsJsonAsync("/api/family/household/members", new { userId = bobId }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // And A can't add ownerB (an owner of another household) either.
        var ownerBId = MemberIds(await Json(await ownerB.GetAsync("/api/family/household"))).Single();
        (await ownerA.PostAsJsonAsync("/api/family/household/members", new { userId = ownerBId }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- Candidates: family.use users not yet in a household, never the caller, never an email ----

    [Fact]
    public async Task Candidates_lists_addable_family_users_without_emails()
    {
        var (_, owner, ownerId) = await ProvisionUser("family.use");
        var (_, _, bobId) = await ProvisionUser("family.use");
        await owner.GetAsync("/api/family/household");

        var resp = await owner.GetAsync("/api/family/household/candidates");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var cands = await Json(resp);
        var ids = cands.EnumerateArray().Select(c => c.GetProperty("userId").GetInt32()).ToList();

        ids.Should().Contain(bobId);   // a family.use user not in any household
        ids.Should().NotContain(ownerId); // never the caller
        cands.GetRawText().Should().NotContain("@"); // email-privacy
        cands.EnumerateArray().Should().OnlyContain(c => !HasProperty(c, "email"));

        // Once Bob joins, he drops out of the candidate list.
        await owner.PostAsJsonAsync("/api/family/household/members", new { userId = bobId });
        var after = await Json(await owner.GetAsync("/api/family/household/candidates"));
        after.EnumerateArray().Select(c => c.GetProperty("userId").GetInt32()).Should().NotContain(bobId);
    }
}
