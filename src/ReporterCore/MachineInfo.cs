using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ccusage.Reporter.Core;

/// <summary>
/// Machine metadata sent with every ingest batch so the Fleet page can show each machine's LAN IP, hardware
/// and (optionally) precise location. Field names map (via <c>JsonSerializerDefaults.Web</c>) to the shared
/// camelCase wire contract consumed by <c>MachineInfoDto</c> server-side.
///
/// <para>The public IP is deliberately NOT collected here: the server records the client IP it observes
/// (never trusting a client-sent value), so the reporter only reports what it can see locally.</para>
///
/// <para>Every probe is best-effort — any failure (no WMI, no registry access, a non-Windows host, a missing
/// NIC) degrades to <c>null</c> for that single field rather than throwing. Hardware probes that require
/// Windows (WMI / registry) are guarded by <see cref="OperatingSystem.IsWindows"/> and simply yield null on
/// other platforms, so the same record builds and runs cross-platform.</para>
///
/// <para>Precise GPS (<see cref="Lat"/>/<see cref="Lng"/>/<see cref="AccuracyM"/>/<see cref="GeoSource"/>) is
/// NOT gathered here — it needs <c>Windows.Devices.Geolocation</c> + the user's consent, which lives in the
/// WPF Agent project. The Agent obtains a fix and calls <see cref="WithGps"/> to attach it; when absent the
/// server falls back to coarse IP-geo of the observed public IP.</para>
/// </summary>
public sealed record MachineInfo(
    string? LocalIp,
    string? Os,
    string? Arch,
    string? OsUser,
    int? CpuCount,
    string? Agent)
{
    // ---- richer best-effort telemetry (all nullable; null on any probe failure) ----
    public string? CpuModel { get; init; }
    public int? LogicalCores { get; init; }
    public int? PhysicalCores { get; init; }
    public long? RamTotalMB { get; init; }
    public string? GpuModel { get; init; }
    public string? MachineGuid { get; init; }
    public string? Domain { get; init; }
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string? Culture { get; init; }
    public string? TimeZoneId { get; init; }
    public long? UptimeSec { get; init; }
    public string? LanIps { get; init; }
    public string? FrameworkVersion { get; init; }

    // ---- precise GPS fix (attached by the Agent via WithGps; null otherwise → server uses IP-geo) ----
    public double? Lat { get; init; }
    public double? Lng { get; init; }
    public double? AccuracyM { get; init; }
    public string? GeoSource { get; init; }

    /// <summary>
    /// Gather the local machine metadata once. The <paramref name="agent"/> kind is provided by the
    /// caller (e.g. "console" or "desktop"). Every probe is best-effort — any failure degrades to null
    /// for that single field rather than throwing. Does NOT gather precise GPS (see <see cref="WithGps"/>).
    /// </summary>
    public static MachineInfo Collect(string agent)
    {
        var lanIps = TryGetAllLocalIpv4();
        var (cpuModel, ramMb, gpu, physCores, manufacturer, model, domain) = TryGetWmiHardware();

        return new MachineInfo(
            LocalIp: lanIps.FirstOrDefault(),
            Os: Try(() => RuntimeInformation.OSDescription),
            Arch: Try(() => RuntimeInformation.OSArchitecture.ToString()),
            OsUser: Try(() => Environment.UserName),
            CpuCount: Try(() => (int?)Environment.ProcessorCount),
            Agent: string.IsNullOrWhiteSpace(agent) ? null : agent.Trim())
        {
            CpuModel = cpuModel,
            LogicalCores = Try(() => (int?)Environment.ProcessorCount),
            PhysicalCores = physCores,
            RamTotalMB = ramMb,
            GpuModel = gpu,
            MachineGuid = TryGetMachineGuid(),
            Domain = domain,
            Manufacturer = manufacturer,
            Model = model,
            Culture = Try(() => CultureInfo.CurrentCulture.Name) is { Length: > 0 } c ? c : null,
            TimeZoneId = Try(() => TimeZoneInfo.Local.Id),
            UptimeSec = Try(() => (long?)(Environment.TickCount64 / 1000)),
            LanIps = lanIps.Count > 0 ? string.Join(",", lanIps) : null,
            FrameworkVersion = Try(() => RuntimeInformation.FrameworkDescription),
        };
    }

    /// <summary>
    /// Return a copy carrying a precise GPS fix (<c>GeoSource = "agent"</c>). Called by the WPF Agent after
    /// it obtains a consented <c>Windows.Devices.Geolocation</c> fix. On denial/failure the Agent simply
    /// skips this call and the server falls back to coarse IP-geo of the observed public IP.
    /// </summary>
    public MachineInfo WithGps(double lat, double lng, double? accuracyM) => this with
    {
        Lat = lat,
        Lng = lng,
        AccuracyM = accuracyM,
        GeoSource = "agent",
    };

    /// <summary>
    /// The primary LAN IPv4: the first unicast IPv4 address on the first network interface that is Up,
    /// not a loopback and not a tunnel. Returns null if none is found.
    /// </summary>
    private static List<string> TryGetAllLocalIpv4()
    {
        var ips = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue; // IPv4 only
                    if (IPAddress.IsLoopback(addr.Address)) continue;
                    var s = addr.Address.ToString();
                    if (!ips.Contains(s)) ips.Add(s);
                }
            }
        }
        catch { /* enumeration not permitted / no NICs — return what we have (possibly empty) */ }

        return ips;
    }

    /// <summary>
    /// The stable per-install Windows MachineGuid from the registry. Null off Windows or when the key is
    /// unreadable. Read via the registry rather than WMI so it works even when WMI is disabled.
    /// </summary>
    private static string? TryGetMachineGuid()
    {
        if (!OperatingSystem.IsWindows()) return null;
        return Try(ReadMachineGuidWindows);
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadMachineGuidWindows()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine
            .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        return key?.GetValue("MachineGuid") as string;
    }

    /// <summary>
    /// Best-effort WMI hardware probe (Windows only). Returns a tuple of nullable fields; every field is
    /// independently null-safe so a single failing query (e.g. a locked-down box with WMI disabled) still
    /// yields the rest. No-op tuple of nulls off Windows.
    /// </summary>
    private static (string? Cpu, long? RamMb, string? Gpu, int? PhysCores, string? Manufacturer, string? Model, string? Domain)
        TryGetWmiHardware()
    {
        if (!OperatingSystem.IsWindows())
            return (null, null, null, null, null, null, null);
        return QueryWmiHardwareWindows();
    }

    [SupportedOSPlatform("windows")]
    private static (string?, long?, string?, int?, string?, string?, string?) QueryWmiHardwareWindows()
    {
        string? cpu = null, gpu = null, manufacturer = null, model = null, domain = null;
        long? ramMb = null;
        int? physCores = null;

        // CPU name + physical cores (summed across sockets).
        cpu = Try(() =>
        {
            using var s = new System.Management.ManagementObjectSearcher(
                "SELECT Name, NumberOfCores FROM Win32_Processor");
            string? name = null;
            var cores = 0;
            foreach (var o in s.Get())
            {
                name ??= (o["Name"] as string)?.Trim();
                if (o["NumberOfCores"] is uint n) cores += (int)n;
                else if (o["NumberOfCores"] is int ni) cores += ni;
            }
            if (cores > 0) physCores = cores;
            return name;
        });

        // RAM total + manufacturer/model/domain.
        Try(() =>
        {
            using var s = new System.Management.ManagementObjectSearcher(
                "SELECT TotalPhysicalMemory, Manufacturer, Model, Domain FROM Win32_ComputerSystem");
            foreach (var o in s.Get())
            {
                if (o["TotalPhysicalMemory"] is ulong bytes) ramMb = (long)(bytes / (1024UL * 1024UL));
                manufacturer ??= (o["Manufacturer"] as string)?.Trim();
                model ??= (o["Model"] as string)?.Trim();
                domain ??= (o["Domain"] as string)?.Trim();
                break;
            }
            return (object?)null;
        });

        // Primary GPU.
        gpu = Try(() =>
        {
            using var s = new System.Management.ManagementObjectSearcher(
                "SELECT Name FROM Win32_VideoController");
            foreach (var o in s.Get())
            {
                var n = (o["Name"] as string)?.Trim();
                if (!string.IsNullOrWhiteSpace(n)) return n;
            }
            return null;
        });

        return (cpu, ramMb, gpu, physCores,
            string.IsNullOrWhiteSpace(manufacturer) ? null : manufacturer,
            string.IsNullOrWhiteSpace(model) ? null : model,
            string.IsNullOrWhiteSpace(domain) ? null : domain);
    }

    private static T? Try<T>(Func<T?> f)
    {
        try { return f(); } catch { return default; }
    }
}
