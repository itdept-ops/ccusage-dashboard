namespace Ccusage.Api.Data.Entities;

/// <summary>
/// One person's membership in a <see cref="Household"/>. A user belongs to at most one household for
/// now (a UNIQUE index on <see cref="UserId"/> enforces this), and a user appears at most once in a
/// given household (a UNIQUE index on (HouseholdId, UserId)). The person is referenced by AppUser id
/// only — their email is never stored here or put on the wire; display name + picture are resolved
/// via a Users join at read time.
/// </summary>
public class HouseholdMember
{
    public int Id { get; set; }

    public int HouseholdId { get; set; }
    public Household? Household { get; set; }

    /// <summary>AppUser id of the member (identity is by id, never email).</summary>
    public int UserId { get; set; }

    /// <summary>The member's role in the household: "owner" | "adult" | "child".</summary>
    public string Role { get; set; } = "adult";

    public DateTime JoinedUtc { get; set; }
}
