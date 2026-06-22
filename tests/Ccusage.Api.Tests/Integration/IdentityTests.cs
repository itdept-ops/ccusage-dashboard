using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ccusage.Api.Data;
using Ccusage.Api.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ccusage.Api.Tests.Integration;

/// <summary>
/// Family Hub — Identity Map (/api/family/identity). PRIVATE + OWNER-SCOPED. Covers:
///
/// <list type="bullet">
///   <item>GATING: every endpoint requires identity.map (403 without) and auth (401 unauthenticated).</item>
///   <item>OWNER-SCOPE: a caller only sees/edits their OWN roles/time — a different user can't read or delete
///   another's, and manual time can't be attributed to a foreign role.</item>
///   <item>MANUAL ENTRY + AGGREGATE: a manual log shows up in the per-role minutes total over a range.</item>
///   <item>CALENDAR DEDUP: committing the same SourceEventId twice imports once (re-import never double-counts).</item>
///   <item>RULES: an upserted classification rule is idempotent by keyword; the commit can add new rules.</item>
/// </list>
/// Calendar import preview against the real Google API isn't exercised here (no live calendar in tests); the
/// dedup that protects re-import is proven through the commit endpoint + the filtered unique index, and the
/// title→role classification is proven by the IdentityClassifyTests unit test.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class IdentityTests(WebAppFactory factory)
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
        var email = $"identity-{Guid.NewGuid():N}@test.local";
        var res = await Admin().PostAsJsonAsync("/api/users", new { email, isEnabled = true, permissions });
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await Json(res)).GetProperty("id").GetInt32();
        return (email, Client(email), id);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        await resp.Content.ReadFromJsonAsync<JsonElement>();

    private async Task<int> TimeEntryCountFor(string email)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
        var lower = email.ToLowerInvariant();
        return await db.IdentityTimeEntries.AsNoTracking().CountAsync(t => t.UserEmail == lower);
    }

    private async Task<int> CreateRole(HttpClient client, string name, string color = "#3d8bff")
    {
        var res = await client.PostAsJsonAsync("/api/family/identity/roles", new { name, color });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await Json(res)).GetProperty("id").GetInt32();
    }

    // =====================================================================================
    // GATING — identity.map required (403), auth required (401)
    // =====================================================================================

    [Fact]
    public async Task Identity_endpoints_require_identity_map()
    {
        var (_, plain, _) = await ProvisionUser("family.use"); // family.use is NOT enough

        (await plain.GetAsync("/api/family/identity/")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await plain.PostAsJsonAsync("/api/family/identity/roles", new { name = "Coder", color = "#3d8bff" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await plain.PostAsJsonAsync("/api/family/identity/time", new { roleId = 1, date = "2026-06-01", minutes = 60 }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await plain.DeleteAsync("/api/family/identity/roles/1")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await plain.GetAsync("/api/family/identity/calendar-status")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await plain.PostAsJsonAsync("/api/family/identity/import/preview", new { }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await plain.PostAsJsonAsync("/api/family/identity/rules", new { keyword = "x", roleId = 1 }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Identity_endpoints_require_authentication()
    {
        var anon = factory.CreateClient();
        (await anon.GetAsync("/api/family/identity/")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anon.PostAsJsonAsync("/api/family/identity/roles", new { name = "Coder" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anon.PostAsJsonAsync("/api/family/identity/time", new { roleId = 1, date = "2026-06-01", minutes = 60 }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anon.GetAsync("/api/family/identity/calendar-status")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // =====================================================================================
    // ROLES + MANUAL TIME + AGGREGATE
    // =====================================================================================

    [Fact]
    public async Task Manual_time_log_appears_in_the_role_split_aggregate()
    {
        var (_, owner, _) = await ProvisionUser("identity.map");
        var coder = await CreateRole(owner, "Coder", "#22c55e");
        var parent = await CreateRole(owner, "Parent", "#f59e0b");

        // 90 minutes Coder + 30 + 60 minutes Parent over the window.
        (await owner.PostAsJsonAsync("/api/family/identity/time",
            new { roleId = coder, date = "2026-06-10", minutes = 90 })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await owner.PostAsJsonAsync("/api/family/identity/time",
            new { roleId = parent, date = "2026-06-11", minutes = 30 })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await owner.PostAsJsonAsync("/api/family/identity/time",
            new { roleId = parent, date = "2026-06-11", minutes = 60 })).StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await Json(await owner.GetAsync(
            "/api/family/identity/?fromUtc=2026-06-01T00:00:00Z&toUtc=2026-06-30T00:00:00Z"));
        body.GetProperty("roles").GetArrayLength().Should().Be(2);

        var totals = body.GetProperty("totals").EnumerateArray().ToList();
        totals.Should().HaveCount(2);
        // Sorted by minutes descending: Parent (90) then Coder (90)? both 90 — assert by roleId lookup.
        int MinutesFor(int roleId) => totals.Single(t => t.GetProperty("roleId").GetInt32() == roleId)
            .GetProperty("minutes").GetInt32();
        MinutesFor(coder).Should().Be(90);
        MinutesFor(parent).Should().Be(90);
    }

    [Fact]
    public async Task Manual_time_validates_minutes_and_role_ownership()
    {
        var (_, owner, _) = await ProvisionUser("identity.map");
        var (_, other, _) = await ProvisionUser("identity.map");
        var ownRole = await CreateRole(owner, "Coder");
        var foreignRole = await CreateRole(other, "Athlete");

        // Minutes < 1 → 400.
        (await owner.PostAsJsonAsync("/api/family/identity/time",
            new { roleId = ownRole, date = "2026-06-10", minutes = 0 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // A role the caller doesn't own → 404 (can't attribute time to a foreign role).
        (await owner.PostAsJsonAsync("/api/family/identity/time",
            new { roleId = foreignRole, date = "2026-06-10", minutes = 60 })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Role_name_must_be_unique_per_owner()
    {
        var (_, owner, _) = await ProvisionUser("identity.map");
        await CreateRole(owner, "Coder");
        var dup = await owner.PostAsJsonAsync("/api/family/identity/roles", new { name = "Coder", color = "#22c55e" });
        dup.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Role_colour_must_be_a_palette_colour()
    {
        var (_, owner, _) = await ProvisionUser("identity.map");
        var bad = await owner.PostAsJsonAsync("/api/family/identity/roles",
            new { name = "Coder", color = "#000000" });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Deleting_a_role_removes_its_time_entries_and_rules()
    {
        var (email, owner, _) = await ProvisionUser("identity.map");
        var role = await CreateRole(owner, "Coder");
        await owner.PostAsJsonAsync("/api/family/identity/time", new { roleId = role, date = "2026-06-10", minutes = 60 });
        await owner.PostAsJsonAsync("/api/family/identity/rules", new { keyword = "standup", roleId = role });
        (await TimeEntryCountFor(email)).Should().Be(1);

        (await owner.DeleteAsync($"/api/family/identity/roles/{role}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await TimeEntryCountFor(email)).Should().Be(0);

        var body = await Json(await owner.GetAsync("/api/family/identity/"));
        body.GetProperty("roles").GetArrayLength().Should().Be(0);
        body.GetProperty("rules").GetArrayLength().Should().Be(0);
    }

    // =====================================================================================
    // OWNER-SCOPE — a caller can't read or delete another user's roles/time
    // =====================================================================================

    [Fact]
    public async Task A_caller_only_sees_their_own_roles_and_time()
    {
        var (aliceEmail, alice, _) = await ProvisionUser("identity.map");
        var (_, bob, _) = await ProvisionUser("identity.map");

        var aliceRole = await CreateRole(alice, "AliceCoder", "#a855f7");
        await alice.PostAsJsonAsync("/api/family/identity/time", new { roleId = aliceRole, date = "2026-06-10", minutes = 60 });
        await CreateRole(bob, "BobAthlete", "#ef4444");

        // Bob's payload never contains Alice's role name or email.
        var bobRaw = await (await bob.GetAsync("/api/family/identity/")).Content.ReadAsStringAsync();
        bobRaw.Should().NotContain("AliceCoder");
        bobRaw.Should().NotContain(aliceEmail);
        var bobBody = JsonDocument.Parse(bobRaw).RootElement;
        bobBody.GetProperty("roles").GetArrayLength().Should().Be(1); // only Bob's own role
    }

    [Fact]
    public async Task A_caller_cannot_delete_another_users_role()
    {
        var (_, alice, _) = await ProvisionUser("identity.map");
        var (_, bob, _) = await ProvisionUser("identity.map");
        var aliceRole = await CreateRole(alice, "AliceCoder");

        (await bob.DeleteAsync($"/api/family/identity/roles/{aliceRole}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        // Alice's role survives.
        (await Json(await alice.GetAsync("/api/family/identity/")))
            .GetProperty("roles").GetArrayLength().Should().Be(1);
    }

    // =====================================================================================
    // CALENDAR IMPORT — commit is idempotent on SourceEventId (re-import never double-counts)
    // =====================================================================================

    [Fact]
    public async Task Import_commit_dedupes_on_source_event_id()
    {
        var (email, owner, _) = await ProvisionUser("identity.map");
        var role = await CreateRole(owner, "Coder");

        var payload = new
        {
            items = new[]
            {
                new { sourceEventId = "evt-1", roleId = role, date = "2026-06-10", minutes = 60, note = "standup" },
                new { sourceEventId = "evt-2", roleId = role, date = "2026-06-11", minutes = 30, note = "review" },
            },
        };

        var first = await Json(await owner.PostAsJsonAsync("/api/family/identity/import/commit", payload));
        first.GetProperty("imported").GetInt32().Should().Be(2);
        first.GetProperty("skipped").GetInt32().Should().Be(0);
        (await TimeEntryCountFor(email)).Should().Be(2);

        // RE-IMPORT the SAME window: both ids already exist → imported 0, skipped 2, count UNCHANGED.
        var second = await Json(await owner.PostAsJsonAsync("/api/family/identity/import/commit", payload));
        second.GetProperty("imported").GetInt32().Should().Be(0);
        second.GetProperty("skipped").GetInt32().Should().Be(2);
        (await TimeEntryCountFor(email)).Should().Be(2);
    }

    [Fact]
    public async Task Import_commit_skips_foreign_roles_and_repeated_ids_within_a_batch()
    {
        var (email, owner, _) = await ProvisionUser("identity.map");
        var (_, other, _) = await ProvisionUser("identity.map");
        var role = await CreateRole(owner, "Coder");
        var foreign = await CreateRole(other, "Athlete");

        var payload = new
        {
            items = new[]
            {
                new { sourceEventId = "evt-1", roleId = role, date = "2026-06-10", minutes = 60 },
                // Same id repeated in the batch → only the first imports.
                new { sourceEventId = "evt-1", roleId = role, date = "2026-06-10", minutes = 60 },
                // A role the caller doesn't own → skipped.
                new { sourceEventId = "evt-2", roleId = foreign, date = "2026-06-11", minutes = 30 },
            },
        };
        var res = await Json(await owner.PostAsJsonAsync("/api/family/identity/import/commit", payload));
        res.GetProperty("imported").GetInt32().Should().Be(1);
        res.GetProperty("skipped").GetInt32().Should().Be(2);
        (await TimeEntryCountFor(email)).Should().Be(1);
    }

    [Fact]
    public async Task Import_commit_can_save_new_rules_for_next_time()
    {
        var (_, owner, _) = await ProvisionUser("identity.map");
        var role = await CreateRole(owner, "Coder");

        var payload = new
        {
            items = new[] { new { sourceEventId = "evt-1", roleId = role, date = "2026-06-10", minutes = 60 } },
            newRules = new[] { new { keyword = "Standup", roleId = role } },
        };
        (await owner.PostAsJsonAsync("/api/family/identity/import/commit", payload))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // The rule is stored lower-cased and resolves to the role.
        var body = await Json(await owner.GetAsync("/api/family/identity/"));
        var rules = body.GetProperty("rules").EnumerateArray().ToList();
        rules.Should().ContainSingle();
        rules[0].GetProperty("keyword").GetString().Should().Be("standup");
        rules[0].GetProperty("roleId").GetInt32().Should().Be(role);
    }

    // =====================================================================================
    // RULES — UNIQUE-keyword upsert (idempotent); foreign role rejected
    // =====================================================================================

    [Fact]
    public async Task Rule_upsert_is_idempotent_by_keyword()
    {
        var (_, owner, _) = await ProvisionUser("identity.map");
        var coder = await CreateRole(owner, "Coder");
        var athlete = await CreateRole(owner, "Athlete", "#22c55e");

        // Create then re-point the SAME keyword → still ONE rule, now mapped to athlete.
        await owner.PostAsJsonAsync("/api/family/identity/rules", new { keyword = "training", roleId = coder });
        await owner.PostAsJsonAsync("/api/family/identity/rules", new { keyword = "training", roleId = athlete, priority = 3 });

        var rules = (await Json(await owner.GetAsync("/api/family/identity/")))
            .GetProperty("rules").EnumerateArray().ToList();
        rules.Should().ContainSingle();
        rules[0].GetProperty("roleId").GetInt32().Should().Be(athlete);
        rules[0].GetProperty("priority").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task Rule_rejects_a_foreign_role()
    {
        var (_, owner, _) = await ProvisionUser("identity.map");
        var (_, other, _) = await ProvisionUser("identity.map");
        var foreign = await CreateRole(other, "Athlete");

        (await owner.PostAsJsonAsync("/api/family/identity/rules", new { keyword = "x", roleId = foreign }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // =====================================================================================
    // CALENDAR STATUS — graceful when not connected (configured in tests, but caller not connected)
    // =====================================================================================

    [Fact]
    public async Task Calendar_status_reports_not_connected_for_a_fresh_user()
    {
        var (_, owner, _) = await ProvisionUser("identity.map");
        var body = await Json(await owner.GetAsync("/api/family/identity/calendar-status"));
        body.GetProperty("connected").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Import_preview_degrades_gracefully_when_not_connected()
    {
        var (_, owner, _) = await ProvisionUser("identity.map");
        // No connected calendar → a graceful 200 not-ready body, NEVER a 500.
        var res = await owner.PostAsJsonAsync("/api/family/identity/import/preview", new { });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(res);
        body.GetProperty("connected").GetBoolean().Should().BeFalse();
    }
}
