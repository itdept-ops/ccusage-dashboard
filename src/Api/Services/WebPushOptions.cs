namespace Ccusage.Api.Services;

/// <summary>
/// Bound from the <c>WebPush</c> configuration section. VAPID keypair + subject used to sign Web Push
/// requests. <see cref="PublicKey"/> is intentionally PUBLIC (handed to the browser so it can subscribe).
/// <see cref="PrivateKey"/> is a SECRET (read from the git-ignored appsettings.Local.json locally, or the
/// <c>WebPush__PrivateKey</c> env var in prod, sourced from SSM) and is NEVER logged or returned. When the
/// keypair is UNSET, the whole web-push surface is a no-op: the sender does nothing, and
/// <c>GET /api/push/vapid-public</c> returns 404 — every other feature still works.
///
/// <para>Generate a keypair once with <c>WebPush.VapidHelper.GenerateVapidKeys()</c> (base64url public +
/// private). <see cref="Subject"/> is a contact URI the push services require — a <c>mailto:</c> or an
/// <c>https://</c> URL identifying the app operator.</para>
/// </summary>
public sealed class WebPushOptions
{
    public const string SectionName = "WebPush";

    /// <summary>PUBLIC VAPID key (base64url). Safe to expose — the client needs it to subscribe.</summary>
    public string? PublicKey { get; set; }

    /// <summary>PRIVATE VAPID key (base64url) — a SECRET. Blank disables web-push entirely. Never logged.</summary>
    public string? PrivateKey { get; set; }

    /// <summary>VAPID subject: a <c>mailto:</c> or <c>https://</c> contact URI for the app operator.</summary>
    public string Subject { get; set; } = "mailto:admin@usageiq.online";

    /// <summary>True only when BOTH keys are present — the gate for every web-push code path.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(PublicKey) && !string.IsNullOrWhiteSpace(PrivateKey);
}
