namespace Ccusage.Api.Data.Entities;

/// <summary>
/// One directed edge in a user's curated chat "circle": <see cref="OwnerEmail"/> may pick
/// <see cref="ContactEmail"/> in the New-DM / channel-member picker. Contacts are MUTUAL — adding
/// a contact writes both directions (owner→contact AND contact→owner) and removing deletes both —
/// but each direction is its own row so a unique index on (OwnerEmail, ContactEmail) can enforce
/// no duplicates per pair. Both emails are stored lower-cased; a self-contact is never written.
/// </summary>
public class ChatContact
{
    public int Id { get; set; }

    /// <summary>The user whose circle this row belongs to, stored lower-cased.</summary>
    public string OwnerEmail { get; set; } = "";

    /// <summary>The person in the owner's circle, stored lower-cased.</summary>
    public string ContactEmail { get; set; } = "";

    public DateTime CreatedUtc { get; set; }

    /// <summary>Email of the admin who added this contact (lower-cased); null for legacy/unknown.</summary>
    public string? AddedByEmail { get; set; }
}
