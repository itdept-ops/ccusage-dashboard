namespace Ccusage.Api.Data.Entities;

/// <summary>The kind of wearable signal a <see cref="HealthImportLog"/> row records — so the same vendor
/// record id can't collide across signal kinds.</summary>
public enum HealthSignalKind
{
    Steps = 0,
    Sleep = 1,
    HeartRate = 2,
    Workout = 3,
}

/// <summary>
/// PROGRAM-2 #1 — the de-dup spine for wearable sync. One row per (owner, provider, signalKind, vendor
/// record id) records that a specific provider record has already been imported and WHICH tracker row it
/// maps to, so a re-pull of the same day/record NEVER double-writes.
///
/// A FILTERED UNIQUE index on (UserEmail, Provider, SignalKind, SourceRef) — filtered to non-null SourceRef
/// — is the idempotency guard: the sync probes this log before writing, and the unique index is the
/// last-line defence against a concurrent double-import. For the day-keyed signals (steps, resting HR) the
/// <see cref="SourceRef"/> is the local date string; for record-keyed signals (sleep, workouts) it is the
/// vendor's logId.
/// </summary>
public class HealthImportLog
{
    public long Id { get; set; }

    /// <summary>Owner email, stored lower-cased (the sync only ever writes the owner's own rows).</summary>
    public string UserEmail { get; set; } = "";

    /// <summary>Which wearable provider this import came from.</summary>
    public HealthProvider Provider { get; set; }

    /// <summary>The local date this imported record maps to (the tracker day).</summary>
    public DateOnly LocalDate { get; set; }

    /// <summary>Which signal kind this row records.</summary>
    public HealthSignalKind SignalKind { get; set; }

    /// <summary>The vendor's stable reference for this record: the sleep/workout logId for record-keyed
    /// signals, or the local-date string for day-keyed ones (steps / resting HR). Part of the filtered
    /// unique key — never null in practice.</summary>
    public string? SourceRef { get; set; }

    /// <summary>The id of the tracker entity (DailyActivity / SleepEntry / ExerciseEntry) this record was
    /// written to — so a re-sync can locate and OVERWRITE the same Watch-sourced row rather than duplicating.</summary>
    public long TrackerEntityId { get; set; }

    public DateTime CreatedUtc { get; set; }
}
