namespace Ccusage.Api.Data.Entities;

/// <summary>
/// A Family Hub "plan poll" (F6b) — a Doodle-style group decision owned by a <see cref="Household"/>. A
/// poll is either a TIME poll (its options are candidate time slots, each with a start/end — typically
/// seeded from the find-a-time helper) or a TEXT poll (its options are free-text labels, e.g. restaurant
/// choices). Members vote by marking every option that works for them; the creator (or any member) can
/// close it, picking a winner (defaulting to the most-voted). A TIME option can then be booked onto the
/// caller's connected calendar. People are referenced by AppUser id only — an email is never stored here
/// or put on the wire.
/// </summary>
public class FamilyPlanPoll
{
    public long Id { get; set; }

    /// <summary>The owning household — the poll is visible to and votable by all its members.</summary>
    public int HouseholdId { get; set; }

    /// <summary>AppUser id of whoever created the poll (identity is by id, never email).</summary>
    public int CreatedByUserId { get; set; }

    public string Title { get; set; } = "";

    /// <summary>What the options represent: "time" (start/end slots) | "text" (free-text labels).</summary>
    public string Kind { get; set; } = "time";

    /// <summary>True once the poll has been closed; closed polls no longer accept votes.</summary>
    public bool Closed { get; set; }

    /// <summary>The winning option id once closed (most-voted by default); null while open or if tie/empty.</summary>
    public long? WinningOptionId { get; set; }

    public DateTime CreatedUtc { get; set; }

    public List<FamilyPlanPollOption> Options { get; set; } = new();
}
