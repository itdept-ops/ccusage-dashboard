using Ccusage.Api.Auth;
using Ccusage.Api.Data;
using Ccusage.Api.Dtos;
using Ccusage.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Ccusage.Api.Endpoints;

/// <summary>
/// Fleet management mutations (reporter.manage). Every operation works on RAW attribution values
/// (an empty string is the real value behind the "local" label) over the <c>machine</c> or <c>user</c>
/// dimension, uses EF Core bulk ops (no rows loaded), and writes an audit entry. Machines own no ingest
/// keys, so revocation is user-scoped only.
/// </summary>
public static class FleetEndpoints
{
    private const string Machine = "machine";
    private const string User = "user";

    public static void MapFleetEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/fleet").RequireAuthorization()
            .RequireAnyPermission(Permissions.ReporterManage);

        // POST /api/fleet/reassign — combine or transfer: every record whose dimension value is one of
        // `from` gets its dimension column rewritten to `to`. `from` must be non-empty; `to` may be ""
        // (re-label to local). Reject a no-op where the only `from` equals `to`.
        g.MapPost("/reassign", async (FleetReassignRequest req, UsageDbContext db, AuditLogger audit, CancellationToken ct) =>
        {
            if (!IsValidDimension(req.Dimension, out var dimension))
                return BadDimension();

            var from = (req.From ?? Array.Empty<string>()).Distinct().ToArray();
            if (from.Length == 0)
                return Results.BadRequest(new { message = "`from` must contain at least one value." });

            var to = req.To ?? "";
            if (from.Length == 1 && from[0] == to)
                return Results.BadRequest(new { message = "Nothing to reassign: the only source equals the target." });

            long affected = dimension == Machine
                ? await db.UsageRecords.Where(r => from.Contains(r.MachineName))
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.MachineName, to), ct)
                : await db.UsageRecords.Where(r => from.Contains(r.ReportedByUser))
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.ReportedByUser, to), ct);

            await audit.LogAsync("fleet.reassign", null,
                $"{dimension}: [{string.Join(", ", from.Select(Label))}] -> {Label(to)}; count={affected}", ct);
            return Results.Ok(new FleetReassignResultDto { Affected = affected });
        });

        // POST /api/fleet/delete — permanently bulk-delete every record whose dimension value is one of
        // `names`. Destructive: reject an empty `names` array (never a WHERE-less delete).
        g.MapPost("/delete", async (FleetDeleteRequest req, UsageDbContext db, AuditLogger audit, CancellationToken ct) =>
        {
            if (!IsValidDimension(req.Dimension, out var dimension))
                return BadDimension();

            var names = (req.Names ?? Array.Empty<string>()).Distinct().ToArray();
            if (names.Length == 0)
                return Results.BadRequest(new { message = "`names` must contain at least one value." });

            long deleted = dimension == Machine
                ? await db.UsageRecords.Where(r => names.Contains(r.MachineName)).ExecuteDeleteAsync(ct)
                : await db.UsageRecords.Where(r => names.Contains(r.ReportedByUser)).ExecuteDeleteAsync(ct);

            await audit.LogAsync("fleet.delete", null,
                $"{dimension}: [{string.Join(", ", names.Select(Label))}]; deleted={deleted}", ct);
            return Results.Ok(new FleetDeleteResultDto { Deleted = deleted });
        });

        // POST /api/fleet/revoke-keys — USER dimension only. Revoke every currently-active ingest key
        // owned by the user: match on UserId == (AppUser with this email).Id OR CreatedByEmail == email
        // (case-insensitive), so legacy keys with no user link are covered.
        g.MapPost("/revoke-keys", async (FleetRevokeKeysRequest req, UsageDbContext db, AuditLogger audit, CancellationToken ct) =>
        {
            var email = (req.Email ?? "").Trim();
            if (email.Length == 0)
                return Results.BadRequest(new { message = "`email` is required." });

            var lower = email.ToLowerInvariant();
            var userId = await db.Users.AsNoTracking()
                .Where(u => u.Email == lower).Select(u => (int?)u.Id).FirstOrDefaultAsync(ct);

            var now = DateTime.UtcNow;
            var revoked = await db.IngestKeys
                .Where(k => k.RevokedUtc == null
                            && ((userId != null && k.UserId == userId) || k.CreatedByEmail.ToLower() == lower))
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.RevokedUtc, now), ct);

            await audit.LogAsync("fleet.revoke-keys", email, $"revoked={revoked}", ct);
            return Results.Ok(new FleetRevokeKeysResultDto { Revoked = revoked });
        });
    }

    private static bool IsValidDimension(string? value, out string normalized)
    {
        normalized = (value ?? "").Trim().ToLowerInvariant();
        return normalized is Machine or User;
    }

    private static IResult BadDimension() =>
        Results.BadRequest(new { message = "`dimension` must be 'machine' or 'user'." });

    private static string Label(string raw) => string.IsNullOrEmpty(raw) ? "local" : raw;
}
