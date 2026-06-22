namespace Ccusage.Api.Data.Entities;

/// <summary>
/// A classification RULE so calendar imports auto-map event titles to roles ("soccer" → Athlete,
/// "standup" → Coder) and re-imports stay idempotent. OWNER-SCOPED: every endpoint binds the caller's email.
/// Matching is DETERMINISTIC substring — <see cref="Keyword"/> (lower-cased) is matched case-insensitively as a
/// substring of the event title; no AI is involved. UNIQUE (UserEmail, Keyword) so confirming a mapping twice
/// UPDATES rather than duplicates. When several keywords match one title, higher <see cref="Priority"/> wins
/// (ties broken by longer keyword — the more specific match).
/// </summary>
public class IdentityRule
{
    public int Id { get; set; }

    /// <summary>The owner, stored lower-cased (the identity key; UNIQUE with <see cref="Keyword"/>).</summary>
    public string UserEmail { get; set; } = "";

    /// <summary>The owner's AppUser id, kept alongside the email for identity joins.</summary>
    public int UserId { get; set; }

    /// <summary>The lower-cased keyword matched as a case-insensitive substring of an event title (1..128).</summary>
    public string Keyword { get; set; } = "";

    /// <summary>The role this keyword maps to (FK → <see cref="IdentityRole.Id"/>; owner-scoped).</summary>
    public int RoleId { get; set; }

    /// <summary>Higher wins when multiple keywords match one title (default 0).</summary>
    public int Priority { get; set; }

    /// <summary>When this rule was created (UTC).</summary>
    public DateTime CreatedUtc { get; set; }
}
