using System.Text.Json;
using Ccusage.Api.Dtos;
using Ccusage.Reporter.Core;
using FluentAssertions;

namespace Ccusage.Api.Tests.Unit;

/// <summary>
/// The reporter's <see cref="MachineInfo"/> record and the server's <see cref="MachineInfoDto"/> are two
/// halves of one wire contract: the reporter serializes the record (camelCase, JsonSerializerDefaults.Web)
/// and the server deserializes the DTO from the same bytes. These tests pin that the richer telemetry +
/// precise GPS fields survive the round-trip with matching property names — a rename on either side that
/// breaks the contract would fail here rather than silently dropping a field at ingest.
/// </summary>
public class MachineInfoDtoRoundTripTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Reporter_record_round_trips_into_the_server_dto_with_all_telemetry_fields()
    {
        var info = new MachineInfo(
            LocalIp: "192.168.1.20", Os: "Microsoft Windows 11", Arch: "X64",
            OsUser: "alice", CpuCount: 16, Agent: "desktop")
        {
            CpuModel = "AMD Ryzen 9 5900X 12-Core Processor",
            LogicalCores = 24,
            PhysicalCores = 12,
            RamTotalMB = 65536,
            GpuModel = "NVIDIA GeForce RTX 3080",
            MachineGuid = "11111111-2222-3333-4444-555555555555",
            Domain = "WORKGROUP",
            Manufacturer = "Dell Inc.",
            Model = "XPS 15 9520",
            Culture = "en-US",
            TimeZoneId = "Pacific Standard Time",
            UptimeSec = 123456,
            LanIps = "192.168.1.20,10.0.0.5",
            FrameworkVersion = ".NET 9.0.0",
        };

        var json = JsonSerializer.Serialize(info, Web);
        var dto = JsonSerializer.Deserialize<MachineInfoDto>(json, Web)!;

        dto.LocalIp.Should().Be("192.168.1.20");
        dto.Os.Should().Be("Microsoft Windows 11");
        dto.Arch.Should().Be("X64");
        dto.OsUser.Should().Be("alice");
        dto.CpuCount.Should().Be(16);
        dto.Agent.Should().Be("desktop");
        dto.CpuModel.Should().Be("AMD Ryzen 9 5900X 12-Core Processor");
        dto.LogicalCores.Should().Be(24);
        dto.PhysicalCores.Should().Be(12);
        dto.RamTotalMB.Should().Be(65536);
        dto.GpuModel.Should().Be("NVIDIA GeForce RTX 3080");
        dto.MachineGuid.Should().Be("11111111-2222-3333-4444-555555555555");
        dto.Domain.Should().Be("WORKGROUP");
        dto.Manufacturer.Should().Be("Dell Inc.");
        dto.Model.Should().Be("XPS 15 9520");
        dto.Culture.Should().Be("en-US");
        dto.TimeZoneId.Should().Be("Pacific Standard Time");
        dto.UptimeSec.Should().Be(123456);
        dto.LanIps.Should().Be("192.168.1.20,10.0.0.5");
        dto.FrameworkVersion.Should().Be(".NET 9.0.0");

        // No GPS attached → the location half stays null and the server will fall back to IP-geo.
        dto.Lat.Should().BeNull();
        dto.Lng.Should().BeNull();
        dto.AccuracyM.Should().BeNull();
        dto.GeoSource.Should().BeNull();
    }

    [Fact]
    public void WithGps_attaches_a_precise_agent_fix_that_round_trips()
    {
        var info = new MachineInfo("192.168.1.20", "Windows 11", "X64", "alice", 16, "desktop")
            .WithGps(47.6062, -122.3321, accuracyM: 12.5);

        info.GeoSource.Should().Be("agent");

        var dto = JsonSerializer.Deserialize<MachineInfoDto>(JsonSerializer.Serialize(info, Web), Web)!;
        dto.Lat.Should().BeApproximately(47.6062, 0.0001);
        dto.Lng.Should().BeApproximately(-122.3321, 0.0001);
        dto.AccuracyM.Should().BeApproximately(12.5, 0.01);
        dto.GeoSource.Should().Be("agent");
    }

    [Fact]
    public void Collect_is_best_effort_and_never_throws()
    {
        var act = () => MachineInfo.Collect("console");
        var info = act.Should().NotThrow().Subject;
        info.Agent.Should().Be("console");
        // No precise fix is gathered by Collect (that is the Agent's WithGps job).
        info.GeoSource.Should().BeNull();
    }
}
