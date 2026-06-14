using Google.Apis.Auth;

namespace Ccusage.Api.Services;

/// <summary>
/// Verifies a Google ID token (signature against Google's keys, audience, issuer, expiry) and
/// returns its payload. Abstracted so the sign-in flow can be tested without calling Google.
/// </summary>
public interface IGoogleTokenValidator
{
    Task<GoogleJsonWebSignature.Payload> ValidateAsync(string idToken, string clientId, CancellationToken ct);
}

/// <summary>Production implementation backed by Google's token-validation library.</summary>
public sealed class GoogleTokenValidator : IGoogleTokenValidator
{
    public Task<GoogleJsonWebSignature.Payload> ValidateAsync(string idToken, string clientId, CancellationToken ct) =>
        GoogleJsonWebSignature.ValidateAsync(idToken, new GoogleJsonWebSignature.ValidationSettings
        {
            Audience = new[] { clientId },
        });
}
