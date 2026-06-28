namespace Ccusage.Api.Data.Entities;

/// <summary>
/// A "habit pact" — a shared accountability goal an owner creates and invites their MUTUAL chat contacts to
/// join (e.g. "log a workout 5 times this week"). Progress is computed at read time from each member's
/// already-shareable <see cref="ActivityEvent"/> counts of the matching <see cref="Kind"/> within the pact's
/// active period — it never reads private tracker amounts or health data.
///
/// PRIVACY — <see cref="OwnerEmail"/> is the lower-cased creator email and is NEVER serialized to a client;
/// the owner and every member are exposed only as an AppUser id + a <see cref="Services.DisplayName"/>-formatted
/// name (email-privacy). Membership is CONSTRAINED to the owner's mutual chat contacts (the same directed-edge
/// check the feed circle uses) so a pact can never be a spam-invite vector.
/// </summary>
public class HabitPact
{
    public long Id { get; set; }

    /// <summary>The creating user's lower-cased email — the owner key. NEVER serialized to a client.</summary>
    public string OwnerEmail { get; set; } = "";

    /// <summary>The pact title (validated: trimmed + capped on write).</summary>
    public string Title { get; set; } = "";

    /// <summary>The activity kind the pact tracks — one of <see cref="Services.ActivityEmitter.Kinds"/>
    /// (<c>workout.logged</c> | <c>challenge.dayComplete</c> | <c>hydration.goalHit</c>). Counts of matching
    /// <see cref="ActivityEvent"/> rows in the period are each member's progress.</summary>
    public string Kind { get; set; } = "";

    /// <summary>The target count each member aims to reach in the period (e.g. 5 workouts). Clamped to a sane
    /// positive bound on write.</summary>
    public int TargetIntValue { get; set; }

    /// <summary>The length of the pact period in days (clamped on write). With <see cref="StartUtc"/> this
    /// bounds the window over which matching events count toward progress.</summary>
    public int PeriodDays { get; set; }

    public DateTime StartUtc { get; set; }

    /// <summary>When the pact ends. Null while open-ended (progress windows from StartUtc + PeriodDays).</summary>
    public DateTime? EndUtc { get; set; }

    public DateTime CreatedUtc { get; set; }

    /// <summary>When the owner archived the pact, if ever. Null = active. Archived pacts are hidden from the
    /// owner's active list but never hard-deleted.</summary>
    public DateTime? ArchivedUtc { get; set; }
}
