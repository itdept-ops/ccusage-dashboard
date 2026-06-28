namespace Ccusage.Api.Data.Entities;

/// <summary>
/// A manually-entered point-in-time balance for one household <see cref="FinanceAccount"/> (there is no live
/// bank feed). The <see cref="Balance"/> is SIGNED per the account's <see cref="FinanceAccount.Kind"/>: a bank
/// account is a positive ASSET, a credit-card/loan is a negative LIABILITY. Net worth = the sum of each
/// account's MOST-RECENT snapshot (latest <see cref="AsOfDate"/>). A UNIQUE (HouseholdId, AccountId, AsOfDate)
/// index makes a same-day re-entry an upsert (latest-wins). Snapshots are private to the owning household
/// (family.use AND family.finance); a cross-household account/id is a 404. People by AppUser id only — no email.
/// </summary>
public class FinanceBalanceSnapshot
{
    public int Id { get; set; }

    /// <summary>The owning household — snapshots are private to its members.</summary>
    public int HouseholdId { get; set; }

    /// <summary>The account this balance is for.</summary>
    public int AccountId { get; set; }
    public FinanceAccount? Account { get; set; }

    /// <summary>The date this balance was true (the "as of" date).</summary>
    public DateOnly AsOfDate { get; set; }

    /// <summary>The SIGNED balance: a bank account is positive (asset); a credit/loan is negative (liability).
    /// The family enters the sign per the account kind — net worth sums these directly.</summary>
    public decimal Balance { get; set; }

    /// <summary>An optional note on the snapshot.</summary>
    public string? Note { get; set; }

    /// <summary>The AppUser who entered the balance (id only — never an email).</summary>
    public int EnteredByUserId { get; set; }

    public DateTime CreatedUtc { get; set; }
}
