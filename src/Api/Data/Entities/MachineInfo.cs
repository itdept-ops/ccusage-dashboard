namespace Ccusage.Api.Data.Entities;

/// <summary>
/// System metadata for one reporting machine, keyed by the machine name (<see cref="UsageRecord.MachineName"/>).
/// One row per machine, upserted on every ingest from that machine. Everything except the server-observed
/// <see cref="PublicIp"/> comes from the client's <c>machineInfo</c> payload; the public IP is recorded
/// server-side (the same forwarded client address the ingest filter stamps) and never trusted from the client.
/// </summary>
public class MachineInfo
{
    public int Id { get; set; }

    /// <summary>The machine name this metadata describes (matches <see cref="UsageRecord.MachineName"/>). Unique.</summary>
    public string Name { get; set; } = "";

    /// <summary>Primary LAN IPv4 the client detected (client-reported; informational).</summary>
    public string? LocalIp { get; set; }

    /// <summary>The public IP the server observed for the ingest request — never the client payload.</summary>
    public string? PublicIp { get; set; }

    /// <summary><c>RuntimeInformation.OSDescription</c> as reported by the client.</summary>
    public string? Os { get; set; }

    /// <summary><c>RuntimeInformation.OSArchitecture</c> (e.g. "X64").</summary>
    public string? Arch { get; set; }

    /// <summary>Hostname; mirrors the batch's <c>machine</c> value.</summary>
    public string? Hostname { get; set; }

    /// <summary><c>Environment.UserName</c> on the reporting machine.</summary>
    public string? OsUser { get; set; }

    /// <summary>Which client posted: "desktop" (WPF tray) or "console" (CLI reporter).</summary>
    public string? Agent { get; set; }

    /// <summary>The reporter version string from the batch (<c>reporter</c>).</summary>
    public string? ReporterVersion { get; set; }

    /// <summary><c>Environment.ProcessorCount</c> on the reporting machine.</summary>
    public int? CpuCount { get; set; }

    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
}
