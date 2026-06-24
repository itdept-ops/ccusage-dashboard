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

    // ---- Richer best-effort hardware/OS telemetry (all client-reported; every field is nullable and is
    // null when the client could not probe it — a missing probe never blocks the ingest). Gathered once per
    // process by the reporter's MachineInfo.Collect (WMI / registry / Environment, Windows-guarded).
    /// <summary>CPU brand string, e.g. "AMD Ryzen 9 5900X 12-Core Processor" (WMI Win32_Processor.Name).</summary>
    public string? CpuModel { get; set; }
    /// <summary>Logical processor count (= <see cref="CpuCount"/>, kept distinct for clarity).</summary>
    public int? LogicalCores { get; set; }
    /// <summary>Physical core count (WMI NumberOfCores summed across sockets); null off Windows.</summary>
    public int? PhysicalCores { get; set; }
    /// <summary>Total physical RAM in megabytes (WMI Win32_ComputerSystem.TotalPhysicalMemory / 1MiB).</summary>
    public long? RamTotalMB { get; set; }
    /// <summary>Primary GPU name (WMI Win32_VideoController.Name; first non-virtual adapter).</summary>
    public string? GpuModel { get; set; }
    /// <summary>The stable per-install Windows MachineGuid (registry HKLM\SOFTWARE\Microsoft\Cryptography).</summary>
    public string? MachineGuid { get; set; }
    /// <summary>AD/Workgroup domain the machine is joined to (WMI Win32_ComputerSystem.Domain).</summary>
    public string? Domain { get; set; }
    /// <summary>System manufacturer, e.g. "Dell Inc." (WMI Win32_ComputerSystem.Manufacturer).</summary>
    public string? Manufacturer { get; set; }
    /// <summary>System model, e.g. "XPS 15 9520" (WMI Win32_ComputerSystem.Model).</summary>
    public string? Model { get; set; }
    /// <summary>Current UI culture / locale, e.g. "en-US" (CultureInfo.CurrentCulture.Name).</summary>
    public string? Culture { get; set; }
    /// <summary>IANA-ish time zone id of the reporting machine (TimeZoneInfo.Local.Id).</summary>
    public string? TimeZoneId { get; set; }
    /// <summary>Seconds since the machine last booted (Environment.TickCount64 / 1000) at report time.</summary>
    public long? UptimeSec { get; set; }
    /// <summary>All LAN IPv4 addresses (comma-joined) the client enumerated; null when none/blocked.</summary>
    public string? LanIps { get; set; }
    /// <summary>.NET runtime version the reporter runs on (RuntimeInformation.FrameworkDescription).</summary>
    public string? FrameworkVersion { get; set; }

    // ---- Location of the machine. Two sources, in priority order:
    //   "agent"  — a PRECISE GPS fix from Windows.Devices.Geolocation, captured client-side with the user's
    //              consent; Lat/Lng are exact and AccuracyM is the reported radius. The IP-geo backfill must
    //              NOT overwrite an agent fix (see GeoSource gate in MachineGeoBackfillService).
    //   "ip-api" — the coarse city/lat-lng of the server-observed PublicIp, resolved off-path & cached;
    //              the fallback whenever no precise fix was sent (denial / non-Windows / older OS).
    // City/Region/Country + Lat/Lng + GeoUpdatedUtc were pre-existing (IP-geo only). GeoSource + AccuracyM
    // are new and distinguish a precise agent fix from a coarse IP estimate.
    /// <summary>Coarse city for the resolved location (IP-geo or agent reverse-geo; null until resolved).</summary>
    public string? City { get; set; }
    public string? Region { get; set; }
    public string? Country { get; set; }
    public double? Lat { get; set; }
    public double? Lng { get; set; }

    /// <summary>GPS accuracy radius in metres for a precise <c>agent</c> fix; null for an IP-geo estimate.</summary>
    public double? AccuracyM { get; set; }

    /// <summary>How <see cref="Lat"/>/<see cref="Lng"/> were resolved: <c>agent</c> (precise GPS) or
    /// <c>ip-api</c> (coarse IP-geo). Null when no location is known. Gates the IP-geo backfill: an
    /// <c>agent</c> row is never downgraded to a coarse IP estimate.</summary>
    public string? GeoSource { get; set; }

    /// <summary>When the location for this machine was last resolved; null when never done.</summary>
    public DateTime? GeoUpdatedUtc { get; set; }
}
