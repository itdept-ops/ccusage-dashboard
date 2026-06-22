namespace Ccusage.Api.Data.Entities;

/// <summary>
/// One ROLE the owner plays (e.g. "Parent", "Coder", "Athlete"). OWNER-SCOPED private data: a row exists
/// only because the owner (who holds <c>identity.map</c>) defined it, and ONLY the owner ever reads or edits
/// it (every endpoint binds the caller's email). Time entries are attributed to a role; the radial "web"
/// colours each slice by <see cref="Color"/>. Mirrors the CycleProfile UserEmail+UserId identity pattern.
/// UNIQUE (UserEmail, Name) so a user can't have two roles with the same label (and a classification rule
/// can resolve a name to exactly one row).
/// </summary>
public class IdentityRole
{
    public int Id { get; set; }

    /// <summary>The owner, stored lower-cased (the identity key; UNIQUE with <see cref="Name"/>).</summary>
    public string UserEmail { get; set; } = "";

    /// <summary>The owner's AppUser id, kept alongside the email for identity joins.</summary>
    public int UserId { get; set; }

    /// <summary>The role label, e.g. "Parent" (1..64 chars, validated at the endpoint).</summary>
    public string Name { get; set; } = "";

    /// <summary>A hex colour (e.g. "#3d8bff") for the chart slice; validated to an allowed palette server-side.</summary>
    public string Color { get; set; } = "#3d8bff";

    /// <summary>Archived roles keep their time history but drop out of the picker + default chart.</summary>
    public bool Archived { get; set; }

    /// <summary>Display ordering in the roles manager (ascending).</summary>
    public int SortOrder { get; set; }

    /// <summary>When this role was created (UTC).</summary>
    public DateTime CreatedUtc { get; set; }
}
