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
/// The machineInfo half of ingest: the batch's machineInfo sub-object upserts a MachineInfos row (one
/// per machine name), the server records the request's public IP (never a client-sent value), FirstSeenUtc
/// is stable across re-pushes while LastSeenUtc advances, and the /api/fleet machine row surfaces the
/// metadata. A machine with usage but no metadata row (legacy) returns nulls without crashing.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class MachineInfoIntegrationTests(WebAppFactory factory)
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
        var email = $"mi-{Guid.NewGuid():N}@test.local";
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

    private static object Row(string dedupKey, string model = "claude-opus-4-8", string cwd = @"C:\work\mi-repo") => new
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

    private static object MachineInfo(string agent = "desktop") => new
    {
        localIp = "192.168.1.20",
        os = "Microsoft Windows 11",
        arch = "X64",
        osUser = "alice",
        cpuCount = 16,
        agent,
        // A client-sent publicIp must be ignored — the server uses the observed request address.
        publicIp = "203.0.113.250",
    };

    /// <summary>A machineInfo carrying the full richer-telemetry payload (and optional precise GPS).</summary>
    private static object RichMachineInfo(
        string agent = "desktop", double? lat = null, double? lng = null,
        double? accuracyM = null, string? geoSource = null) => new
    {
        localIp = "192.168.1.20",
        os = "Microsoft Windows 11",
        arch = "X64",
        osUser = "alice",
        cpuCount = 16,
        agent,
        cpuModel = "AMD Ryzen 9 5900X 12-Core Processor",
        logicalCores = 24,
        physicalCores = 12,
        ramTotalMB = 65536L,
        gpuModel = "NVIDIA GeForce RTX 3080",
        machineGuid = "11111111-2222-3333-4444-555555555555",
        domain = "WORKGROUP",
        manufacturer = "Dell Inc.",
        model = "XPS 15 9520",
        culture = "en-US",
        timeZoneId = "Pacific Standard Time",
        uptimeSec = 123456L,
        lanIps = "192.168.1.20,10.0.0.5",
        frameworkVersion = ".NET 9.0.0",
        lat,
        lng,
        accuracyM,
        geoSource,
    };

    [Fact]
    public async Task Ingest_with_machineInfo_upserts_a_machine_row_with_persisted_fields_and_server_public_ip()
    {
        var (_, ownerClient) = await ProvisionUser("reporter.self");
        var key = await CreateKeyAs(ownerClient, "mi-upsert");
        var machine = "mi-box-" + Guid.NewGuid().ToString("N")[..8];

        var resp = await WithKey(key).PostAsJsonAsync("/api/ingest", new
        {
            source = "claude",
            machine,
            reporter = "reporter/2.3.4",
            machineInfo = MachineInfo("desktop"),
            rows = new[] { Row(Guid.NewGuid().ToString("N")) },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
        var mi = await db.MachineInfos.AsNoTracking().SingleAsync(m => m.Name == machine);

        mi.LocalIp.Should().Be("192.168.1.20");
        mi.Os.Should().Be("Microsoft Windows 11");
        mi.Arch.Should().Be("X64");
        mi.OsUser.Should().Be("alice");
        mi.CpuCount.Should().Be(16);
        mi.Agent.Should().Be("desktop");
        mi.ReporterVersion.Should().Be("reporter/2.3.4");   // from batch.reporter
        mi.Hostname.Should().Be(machine);                   // from batch.machine
        // The server records the OBSERVED request IP and never the client-sent 203.0.113.250.
        mi.PublicIp.Should().NotBe("203.0.113.250");
        mi.FirstSeenUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(5));
        mi.LastSeenUtc.Should().BeOnOrAfter(mi.FirstSeenUtc);
    }

    [Fact]
    public async Task Second_post_keeps_FirstSeenUtc_stable_and_advances_LastSeenUtc()
    {
        var (_, ownerClient) = await ProvisionUser("reporter.self");
        var key = await CreateKeyAs(ownerClient, "mi-stable");
        var machine = "mi-stable-" + Guid.NewGuid().ToString("N")[..8];

        async Task Post(string agent) => (await WithKey(key).PostAsJsonAsync("/api/ingest", new
        {
            source = "claude",
            machine,
            reporter = "reporter/1.0.0",
            machineInfo = MachineInfo(agent),
            rows = new[] { Row(Guid.NewGuid().ToString("N")) },
        })).EnsureSuccessStatusCode();

        await Post("console");

        DateTime firstSeen, lastSeen1;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
            var mi = await db.MachineInfos.AsNoTracking().SingleAsync(m => m.Name == machine);
            mi.Agent.Should().Be("console");
            firstSeen = mi.FirstSeenUtc;
            lastSeen1 = mi.LastSeenUtc;
        }

        await Task.Delay(20);
        await Post("desktop"); // re-push: still one row, FirstSeen unchanged, LastSeen advances, fields refresh

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
            var rows = await db.MachineInfos.AsNoTracking().Where(m => m.Name == machine).ToListAsync();
            rows.Should().ContainSingle(); // upsert, not a second row
            var mi = rows[0];
            mi.FirstSeenUtc.Should().Be(firstSeen);                 // stable across the second post
            mi.LastSeenUtc.Should().BeOnOrAfter(lastSeen1);         // advanced
            mi.Agent.Should().Be("desktop");                        // refreshed from the latest push
        }
    }

    [Fact]
    public async Task Fleet_machine_row_surfaces_the_metadata()
    {
        var (_, ownerClient) = await ProvisionUser("reporter.self", "fleet.view");
        var key = await CreateKeyAs(ownerClient, "mi-fleet");
        var machine = "mi-fleet-" + Guid.NewGuid().ToString("N")[..8];

        (await WithKey(key).PostAsJsonAsync("/api/ingest", new
        {
            source = "claude",
            machine,
            reporter = "reporter/9.9.9",
            machineInfo = MachineInfo("desktop"),
            rows = new[] { Row(Guid.NewGuid().ToString("N")) },
        })).EnsureSuccessStatusCode();

        var fleet = await (await ownerClient.GetAsync("/api/fleet")).Content.ReadFromJsonAsync<JsonElement>();
        var m = fleet.GetProperty("machines").EnumerateArray()
            .First(x => x.GetProperty("name").GetString() == machine);

        m.GetProperty("localIp").GetString().Should().Be("192.168.1.20");
        m.GetProperty("os").GetString().Should().Be("Microsoft Windows 11");
        m.GetProperty("arch").GetString().Should().Be("X64");
        m.GetProperty("osUser").GetString().Should().Be("alice");
        m.GetProperty("agent").GetString().Should().Be("desktop");
        m.GetProperty("reporterVersion").GetString().Should().Be("reporter/9.9.9");
        m.GetProperty("cpuCount").GetInt32().Should().Be(16);
        m.GetProperty("publicIp").ValueKind.Should().NotBe(JsonValueKind.Undefined);
        m.TryGetProperty("firstSeenUtc", out var fs).Should().BeTrue();
        fs.ValueKind.Should().Be(JsonValueKind.String); // metadata row present → non-null
    }

    [Fact]
    public async Task Fleet_machine_without_metadata_returns_nulls_and_does_not_crash()
    {
        // A legacy machine has usage rows but no MachineInfos row. Produce that state the honest way:
        // ingest normally (which creates both), then drop just the metadata row, leaving the usage rows.
        var (_, ownerClient) = await ProvisionUser("reporter.self", "fleet.view");
        var key = await CreateKeyAs(ownerClient, "mi-legacy");
        var machine = "mi-legacy-" + Guid.NewGuid().ToString("N")[..8];

        (await WithKey(key).PostAsJsonAsync("/api/ingest", new
        {
            source = "claude",
            machine,
            machineInfo = MachineInfo("console"),
            rows = new[] { Row(Guid.NewGuid().ToString("N")) },
        })).EnsureSuccessStatusCode();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
            await db.MachineInfos.Where(m => m.Name == machine).ExecuteDeleteAsync();
            (await db.MachineInfos.AnyAsync(m => m.Name == machine)).Should().BeFalse();
            (await db.UsageRecords.AnyAsync(r => r.MachineName == machine)).Should().BeTrue(); // usage survives
        }

        var fleet = await (await ownerClient.GetAsync("/api/fleet")).Content.ReadFromJsonAsync<JsonElement>();
        var m = fleet.GetProperty("machines").EnumerateArray()
            .First(x => x.GetProperty("name").GetString() == machine);

        // The bucket still has its aggregate data...
        m.GetProperty("records").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        m.GetProperty("tokens").GetInt64().Should().BeGreaterThan(0);
        // ...and every metadata field is null (no row yet).
        m.GetProperty("localIp").ValueKind.Should().Be(JsonValueKind.Null);
        m.GetProperty("publicIp").ValueKind.Should().Be(JsonValueKind.Null);
        m.GetProperty("os").ValueKind.Should().Be(JsonValueKind.Null);
        m.GetProperty("agent").ValueKind.Should().Be(JsonValueKind.Null);
        m.GetProperty("cpuCount").ValueKind.Should().Be(JsonValueKind.Null);
        m.GetProperty("firstSeenUtc").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Ingest_with_blank_machine_writes_no_machine_info_row()
    {
        var (_, ownerClient) = await ProvisionUser("reporter.self");
        var key = await CreateKeyAs(ownerClient, "mi-blank");

        // No machine field at all → the upsert is skipped (the local file-sync path has no remote machine).
        (await WithKey(key).PostAsJsonAsync("/api/ingest", new
        {
            source = "claude",
            machineInfo = MachineInfo("console"),
            rows = new[] { Row(Guid.NewGuid().ToString("N")) },
        })).EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
        // "unknown" is the sanitized blank-machine label for usage rows; no metadata row is created for it.
        (await db.MachineInfos.AnyAsync(m => m.Name == "unknown")).Should().BeFalse();
    }

    [Fact]
    public async Task Ingest_persists_richer_telemetry_and_surfaces_it_on_the_fleet_row()
    {
        var (_, ownerClient) = await ProvisionUser("reporter.self", "fleet.view");
        var key = await CreateKeyAs(ownerClient, "mi-rich");
        var machine = "mi-rich-" + Guid.NewGuid().ToString("N")[..8];

        (await WithKey(key).PostAsJsonAsync("/api/ingest", new
        {
            source = "claude",
            machine,
            reporter = "reporter/3.0.0",
            machineInfo = RichMachineInfo("desktop"),
            rows = new[] { Row(Guid.NewGuid().ToString("N")) },
        })).EnsureSuccessStatusCode();

        // Persisted on the entity...
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
            var mi = await db.MachineInfos.AsNoTracking().SingleAsync(m => m.Name == machine);
            mi.CpuModel.Should().Be("AMD Ryzen 9 5900X 12-Core Processor");
            mi.LogicalCores.Should().Be(24);
            mi.PhysicalCores.Should().Be(12);
            mi.RamTotalMB.Should().Be(65536);
            mi.GpuModel.Should().Be("NVIDIA GeForce RTX 3080");
            mi.MachineGuid.Should().Be("11111111-2222-3333-4444-555555555555");
            mi.Domain.Should().Be("WORKGROUP");
            mi.Manufacturer.Should().Be("Dell Inc.");
            mi.Model.Should().Be("XPS 15 9520");
            mi.Culture.Should().Be("en-US");
            mi.TimeZoneId.Should().Be("Pacific Standard Time");
            mi.UptimeSec.Should().Be(123456);
            mi.LanIps.Should().Be("192.168.1.20,10.0.0.5");
            mi.FrameworkVersion.Should().Be(".NET 9.0.0");
        }

        // ...and surfaced on the fleet machine row.
        var fleet = await (await ownerClient.GetAsync("/api/fleet")).Content.ReadFromJsonAsync<JsonElement>();
        var m = fleet.GetProperty("machines").EnumerateArray()
            .First(x => x.GetProperty("name").GetString() == machine);
        m.GetProperty("cpuModel").GetString().Should().Be("AMD Ryzen 9 5900X 12-Core Processor");
        m.GetProperty("physicalCores").GetInt32().Should().Be(12);
        m.GetProperty("ramTotalMB").GetInt64().Should().Be(65536);
        m.GetProperty("gpuModel").GetString().Should().Be("NVIDIA GeForce RTX 3080");
        m.GetProperty("manufacturer").GetString().Should().Be("Dell Inc.");
        m.GetProperty("lanIps").GetString().Should().Be("192.168.1.20,10.0.0.5");
        m.GetProperty("frameworkVersion").GetString().Should().Be(".NET 9.0.0");
    }

    [Fact]
    public async Task Precise_agent_gps_fix_is_stored_as_an_agent_source_location()
    {
        var (_, ownerClient) = await ProvisionUser("reporter.self", "fleet.view");
        var key = await CreateKeyAs(ownerClient, "mi-gps");
        var machine = "mi-gps-" + Guid.NewGuid().ToString("N")[..8];

        (await WithKey(key).PostAsJsonAsync("/api/ingest", new
        {
            source = "claude",
            machine,
            machineInfo = RichMachineInfo("desktop", lat: 47.6062, lng: -122.3321, accuracyM: 12.5, geoSource: "agent"),
            rows = new[] { Row(Guid.NewGuid().ToString("N")) },
        })).EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
        var mi = await db.MachineInfos.AsNoTracking().SingleAsync(m => m.Name == machine);
        mi.GeoSource.Should().Be("agent");
        mi.Lat.Should().BeApproximately(47.6062, 0.0001);
        mi.Lng.Should().BeApproximately(-122.3321, 0.0001);
        mi.AccuracyM.Should().BeApproximately(12.5, 0.01);
        mi.GeoUpdatedUtc.Should().NotBeNull(); // stamped, so the IP-geo backfill won't re-resolve it
    }

    [Fact]
    public async Task Geo_backfill_never_overwrites_a_precise_agent_fix()
    {
        var (_, ownerClient) = await ProvisionUser("reporter.self");
        var key = await CreateKeyAs(ownerClient, "mi-gps-protect");
        var machine = "mi-gps-protect-" + Guid.NewGuid().ToString("N")[..8];

        (await WithKey(key).PostAsJsonAsync("/api/ingest", new
        {
            source = "claude",
            machine,
            machineInfo = RichMachineInfo("desktop", lat: 47.6062, lng: -122.3321, accuracyM: 8, geoSource: "agent"),
            rows = new[] { Row(Guid.NewGuid().ToString("N")) },
        })).EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();

        // Force a routable public IP onto the row and clear the geo timestamp so the backfill WOULD pick it
        // up were it not for the agent-source guard.
        await db.MachineInfos.Where(m => m.Name == machine).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.PublicIp, "8.8.8.8")
            .SetProperty(m => m.GeoUpdatedUtc, (DateTime?)null));

        var ipGeo = scope.ServiceProvider.GetRequiredService<Ccusage.Api.Services.IpGeoService>();
        var backfill = new Ccusage.Api.Services.MachineGeoBackfillService(
            scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Ccusage.Api.Services.MachineGeoBackfillService>.Instance);

        // Run a deterministic pass. The agent-source row must be EXCLUDED from the candidate set entirely.
        await backfill.RunOnceAsync(db, ipGeo, DateTime.UtcNow);

        var mi = await db.MachineInfos.AsNoTracking().SingleAsync(m => m.Name == machine);
        mi.GeoSource.Should().Be("agent");                    // unchanged
        mi.Lat.Should().BeApproximately(47.6062, 0.0001);     // precise coords preserved
        mi.Lng.Should().BeApproximately(-122.3321, 0.0001);
        mi.AccuracyM.Should().BeApproximately(8, 0.01);
        mi.GeoUpdatedUtc.Should().BeNull();                   // never touched (excluded from the candidate set)
    }
}
