using Ccusage.Api.Auth;
using Ccusage.Api.Data;
using Ccusage.Api.Data.Entities;

namespace Ccusage.Api.Services;

/// <summary>Writes audit entries, stamping the acting admin from the current request.</summary>
public sealed class AuditLogger(UsageDbContext db, CurrentUserAccessor current)
{
    public async Task LogAsync(string action, string? targetEmail, string? detail, CancellationToken ct = default)
    {
        var actor = await current.GetUserAsync(ct);
        db.AuditEntries.Add(new AuditEntry
        {
            WhenUtc = DateTime.UtcNow,
            ActorEmail = actor?.Email ?? "system",
            Action = action,
            TargetEmail = targetEmail,
            Detail = detail,
        });
        await db.SaveChangesAsync(ct);
    }
}
