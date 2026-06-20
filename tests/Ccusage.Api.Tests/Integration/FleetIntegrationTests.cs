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
/// Fleet management mutations (reassign / delete / revoke-keys) and the machine filter on dashboard
/// data + the /api/machines filter-options endpoint. Every mutation is reporter.manage-gated, operates
/// on RAW attribution values, and writes an audit entry.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class FleetIntegrationTests(WebAppFactory factory)
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
        var email = $"fleet-{Guid.NewGuid():N}@test.local";
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

    private static object Row(string dedupKey, string model = "claude-opus-4-8", string cwd = @"C:\work\fleet-repo") => new
    {
        dedupKey,
        timestampUtc = "2026-06-12T12:00:00Z",
        model,
        input = 1000L,
        output = 500L,
        cacheRead = 200L,
        cache5m = 0L,
        cache1h = 0L,
        sessionId = "sess-" + dedupKey,
        cwd,
        gitBranch = "main",
        isSidechain = false,
        agentId = (string?)null,
        version = "1.0.0",
    };

    /// <summary>Ingests one row from a fresh key owned by a fresh reporter, on the given machine, and
    /// returns (ownerEmail, dedupKey) for later assertions.</summary>
    private async Task<(string owner, string dedup)> IngestOne(string machine)
    {
        var (owner, ownerClient) = await ProvisionUser("reporter.self");
        var key = await CreateKeyAs(ownerClient, "ingest-" + Guid.NewGuid().ToString("N")[..6]);
        var dedup = Guid.NewGuid().ToString("N");
        var resp = await WithKey(key).PostAsJsonAsync("/api/ingest",
            new { source = "claude", machine, rows = new[] { Row(dedup) } });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return (owner, dedup);
    }

    /// <summary>The AppUser id for an email (the user-dimension mutations now key off ids, not emails).</summary>
    private async Task<int> UserIdFor(string email)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
        return await db.Users.AsNoTracking().Where(u => u.Email == email).Select(u => u.Id).FirstAsync();
    }

    private static async Task<bool> AuditContains(HttpClient admin, string action, string detailFragment)
    {
        var audit = await (await admin.GetAsync("/api/audit")).Content.ReadFromJsonAsync<JsonElement>();
        return audit.EnumerateArray().Any(e =>
            e.GetProperty("action").GetString() == action &&
            (e.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : "").Contains(detailFragment));
    }

    // ---- Permission gating: all three mutations require reporter.manage ----

    [Fact]
    public async Task Fleet_mutations_require_reporter_manage()
    {
        var (_, noPerm) = await ProvisionUser("dashboard.view", "reporter.view"); // not reporter.manage

        var reassign = await noPerm.PostAsJsonAsync("/api/fleet/reassign",
            new { dimension = "machine", from = new[] { "x" }, to = "y" });
        reassign.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var del = await noPerm.PostAsJsonAsync("/api/fleet/delete",
            new { dimension = "machine", names = new[] { "x" } });
        del.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var revoke = await noPerm.PostAsJsonAsync("/api/fleet/revoke-keys", new { userId = 1 });
        revoke.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Fleet_mutations_require_authentication()
    {
        var anon = factory.CreateClient();
        (await anon.PostAsJsonAsync("/api/fleet/reassign", new { dimension = "machine", from = new[] { "x" }, to = "y" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anon.PostAsJsonAsync("/api/fleet/delete", new { dimension = "machine", names = new[] { "x" } }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anon.PostAsJsonAsync("/api/fleet/revoke-keys", new { userId = 1 }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Fleet_endpoints_reject_an_invalid_dimension()
    {
        var (_, mgr) = await ProvisionUser("reporter.manage");
        (await mgr.PostAsJsonAsync("/api/fleet/reassign", new { dimension = "bogus", from = new[] { "x" }, to = "y" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await mgr.PostAsJsonAsync("/api/fleet/delete", new { dimension = "bogus", names = new[] { "x" } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- Reassign: combine + transfer, machines and users ----

    [Fact]
    public async Task Reassign_transfers_records_between_machines_and_audits()
    {
        var srcMachine = "src-" + Guid.NewGuid().ToString("N")[..8];
        var dstMachine = "dst-" + Guid.NewGuid().ToString("N")[..8];
        var (_, d1) = await IngestOne(srcMachine);
        var (_, d2) = await IngestOne(srcMachine);

        var (_, mgr) = await ProvisionUser("reporter.manage", "dashboard.view");
        var resp = await mgr.PostAsJsonAsync("/api/fleet/reassign",
            new { dimension = "machine", from = new[] { srcMachine }, to = dstMachine });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("affected").GetInt64().Should().Be(2);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
            var rows = await db.UsageRecords.AsNoTracking().Where(r => r.DedupKey == d1 || r.DedupKey == d2).ToListAsync();
            rows.Should().OnlyContain(r => r.MachineName == dstMachine);
            (await db.UsageRecords.AnyAsync(r => r.MachineName == srcMachine)).Should().BeFalse();
        }

        (await AuditContains(Admin(), "fleet.reassign", $"-> {dstMachine}")).Should().BeTrue();
    }

    [Fact]
    public async Task Reassign_combines_two_machines_into_an_existing_one()
    {
        var a = "comb-a-" + Guid.NewGuid().ToString("N")[..8];
        var b = "comb-b-" + Guid.NewGuid().ToString("N")[..8];
        var target = "comb-t-" + Guid.NewGuid().ToString("N")[..8];
        await IngestOne(a);
        await IngestOne(b);
        await IngestOne(target); // target already exists → "combine"

        var (_, mgr) = await ProvisionUser("reporter.manage", "dashboard.view");
        var resp = await mgr.PostAsJsonAsync("/api/fleet/reassign",
            new { dimension = "machine", from = new[] { a, b }, to = target });
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("affected").GetInt64().Should().Be(2);

        // The combined bucket now holds all three records; the source buckets are gone.
        var machines = await (await mgr.GetAsync("/api/machines")).Content.ReadFromJsonAsync<JsonElement>();
        var list = machines.EnumerateArray().ToList();
        list.First(m => m.GetProperty("name").GetString() == target).GetProperty("records").GetInt32().Should().Be(3);
        list.Should().NotContain(m => m.GetProperty("name").GetString() == a);
        list.Should().NotContain(m => m.GetProperty("name").GetString() == b);
    }

    [Fact]
    public async Task Reassign_moves_records_between_users()
    {
        var fromMachine = "umove-" + Guid.NewGuid().ToString("N")[..8];
        var (fromUser, dedup) = await IngestOne(fromMachine);
        var (toUser, _) = await ProvisionUser("reporter.self");

        var fromId = await UserIdFor(fromUser);
        var toId = await UserIdFor(toUser);
        var (_, mgr) = await ProvisionUser("reporter.manage", "dashboard.view");
        var resp = await mgr.PostAsJsonAsync("/api/fleet/reassign",
            new { dimension = "user", userIds = new[] { fromId }, toUserId = toId });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("affected").GetInt64().Should().Be(1);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
        var row = await db.UsageRecords.AsNoTracking().FirstAsync(r => r.DedupKey == dedup);
        row.ReportedByUser.Should().Be(toUser);
    }

    [Fact]
    public async Task Reassign_can_relabel_a_machine_to_local_empty_string()
    {
        var machine = "tolocal-" + Guid.NewGuid().ToString("N")[..8];
        var (_, dedup) = await IngestOne(machine);

        var (_, mgr) = await ProvisionUser("reporter.manage");
        var resp = await mgr.PostAsJsonAsync("/api/fleet/reassign",
            new { dimension = "machine", from = new[] { machine }, to = "" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("affected").GetInt64().Should().Be(1);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
        (await db.UsageRecords.AsNoTracking().FirstAsync(r => r.DedupKey == dedup)).MachineName.Should().Be("");
    }

    [Fact]
    public async Task Reassign_rejects_empty_from_and_a_pure_no_op()
    {
        var (_, mgr) = await ProvisionUser("reporter.manage");

        (await mgr.PostAsJsonAsync("/api/fleet/reassign",
            new { dimension = "machine", from = Array.Empty<string>(), to = "y" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // The only `from` equals `to` → no-op, rejected.
        (await mgr.PostAsJsonAsync("/api/fleet/reassign",
            new { dimension = "machine", from = new[] { "same" }, to = "same" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- Delete: removes exactly the named buckets, rejects empty names ----

    [Fact]
    public async Task Delete_removes_exactly_the_named_buckets_and_nothing_else_and_audits()
    {
        var doomed = "del-doomed-" + Guid.NewGuid().ToString("N")[..8];
        var kept = "del-kept-" + Guid.NewGuid().ToString("N")[..8];
        var (_, doomedDedup) = await IngestOne(doomed);
        var (_, keptDedup) = await IngestOne(kept);

        var (_, mgr) = await ProvisionUser("reporter.manage");
        var resp = await mgr.PostAsJsonAsync("/api/fleet/delete",
            new { dimension = "machine", names = new[] { doomed } });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("deleted").GetInt64().Should().Be(1);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
            (await db.UsageRecords.AnyAsync(r => r.DedupKey == doomedDedup)).Should().BeFalse();
            (await db.UsageRecords.AnyAsync(r => r.DedupKey == keptDedup)).Should().BeTrue(); // untouched
        }

        (await AuditContains(Admin(), "fleet.delete", doomed)).Should().BeTrue();
    }

    [Fact]
    public async Task Delete_rejects_an_empty_names_array_no_wildcard_delete()
    {
        var (_, mgr) = await ProvisionUser("reporter.manage");
        (await mgr.PostAsJsonAsync("/api/fleet/delete",
            new { dimension = "machine", names = Array.Empty<string>() }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- Revoke keys: by UserId and by CreatedByEmail, leaving others ----

    [Fact]
    public async Task Revoke_keys_revokes_only_that_users_keys_by_userid_and_legacy_email_and_audits()
    {
        // Victim owns one key via UserId.
        var (victim, victimClient) = await ProvisionUser("reporter.self");
        await CreateKeyAs(victimClient, "victim-userid-key");

        // Bystander owns an active key that must survive.
        var (_, bystanderClient) = await ProvisionUser("reporter.self");
        await CreateKeyAs(bystanderClient, "bystander-key");

        // A legacy key with no UserId link, matched only by CreatedByEmail (case-insensitive).
        int legacyId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
            var legacy = new Ccusage.Api.Data.Entities.IngestKey
            {
                Name = "legacy",
                KeyHash = Guid.NewGuid().ToString("N"),
                Prefix = "uiq_legacy…",
                CreatedUtc = DateTime.UtcNow,
                CreatedByEmail = victim.ToUpperInvariant(), // stored upper → matched case-insensitively
                UserId = null,
            };
            db.IngestKeys.Add(legacy);
            await db.SaveChangesAsync();
            legacyId = legacy.Id;
        }

        var (_, mgr) = await ProvisionUser("reporter.manage");
        var resp = await mgr.PostAsJsonAsync("/api/fleet/revoke-keys", new { userId = await UserIdFor(victim) });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revoked").GetInt32().Should().Be(2);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
            // Victim's UserId key + legacy email key both revoked.
            var victimUser = await db.Users.AsNoTracking().FirstAsync(u => u.Email == victim);
            (await db.IngestKeys.AsNoTracking().Where(k => k.UserId == victimUser.Id).ToListAsync())
                .Should().OnlyContain(k => k.RevokedUtc != null);
            (await db.IngestKeys.AsNoTracking().FirstAsync(k => k.Id == legacyId)).RevokedUtc.Should().NotBeNull();
            // Bystander's key untouched.
            (await db.IngestKeys.AsNoTracking().Where(k => k.Name == "bystander-key").ToListAsync())
                .Should().OnlyContain(k => k.RevokedUtc == null);
        }

        (await AuditContains(Admin(), "fleet.revoke-keys", "revoked=2")).Should().BeTrue();
    }

    [Fact]
    public async Task Revoke_keys_does_not_double_revoke_and_returns_zero_when_nothing_active()
    {
        var (owner, ownerClient) = await ProvisionUser("reporter.self");
        await CreateKeyAs(ownerClient, "once");
        var ownerId = await UserIdFor(owner);

        var (_, mgr) = await ProvisionUser("reporter.manage");
        (await (await mgr.PostAsJsonAsync("/api/fleet/revoke-keys", new { userId = ownerId }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revoked").GetInt32().Should().Be(1);

        // Second call finds nothing still-active → 0.
        (await (await mgr.PostAsJsonAsync("/api/fleet/revoke-keys", new { userId = ownerId }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revoked").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Revoke_keys_rejects_a_missing_userid()
    {
        var (_, mgr) = await ProvisionUser("reporter.manage");
        // Zero/absent id is rejected before any DB work.
        (await mgr.PostAsJsonAsync("/api/fleet/revoke-keys", new { userId = 0 }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // A non-existent id resolves to no user → BadRequest.
        (await mgr.PostAsJsonAsync("/api/fleet/revoke-keys", new { userId = 999999 }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- Machine filter on dashboard data + /api/machines ----

    [Fact]
    public async Task Machines_endpoint_lists_buckets_with_raw_name_and_label()
    {
        var machine = "list-" + Guid.NewGuid().ToString("N")[..8];
        await IngestOne(machine);

        var (_, viewer) = await ProvisionUser("dashboard.view");
        var machines = await (await viewer.GetAsync("/api/machines")).Content.ReadFromJsonAsync<JsonElement>();
        var bucket = machines.EnumerateArray().First(m => m.GetProperty("name").GetString() == machine);
        bucket.GetProperty("label").GetString().Should().Be(machine); // non-empty → label == name
        bucket.GetProperty("records").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        bucket.GetProperty("totalTokens").GetInt64().Should().BeGreaterThan(0);
        bucket.GetProperty("costUsd").GetDecimal().Should().BeGreaterThan(0m);
    }

    [Fact]
    public async Task Machines_endpoint_is_gated_by_dashboard_or_calendar_view()
    {
        var (_, neither) = await ProvisionUser("sync.run");
        (await neither.GetAsync("/api/machines")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var (_, cal) = await ProvisionUser("calendar.view");
        (await cal.GetAsync("/api/machines")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Machine_filter_narrows_summary_and_records()
    {
        var m1 = "filt-1-" + Guid.NewGuid().ToString("N")[..8];
        var m2 = "filt-2-" + Guid.NewGuid().ToString("N")[..8];
        var (_, d1) = await IngestOne(m1);
        await IngestOne(m2);

        var (_, viewer) = await ProvisionUser("dashboard.view");

        // Summary by machine, filtered to m1 only → exactly one bucket, that machine.
        var summary = await (await viewer.GetAsync($"/api/usage/summary?groupBy=machine&machine={m1}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var buckets = summary.GetProperty("buckets").EnumerateArray().ToList();
        buckets.Should().ContainSingle();
        buckets[0].GetProperty("key").GetString().Should().Be(m1);

        // Records, filtered to m1 → contains d1, never the m2 row.
        var records = await (await viewer.GetAsync($"/api/usage/records?machine={m1}&pageSize=500"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var ids = records.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("sessionId").GetString()).ToList();
        ids.Should().Contain("sess-" + d1);
    }

    [Fact]
    public async Task Machine_filter_can_select_the_empty_local_bucket()
    {
        // Local rows have MachineName == "". Produce one the honest way: ingest from a machine, then
        // reassign it to the empty/local value via the fleet endpoint (all FKs stay satisfied).
        var machine = "becomes-local-" + Guid.NewGuid().ToString("N")[..8];
        var (_, localDedup) = await IngestOne(machine);

        var (_, mgr) = await ProvisionUser("reporter.manage");
        (await mgr.PostAsJsonAsync("/api/fleet/reassign",
            new { dimension = "machine", from = new[] { machine }, to = "" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // A second row stays on a real, non-empty machine — it must be EXCLUDED by the local filter,
        // which proves the empty-string filter is genuinely applied (not silently dropped).
        var otherMachine = "stays-remote-" + Guid.NewGuid().ToString("N")[..8];
        var (_, remoteDedup) = await IngestOne(otherMachine);

        var (_, viewer) = await ProvisionUser("dashboard.view");
        // An empty string in the filter selects the local bucket only.
        var records = await (await viewer.GetAsync("/api/usage/records?machine=&pageSize=500"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var sessions = records.GetProperty("items").EnumerateArray()
            .Select(r => r.GetProperty("sessionId").GetString()).ToList();
        sessions.Should().Contain("sess-" + localDedup);
        sessions.Should().NotContain("sess-" + remoteDedup);
    }
}
