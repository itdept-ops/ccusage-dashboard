using Ccusage.Api.Ingestion;

namespace Ccusage.Api.Dtos;

/// <summary>
/// A batch pushed by a remote reporter to <c>POST /api/ingest</c>. Rows are the source-neutral
/// <see cref="ParsedUsage"/> the reporter parsed locally — no raw transcript text leaves the machine.
/// </summary>
public sealed class IngestBatchDto
{
    /// <summary>Parser kind that produced these rows: <c>claude</c> or <c>codex</c>.</summary>
    public string Source { get; set; } = "";

    /// <summary>Optional machine/host identifier (groups rows under a synthetic remote "file").</summary>
    public string? Machine { get; set; }

    /// <summary>Optional reporter version string (informational).</summary>
    public string? Reporter { get; set; }

    /// <summary>Optional system metadata for the reporting machine (LAN IP, OS, arch, etc.).</summary>
    public MachineInfoDto? MachineInfo { get; set; }

    public List<ParsedUsage> Rows { get; set; } = new();
}

/// <summary>
/// Client-reported system metadata for the reporting machine, carried on the ingest batch. The server
/// stores these informationally; it never trusts a client-sent public IP (that is observed server-side).
/// </summary>
public sealed class MachineInfoDto
{
    /// <summary>Primary LAN IPv4 the client detected (e.g. 192.168.1.20).</summary>
    public string? LocalIp { get; set; }

    /// <summary><c>RuntimeInformation.OSDescription</c>.</summary>
    public string? Os { get; set; }

    /// <summary><c>RuntimeInformation.OSArchitecture</c> (e.g. "X64").</summary>
    public string? Arch { get; set; }

    /// <summary><c>Environment.UserName</c>.</summary>
    public string? OsUser { get; set; }

    /// <summary><c>Environment.ProcessorCount</c>.</summary>
    public int? CpuCount { get; set; }

    /// <summary>Which client posted: "desktop" (WPF tray) or "console" (CLI reporter).</summary>
    public string? Agent { get; set; }

    // ---- Richer best-effort hardware/OS telemetry (all nullable, null when the client could not probe it). ----
    /// <summary>CPU brand string (WMI Win32_Processor.Name).</summary>
    public string? CpuModel { get; set; }
    /// <summary>Logical processor count.</summary>
    public int? LogicalCores { get; set; }
    /// <summary>Physical core count (Windows-only; null elsewhere).</summary>
    public int? PhysicalCores { get; set; }
    /// <summary>Total physical RAM in megabytes.</summary>
    public long? RamTotalMB { get; set; }
    /// <summary>Primary GPU name.</summary>
    public string? GpuModel { get; set; }
    /// <summary>Stable per-install Windows MachineGuid (registry).</summary>
    public string? MachineGuid { get; set; }
    /// <summary>AD/Workgroup domain the machine is joined to.</summary>
    public string? Domain { get; set; }
    /// <summary>System manufacturer (WMI).</summary>
    public string? Manufacturer { get; set; }
    /// <summary>System model (WMI).</summary>
    public string? Model { get; set; }
    /// <summary>Current culture / locale, e.g. "en-US".</summary>
    public string? Culture { get; set; }
    /// <summary>Local time zone id.</summary>
    public string? TimeZoneId { get; set; }
    /// <summary>Seconds since last boot.</summary>
    public long? UptimeSec { get; set; }
    /// <summary>All LAN IPv4 addresses, comma-joined.</summary>
    public string? LanIps { get; set; }
    /// <summary>.NET runtime version the reporter runs on.</summary>
    public string? FrameworkVersion { get; set; }

    // ---- Precise GPS fix (agent only). When present and Source == "agent", the server stores these as a
    // precise location that the coarse IP-geo backfill will NOT overwrite. Null on denial / non-Windows /
    // older OS — the server then falls back to IP-geo of the observed public IP. ----
    /// <summary>Precise latitude from Windows.Devices.Geolocation; null when no GPS fix was obtained.</summary>
    public double? Lat { get; set; }
    /// <summary>Precise longitude; null when no GPS fix was obtained.</summary>
    public double? Lng { get; set; }
    /// <summary>GPS accuracy radius in metres for the precise fix; null without one.</summary>
    public double? AccuracyM { get; set; }
    /// <summary>Location source the client is asserting: "agent" for a precise GPS fix. Null/absent → IP-geo fallback.</summary>
    public string? GeoSource { get; set; }
}

/// <summary>Outcome of an ingest batch. Received == Inserted + Duplicates + Skipped.</summary>
public sealed class IngestResultDto
{
    public int Received { get; set; }
    public int Inserted { get; set; }
    /// <summary>Combined token count (all tiers) of the rows actually inserted.</summary>
    public long InsertedTokens { get; set; }
    /// <summary>Rows whose key already existed in the DB.</summary>
    public int Duplicates { get; set; }
    /// <summary>Rows dropped before/at insert: malformed, collapsed within-batch, or DB-rejected.</summary>
    public int Skipped { get; set; }
    public string[] UnpricedModels { get; set; } = Array.Empty<string>();
}

/// <summary>Admin-facing view of an ingest key (never includes the raw key).</summary>
public sealed class IngestKeyDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Prefix { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    /// <summary>The creator resolved to their AppUser id, or null when the stored creator email has no
    /// AppUser row. The raw creator email is NEVER exposed (email-privacy).</summary>
    public int? CreatedByUserId { get; set; }
    /// <summary>The creator's display name (the matching AppUser.Name, "Unknown user" when unresolved).</summary>
    public string CreatedByName { get; set; } = "";
    /// <summary>The owning user's AppUser id; null for orphaned legacy keys (no linked user).</summary>
    public int? OwnerUserId { get; set; }
    /// <summary>The owning user's display name; null for orphaned legacy keys (no linked user).</summary>
    public string? OwnerName { get; set; }
    public DateTime? LastUsedUtc { get; set; }
    public string? LastUsedIp { get; set; }
    public bool Revoked { get; set; }
}

/// <summary>The one-time response when a key is created — carries the raw key.</summary>
public sealed class IngestKeyCreatedDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Prefix { get; set; } = "";
    /// <summary>The full raw key — shown once and never retrievable again.</summary>
    public string Key { get; set; } = "";
}

public sealed class CreateIngestKeyRequest
{
    public string? Name { get; set; }
}
