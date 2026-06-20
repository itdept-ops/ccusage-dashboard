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

            // For the user dimension the client holds no emails — it sends user IDs. Resolve them to the
            // raw owner emails (the attribution values) here; the machine dimension uses raw names as-is.
            string[] from;
            string to;
            if (dimension == User)
            {
                var ids = (req.UserIds ?? Array.Empty<int>()).Distinct().ToArray();
                if (ids.Length == 0)
                    return Results.BadRequest(new { message = "`userIds` must contain at least one value." });

                from = await ResolveUserEmailsAsync(db, ids, ct);
                if (from.Length == 0)
                    return Results.BadRequest(new { message = "None of the given `userIds` resolve to a user." });

                to = req.ToUserId is int toId ? await ResolveUserEmailAsync(db, toId, ct) ?? "" : "";
            }
            else
            {
                from = (req.From ?? Array.Empty<string>()).Distinct().ToArray();
                to = req.To ?? "";
            }

            if (from.Length == 0)
                return Results.BadRequest(new { message = "`from` must contain at least one value." });

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

            // User dimension: the client sends user IDs — resolve to raw owner emails before deleting.
            string[] names;
            if (dimension == User)
            {
                var ids = (req.UserIds ?? Array.Empty<int>()).Distinct().ToArray();
                if (ids.Length == 0)
                    return Results.BadRequest(new { message = "`userIds` must contain at least one value." });

                names = await ResolveUserEmailsAsync(db, ids, ct);
                if (names.Length == 0)
                    return Results.BadRequest(new { message = "None of the given `userIds` resolve to a user." });
            }
            else
            {
                names = (req.Names ?? Array.Empty<string>()).Distinct().ToArray();
                if (names.Length == 0)
                    return Results.BadRequest(new { message = "`names` must contain at least one value." });
            }

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
            // The client sends a user id (no email). Resolve it to the owner email here; revocation then
            // matches that user's keys by UserId OR legacy CreatedByEmail (case-insensitive), as before.
            if (req.UserId <= 0)
                return Results.BadRequest(new { message = "`userId` is required." });

            var email = await ResolveUserEmailAsync(db, req.UserId, ct);
            if (email is null)
                return Results.BadRequest(new { message = "`userId` does not resolve to a user." });

            var lower = email.ToLowerInvariant();
            int? userId = req.UserId;

            var now = DateTime.UtcNow;
            var revoked = await db.IngestKeys
                .Where(k => k.RevokedUtc == null
                            && ((userId != null && k.UserId == userId) || k.CreatedByEmail.ToLower() == lower))
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.RevokedUtc, now), ct);

            await audit.LogAsync("fleet.revoke-keys", email, $"revoked={revoked}", ct);
            return Results.Ok(new FleetRevokeKeysResultDto { Revoked = revoked });
        });
    }

    /// <summary>Resolve a set of user IDs to their RAW owner emails (the attribution values). Ids with no
    /// matching user are silently dropped; the result is the distinct set of resolved emails.</summary>
    private static async Task<string[]> ResolveUserEmailsAsync(UsageDbContext db, int[] ids, CancellationToken ct) =>
        await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => u.Email)
            .Distinct()
            .ToArrayAsync(ct);

    /// <summary>Resolve a single user id to its RAW owner email, or null when no such user exists.</summary>
    private static async Task<string?> ResolveUserEmailAsync(UsageDbContext db, int id, CancellationToken ct) =>
        await db.Users.AsNoTracking()
            .Where(u => u.Id == id)
            .Select(u => u.Email)
            .FirstOrDefaultAsync(ct);

    private static bool IsValidDimension(string? value, out string normalized)
    {
        normalized = (value ?? "").Trim().ToLowerInvariant();
        return normalized is Machine or User;
    }

    private static IResult BadDimension() =>
        Results.BadRequest(new { message = "`dimension` must be 'machine' or 'user'." });

    private static string Label(string raw) => string.IsNullOrEmpty(raw) ? "local" : raw;
}
