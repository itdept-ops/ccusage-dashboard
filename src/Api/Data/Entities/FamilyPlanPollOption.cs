namespace Ccusage.Api.Data.Entities;

/// <summary>
/// One option on a <see cref="FamilyPlanPoll"/>. For a TIME poll it carries a <see cref="StartUtc"/> /
/// <see cref="EndUtc"/> slot (and <see cref="Label"/> is null); for a TEXT poll it carries a free-text
/// <see cref="Label"/> (and the times are null). Members vote for every option that works for them
/// (<see cref="FamilyPlanPollVote"/>). Cascade-deletes with its poll.
/// </summary>
public class FamilyPlanPollOption
{
    public long Id { get; set; }

    /// <summary>The poll this option belongs to (cascade-deletes with the poll).</summary>
    public long PollId { get; set; }
    public FamilyPlanPoll? Poll { get; set; }

    /// <summary>The slot start (UTC) for a TIME option; null for a TEXT option.</summary>
    public DateTime? StartUtc { get; set; }

    /// <summary>The slot end (UTC) for a TIME option; null for a TEXT option.</summary>
    public DateTime? EndUtc { get; set; }

    /// <summary>The free-text choice for a TEXT option; null for a TIME option.</summary>
    public string? Label { get; set; }

    /// <summary>Display order within the poll (as the options were submitted).</summary>
    public int SortOrder { get; set; }

    public List<FamilyPlanPollVote> Votes { get; set; } = new();
}
