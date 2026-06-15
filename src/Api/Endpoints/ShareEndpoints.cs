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
        // ---- Management (authenticated; you can share what you can view) ----
        var shares = app.MapGroup("/api/shares")
            .RequireAuthorization().RequirePermission(Permissions.DashboardView);

        shares.MapGet("/", async (UsageDbContext db, CancellationToken ct) =>
            Results.Ok((await db.ShareLinks.AsNoTracking().OrderByDescending(s => s.Id).ToListAsync(ct)).Select(ToDto)));

        shares.MapPost("/", async (CreateShareRequest req, UsageDbContext db, CurrentUserAccessor me, CancellationToken ct) =>
        {
            var token = GenerateToken();
            var lbl = req.Label?.Trim();
            var user = await me.GetUserAsync(ct);

            var share = new ShareLink
            {
                TokenHash = Hash(token),
                Label = string.IsNullOrEmpty(lbl) ? null : (lbl.Length > 120 ? lbl[..120] : lbl),
                CreatedByEmail = user?.Email ?? "",
                CreatedUtc = DateTime.UtcNow,
                ExpiresUtc = DateTime.UtcNow.AddHours(Math.Clamp(req.ExpiresInHours, 1, 24 * 90)),
                FromDate = req.From,
                ToDate = req.To,
                ProjectIds = req.ProjectId ?? Array.Empty<int>(),
                Models = req.Model ?? Array.Empty<string>(),
                Sources = req.Source ?? Array.Empty<string>(),
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
        });

        shares.MapDelete("/{id:int}", async (int id, UsageDbContext db, CancellationToken ct) =>
            await db.ShareLinks.Where(s => s.Id == id).ExecuteDeleteAsync(ct) > 0
                ? Results.NoContent() : Results.NotFound());

        // ---- Public, anonymous, rate-limited read of a valid (non-expired) link ----
        app.MapGet("/api/share/{token}", async (string token, UsageDbContext db, UsageQueries q, ILoggerFactory lf, CancellationToken ct) =>
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

            // Best-effort, atomic access counter — a concurrent revoke (0 rows) or any write error must
            // never turn a valid read into a 500.
            try
            {
                await db.ShareLinks.Where(s => s.Id == share.Id).ExecuteUpdateAsync(u => u
                    .SetProperty(x => x.AccessCount, x => x.AccessCount + 1)
                    .SetProperty(x => x.LastAccessedUtc, _ => DateTime.UtcNow), ct);
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

    private static string Normalize(string? g) =>
        (g?.ToLowerInvariant()) is "day" or "month" or "project" or "model" or "source" or "session" ? g!.ToLowerInvariant() : "day";

    private static ShareDto ToDto(ShareLink s) => new()
    {
        Id = s.Id, Label = s.Label, CreatedByEmail = s.CreatedByEmail,
        CreatedUtc = s.CreatedUtc, ExpiresUtc = s.ExpiresUtc, Expired = s.ExpiresUtc <= DateTime.UtcNow,
        AccessCount = s.AccessCount, LastAccessedUtc = s.LastAccessedUtc, Scope = Describe(s),
    };

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
