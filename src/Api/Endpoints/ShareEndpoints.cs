using System.Security.Cryptography;
using System.Text;
using Ccusage.Api.Auth;
using Ccusage.Api.Data;
using Ccusage.Api.Data.Entities;
using Ccusage.Api.Dtos;
using Ccusage.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Ccusage.Api.Endpoints;

public static class ShareEndpoints
{
    public static void MapShareEndpoints(this WebApplication app)
    {
        // ---- Management (authenticated; gated by the shares.* permissions) ----
        var shares = app.MapGroup("/api/shares").RequireAuthorization();

        shares.MapGet("/", async (UsageDbContext db, TokenProtector protector, CancellationToken ct) =>
        {
            var list = await db.ShareLinks.AsNoTracking().OrderByDescending(s => s.Id).ToListAsync(ct);
            var resolver = await BuildOwnerResolverAsync(db, list.Select(s => s.CreatedByEmail), ct);
            return Results.Ok(list.Select(s => ToDto(s, protector, resolver)));
        }).RequireAnyPermission(Permissions.SharesView, Permissions.SharesManage);

        shares.MapPost("/", async (CreateShareRequest req, UsageDbContext db, CurrentUserAccessor me, TokenProtector protector, CancellationToken ct) =>
        {
            var token = GenerateToken();
            var lbl = req.Label?.Trim();
            var user = await me.GetUserAsync(ct);

            var share = new ShareLink
            {
                TokenHash = Hash(token),
                TokenEnc = protector.Protect(token),
                Label = string.IsNullOrEmpty(lbl) ? null : (lbl.Length > 120 ? lbl[..120] : lbl),
                CreatedByEmail = user?.Email ?? "",
                CreatedUtc = DateTime.UtcNow,
                ExpiresUtc = DateTime.UtcNow.AddHours(Math.Clamp(req.ExpiresInHours, 1, 24 * 90)),
                FromDate = req.From,
                ToDate = req.To,
                ProjectIds = CapIds(req.ProjectId),
                Models = CapLabels(req.Model),
                Sources = CapLabels(req.Source),
                IncludeSidechain = req.IncludeSidechain,
                GroupBy = Normalize(req.GroupBy),
            };
            db.ShareLinks.Add(share);
            await db.SaveChangesAsync(ct);

            // The full token is returned exactly once — only its hash is stored.
            return Results.Ok(new ShareCreatedDto
            {
                Id = share.Id, Token = token, Path = $"/share/{token}", ExpiresUtc = share.ExpiresUtc, Label = share.Label,
            });
        }).RequirePermission(Permissions.SharesManage);

        shares.MapPut("/{id:int}", async (int id, UpdateShareRequest req, UsageDbContext db, TokenProtector protector, CancellationToken ct) =>
        {
            var s = await db.ShareLinks.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return Results.NotFound();

            s.ExpiresUtc = DateTime.UtcNow.AddHours(Math.Clamp(req.ExpiresInHours, 1, 24 * 90));
            var lbl = req.Label?.Trim();
            s.Label = string.IsNullOrEmpty(lbl) ? null : (lbl.Length > 120 ? lbl[..120] : lbl);
            await db.SaveChangesAsync(ct);
            var resolver = await BuildOwnerResolverAsync(db, new[] { s.CreatedByEmail }, ct);
            return Results.Ok(ToDto(s, protector, resolver));
        }).RequirePermission(Permissions.SharesManage);

        shares.MapDelete("/{id:int}", async (int id, UsageDbContext db, CancellationToken ct) =>
            await db.ShareLinks.Where(s => s.Id == id).ExecuteDeleteAsync(ct) > 0
                ? Results.NoContent() : Results.NotFound())
            .RequirePermission(Permissions.SharesManage);

        // Per-view detail: who (IP) viewed a link and when (most recent first).
        shares.MapGet("/{id:int}/accesses", async (int id, UsageDbContext db, CancellationToken ct) =>
            Results.Ok(await db.ShareAccesses.AsNoTracking()
                .Where(a => a.ShareLinkId == id)
                .OrderByDescending(a => a.Id).Take(100)
                .Select(a => new ShareAccessDto { WhenUtc = a.WhenUtc, Ip = a.Ip })
                .ToListAsync(ct)))
            .RequireAnyPermission(Permissions.SharesView, Permissions.SharesManage);

        // ---- Public, anonymous, rate-limited read of a valid (non-expired) link ----
        app.MapGet("/api/share/{token}", async (string token, HttpContext http, UsageDbContext db, UsageQueries q, ILoggerFactory lf, CancellationToken ct) =>
        {
            var share = await db.ShareLinks.AsNoTracking().FirstOrDefaultAsync(s => s.TokenHash == Hash(token), ct);
            if (share is null || share.ExpiresUtc <= DateTime.UtcNow)
                return Results.NotFound(); // invalid or expired — indistinguishable to the caller

            // Scope is rebuilt from the stored share — the caller cannot widen it.
            var filter = new UsageFilterQuery(
                share.FromDate, share.ToDate,
                share.ProjectIds.Length == 0 ? null : share.ProjectIds,
                share.Models.Length == 0 ? null : share.Models,
                share.Sources.Length == 0 ? null : share.Sources,
                share.IncludeSidechain);

            var dto = new PublicShareDto
            {
                Label = share.Label,
                GeneratedAtUtc = DateTime.UtcNow,
                ExpiresUtc = share.ExpiresUtc,
                GroupBy = share.GroupBy,
                Scope = Describe(share),
                Summary = await q.SummaryAsync(filter, share.GroupBy, ct),
                Models = await q.SummaryAsync(filter, "model", ct),
            };

            // Best-effort access recording — the counter (for the list) plus a per-view row (IP + time).
            // A concurrent revoke (0 rows / FK gone) or any write error must never 500 a valid read.
            try
            {
                await db.ShareLinks.Where(s => s.Id == share.Id).ExecuteUpdateAsync(u => u
                    .SetProperty(x => x.AccessCount, x => x.AccessCount + 1)
                    .SetProperty(x => x.LastAccessedUtc, _ => DateTime.UtcNow), ct);

                db.ShareAccesses.Add(new ShareAccess
                {
                    ShareLinkId = share.Id,
                    WhenUtc = DateTime.UtcNow,
                    Ip = http.Connection.RemoteIpAddress?.ToString(),
                });
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                lf.CreateLogger("ShareAccess").LogWarning(ex, "Failed to record share access.");
            }

            return Results.Ok(dto);
        }).AllowAnonymous().RequireRateLimiting("share");
    }

    private static string GenerateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    // A share's stored filter is attacker-supplied via the create body; cap it before it is persisted
    // (and later replayed on every public read) — mirror the ingest sanitization: bound the array
    // lengths, clamp each label, and de-dup. Empty/null collapses to an empty array (no filter).
    private const int MaxFilterIds = 200;     // <= 200 project ids
    private const int MaxFilterLabels = 100;  // <= 100 model/source labels
    private const int MaxLabelLen = 128;      // each label clamped to <= 128 chars

    private static int[] CapIds(int[]? ids) =>
        ids is null or { Length: 0 } ? Array.Empty<int>()
            : ids.Distinct().Take(MaxFilterIds).ToArray();

    private static string[] CapLabels(string[]? labels)
    {
        if (labels is null or { Length: 0 }) return Array.Empty<string>();
        return labels
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Length > MaxLabelLen ? l[..MaxLabelLen] : l)
            .Distinct()
            .Take(MaxFilterLabels)
            .ToArray();
    }

    private static string Normalize(string? g) =>
        (g?.ToLowerInvariant()) is "day" or "month" or "project" or "model" or "source" or "session" ? g!.ToLowerInvariant() : "day";

    private static ShareDto ToDto(ShareLink s, TokenProtector protector,
        IReadOnlyDictionary<string, (int Id, string Name)> owners)
    {
        var token = protector.Unprotect(s.TokenEnc);
        var (id, name) = ResolveOwner(s.CreatedByEmail, owners);
        return new()
        {
            Id = s.Id, Label = s.Label,
            Path = token is null ? null : $"/share/{token}",
            CreatedByUserId = id,
            CreatedByName = name,
            CreatedUtc = s.CreatedUtc, ExpiresUtc = s.ExpiresUtc, Expired = s.ExpiresUtc <= DateTime.UtcNow,
            AccessCount = s.AccessCount, LastAccessedUtc = s.LastAccessedUtc, Scope = Describe(s),
        };
    }

    /// <summary>Resolve a set of RAW creator emails to {AppUser.Id, AppUser.Name}. The raw email is NEVER
    /// exposed (email-privacy); the dictionary is keyed case-insensitively on the stored (lower-cased) email.</summary>
    private static async Task<IReadOnlyDictionary<string, (int Id, string Name)>> BuildOwnerResolverAsync(
        UsageDbContext db, IEnumerable<string> emails, CancellationToken ct)
    {
        var lower = emails.Where(e => !string.IsNullOrEmpty(e))
            .Select(e => e.ToLowerInvariant()).Distinct().ToList();
        if (lower.Count == 0)
            return new Dictionary<string, (int, string)>(StringComparer.OrdinalIgnoreCase);
        return (await db.Users.AsNoTracking()
                .Where(u => lower.Contains(u.Email))
                .Select(u => new { u.Id, u.Email, u.Name }).ToListAsync(ct))
            .GroupBy(u => u.Email, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (g.First().Id, g.First().Name), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Map a raw creator email to {id, name}: the matching AppUser, else {null, "Unknown user"} for
    /// an orphaned/legacy email with no AppUser row. Never returns an email.</summary>
    private static (int? Id, string Name) ResolveOwner(
        string email, IReadOnlyDictionary<string, (int Id, string Name)> owners)
    {
        if (!string.IsNullOrEmpty(email) && owners.TryGetValue(email, out var u))
            return (u.Id, string.IsNullOrEmpty(u.Name) ? "Unknown user" : u.Name);
        return (null, "Unknown user");
    }

    private static string Describe(ShareLink s)
    {
        var parts = new List<string>();
        if (s.FromDate is { } f && s.ToDate is { } t) parts.Add($"{f:MMM d}–{t:MMM d}");
        else if (s.FromDate is { } f2) parts.Add($"from {f2:MMM d}");
        else if (s.ToDate is { } t2) parts.Add($"through {t2:MMM d}");
        else parts.Add("all time");
        if (s.Sources.Length > 0) parts.Add(string.Join(", ", s.Sources));
        if (s.ProjectIds.Length > 0) parts.Add($"{s.ProjectIds.Length} project{(s.ProjectIds.Length == 1 ? "" : "s")}");
        if (s.Models.Length > 0) parts.Add($"{s.Models.Length} model{(s.Models.Length == 1 ? "" : "s")}");
        if (!s.IncludeSidechain) parts.Add("no subagents");
        parts.Add($"by {s.GroupBy}");
        return string.Join(" · ", parts);
    }
}
