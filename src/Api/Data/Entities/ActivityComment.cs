namespace Ccusage.Api.Data.Entities;

/// <summary>
/// One free-text comment a user posted on a single <see cref="ActivityEvent"/> in the social feed. It is
/// the conversational counterpart of <see cref="ActivityReaction"/> (the one-tap cheer): where a cheer is a
/// fixed reaction, a comment carries a short validated body.
///
/// PRIVACY — <see cref="AuthorEmail"/> is the lower-cased author email and is the identity/dedup key; it is
/// NEVER serialized to a client. The feed exposes the author only as an AppUser id + a
/// <see cref="Services.DisplayName"/>-formatted name (email-privacy), and the comment notification names the
/// author by a DisplayName-formatted name only.
///
/// VISIBILITY — a user may only read/post a comment on an event they can already SEE in the feed (their own,
/// or a sharing contact's when they've opted to view) — the endpoints re-run the SAME circle/visibility check
/// the feed read uses (404, never 403, when not visible). The body is length-capped + control-char stripped
/// (mirroring the chat reaction validation) and soft-deleted (<see cref="DeletedUtc"/>) rather than hard
/// removed, so a chat.moderate override leaves an audit trail.
/// </summary>
public class ActivityComment
{
    public long Id { get; set; }

    /// <summary>FK to the commented <see cref="ActivityEvent"/>. Cascade-deletes with the event.</summary>
    public long ActivityEventId { get; set; }

    /// <summary>The author's lower-cased email — the identity key. NEVER serialized to a client.</summary>
    public string AuthorEmail { get; set; } = "";

    /// <summary>The comment text — validated (length-capped ~500, control-chars stripped) on write.</summary>
    public string Body { get; set; } = "";

    public DateTime CreatedUtc { get; set; }

    /// <summary>When the author last edited the body, if ever. Null = never edited.</summary>
    public DateTime? EditedUtc { get; set; }

    /// <summary>When the comment was soft-deleted (by its author OR a chat.moderate caller). Null = live.
    /// A soft-deleted comment is excluded from the thread read and its body is never re-served.</summary>
    public DateTime? DeletedUtc { get; set; }
}
