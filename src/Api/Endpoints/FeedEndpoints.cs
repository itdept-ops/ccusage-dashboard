using Ccusage.Api.Auth;
using Ccusage.Api.Data;
using Ccusage.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Ccusage.Api.Endpoints;

/// <summary>
/// The social ACTIVITY FEED (<c>/api/feed</c>) — the read side of the activity spine. DISTINCT from the admin
/// audit page at <c>/activity</c> (<c>GET /api/logs</c>, <c>activity.view</c>): that is the RequestLog trail;
/// this is the circle-scoped social feed.
///
/// PRIVACY (enforced here):
/// <list type="bullet">
///   <item>VIEW gate: reuses <see cref="Permissions.TrackerSelf"/> (no new permission) — the only events come
///   from tracker/75-Hard actions, all gated by tracker.self, and the sharing circle is the same contacts
///   graph the tracker uses.</item>
///   <item>AUDIENCE: an event is visible only when the actor is the CALLER, OR the actor is in the caller's
///   contact circle AND that actor is sharing (<c>ShareActivity</c>). The caller always sees their OWN events.
///   A secondary per-user <c>ViewActivityFeed</c> opt-in: when OFF, the feed returns ONLY the caller's own
///   events (circle events are withheld until they opt in to viewing).</item>
///   <item>WIRE SHAPE: rows carry an AppUser id + a <see cref="DisplayName"/>-formatted name — NEVER the actor
///   email (email-privacy) — and only the non-sensitive int/label payload the emitter stored.</item>
/// </list>
/// Keyset paging mirrors <c>/api/logs</c>/<c>/api/ai-usage</c> (<c>?before=&amp;limit=</c>, newest-first).
/// </summary>
public static class FeedEndpoints
{
    /// <summary>One feed row. The actor is an AppUser id + display name — NEVER an email.</summary>
    public sealed record FeedItemDto(
        long Id, int ActorUserId, string ActorName, string Kind, int? IntValue, string? Label, DateTime CreatedUtc);

    /// <summary>A page of feed items + the keyset cursor for the next page (null when no more).</summary>
    public sealed record FeedPageDto(IReadOnlyList<FeedItemDto> Items, long? NextBefore);

    public static void MapFeedEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/feed")
            .RequireAuthorization()
            .RequirePermission(Permissions.TrackerSelf);

        // ---- GET /api/feed : the caller's circle activity feed (newest-first, keyset paged) ----
        g.MapGet("/", async (
            long? before, int? limit, CurrentUserAccessor me, UsageDbContext db, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;
            var callerEmail = caller.Email.ToLowerInvariant();
            var take = Math.Clamp(limit ?? 50, 1, 100);

            // The caller's contact circle: actors who have the caller in their circle (row Owner=actor,
            // Contact=caller — mirrors ContactGraph.SharingEmails minus the tracker-sharing join, since the
            // feed gates on the ACTIVITY share flag, not the tracker one).
            var circleEmails = db.ChatContacts.AsNoTracking()
                .Where(c => c.ContactEmail == callerEmail)
                .Select(c => c.OwnerEmail);

            // Circle events are only included when (a) the actor opted to SHARE activity AND (b) the caller
            // opted to VIEW the feed. Otherwise the feed is the caller's OWN events only (still circle-correct:
            // a user always sees themselves).
            var sharingCircle = db.Users.AsNoTracking()
                .Where(u => u.IsEnabled && u.ShareActivity && u.Email != callerEmail
                            && circleEmails.Contains(u.Email))
                .Select(u => u.Email);

            var query = db.ActivityEvents.AsNoTracking();
            query = caller.ViewActivityFeed
                ? query.Where(e => e.ActorEmail == callerEmail || sharingCircle.Contains(e.ActorEmail))
                : query.Where(e => e.ActorEmail == callerEmail);

            if (before is { } b) query = query.Where(e => e.Id < b);

            var rows = await query
                .OrderByDescending(e => e.Id)
                .Take(take)
                .Select(e => new { e.Id, e.ActorEmail, e.Kind, e.IntValue, e.Label, e.CreatedUtc })
                .ToListAsync(ct);

            // Resolve every distinct actor email -> AppUser id + DisplayName-formatted name in ONE query
            // (email-privacy: the raw actor email NEVER reaches the client).
            var actors = await ChatNotificationService.ResolveActorsAsync(
                db, rows.Select(r => r.ActorEmail).ToArray(), ct);

            var items = rows.Select(r =>
            {
                var actor = actors.GetValueOrDefault(r.ActorEmail.ToLowerInvariant());
                return new FeedItemDto(
                    r.Id,
                    actor.Id,
                    string.IsNullOrEmpty(actor.Name) ? DisplayName.Unknown : actor.Name,
                    r.Kind, r.IntValue, r.Label, r.CreatedUtc);
            }).ToList();

            // Keyset cursor: a full page implies there may be more (oldest id on this page).
            long? nextBefore = items.Count == take ? items[^1].Id : null;
            return Results.Ok(new FeedPageDto(items, nextBefore));
        });
    }
}
