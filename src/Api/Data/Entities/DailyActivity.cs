namespace Ccusage.Api.Data.Entities;

/// <summary>
/// One day's manually-recorded smartwatch activity stats (steps, distance, active calories) on a user's
/// local date. At most one row per (user, local date) — recording again on the same day upserts the
/// existing row (a UNIQUE index on (UserEmail, LocalDate) enforces this). Distance is always stored in
/// metres (the backend is metric-only; the client converts to mi for Imperial display). The active
/// calories factor into the day's resolved "calories out" per <see cref="CalorieMode"/>: ADD on top of
/// logged exercises, or OVERRIDE the logged-exercise sum. Watch stats are part of the tracker DAY (like
/// hydration/exercise): a permitted viewer sees them read-only, but only the owner may write.
/// </summary>
public class DailyActivity
{
    public long Id { get; set; }

    /// <summary>Owner email, stored lower-cased.</summary>
    public string UserEmail { get; set; } = "";

    /// <summary>The day these stats apply to, in the app's display timezone.</summary>
    public DateOnly LocalDate { get; set; }

    /// <summary>Step count for the day, or null when not recorded.</summary>
    public int? Steps { get; set; }

    /// <summary>Distance covered in metres, or null when not recorded.</summary>
    public int? DistanceMeters { get; set; }

    /// <summary>Active calories burned per the watch, or null when not recorded.</summary>
    public int? ActiveCalories { get; set; }

    /// <summary>Resting heart rate (bpm) per the watch, or null when not recorded. SENSITIVE: owner-only —
    /// auto-imported by the wearable sync and never surfaced to coach/family overlays.</summary>
    public int? RestingHeartRate { get; set; }

    /// <summary>How <see cref="ActiveCalories"/> combines with the logged-exercise sum (Add | Override).</summary>
    public ActivityCalorieMode CalorieMode { get; set; } = ActivityCalorieMode.Add;

    /// <summary>Whether this row was typed by the user (<see cref="SourceKind.Manual"/>, default) or auto-
    /// imported from a connected wearable (<see cref="SourceKind.Watch"/>). A wearable re-sync only ever
    /// overwrites a Watch row — never a Manual one.</summary>
    public SourceKind Source { get; set; } = SourceKind.Manual;

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
