namespace Ccusage.Api.Services.Health;

/// <summary>
/// Bound from the <c>Fitbit</c> configuration section. <see cref="ClientId"/> + <see cref="ClientSecret"/>
/// are secrets (read from the git-ignored appsettings.Local.json locally, or the <c>Fitbit__ClientId</c> /
/// <c>Fitbit__ClientSecret</c> env vars — SSM — in prod) and are NEVER logged. When EITHER is blank the
/// provider is "not configured": /api/health/status reports configured:false and connect / sync degrade
/// gracefully, exactly like <see cref="GoogleCalendarService"/>. The OAuth + API hosts are FIXED below
/// (never user-controlled), so there is no SSRF surface.
/// </summary>
public sealed class FitbitOptions
{
    public const string SectionName = "Fitbit";

    /// <summary>Fitbit OAuth client id (also handed to the browser to build the authorize URL).</summary>
    public string? ClientId { get; set; }

    /// <summary>Fitbit OAuth client secret. Blank → the provider is "not configured".</summary>
    public string? ClientSecret { get; set; }

    /// <summary>The redirect URI registered with Fitbit (the SPA's /settings/health callback). The frontend
    /// must send the SAME value in /connect; defaulted here for convenience but config-overridable.</summary>
    public string? RedirectUri { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
