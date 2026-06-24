namespace Ccusage.Api.Data.Entities;

/// <summary>
/// A structured, per-account record of a sign-in attempt that reached a known/created user row.
/// Written best-effort by <c>GoogleAuthService.SignInAsync</c> (a logging failure never blocks a
/// sign-in). Distinct from <see cref="AuditEntry"/>: this is the user-facing login history (with the
/// server-observed IP + user-agent), not the security audit trail. The unprovisioned-unknown-account
/// and invalid-token paths are deliberately NOT recorded here.
/// </summary>
public class LoginEvent
{
    public long Id { get; set; }

    /// <summary>The account email, stored lower-cased. Indexed (the per-user history filters on it).</summary>
    public string Email { get; set; } = "";

    /// <summary>The <see cref="AppUser.Id"/> when the row is known/created; null otherwise.</summary>
    public int? UserId { get; set; }

    public DateTime WhenUtc { get; set; }

    /// <summary>The server-observed client IP (post-UseForwardedHeaders); may be "" if unavailable.</summary>
    public string Ip { get; set; } = "";

    public bool Success { get; set; }

    /// <summary>Short outcome: "ok", "auto-provisioned", "account disabled", or "google id mismatch".</summary>
    public string Reason { get; set; } = "";

    /// <summary>Display name from the Google token, if any.</summary>
    public string? Name { get; set; }

    /// <summary>The request User-Agent header, truncated to ~256 chars.</summary>
    public string? UserAgent { get; set; }

    // ---- Best-effort web client info, captured client-side and stamped onto the latest successful login
    // event via POST /api/client-info (all nullable; null when the SPA couldn't probe a field or an older
    // client never sent any). No PII beyond device/agent characteristics — never a precise location here.

    /// <summary><c>navigator.platform</c> (e.g. "Win32", "MacIntel"), truncated to ~64 chars.</summary>
    public string? Platform { get; set; }

    /// <summary>Reported screen width in CSS pixels (<c>screen.width</c>).</summary>
    public int? ScreenWidth { get; set; }

    /// <summary>Reported screen height in CSS pixels (<c>screen.height</c>).</summary>
    public int? ScreenHeight { get; set; }

    /// <summary>Device pixel ratio (<c>devicePixelRatio</c>), e.g. 1, 1.5, 2.</summary>
    public double? DevicePixelRatio { get; set; }

    /// <summary>Comma-joined <c>navigator.languages</c> (e.g. "en-US,en"), truncated to ~128 chars.</summary>
    public string? Languages { get; set; }

    /// <summary>IANA time zone from <c>Intl.DateTimeFormat().resolvedOptions().timeZone</c>, ~64 chars.</summary>
    public string? TimeZone { get; set; }

    /// <summary><c>navigator.hardwareConcurrency</c> — logical CPU count the browser exposes.</summary>
    public int? HardwareConcurrency { get; set; }

    /// <summary><c>navigator.deviceMemory</c> in GB (Chromium-only; null elsewhere).</summary>
    public double? DeviceMemory { get; set; }

    /// <summary><c>navigator.maxTouchPoints</c> — 0 on non-touch devices.</summary>
    public int? TouchPoints { get; set; }

    /// <summary><c>screen.colorDepth</c> in bits (e.g. 24, 30).</summary>
    public int? ColorDepth { get; set; }
}
