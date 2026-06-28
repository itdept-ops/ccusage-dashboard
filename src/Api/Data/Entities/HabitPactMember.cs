namespace Ccusage.Api.Data.Entities;

/// <summary>The status of a <see cref="HabitPactMember"/> in a pact.</summary>
public enum HabitPactMemberStatus
{
    /// <summary>Invited by the owner but has not yet accepted.</summary>
    Invited = 0,

    /// <summary>An active participant (the owner is auto-Active on create; an invitee becomes Active on join).</summary>
    Active = 1,

    /// <summary>Left the pact (or declined). Retained for history; excluded from progress.</summary>
    Left = 2,
}

/// <summary>
/// One participant in a <see cref="HabitPact"/>. The owner is seeded as an <see cref="HabitPactMemberStatus.Active"/>
/// member on create; invitees are added <see cref="HabitPactMemberStatus.Invited"/> and become Active when they
/// join. There is at most ONE row per (pact, member) — the unique index enforces it.
///
/// PRIVACY — <see cref="MemberEmail"/> is the lower-cased member email and is NEVER serialized to a client; the
/// member is exposed only as an AppUser id + a <see cref="Services.DisplayName"/>-formatted name (email-privacy).
/// An invitee is ONLY ever a mutual chat contact of the owner — the endpoint resolves the client-supplied AppUser
/// id to an email server-side and rejects any id that isn't in the owner's mutual circle.
/// </summary>
public class HabitPactMember
{
    public long Id { get; set; }

    /// <summary>FK to the parent <see cref="HabitPact"/>. Cascade-deletes with the pact.</summary>
    public long HabitPactId { get; set; }

    /// <summary>The participant's lower-cased email — the membership key. NEVER serialized to a client.</summary>
    public string MemberEmail { get; set; } = "";

    public DateTime JoinedUtc { get; set; }

    public HabitPactMemberStatus Status { get; set; }
}
