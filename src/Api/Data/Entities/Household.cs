namespace Ccusage.Api.Data.Entities;

/// <summary>
/// A family household — the private unit that owns Family Hub data. One is auto-provisioned for a
/// caller who holds <c>family.use</c> and isn't yet in a household, named after them (e.g.
/// "Alex’s Family") with the caller as the OWNER member. Family data is private to the household and
/// only selectively shareable to specific contacts later; members are exposed by AppUser id +
/// display name + picture only — never by email.
/// </summary>
public class Household
{
    public int Id { get; set; }

    /// <summary>The household's display name (e.g. "Alex’s Family"); editable by the owner.</summary>
    public string Name { get; set; } = "";

    /// <summary>AppUser id of whoever first created the household (its original owner).</summary>
    public int CreatedByUserId { get; set; }

    public DateTime CreatedUtc { get; set; }

    public List<HouseholdMember> Members { get; set; } = new();
}
