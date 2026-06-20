namespace Ccusage.Api.Data.Entities;

/// <summary>
/// One row of a <see cref="FamilyList"/> (a thing to buy or a task to do). Cascade-deletes with its
/// list. People (who completed it, who it's assigned to) are referenced by AppUser id only — an email
/// is never stored here or put on the wire.
/// </summary>
public class FamilyListItem
{
    public long Id { get; set; }

    public long ListId { get; set; }
    public FamilyList? List { get; set; }

    public string Text { get; set; } = "";

    /// <summary>Checked off?</summary>
    public bool Done { get; set; }

    /// <summary>AppUser id of whoever last checked it off; null when not done.</summary>
    public int? DoneByUserId { get; set; }

    /// <summary>AppUser id of the person this item is assigned to; null when unassigned.</summary>
    public int? AssignedToUserId { get; set; }

    /// <summary>Manual ordering within the list (ascending).</summary>
    public int SortOrder { get; set; }

    public DateTime CreatedUtc { get; set; }
}
