using Ccusage.Api.Auth;
using Ccusage.Api.Data;
using Ccusage.Api.Data.Entities;
using Ccusage.Api.Dtos;
using Ccusage.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Ccusage.Api.Endpoints;

/// <summary>
/// The AGENT INBOX / "Overnight" surface (<c>/api/agents/inbox</c>): a dedicated, browsable view of "what your
/// OS did for you" — the caller's OWN proactive-agent deliveries, grouped by period, each with a deep-link to
/// act and a one-tap triage (mark handled / handle-all).
///
/// <para>INVARIANTS (the review checks these):</para>
/// <list type="bullet">
///   <item>OWNER-SCOPED — every read/write keys on <c>RecipientEmail == caller.Email</c> (the SAME ownership
///   filter the bell inbox at <c>/api/inbox</c> uses); a caller never sees or mutates another user's items.</item>
///   <item>AGENT-PRODUCED ONLY — the set is exactly <see cref="NotificationType.AgentNudge"/> (the type the
///   <see cref="Services.AgentScheduler"/> + the agents /test endpoint write); chat/family/social notifications
///   never appear here.</item>
///   <item>NO MIGRATION — "handled" REUSES the existing <see cref="Notification.IsRead"/> flag; this surface adds
///   no column and never touches the migration snapshot.</item>
///   <item>EXISTING GATE — <see cref="Permissions.AgentsUse"/> (the agent companion); a holder with no agent
///   deliveries simply gets empty groups (the FE renders the empty state).</item>
///   <item>DISPLAY NAMES ONLY — an AgentNudge is self-scoped and carries no actor, so no email is ever on the
///   wire; the only identity exposed is the friendly per-kind agent label.</item>
/// </list>
///
/// v1 is scoped to the persisted agent DELIVERIES (the notification rows). "Ask that Acts" confirm-chip actions
/// are INTERACTIVE and not persisted server-side (the propose/confirm round-trip lives entirely in the ask
/// surface), so there is no pending-action store to fold in here; if one is ever added, it would extend this
/// inbox as a second source — no storage is invented for it now.
/// </summary>
public static class AgentInboxEndpoints
{
    public static void MapAgentInboxEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/agents/inbox")
            .RequireAuthorization()
            .RequirePermission(Permissions.AgentsUse);

        // ---- GET /api/agents/inbox : the caller's agent deliveries, grouped by period (newest-first) ----
        // Optional ?handled=false returns only un-triaged items; ?limit caps the page (default 100, max 200).
        g.MapGet("/", async (
            bool? handled, int? limit, CurrentUserAccessor me, UsageDbContext db, CancellationToken ct) =>
        {
            var user = (await me.GetUserAsync(ct))!;
            var email = user.Email.ToLowerInvariant();
            var take = Math.Clamp(limit ?? 100, 1, 200);

            var q = db.Notifications.AsNoTracking()
                .Where(n => n.RecipientEmail == email && n.Type == NotificationType.AgentNudge);
            if (handled == false) q = q.Where(n => !n.IsRead);

            var rows = await q.OrderByDescending(n => n.Id).Take(take).ToListAsync(ct);

            // Period buckets are computed in the CALLER'S display timezone so "Overnight / Today / Earlier"
            // matches what the user sees everywhere else (the same TZ the agents themselves schedule in).
            var tz = await TrackerVisibility.DisplayTzAsync(db, ct);
            var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
            var today = DateOnly.FromDateTime(nowLocal.DateTime);

            var items = rows.Select(n => ToItem(n, tz, today, nowLocal.DateTime)).ToList();
            var unhandled = rows.Count(n => !n.IsRead);

            return Results.Ok(new AgentInboxDto
            {
                UnhandledCount = unhandled,
                Groups = items
                    .GroupBy(i => i.Period)
                    .OrderBy(grp => PeriodOrder(grp.Key))
                    .Select(grp => new AgentInboxGroupDto
                    {
                        Period = grp.Key,
                        Items = grp.ToList(),
                    })
                    .ToList(),
            });
        });

        // ---- POST /api/agents/inbox/handle : mark specific agent items handled (flips the existing IsRead) ----
        // Owner-guarded: only the caller's OWN AgentNudge rows flip; a foreign or non-agent id is a silent no-op.
        g.MapPost("/handle", async (
            MarkNotificationsReadRequest req, CurrentUserAccessor me, UsageDbContext db, CancellationToken ct) =>
        {
            var user = (await me.GetUserAsync(ct))!;
            var email = user.Email.ToLowerInvariant();
            var ids = (req.Ids ?? Array.Empty<long>()).Distinct().ToArray();
            if (ids.Length > 0)
                await db.Notifications
                    .Where(n => n.RecipientEmail == email
                        && n.Type == NotificationType.AgentNudge
                        && !n.IsRead
                        && ids.Contains(n.Id))
                    .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true), ct);

            var unhandled = await db.Notifications.CountAsync(
                n => n.RecipientEmail == email && n.Type == NotificationType.AgentNudge && !n.IsRead, ct);
            return Results.Ok(new { unhandledCount = unhandled });
        });

        // ---- POST /api/agents/inbox/handle-all : mark every un-triaged agent item handled ----
        g.MapPost("/handle-all", async (CurrentUserAccessor me, UsageDbContext db, CancellationToken ct) =>
        {
            var user = (await me.GetUserAsync(ct))!;
            var email = user.Email.ToLowerInvariant();
            await db.Notifications
                .Where(n => n.RecipientEmail == email && n.Type == NotificationType.AgentNudge && !n.IsRead)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true), ct);
            return Results.Ok(new { unhandledCount = 0 });
        });
    }

    /// <summary>Map a persisted AgentNudge row to its inbox-item wire form: the per-kind agent label (derived
    /// from the deep-link — the kind isn't stored on the row), the summary text, when, the deep-link, and the
    /// existing read flag re-surfaced as <c>handled</c>. NO email is ever on this shape.</summary>
    private static AgentInboxItemDto ToItem(Notification n, TimeZoneInfo tz, DateOnly today, DateTime nowLocal)
    {
        var localCreated = TimeZoneInfo.ConvertTime(new DateTimeOffset(
            DateTime.SpecifyKind(n.CreatedUtc, DateTimeKind.Utc)), tz).DateTime;
        return new AgentInboxItemDto
        {
            Id = n.Id,
            AgentKind = KindFromLink(n.Link),
            AgentLabel = LabelFromLink(n.Link),
            Summary = n.Text,
            DeepLink = n.Link,
            CreatedUtc = n.CreatedUtc,
            Handled = n.IsRead,
            Period = PeriodFor(localCreated, today, nowLocal),
        };
    }

    /// <summary>The browsable period bucket for a delivery, in the caller's local time:
    /// "overnight" = delivered earlier TODAY before 6am (the classic "while you slept" batch);
    /// "today" = delivered TODAY at/after 6am; "earlier" = any prior local day.</summary>
    private static string PeriodFor(DateTime localCreated, DateOnly today, DateTime nowLocal)
    {
        var createdDay = DateOnly.FromDateTime(localCreated);
        if (createdDay < today) return "earlier";
        return localCreated.Hour < 6 ? "overnight" : "today";
    }

    private static int PeriodOrder(string period) => period switch
    {
        "overnight" => 0,
        "today" => 1,
        _ => 2,
    };

    /// <summary>Recover the agent kind from the delivery's deep-link (the row stores only the AgentNudge type +
    /// the per-kind link the composer set). Falls back to "agent" for an unrecognized/absent link.</summary>
    private static string KindFromLink(string? link) => (link ?? "") switch
    {
        "/family/today" => "morningBriefing",
        "/challenge" => "streakRescue",
        "/family/finance" => "budgetAlert",
        "/grocery" => "lowStaples",
        "/meds" => "medicationDue",
        _ => "agent",
    };

    /// <summary>The friendly agent display label for a delivery (derived from the same deep-link).</summary>
    private static string LabelFromLink(string? link) => (link ?? "") switch
    {
        "/family/today" => "Morning Briefing",
        "/challenge" => "Streak Rescue",
        "/family/finance" => "Budget Alert",
        "/grocery" => "Low Staples",
        "/meds" => "Medication Reminder",
        _ => "Your assistant",
    };
}
