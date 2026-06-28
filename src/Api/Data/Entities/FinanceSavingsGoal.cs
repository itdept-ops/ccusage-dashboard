namespace Ccusage.Api.Data.Entities;

/// <summary>
/// A household savings goal (an emergency fund, a trip, a down-payment) with a manually-tracked
/// <see cref="SavedAmount"/> progressing toward a <see cref="TargetAmount"/>. No bank feed — the family enters
/// contributions by hand. <see cref="Owner"/> reuses the <see cref="FinanceAccount.Owner"/> vocabulary
/// ("his"|"hers"|"joint"|"unassigned") so the same color palette tags whose goal it is. Private to the owning
/// household (family.use AND family.finance); a cross-household id is a 404. People are referenced by AppUser
/// id only — no email here.
/// </summary>
public class FinanceSavingsGoal
{
    public int Id { get; set; }

    /// <summary>The owning household — goals are private to its members.</summary>
    public int HouseholdId { get; set; }

    /// <summary>The goal's display name (e.g. "Emergency fund", "Hawaii 2027").</summary>
    public string Name { get; set; } = "";

    /// <summary>The target amount to reach (always &gt;= 0).</summary>
    public decimal TargetAmount { get; set; }

    /// <summary>The amount saved so far — manually tracked (contributions adjust this; always &gt;= 0).</summary>
    public decimal SavedAmount { get; set; }

    /// <summary>An optional target date for the goal.</summary>
    public DateOnly? TargetDate { get; set; }

    /// <summary>Who the goal belongs to: "his" | "hers" | "joint" | "unassigned" (reuses the account vocab).</summary>
    public string Owner { get; set; } = "unassigned";

    /// <summary>An optional accent color (hex) for the goal card.</summary>
    public string? Color { get; set; }

    /// <summary>An optional icon key for the goal card.</summary>
    public string? Icon { get; set; }

    /// <summary>Archived goals are hidden by default (archive over hard-delete).</summary>
    public bool Archived { get; set; }

    /// <summary>The AppUser who created the goal (id only — never an email).</summary>
    public int CreatedByUserId { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
