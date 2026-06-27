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
        ["/family/identity"] = new[] { Permissions.IdentityMap },
        ["/locations"] = new[] { Permissions.LocationSelf },
        ["/users"] = new[] { Permissions.UsersView },
        ["/activity"] = new[] { Permissions.ActivityView },
        // Tracker / Tools / Social pages the home picker also offers — must be allow-listed here too, else a
        // PATCH /api/auth/home for them was rejected (and the SPA's canAccessHome rejected them), so choosing
        // any of these as your landing page silently failed. Each mirrors its route guard.
        ["/ask"] = new[] { Permissions.TrackerAi },
        ["/challenge"] = new[] { Permissions.TrackerSelf },
        ["/trophies"] = new[] { Permissions.TrackerSelf },
        ["/feed"] = new[] { Permissions.TrackerSelf },
        ["/automations"] = new[] { Permissions.AutomationsUse },
        ["/bills"] = new[] { Permissions.BillsUse },
        ["/grocery"] = new[] { Permissions.GroceryUse },
        ["/recipes"] = new[] { Permissions.RecipesUse },
        ["/meal-planner"] = new[] { Permissions.MealsUse },
        ["/people"] = new[] { Permissions.ChatRead, Permissions.FamilyUse },

        // Mobile surfaces (the mobile-first redesigns) — mirror the route guards in app.routes.ts so a mobile
        // page can be set as the landing page. The /beta section + tracker-beta are gated by the mobile-platform
        // permission (platform.mobile). (Routes that ALSO require a feature perm, e.g. /beta/family needs
        // family.use, are filtered in the SPA picker; platform.mobile is the section gate here.)
        ["/tracker-beta"] = new[] { Permissions.PlatformMobile },
        ["/beta"] = new[] { Permissions.PlatformMobile },
        ["/beta/home"] = new[] { Permissions.PlatformMobile },
        ["/beta/dashboard"] = new[] { Permissions.PlatformMobile },
        ["/beta/family"] = new[] { Permissions.PlatformMobile },
        ["/beta/bills"] = new[] { Permissions.PlatformMobile },
        ["/beta/wrapped"] = new[] { Permissions.PlatformMobile },
        ["/beta/settings"] = new[] { Permissions.PlatformMobile },
        ["/beta/chat"] = new[] { Permissions.PlatformMobile },
        ["/beta/ask"] = new[] { Permissions.PlatformMobile },
        ["/beta/meals"] = new[] { Permissions.PlatformMobile },
        ["/beta/people"] = new[] { Permissions.PlatformMobile },
        ["/beta/fleet"] = new[] { Permissions.PlatformMobile },
        ["/beta/trophies"] = new[] { Permissions.PlatformMobile },
        ["/beta/automations"] = new[] { Permissions.PlatformMobile },
    };

    public static bool IsKnown(string route) => Map.ContainsKey(route);

    /// <summary>Whether the caller (by their permission set) may land on <paramref name="route"/>.</summary>
    public static bool CanAccess(string route, IReadOnlySet<string> permissions) =>
        Map.TryGetValue(route, out var required) && required.Any(permissions.Contains);
}
