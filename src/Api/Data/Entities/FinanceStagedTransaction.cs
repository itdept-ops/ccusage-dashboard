namespace Ccusage.Api.Data.Entities;

/// <summary>
/// One PARSED-BUT-NOT-YET-COMMITTED transaction in the import staging area (the parse→review→commit flow).
/// Every import format (Rocket Money, generic column-mapped CSV, OFX/QFX) lands here FIRST; the live ledger
/// (<see cref="FinanceTransaction"/>) is only touched on an explicit commit. Staged rows are private to the
/// owning household, hung on a <see cref="FinanceImport"/> (cascade-deleted with it), and carry everything the
/// review UI needs: the parsed fields, the account it will land on (by stable key, find-or-created on commit),
/// the dedup verdict (<see cref="IsDuplicate"/> against the committed ledger AND within the batch — FITID-
/// preferred for OFX), the categorization (file/rule/ai/none) the user may override, and an exclude flag.
/// People are referenced by AppUser id only — an email is never stored here.
/// </summary>
public class FinanceStagedTransaction
{
    public long Id { get; set; }

    /// <summary>The owning household — staged rows are private to its members and never committable across households.</summary>
    public int HouseholdId { get; set; }

    /// <summary>The staging batch this row belongs to (a <see cref="FinanceImport"/> with Status='staged').</summary>
    public long ImportId { get; set; }
    public FinanceImport? Import { get; set; }

    /// <summary>The row's position in the source file (0-based, header excluded) — for stable review ordering.</summary>
    public int RowIndex { get; set; }

    /// <summary>The transaction date.</summary>
    public DateOnly Date { get; set; }

    /// <summary>The merchant/payee display string.</summary>
    public string Merchant { get; set; } = "";

    /// <summary>The free-text description (optional).</summary>
    public string? Description { get; set; }

    /// <summary>The raw signed amount exactly as parsed (negative = money out).</summary>
    public decimal RawAmount { get; set; }

    /// <summary>The size of the money movement, always &gt;= 0 (abs of <see cref="RawAmount"/>).</summary>
    public decimal Magnitude { get; set; }

    /// <summary>Classified flow: "expense" | "income" | "transfer" (the review UI may override).</summary>
    public string Kind { get; set; } = "expense";

    /// <summary>The stable account key ("name|institution", lower-cased) this row find-or-creates on commit.</summary>
    public string AccountKey { get; set; } = "";

    /// <summary>The account display name (find-or-created on commit).</summary>
    public string AccountName { get; set; } = "";

    /// <summary>The institution (optional).</summary>
    public string? Institution { get; set; }

    /// <summary>The raw account-type token from the source (drives the account's inferred Kind on commit).</summary>
    public string AccountTypeRaw { get; set; } = "";

    /// <summary>The category as it WILL be committed: the file's own category, or a rule/default/ai suggestion the
    /// user accepted/edited. Null = Uncategorized.</summary>
    public string? Category { get; set; }

    /// <summary>An AI-suggested category awaiting user review (written by /categorize-ai). Distinct from
    /// <see cref="Category"/> so a suggestion never silently overwrites a file/rule category; the UI surfaces it.</summary>
    public string? SuggestedCategory { get; set; }

    /// <summary>Where <see cref="Category"/> came from: "file" (the source's own column), "rule" (a household
    /// FinanceCategoryRule or the built-in default merchant→category map), "ai" (a Gemini suggestion), or
    /// "none" (still Uncategorized).</summary>
    public string CategorySource { get; set; } = "none";

    /// <summary>The bank's stable transaction id when the source provided one (OFX <c>&lt;FITID&gt;</c>) — the
    /// BEST dedup key. Null for formats without one (then <see cref="DedupHash"/> is used).</summary>
    public string? Fitid { get; set; }

    /// <summary>The content dedup hash (same shape as <see cref="FinanceTransaction.DedupHash"/>) — the fallback
    /// dedup key when there is no <see cref="Fitid"/>, and what is written to the committed row.</summary>
    public string DedupHash { get; set; } = "";

    /// <summary>True when this row already exists in the committed ledger OR collides with an earlier row in
    /// THIS batch (FITID-preferred, hash fallback, cross-format aware). Skipped on commit so nothing double-counts.</summary>
    public bool IsDuplicate { get; set; }

    /// <summary>True when the user un-checked this row in review — it is NOT committed.</summary>
    public bool Excluded { get; set; }

    public DateTime CreatedUtc { get; set; }
}
