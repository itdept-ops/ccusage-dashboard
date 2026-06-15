namespace Ccusage.Api.Data.Entities;

/// <summary>One anonymous view of a public share link — when, and from which client IP.</summary>
public class ShareAccess
{
    public long Id { get; set; }
    public int ShareLinkId { get; set; }
    public ShareLink? ShareLink { get; set; }
    public DateTime WhenUtc { get; set; }
    public string? Ip { get; set; }
}
