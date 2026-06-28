namespace Ccusage.Api.Data.Entities;

/// <summary>
/// A household monthly spending budget for ONE category (matching <see cref="FinanceTransaction.Category"/>),
/// or an OVERALL whole-month budget when <see cref="Category"/> is null/empty. Pure budget intent — the
/// spend-vs-budget math is computed deterministically server-side from the EXPENSE-only ledger (transfers
/// excluded), never stored. Budgets are private to the owning household (gated by family.use AND
/// family.finance); a cross-household id is a 404. People are referenced by AppUser id only — no email here.
/// A UNIQUE (HouseholdId, Category) index means one budget per category (and one overall budget) per household.
/// </summary>
public class FinanceBudget
{
    public int Id { get; set; }

    /// <summary>The owning household — budgets are private to its members.</summary>
    public int HouseholdId { get; set; }

    /// <summary>The category this budget caps (matches <see cref="FinanceTransaction.Category"/>); null/empty
    /// is the household's single OVERALL whole-month budget.</summary>
    public string? Category { get; set; }

    /// <summary>The monthly limit in dollars (always &gt;= 0).</summary>
    public decimal LimitAmount { get; set; }

    /// <summary>The budget period — "monthly" in v1 (the only supported value).</summary>
    public string Period { get; set; } = "monthly";

    /// <summary>The AppUser who created the budget (id only — never an email).</summary>
    public int CreatedByUserId { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
