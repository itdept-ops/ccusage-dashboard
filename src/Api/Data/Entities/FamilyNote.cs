namespace Ccusage.Api.Data.Entities;

/// <summary>
/// A family note (markdown body) owned by a <see cref="Household"/>. It is private to the household
/// and may be selectively shared to specific contacts via <see cref="FamilyShare"/> rows. People are
/// referenced by AppUser id only — an email is never stored here or put on the wire.
/// </summary>
public class FamilyNote
{
    public long Id { get; set; }

    /// <summary>The owning household — the note is visible to all its members.</summary>
    public int HouseholdId { get; set; }

    /// <summary>AppUser id of whoever created the note (identity is by id, never email).</summary>
    public int CreatedByUserId { get; set; }

    public string Title { get; set; } = "";

    /// <summary>Free-form markdown body.</summary>
    public string Body { get; set; } = "";

    /// <summary>Pinned notes float to the top of the family's list.</summary>
    public bool Pinned { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
