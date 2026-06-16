using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Ccusage.Reporter.Core;

/// <summary>
/// Machine metadata sent with every ingest batch so the Fleet page can show each machine's LAN IP and
/// system info. Field names map (via <c>JsonSerializerDefaults.Web</c>) to the shared camelCase wire
/// contract: <c>localIp</c>, <c>os</c>, <c>arch</c>, <c>osUser</c>, <c>cpuCount</c>, <c>agent</c>.
///
/// <para>The public IP is deliberately NOT collected here: the server records the client IP it observes
/// (never trusting a client-sent value), so the reporter only reports what it can see locally.</para>
///
/// <para>The <c>agent</c> kind is supplied by the caller (the console reporter passes "console", the WPF
/// tray app passes "desktop") rather than auto-detected.</para>
/// </summary>
public sealed record MachineInfo(
    string? LocalIp,
    string? Os,
    string? Arch,
    string? OsUser,
    int? CpuCount,
    string? Agent)
{
    /// <summary>
    /// Gather the local machine metadata once. The <paramref name="agent"/> kind is provided by the
    /// caller (e.g. "console" or "desktop"). Every probe is best-effort — any failure degrades to null
    /// for that single field rather than throwing.
    /// </summary>
    public static MachineInfo Collect(string agent) => new(
        LocalIp: TryGetLocalIpv4(),
        Os: Try(() => RuntimeInformation.OSDescription),
        Arch: Try(() => RuntimeInformation.OSArchitecture.ToString()),
        OsUser: Try(() => Environment.UserName),
        CpuCount: Try(() => (int?)Environment.ProcessorCount),
        Agent: string.IsNullOrWhiteSpace(agent) ? null : agent.Trim());

    /// <summary>
    /// The primary LAN IPv4: the first unicast IPv4 address on the first network interface that is Up,
    /// not a loopback and not a tunnel. Returns null if none is found.
    /// </summary>
    private static string? TryGetLocalIpv4()
    {
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
                    return addr.Address.ToString();
                }
            }
        }
        catch { /* enumeration not permitted / no NICs — fall through to null */ }

        return null;
    }

    private static T? Try<T>(Func<T?> f)
    {
        try { return f(); } catch { return default; }
    }
}
