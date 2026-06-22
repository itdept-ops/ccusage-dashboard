using Ccusage.Api.Auth;
using Ccusage.Api.Data;
using Ccusage.Api.Dtos;
using Ccusage.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Ccusage.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/api/auth");

        // Public: the SPA fetches the client id to initialize Google Identity Services.
        auth.MapGet("/config", (IConfiguration cfg) =>
                Results.Ok(new AuthConfigDto { GoogleClientId = cfg["Google:ClientId"] ?? "" }))
            .AllowAnonymous();

        // Public: exchange a Google ID token for an app JWT (allowlist enforced server-side).
        auth.MapPost("/google", async (GoogleLoginRequest req, GoogleAuthService svc, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.IdToken))
                return Results.BadRequest(new { message = "idToken is required" });

            var result = await svc.SignInAsync(req.IdToken, ct);
            return result.Status switch
            {
                SignInStatus.Ok => Results.Ok(result.Auth),
                SignInStatus.Forbidden => Results.Json(
                    new { message = $"{result.Email} is not authorized to access Usage IQ.", email = result.Email },
                    statusCode: StatusCodes.Status403Forbidden),
                _ => Results.Unauthorized(),
            };
        }).AllowAnonymous().RequireRateLimiting("auth");

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
                IsEnabled = u.IsEnabled,
                Permissions = u.Permissions.ToArray(),
                HomeRoute = u.HomeRoute,
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
    }
}
