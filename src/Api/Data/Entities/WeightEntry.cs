namespace Ccusage.Api.Data.Entities;

/// <summary>
/// One body-weight reading on a user's local date, for the weight-over-time trend. At most one row per
/// (user, local date) — logging again on the same day upserts the existing row. Weight is always stored
/// in kilograms (the backend is metric-only; the client converts for imperial display). Keyed for reads
/// by (UserEmail, LocalDate). Weight history is PRIVATE: never exposed to viewers, only the owner.
/// </summary>
public class WeightEntry
{
    public long Id { get; set; }

    /// <summary>Owner email, stored lower-cased.</summary>
    public string UserEmail { get; set; } = "";

    /// <summary>The day this weight was recorded on, in the app's display timezone.</summary>
    public DateOnly LocalDate { get; set; }

    /// <summary>Body weight in kilograms.</summary>
    public double WeightKg { get; set; }

    public DateTime CreatedUtc { get; set; }
}
