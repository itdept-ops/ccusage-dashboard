namespace Ccusage.Api.Services;

/// <summary>
/// Bound from the <c>FatSecret</c> configuration section. <see cref="ClientId"/> + <see cref="ClientSecret"/>
/// are secrets (read from the git-ignored appsettings.Local.json locally, or the
/// <c>FatSecret__ClientId</c> / <c>FatSecret__ClientSecret</c> env vars in prod) and are NEVER logged.
/// When either is blank the provider is disabled (it never participates in the search fallback).
/// <see cref="TokenUrl"/> and <see cref="ApiUrl"/> are fixed, non-user-controlled hosts (no SSRF surface).
/// </summary>
public sealed class FatSecretOptions
{
    public const string SectionName = "FatSecret";

    /// <summary>FatSecret Platform OAuth2 client id. Blank disables the FatSecret fallback.</summary>
    public string? ClientId { get; set; }

    /// <summary>FatSecret Platform OAuth2 client secret. Blank disables the FatSecret fallback.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>The OAuth2 client-credentials token endpoint. Never chosen from user input.</summary>
    public string TokenUrl { get; set; } = "https://oauth.fatsecret.com/connect/token";

    /// <summary>The Platform REST API root. Never chosen from user input.</summary>
    public string ApiUrl { get; set; } = "https://platform.fatsecret.com/rest";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
