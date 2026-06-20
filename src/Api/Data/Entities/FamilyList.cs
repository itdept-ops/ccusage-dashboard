namespace Ccusage.Api.Data.Entities;

/// <summary>
/// A family list (a shopping list or a to-do list) owned by a <see cref="Household"/>. It is private
/// to the household and may be selectively shared to specific contacts via <see cref="FamilyShare"/>
/// rows. Its <see cref="FamilyListItem"/> rows cascade-delete with it. People are referenced by AppUser
/// id only — an email is never stored here or put on the wire.
/// </summary>
public class FamilyList
{
    public long Id { get; set; }

    /// <summary>The owning household — the list is visible to all its members.</summary>
    public int HouseholdId { get; set; }

    /// <summary>AppUser id of whoever created the list (identity is by id, never email).</summary>
    public int CreatedByUserId { get; set; }

    public string Name { get; set; } = "";

    /// <summary>The list flavor: "shopping" | "todo".</summary>
    public string Kind { get; set; } = "todo";

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public List<FamilyListItem> Items { get; set; } = new();
}
