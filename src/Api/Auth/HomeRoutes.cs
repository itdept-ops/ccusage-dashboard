namespace Ccusage.Api.Auth;

/// <summary>
/// The set of page routes a user may choose as their landing "home", and the permission each one
/// requires. Mirrors the route guards in the SPA's <c>app.routes.ts</c> EXACTLY — a route may be set
/// as home only when the caller currently holds (one of) the permission(s) its guard checks. This is
/// the single source of truth the self-service <c>PATCH /api/auth/home</c> endpoint validates against,
/// so a user can never persist a home they cannot access.
/// </summary>
public static class HomeRoutes
{
    /// <summary>route -> the permission keys that grant access; the caller needs ANY one of them.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Map = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["/"] = new[] { Permissions.DashboardView },
        ["/calendar"] = new[] { Permissions.CalendarView },
        ["/pricing"] = new[] { Permissions.PricingView },
        ["/settings"] = new[] { Permissions.SettingsView },
        ["/reporter"] = new[] { Permissions.ReporterView, Permissions.ReporterManage, Permissions.ReporterSelf },
        ["/fleet"] = new[] { Permissions.FleetView, Permissions.ReporterManage },
        ["/chat"] = new[] { Permissions.ChatRead },
        ["/tracker"] = new[] { Permissions.TrackerSelf },
        ["/family"] = new[] { Permissions.FamilyUse },
        ["/locations"] = new[] { Permissions.LocationSelf },
        ["/users"] = new[] { Permissions.UsersView },
        ["/activity"] = new[] { Permissions.ActivityView },
    };

    public static bool IsKnown(string route) => Map.ContainsKey(route);

    /// <summary>Whether the caller (by their permission set) may land on <paramref name="route"/>.</summary>
    public static bool CanAccess(string route, IReadOnlySet<string> permissions) =>
        Map.TryGetValue(route, out var required) && required.Any(permissions.Contains);
}
