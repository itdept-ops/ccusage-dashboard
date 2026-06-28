namespace Ccusage.Api.Data.Entities;

/// <summary>
/// One night's sleep log, mapped to a user's local date. CONVENTION: a night belongs to the WAKE date —
/// the morning a user logs "I slept 7.5h" stamps that day's <see cref="LocalDate"/> (the same metric-only
/// <c>LocalDate</c> the other tracker verticals share). Unlike <see cref="HydrationEntry"/>/<see cref="CoffeeEntry"/>,
/// sleep is naturally one row per night, but we do NOT enforce a unique constraint (naps / split sleep are
/// allowed) — just a read index on (UserEmail, LocalDate) for the day-view + the rolling-average window.
///
/// PRIVACY: sleep is mildly personal and stays OWNER-ONLY. It is NOT surfaced to a sharing contact / coach
/// in the tracker day, NOT in the family overlay, and emits NO activity-feed event. The owner is the only
/// reader (the day DTO nulls it for any non-self viewer).
/// </summary>
public class SleepEntry
{
    public long Id { get; set; }

    /// <summary>Owner email, stored lower-cased.</summary>
    public string UserEmail { get; set; } = "";

    /// <summary>The wake date this night maps to, in the app's display timezone.</summary>
    public DateOnly LocalDate { get; set; }

    /// <summary>Hours slept (0..24), one decimal place.</summary>
    public decimal Hours { get; set; }

    /// <summary>Self-rated sleep quality, 1 (poor) .. 5 (great).</summary>
    public int Quality { get; set; }

    /// <summary>Optional bedtime as a local time-of-day (no date); null when not recorded.</summary>
    public TimeOnly? BedTime { get; set; }

    /// <summary>Optional wake time as a local time-of-day (no date); null when not recorded.</summary>
    public TimeOnly? WakeTime { get; set; }

    /// <summary>Optional free-text note (e.g. "woke up twice"); trimmed, &lt;= 200 chars.</summary>
    public string? Note { get; set; }

    /// <summary>Whether this row was typed by the user (<see cref="SourceKind.Manual"/>, default) or auto-
    /// imported from a connected wearable (<see cref="SourceKind.Watch"/>). A wearable re-sync only ever
    /// overwrites a Watch row — never a Manual one.</summary>
    public SourceKind Source { get; set; } = SourceKind.Manual;

    public DateTime CreatedUtc { get; set; }
}
