namespace Ccusage.Api.Data.Entities;

/// <summary>
/// A bank/credit account on a <see cref="Household"/>'s shared finances, materialized from a Rocket Money
/// CSV import: one row per distinct (AccountName, Institution) seen in the export. The importer NEVER
/// assumes whose account it is — every account starts <see cref="Owner"/> = "unassigned" and the family
/// LABELS each one afterward (his/hers/joint), which is how the two SoFi accounts get told apart. Finance
/// data is household-private and extra-sensitive (gated by BOTH family.use AND family.finance) and is never
/// shared to outside contacts. People are referenced by AppUser id only — an email is never stored here.
/// </summary>
public class FinanceAccount
{
    public int Id { get; set; }

    /// <summary>The owning household — accounts are private to its members.</summary>
    public int HouseholdId { get; set; }

    /// <summary>The account's display name (Rocket Money "Account Name").</summary>
    public string Name { get; set; } = "";

    /// <summary>The institution (Rocket Money "Institution Name"); null/blank when not provided.</summary>
    public string? Institution { get; set; }

    /// <summary>Who the account belongs to: "his" | "hers" | "joint" | "unassigned" (default "unassigned").</summary>
    public string Owner { get; set; } = "unassigned";

    /// <summary>Account flavor inferred from the CSV "Account Type": "bank" | "credit" | "other".</summary>
    public string Kind { get; set; } = "other";

    public DateTime CreatedUtc { get; set; }
}
