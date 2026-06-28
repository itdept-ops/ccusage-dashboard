namespace Ccusage.Api.Data.Entities;

/// <summary>
/// A household merchant→category rule used by the DETERMINISTIC auto-categorizer at import time. When the
/// source file has no category for a row, the importer looks the (lower-cased) merchant up against these rules
/// (plus a built-in default map) before any optional AI is considered. Rules are seeded from a default
/// merchant→category map and auto-LEARNED: when a user fixes a category at review with "apply to future", an
/// <c>equals</c> rule for that merchant is upserted. Private to the owning household.
/// </summary>
public class FinanceCategoryRule
{
    public long Id { get; set; }

    /// <summary>The owning household — rules are private to its members.</summary>
    public int HouseholdId { get; set; }

    /// <summary>How <see cref="Pattern"/> is tested against the lower-cased merchant: "contains" (substring)
    /// or "equals" (exact match). Auto-learned rules use "equals".</summary>
    public string MatchType { get; set; } = "equals";

    /// <summary>The pattern to match, stored LOWER-CASED (the merchant is lower-cased before testing).</summary>
    public string Pattern { get; set; } = "";

    /// <summary>The category to assign when this rule matches.</summary>
    public string Category { get; set; } = "";

    public DateTime CreatedUtc { get; set; }
}
