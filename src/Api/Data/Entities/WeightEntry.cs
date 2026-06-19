namespace Ccusage.Api.Data.Entities;

/// <summary>
/// One body-weight reading on a user's local date and named time-of-day slot, for the weight-over-time
/// trend and per-slot statistics. At most one row per (user, local date, slot) — logging again for the
/// same day+slot upserts the existing row. A user can weigh in at several slots on one day (e.g. morning
/// AND evening) and those readings coexist. Weight is always stored in kilograms (the backend is
/// metric-only; the client converts for imperial display). Keyed for reads by (UserEmail, LocalDate).
/// Weight history is PRIVATE: never exposed to viewers, only the owner.
/// </summary>
public class WeightEntry
{
    public long Id { get; set; }

    /// <summary>Owner email, stored lower-cased.</summary>
    public string UserEmail { get; set; } = "";

    /// <summary>The day this weight was recorded on, in the app's display timezone.</summary>
    public DateOnly LocalDate { get; set; }

    /// <summary>
    /// The named time-of-day slot this reading belongs to. <see cref="WeightSlot.Unspecified"/> is the
    /// default for readings logged without a slot (and for rows that predate slots).
    /// </summary>
    public WeightSlot Slot { get; set; } = WeightSlot.Unspecified;

    /// <summary>Body weight in kilograms.</summary>
    public double WeightKg { get; set; }

    public DateTime CreatedUtc { get; set; }
}
