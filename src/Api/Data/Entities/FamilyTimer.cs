namespace Ccusage.Api.Data.Entities;

/// <summary>
/// A short-lived shared countdown owned by a <see cref="Household"/> and visible to the whole family.
/// When <see cref="EndsUtc"/> arrives the background tick marks it <see cref="Done"/> and pings every
/// household member via the existing in-app notification path. People are referenced by AppUser id
/// only — an email is never stored here or put on the wire.
/// </summary>
public class FamilyTimer
{
    public long Id { get; set; }

    /// <summary>The owning household — the timer is visible to all its members.</summary>
    public int HouseholdId { get; set; }

    /// <summary>AppUser id of whoever started the timer (identity is by id, never email).</summary>
    public int StartedByUserId { get; set; }

    public string Label { get; set; } = "";

    /// <summary>When the countdown finishes (UTC).</summary>
    public DateTime EndsUtc { get; set; }

    /// <summary>True once the tick has completed the timer and notified the household.</summary>
    public bool Done { get; set; }

    public DateTime CreatedUtc { get; set; }
}
