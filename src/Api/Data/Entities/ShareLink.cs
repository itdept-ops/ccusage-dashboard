namespace Ccusage.Api.Data.Entities;

/// <summary>
/// A public, unauthenticated, time-limited link to a read-only aggregate view. Only the SHA-256
/// hash of the token is stored, so a database leak can't reveal live links. The filter scope is
/// baked in at creation and enforced server-side — a holder can't widen what they see.
/// </summary>
public class ShareLink
{
    public int Id { get; set; }

    /// <summary>SHA-256 (hex) of the random token — the deterministic key for the public lookup.</summary>
    public string TokenHash { get; set; } = "";

    /// <summary>The token encrypted at rest (AES-GCM via TokenProtector) so the link can be re-copied.</summary>
    public string? TokenEnc { get; set; }

    public string? Label { get; set; }
    public string CreatedByEmail { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }

    // ---- Baked-in, server-enforced filter scope ----
    public DateOnly? FromDate { get; set; }
    public DateOnly? ToDate { get; set; }
    public int[] ProjectIds { get; set; } = Array.Empty<int>();
    public string[] Models { get; set; } = Array.Empty<string>();
    public string[] Sources { get; set; } = Array.Empty<string>();
    public bool IncludeSidechain { get; set; } = true;
    public string GroupBy { get; set; } = "day";

    public int AccessCount { get; set; }
    public DateTime? LastAccessedUtc { get; set; }
}
