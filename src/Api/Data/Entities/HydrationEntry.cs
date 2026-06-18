namespace Ccusage.Api.Data.Entities;

/// <summary>
/// One fluid-intake (hydration) log on a user's local date. UNLIKE <see cref="WeightEntry"/>, multiple
/// rows per (user, local date) are expected — a person drinks several times a day — so there is NO unique
/// constraint, just a read index on (UserEmail, LocalDate). Volume is always stored in millilitres (the
/// backend is metric-only; the client converts to oz for Imperial display). Hydration totals/entries are
/// part of the tracker DAY (like food/exercise): a permitted viewer sees them read-only, unlike the
/// PRIVATE weight history.
/// </summary>
public class HydrationEntry
{
    public long Id { get; set; }

    /// <summary>Owner email, stored lower-cased.</summary>
    public string UserEmail { get; set; } = "";

    /// <summary>The day this drink was logged on, in the app's display timezone.</summary>
    public DateOnly LocalDate { get; set; }

    /// <summary>Volume in millilitres.</summary>
    public int AmountMl { get; set; }

    /// <summary>Optional drink label (e.g. "Water", "Coffee", "Tea"); trimmed, &lt;= 64 chars.</summary>
    public string? Label { get; set; }

    public DateTime CreatedUtc { get; set; }
}
