namespace Ccusage.Api.Data.Entities;

/// <summary>
/// A family reminder owned by a <see cref="Household"/>. When its <see cref="DueUtc"/> arrives the
/// background tick (FamilyReminderService) pings the <see cref="TargetUserId"/> via the existing
/// in-app notification path (bell + toast + unread), then either advances <see cref="DueUtc"/> to the
/// next occurrence (when recurring) or deactivates the reminder. People are referenced by AppUser id
/// only — an email is never stored here or put on the wire.
/// </summary>
public class FamilyReminder
{
    public long Id { get; set; }

    /// <summary>The owning household — the reminder is visible to all its members.</summary>
    public int HouseholdId { get; set; }

    /// <summary>AppUser id of whoever created the reminder (identity is by id, never email).</summary>
    public int CreatedByUserId { get; set; }

    /// <summary>AppUser id of the household member who gets pinged (defaults to the creator).</summary>
    public int TargetUserId { get; set; }

    public string Text { get; set; } = "";

    /// <summary>When the reminder is due to fire (UTC).</summary>
    public DateTime DueUtc { get; set; }

    /// <summary>How the reminder repeats: "none" | "daily" | "weekly" | "weekdays".</summary>
    public string Recurrence { get; set; } = "none";

    /// <summary>False once a one-shot reminder has fired (recurring ones stay active).</summary>
    public bool Active { get; set; } = true;

    /// <summary>The last time the tick fired this reminder (UTC); null until it first fires.</summary>
    public DateTime? LastFiredUtc { get; set; }

    public DateTime CreatedUtc { get; set; }
}
