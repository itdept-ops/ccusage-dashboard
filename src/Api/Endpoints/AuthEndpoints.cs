using Ccusage.Api.Auth;
using Ccusage.Api.Data;
using Ccusage.Api.Dtos;
using Ccusage.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Ccusage.Api.Endpoints;

public static class AuthEndpoints
{
    /// <summary>Name of the HttpOnly cookie that carries the app JWT for browser clients.</summary>
    public const string JwtCookieName = "usage_iq_jwt";

    /// <summary>
    /// Options for the JWT cookie: HttpOnly (unreadable by JS, so an XSS foothold can't steal the token),
    /// SameSite=Lax (not sent on cross-site sub-requests, which blocks CSRF on state-changing calls),
    /// Secure only when the request is https (so plain-http local dev still works), path '/', expiring with
    /// the token. The same options (minus Expires) are used to Delete the cookie on logout.
    /// </summary>
    private static CookieOptions JwtCookieOptions(HttpContext http, DateTime? expiresUtc)
    {
        // Secure everywhere EXCEPT local http dev. Deriving Secure purely from Request.IsHttps is unsafe
        // behind the prod proxy chain: nginx/Kestrel see the request over internal http, so IsHttps is false
        // and the cookie would be set WITHOUT Secure even though the browser reached us over https. Force
        // Secure in any non-Development environment (the browser hop is always https there via Caddy).
        var env = http.RequestServices.GetRequiredService<IWebHostEnvironment>();
        return new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps || !env.IsDevelopment(),
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = expiresUtc is { } e ? new DateTimeOffset(DateTime.SpecifyKind(e, DateTimeKind.Utc)) : null,
        };
    }

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/api/auth");

        // Public: the SPA fetches the client id to initialize Google Identity Services.
        auth.MapGet("/config", (IConfiguration cfg) =>
                Results.Ok(new AuthConfigDto { GoogleClientId = cfg["Google:ClientId"] ?? "" }))
            .AllowAnonymous();

        // Public: exchange a Google ID token for an app JWT (allowlist enforced server-side).
        auth.MapPost("/google", async (GoogleLoginRequest req, GoogleAuthService svc, HttpContext http, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.IdToken))
                return Results.BadRequest(new { message = "idToken is required" });

            var result = await svc.SignInAsync(req.IdToken, ct);
            if (result.Status == SignInStatus.Ok && result.Auth is not null)
            {
                // Set the JWT as an HttpOnly cookie so browser clients never hold it in JS (closes the XSS
                // token-theft path). The token is ALSO still returned in the body for backward-compat and
                // non-browser clients, but the SPA now authenticates via this cookie and does not store it.
                http.Response.Cookies.Append(JwtCookieName, result.Auth.Token, JwtCookieOptions(http, result.Auth.ExpiresAtUtc));
            }
            return result.Status switch
            {
                SignInStatus.Ok => Results.Ok(result.Auth),
                SignInStatus.Forbidden => Results.Json(
                    new { message = $"{result.Email} is not authorized to access Usage IQ.", email = result.Email },
                    statusCode: StatusCodes.Status403Forbidden),
                _ => Results.Unauthorized(),
            };
        }).AllowAnonymous().RequireRateLimiting("auth");

        // Clear the HttpOnly auth cookie (only the server can, by design). Anonymous-ok so a caller whose
        // token already expired can still fully sign out. The SPA calls this on logout.
        auth.MapPost("/logout", (HttpContext http) =>
        {
            http.Response.Cookies.Delete(JwtCookieName, JwtCookieOptions(http, null));
            return Results.Ok(new { ok = true });
        }).AllowAnonymous();

        // Authorized: current user + live permissions (re-read from the DB).
        auth.MapGet("/me", async (CurrentUserAccessor accessor, CancellationToken ct) =>
        {
            var u = await accessor.GetUserAsync(ct);
            if (u is null || !u.IsEnabled)
                return Results.Json(new { message = "Your account is not provisioned or has been disabled." },
                    statusCode: StatusCodes.Status403Forbidden);

            return Results.Ok(new MeDto
            {
                UserId = u.Id,
                Email = u.Email,
                Name = u.Name,
                Picture = u.Picture,
                IsEnabled = u.IsEnabled,
                Permissions = u.Permissions.ToArray(),
                HomeRoute = u.HomeRoute,
                DisplayNameMode = DisplayName.ModeToWire(u.DisplayNameMode),
                Nickname = u.Nickname,
                AppearOffline = u.AppearOffline,
                PresenceStatus = u.PresenceStatus,
                ShareAutoContext = u.ShareAutoContext,
                ShareActivity = u.ShareActivity,
                ViewActivityFeed = u.ViewActivityFeed,
                NudgesOptOut = u.NudgesOptOut,
            });
        }).RequireAuthorization();

        // Self-service: update the CALLER's OWN display/presence preferences (how THEY appear to everyone,
        // their appear-offline toggle, their status + auto-context opt-in). Authentication only — never
        // users.manage; a user can only ever change their own row. Partial: only non-null fields apply.
        // Nickname/status are sanitized server-side (never an email). Returns the fresh effective values.
        auth.MapPatch("/profile", async (SetProfileRequest req, CurrentUserAccessor accessor, UsageDbContext db, CancellationToken ct) =>
        {
            var caller = await accessor.GetUserAsync(ct);
            if (caller is null || !caller.IsEnabled)
                return Results.Json(new { message = "Your account is not provisioned or has been disabled." },
                    statusCode: StatusCodes.Status403Forbidden);

            var user = await db.Users.FirstOrDefaultAsync(x => x.Id == caller.Id, ct);
            if (user is null)
                return Results.Json(new { message = "Your account is not provisioned or has been disabled." },
                    statusCode: StatusCodes.Status403Forbidden);

            if (req.DisplayNameMode is not null)
            {
                if (!DisplayName.TryParseMode(req.DisplayNameMode, out var mode))
                    return Results.BadRequest(new { message = $"'{req.DisplayNameMode}' is not a valid display-name mode." });
                user.DisplayNameMode = mode;
            }

            // Empty string clears; any other value is sanitized (control chars / '@' / length capped).
            if (req.Nickname is not null)
                user.Nickname = DisplayName.SanitizeNickname(req.Nickname);
            if (req.PresenceStatus is not null)
                user.PresenceStatus = DisplayName.SanitizeStatus(req.PresenceStatus);
            if (req.AppearOffline is { } off) user.AppearOffline = off;
            if (req.ShareAutoContext is { } share) user.ShareAutoContext = share;
            if (req.ShareActivity is { } shareAct) user.ShareActivity = shareAct;
            if (req.ViewActivityFeed is { } viewFeed) user.ViewActivityFeed = viewFeed;
            if (req.NudgesOptOut is { } nudgeOut) user.NudgesOptOut = nudgeOut;

            await db.SaveChangesAsync(ct);

            return Results.Ok(new
            {
                displayNameMode = DisplayName.ModeToWire(user.DisplayNameMode),
                nickname = user.Nickname,
                appearOffline = user.AppearOffline,
                presenceStatus = user.PresenceStatus,
                shareAutoContext = user.ShareAutoContext,
                shareActivity = user.ShareActivity,
                viewActivityFeed = user.ViewActivityFeed,
                nudgesOptOut = user.NudgesOptOut,
            });
        }).RequireAuthorization();

        // Self-service: set (or clear) the CALLER's own landing page. Gated by authentication only —
        // every signed-in user may set their OWN home (never users.manage). The route must be null (clear)
        // or one of the known page routes AND one the caller currently has permission to reach, so a user
        // can never persist a home they cannot access.
        auth.MapPatch("/home", async (SetHomeRequest req, CurrentUserAccessor accessor, UsageDbContext db, CancellationToken ct) =>
        {
            var caller = await accessor.GetUserAsync(ct);
            if (caller is null || !caller.IsEnabled)
                return Results.Json(new { message = "Your account is not provisioned or has been disabled." },
                    statusCode: StatusCodes.Status403Forbidden);

            var route = string.IsNullOrWhiteSpace(req.Route) ? null : req.Route.Trim();

            if (route is not null)
            {
                if (!HomeRoutes.IsKnown(route))
                    return Results.BadRequest(new { message = $"'{route}' is not a valid home route." });
                if (!HomeRoutes.CanAccess(route, caller.Permissions))
                    return Results.BadRequest(new { message = "You do not have access to that page." });
            }

            // Re-load the tracked row (the accessor reads AsNoTracking) and persist the caller's own home.
            var user = await db.Users.FirstOrDefaultAsync(x => x.Id == caller.Id, ct);
            if (user is null)
                return Results.Json(new { message = "Your account is not provisioned or has been disabled." },
                    statusCode: StatusCodes.Status403Forbidden);

            user.HomeRoute = route;
            await db.SaveChangesAsync(ct);

            return Results.Ok(new { homeRoute = route });
        }).RequireAuthorization();

        // Self-service: stamp best-effort web client info (device/agent characteristics — NO precise
        // location, no PII) onto the caller's MOST-RECENT successful login event. Authentication only; the
        // caller can only ever touch their OWN login rows (matched by their JWT email). BEST-EFFORT: any
        // failure is swallowed (a 200 is still returned) so this can never break the post-login flow. Each
        // field is optional + sanitized/clamped; an absent field leaves the stored value unchanged.
        auth.MapPost("/client-info", async (ClientInfoRequest req, CurrentUserAccessor accessor, UsageDbContext db, CancellationToken ct) =>
        {
            try
            {
                var caller = await accessor.GetUserAsync(ct);
                if (caller is null || !caller.IsEnabled)
                    return Results.Ok(new { ok = false });

                // The login this client-info belongs to: the caller's newest SUCCESSFUL event (the one the
                // current session was issued for). Bounded to the last few minutes so a late/duplicate POST
                // never back-fills an unrelated historical row.
                var cutoff = DateTime.UtcNow.AddMinutes(-10);
                var ev = await db.LoginEvents
                    .Where(e => e.Email == caller.Email && e.Success && e.WhenUtc >= cutoff)
                    .OrderByDescending(e => e.WhenUtc).ThenByDescending(e => e.Id)
                    .FirstOrDefaultAsync(ct);
                if (ev is null) return Results.Ok(new { ok = false });

                if (req.Platform is not null) ev.Platform = Trunc(req.Platform, 64);
                if (req.ScreenWidth is int sw) ev.ScreenWidth = Math.Clamp(sw, 0, 100_000);
                if (req.ScreenHeight is int sh) ev.ScreenHeight = Math.Clamp(sh, 0, 100_000);
                if (req.DevicePixelRatio is double dpr && double.IsFinite(dpr))
                    ev.DevicePixelRatio = Math.Clamp(dpr, 0, 100);
                if (req.Languages is not null) ev.Languages = Trunc(req.Languages, 128);
                if (req.TimeZone is not null) ev.TimeZone = Trunc(req.TimeZone, 64);
                if (req.HardwareConcurrency is int hc) ev.HardwareConcurrency = Math.Clamp(hc, 0, 4096);
                if (req.DeviceMemory is double dm && double.IsFinite(dm))
                    ev.DeviceMemory = Math.Clamp(dm, 0, 4096);
                if (req.TouchPoints is int tp) ev.TouchPoints = Math.Clamp(tp, 0, 256);
                if (req.ColorDepth is int cd) ev.ColorDepth = Math.Clamp(cd, 0, 256);

                await db.SaveChangesAsync(ct);
                return Results.Ok(new { ok = true });
            }
            catch
            {
                // Client info is a best-effort nicety — never let it surface an error to the SPA.
                return Results.Ok(new { ok = false });
            }
        }).RequireAuthorization();
    }

    /// <summary>Trim + collapse a client string to a max length; returns null for blank input.</summary>
    private static string? Trunc(string? s, int max)
    {
        var t = (s ?? "").Trim();
        if (t.Length == 0) return null;
        return t.Length > max ? t[..max] : t;
    }
}
