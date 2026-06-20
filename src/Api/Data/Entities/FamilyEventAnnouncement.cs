namespace Ccusage.Api.Data.Entities;

/// <summary>
/// An "announce-once" ledger row for the Family Hub event heads-up (F6b). When the background tick posts a
/// heads-up that a household member's calendar event is starting soon, it records the (household, Google
/// event id) here so a later tick never re-announces the same event. The unique (HouseholdId,
/// GoogleEventId) index enforces once-per-event-per-household. No email or event content is stored — only
/// the opaque Google event id and the event's start.
/// </summary>
public class FamilyEventAnnouncement
{
    public long Id { get; set; }

    /// <summary>The household whose Family channel got the heads-up.</summary>
    public int HouseholdId { get; set; }

    /// <summary>The opaque Google Calendar event id that was announced.</summary>
    public string GoogleEventId { get; set; } = "";

    /// <summary>The announced event's start (UTC) — handy for pruning/diagnostics.</summary>
    public DateTime EventStartUtc { get; set; }

    /// <summary>When the heads-up was posted (UTC).</summary>
    public DateTime AnnouncedUtc { get; set; }
}
