namespace Ccusage.Api.Data.Entities;

/// <summary>
/// One day's optional self-log for the JOURNAL & MOOD tracker — a near-exact sibling of <see cref="CycleDayLog"/>
/// (the day-log template). A row exists only because the owner (who holds <c>tracker.self</c>) logged it, and
/// ONLY the owner ever reads or writes it (owner-scoped on every endpoint, keyed by the caller's lower-cased
/// email). The FREE TEXT (<see cref="GratitudeText"/>/<see cref="ReflectionText"/>) is the most sensitive data
/// here — it is NEVER put on the wire for any other viewer and NEVER sent to (or logged by) the AI: only an
/// AGGREGATE projection (mood/energy/tag FREQUENCIES + counts) is ever narrated by the weekly-reflection note.
///
/// <para>Mirrors the day-log conventions: lower-cased <see cref="UserEmail"/> (maxlen 256), a calendar
/// <see cref="LocalDate"/> (DateOnly), a small mood vocabulary, a vocab-capped Postgres <c>text[]</c> for
/// <see cref="Tags"/>, timestamptz <see cref="CreatedUtc"/>/<see cref="UpdatedUtc"/>. UNIQUE
/// (UserEmail, LocalDate) — at most one entry per day, upserted in place.</para>
/// </summary>
public class JournalEntry
{
    public int Id { get; set; }

    /// <summary>The owner, stored lower-cased (the identity key; UNIQUE with <see cref="LocalDate"/>).</summary>
    public string UserEmail { get; set; } = "";

    /// <summary>The owner's AppUser id, kept alongside the email for identity joins.</summary>
    public int UserId { get; set; }

    /// <summary>The calendar day this entry is for (the owner's local date). UNIQUE per owner.</summary>
    public DateOnly LocalDate { get; set; }

    /// <summary>A small mood vocabulary, stored as free text (great/good/ok/low/rough), or null when not
    /// recorded. The write normalises an unknown value (or a 1..5 numeric) to this vocabulary.</summary>
    public string? Mood { get; set; }

    /// <summary>A 1..5 self-rated energy level, or null when not recorded.</summary>
    public int? Energy { get; set; }

    /// <summary>A small tag vocabulary (e.g. work/family/health/sleep/exercise/social/rest/stress/creative/
    /// nature/learning/money), stored as a Postgres <c>text[]</c>. Empty when nothing was logged.</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>A short free-text gratitude note (maxlen ~500), or null. PRIVATE: owner-only, never to the AI.</summary>
    public string? GratitudeText { get; set; }

    /// <summary>A longer free-text reflection (maxlen ~2000), or null. PRIVATE: owner-only, never to the AI.</summary>
    public string? ReflectionText { get; set; }

    /// <summary>When this entry was first created (UTC).</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>When this entry was last updated (UTC) — bumped on every upsert.</summary>
    public DateTime UpdatedUtc { get; set; }
}
