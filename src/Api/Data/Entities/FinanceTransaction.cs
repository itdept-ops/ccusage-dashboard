namespace Ccusage.Api.Data.Entities;

/// <summary>
/// One transaction imported from a Rocket Money CSV, hung on a <see cref="FinanceAccount"/>. The amount is
/// stored three ways so dashboard math is unambiguous: a non-negative <see cref="Magnitude"/> (the size of
/// the money movement), the raw signed <see cref="RawAmount"/> straight from the CSV, and a classified
/// <see cref="Kind"/> ("expense" | "income" | "transfer"). Spending totals sum EXPENSE magnitudes only;
/// transfers (incl. credit-card payments) are excluded so moving money between your own accounts never
/// looks like spending. A stable <see cref="DedupHash"/> (UNIQUE per household) makes re-importing the same
/// or overlapping exports a no-op. People are referenced by AppUser id only — an email is never stored here.
/// </summary>
public class FinanceTransaction
{
    public long Id { get; set; }

    /// <summary>The owning household — transactions are private to its members.</summary>
    public int HouseholdId { get; set; }

    /// <summary>The account this transaction belongs to.</summary>
    public int AccountId { get; set; }
    public FinanceAccount? Account { get; set; }

    /// <summary>The transaction date (the CSV "Date").</summary>
    public DateOnly Date { get; set; }

    /// <summary>The merchant/payee: Rocket Money "Custom Name" if present, else "Name".</summary>
    public string Merchant { get; set; } = "";

    /// <summary>The CSV "Description" (optional).</summary>
    public string? Description { get; set; }

    /// <summary>The size of the money movement, always &gt;= 0 (the absolute value of the raw amount).</summary>
    public decimal Magnitude { get; set; }

    /// <summary>The raw signed amount exactly as parsed from the CSV (negative = money out, per Rocket Money).</summary>
    public decimal RawAmount { get; set; }

    /// <summary>Classified flow: "expense" | "income" | "transfer". Spending = expense only.</summary>
    public string Kind { get; set; } = "expense";

    /// <summary>The Rocket Money "Category" (optional).</summary>
    public string? Category { get; set; }

    /// <summary>The Rocket Money "Note" (optional).</summary>
    public string? Note { get; set; }

    /// <summary>
    /// Stable dedup key over (accountKey | date | amount | merchant | description). A UNIQUE index on
    /// (HouseholdId, DedupHash) means re-importing the same export (or an overlapping one) skips rows that
    /// are already present.
    /// </summary>
    public string DedupHash { get; set; } = "";

    /// <summary>The import batch this row came in on (a <see cref="FinanceImport"/>).</summary>
    public long ImportId { get; set; }

    public DateTime CreatedUtc { get; set; }
}
