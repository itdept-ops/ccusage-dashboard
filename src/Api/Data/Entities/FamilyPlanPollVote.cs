namespace Ccusage.Api.Data.Entities;

/// <summary>
/// A single Doodle-style vote: member <see cref="UserId"/> marked option <see cref="OptionId"/> as one
/// that works for them. A member may vote for many options on a poll, but at most once per option (the
/// unique (OptionId, UserId) index). Re-voting on a poll replaces the member's prior votes for it.
/// Cascade-deletes with its option. People are referenced by AppUser id only — never email.
/// </summary>
public class FamilyPlanPollVote
{
    public long Id { get; set; }

    /// <summary>The option this vote is for (cascade-deletes with the option).</summary>
    public long OptionId { get; set; }
    public FamilyPlanPollOption? Option { get; set; }

    /// <summary>AppUser id of the voter (identity is by id, never email).</summary>
    public int UserId { get; set; }

    public DateTime CreatedUtc { get; set; }
}
