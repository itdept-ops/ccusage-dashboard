using System.Security.Claims;
using Ccusage.Api.Auth;
using Ccusage.Api.Data.Entities;
using Ccusage.Api.Services;

namespace Ccusage.Api.Infrastructure;

/// <summary>
/// On every <em>authenticated</em> request, stamps the caller as currently online in the in-memory
/// <see cref="PresenceTracker"/>. Reads the JWT's literal "email"/"name"/"picture" claims
/// (Program.cs sets MapInboundClaims=false, so claim types are not remapped to URIs).
///
/// Best-effort: it never throws and never short-circuits the request — presence is a nicety, not a
/// gate. Registered AFTER UseAuthentication/UseAuthorization so <c>User</c> is populated, and because
/// the SPA already polls /me + /sync/status every ~15-20s, this keeps presence fresh with no
/// dedicated client heartbeat.
/// </summary>
public sealed class PresenceMiddleware(RequestDelegate next, PresenceTracker presence)
{
    public async Task Invoke(HttpContext ctx)
    {
        try
        {
            if (ctx.User.Identity?.IsAuthenticated == true)
            {
                // Only stamp presence for a user OnTokenValidated actually resolved to an enabled account
                // (it stashes the loaded AppUser under LoadedUserKey for exactly those). A token whose
                // account was deleted — or disabled — is never stashed, so we skip it here; otherwise an
                // offboarded/unknown account keeps advertising itself as online (and its shared city) until
                // its still-valid token expires.
                var stampable = ctx.Items.TryGetValue(CurrentUserAccessor.LoadedUserKey, out var stashed)
                    && stashed is AppUser user && user.IsEnabled;

                var email = ctx.User.FindFirstValue("email");
                if (stampable && !string.IsNullOrWhiteSpace(email))
                    presence.Touch(email, ctx.User.FindFirstValue("name"), ctx.User.FindFirstValue("picture"));
            }
        }
        catch
        {
            // Never let presence bookkeeping disturb the request.
        }

        await next(ctx);
    }
}
