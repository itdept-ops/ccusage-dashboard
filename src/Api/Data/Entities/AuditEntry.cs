namespace Ccusage.Api.Data.Entities;

/// <summary>An append-only record of a sensitive change (currently user/permission edits).</summary>
public class AuditEntry
{
    public long Id { get; set; }

    public DateTime WhenUtc { get; set; }

    /// <summary>Email of the admin who made the change.</summary>
    public string ActorEmail { get; set; } = "";

    /// <summary>e.g. <c>user.created</c>, <c>user.updated</c>, <c>user.deleted</c>.</summary>
    public string Action { get; set; } = "";

    /// <summary>Email of the user the change affected (if any).</summary>
    public string? TargetEmail { get; set; }

    /// <summary>Human-readable summary of what changed.</summary>
    public string? Detail { get; set; }
}
