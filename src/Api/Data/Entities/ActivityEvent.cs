namespace Ccusage.Api.Data.Entities;

/// <summary>
/// One social-feed event in the SHARED activity spine. A single row per actor action (NOT fanned out
/// per-viewer): the feed read filters this table by the VIEWER's contact circle at read time, so an
/// actor's audience can change (contacts added/removed) without rewriting or backfilling rows.
///
/// PRIVACY — this row carries only ALREADY-shareable, non-sensitive facts:
/// counts/booleans/labels (e.g. "logged a workout", "completed day 12", "hit the hydration goal").
/// It NEVER carries raw private content, finance amounts/balances, location coordinates, cycle
/// (mood/symptoms/intimacy) data, private family notes, or anyone's email. The actor is keyed by
/// <see cref="ActorEmail"/> (lower-cased) which is NEVER put on the wire — the feed resolves it to an
/// AppUser id + a <see cref="Services.DisplayName"/>-formatted name server-side (email-privacy).
///
/// A row is written ONLY when the actor has opted their activity-sharing on
/// (<see cref="AppUser.ShareActivity"/>); the emitter no-ops otherwise, so a private action never
/// becomes an event in the first place.
/// </summary>
public class ActivityEvent
{
    public long Id { get; set; }

    /// <summary>The acting user's lower-cased email — the circle key. NEVER serialized to a client.</summary>
    public string ActorEmail { get; set; } = "";

    /// <summary>The event kind (a stable, non-sensitive token): "workout.logged" | "challenge.dayComplete"
    /// | "hydration.goalHit" | "challenge.started". Capped to the column length.</summary>
    public string Kind { get; set; } = "";

    public DateTime CreatedUtc { get; set; }

    /// <summary>A single non-sensitive numeric payload, meaning per <see cref="Kind"/>: workout duration
    /// minutes, the completed day number, or the current streak. Null when not applicable.</summary>
    public int? IntValue { get; set; }

    /// <summary>A single non-sensitive label, e.g. the exercise name (already snapshotted + capped on the
    /// source row). NEVER private free-text (no confessions, notes, amounts). Null when not applicable.</summary>
    public string? Label { get; set; }
}
