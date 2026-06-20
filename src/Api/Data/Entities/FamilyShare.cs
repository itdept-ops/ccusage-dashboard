namespace Ccusage.Api.Data.Entities;

/// <summary>
/// The Family Hub's reusable sharing primitive: a single row grants ONE person (by AppUser id) access
/// to ONE family item (a note or a list) that lives in someone else's household. It is polymorphic by
/// <see cref="ItemType"/> ("note" | "list") + <see cref="ItemId"/>, with a unique index over
/// (ItemType, ItemId, SharedWithUserId) so a person is shared an item at most once.
///
/// VISIBILITY of a family item to a caller = (caller is a member of the item's household) OR (a share
/// row exists for (itemType, itemId, caller)). EDIT = (household member) OR (a share with CanEdit=true).
/// Shared-in people who are NOT household members see ONLY the specific items shared to them — never
/// the rest of the household's data. People are referenced by AppUser id only; no email is stored.
/// </summary>
public class FamilyShare
{
    public long Id { get; set; }

    /// <summary>Which kind of item is shared: "note" | "list".</summary>
    public string ItemType { get; set; } = "";

    /// <summary>The shared item's id (a <see cref="FamilyNote"/> or <see cref="FamilyList"/> id).</summary>
    public long ItemId { get; set; }

    /// <summary>AppUser id of the person the item is shared WITH (identity is by id, never email).</summary>
    public int SharedWithUserId { get; set; }

    /// <summary>When true the shared-in person may edit, not just view.</summary>
    public bool CanEdit { get; set; }

    /// <summary>AppUser id of whoever created the share (a household member / the item's creator).</summary>
    public int CreatedByUserId { get; set; }

    public DateTime CreatedUtc { get; set; }
}
