using System.Globalization;
using System.Text;
using Ccusage.Api.Auth;
using Ccusage.Api.Data;
using Ccusage.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Ccusage.Api.Endpoints;

/// <summary>
/// Family Hub — the FAMILY ASSISTANT: one chat box (<c>POST /api/family/assistant</c>) over the whole
/// household. Gated by <see cref="Permissions.FamilyUse"/> on top of <c>.RequireAuthorization()</c> and
/// rate-limited (the shared "ai" policy), it CLONES the family-AI pattern exactly:
///
/// <list type="bullet">
///   <item>The endpoint assembles a COMPACT, household-scoped, read-only SNAPSHOT server-side (today + the
///   next ~3 days of the caller's connected calendar; today's/overdue reminders; open chores with the
///   assignee NAME + points; shopping/todo LIST names + open counts; this week's planned meal titles) and —
///   ONLY when the caller ALSO holds <see cref="Permissions.FamilyFinance"/> — the current month's finance
///   totals (spent / income / top categories), using the SAME math as GET /finance/summary.</item>
///   <item>NO email is ever on the wire — people in the snapshot are display NAMES only.</item>
///   <item>The assistant NEVER auto-applies: it returns an <c>answer</c> plus 0..6 PROPOSED <c>actions</c>
///   (each in the closed set list_add/reminder/timer/calendar_event/chore/meal), and the FRONTEND calls the
///   matching EXISTING write endpoint on user confirm. This endpoint WRITES NOTHING and proposes NO finance
///   write (finance is answer-only).</item>
///   <item>Graceful: a 400 for an empty message; a 503 (never a 500) when Gemini is unconfigured or the call
///   fails. Per-user calls are not cached.</item>
/// </list>
/// </summary>
public static class FamilyAssistantEndpoints
{
    /// <summary>The assistant request: the family member's free-text message. The snapshot is server-built;
    /// the client sends only the message (never any household DATA or facts).</summary>
    public sealed record AssistantRequest(string? Message);

    /// <summary>One PROPOSED action for the frontend to confirm then write via the matching existing endpoint.
    /// <see cref="Type"/> is one of list_add/reminder/timer/calendar_event/chore/meal; <see cref="Params"/>
    /// carries the clamped, named values for that endpoint. Nothing is created server-side.</summary>
    public sealed record AssistantActionDto(string Type, string Title, IReadOnlyDictionary<string, object?> Params);

    /// <summary>The assistant response: a warm, concise answer drawn ONLY from the snapshot + 0..6 proposed
    /// actions to confirm. This endpoint always WRITES NOTHING.</summary>
    public sealed record AssistantDto(string Answer, IReadOnlyList<AssistantActionDto> Actions);

    // Snapshot caps (keep the DATA block tight — the service caps the whole blob too).
    private const int MaxSnapshotEvents = 12;
    private const int MaxSnapshotReminders = 15;
    private const int MaxSnapshotChores = 25;
    private const int MaxSnapshotLists = 15;
    private const int MaxSnapshotMeals = 14;
    private const int MaxSnapshotFinanceCategories = 4;
    private const int CalendarLookaheadDays = 4; // today + next ~3 days

    public static void MapFamilyAssistantEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/family")
            .RequireAuthorization()
            .RequirePermission(Permissions.FamilyUse);

        // ---- POST /assistant : one chat box over the household; answers + PROPOSED actions ----
        // Assembles a compact household snapshot server-side, then asks Gemini to answer + propose actions the
        // FRONTEND writes on confirm. WRITES NOTHING. Rate-limited; 400 empty message; graceful 503.
        g.MapPost("/assistant", async (
            AssistantRequest req, CurrentUserAccessor me, CurrentHouseholdAccessor households,
            GoogleCalendarService cal, GeminiService gemini, UsageDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req?.Message))
                return Results.BadRequest(new { message = "Type a message for your family assistant." });
            if (!gemini.IsConfigured)
                return AiUnavailable();

            var caller = (await me.GetUserAsync(ct))!;
            var household = (await households.GetOrCreateForCallerAsync(caller, ct))!;
            var tz = FamilyTodayService.ResolveTimeZone(household.TimeZone);

            var now = DateTime.UtcNow;
            var referenceLocal = TimeZoneInfo.ConvertTimeFromUtc(now, tz);

            // Build the COMPACT, household-scoped, read-only snapshot. Finance facts ONLY when the caller also
            // holds family.finance (the extra money gate) — otherwise they never touch the snapshot path.
            var includeFinance = caller.Permissions.Contains(Permissions.FamilyFinance);
            var snapshot = await BuildSnapshotAsync(
                db, cal, household.Id, caller.Id, tz, now, referenceLocal, includeFinance, ct);

            var result = await gemini.FamilyAssistantAsync(req.Message, snapshot, referenceLocal, tz, ct);
            if (result is null) return AiUnavailable();

            var actions = result.Actions
                .Select(a => new AssistantActionDto(a.Type, a.Title, a.Params))
                .ToList();
            return Results.Ok(new AssistantDto(result.Answer, actions));
        }).RequireRateLimiting(AiEndpoints.RateLimitPolicy);
    }

    // =====================================================================================
    // SNAPSHOT — a compact, household-scoped, read-only DATA block (NO email; capped)
    // =====================================================================================

    /// <summary>
    /// Assemble the compact household snapshot the assistant answers from: today + the next ~3 days of the
    /// caller's connected calendar (best-effort/optional), today's + overdue reminders, open chores (title +
    /// assignee NAME + points), shopping/todo list names + open counts, this week's planned meal titles, and —
    /// ONLY when <paramref name="includeFinance"/> — the current month's finance totals (same math as
    /// GET /finance/summary). Everything is household-scoped and exposes display NAMES only (never an email).
    /// </summary>
    private static async Task<string> BuildSnapshotAsync(
        UsageDbContext db, GoogleCalendarService cal, int householdId, int callerId, TimeZoneInfo tz,
        DateTime nowUtc, DateTime referenceLocal, bool includeFinance, CancellationToken ct)
    {
        var localDate = DateOnly.FromDateTime(referenceLocal);
        var sb = new StringBuilder();
        sb.Append("LOCAL_NOW: ").Append(referenceLocal.ToString("yyyy-MM-ddTHH:mm:ss"))
          .Append(" (").Append(referenceLocal.ToString("dddd", CultureInfo.InvariantCulture)).Append(")\n");

        // ---- Calendar: today + next ~3 days (caller's connected calendar; best-effort/optional) ----
        await AppendCalendarAsync(sb, cal, callerId, tz, localDate, ct);

        // ---- Reminders: today's + any overdue still-active ones (household-scoped; names only) ----
        await AppendRemindersAsync(sb, db, householdId, tz, localDate, ct);

        // ---- Chores: OPEN chores with assignee NAME + points + recurrence ----
        await AppendChoresAsync(sb, db, householdId, ct);

        // ---- Lists: shopping/todo list names + open counts ----
        await AppendListsAsync(sb, db, householdId, ct);

        // ---- Meals: this week's planned meal titles ----
        await AppendMealsAsync(sb, db, householdId, tz, ct);

        // ---- Finance: current month totals — ONLY when the caller also holds family.finance ----
        if (includeFinance)
            await AppendFinanceAsync(sb, db, householdId, ct);

        return sb.ToString();
    }

    private static async Task AppendCalendarAsync(
        StringBuilder sb, GoogleCalendarService cal, int callerId, TimeZoneInfo tz, DateOnly localDate,
        CancellationToken ct)
    {
        try
        {
            var startLocal = localDate.ToDateTime(TimeOnly.MinValue);
            var startUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(startLocal, DateTimeKind.Unspecified), tz);
            var endUtc = TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(startLocal.AddDays(CalendarLookaheadDays), DateTimeKind.Unspecified), tz);

            var result = await cal.ListEventsAsync(callerId, startUtc, endUtc, ct);
            if (!result.Ok || result.Value is null || result.Value.Count == 0) return;

            var events = result.Value
                .OrderBy(e => e.StartUtc ?? DateTime.MaxValue)
                .Take(MaxSnapshotEvents)
                .ToList();
            if (events.Count == 0) return;

            sb.Append("UPCOMING_EVENTS (next ").Append(CalendarLookaheadDays).Append(" days):\n");
            foreach (var e in events)
            {
                string when;
                if (e.AllDay && e.StartUtc is { } sd)
                    when = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(sd, DateTimeKind.Utc), tz)
                        .ToString("ddd MMM d 'all day'", CultureInfo.InvariantCulture);
                else if (e.StartUtc is { } s)
                    when = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(s, DateTimeKind.Utc), tz)
                        .ToString("ddd MMM d h:mm tt", CultureInfo.InvariantCulture);
                else
                    when = "time unknown";
                sb.Append("- ").Append(Cap(e.Title, 120)).Append(" (").Append(when).Append(")\n");
            }
        }
        catch
        {
            // A calendar hiccup (or no connection) must never break the assistant — simply omit events.
        }
    }

    private static async Task AppendRemindersAsync(
        StringBuilder sb, UsageDbContext db, int householdId, TimeZoneInfo tz, DateOnly localDate,
        CancellationToken ct)
    {
        // [localMidnight, nextLocalMidnight) in UTC for "today"; anything active before that is "overdue".
        var localMidnight = localDate.ToDateTime(TimeOnly.MinValue);
        var todayEndUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(localMidnight.AddDays(1), DateTimeKind.Unspecified), tz);

        var rows = await db.FamilyReminders.AsNoTracking()
            .Where(r => r.HouseholdId == householdId && r.Active && r.DueUtc < todayEndUtc)
            .OrderBy(r => r.DueUtc)
            .Take(MaxSnapshotReminders)
            .Select(r => new { r.Text, r.DueUtc, r.TargetUserId, r.Recurrence })
            .ToListAsync(ct);
        if (rows.Count == 0) return;

        var names = await NamesAsync(db, rows.Select(r => r.TargetUserId), ct);
        sb.Append("REMINDERS (today + overdue):\n");
        foreach (var r in rows)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(r.DueUtc, DateTimeKind.Utc), tz);
            sb.Append("- ").Append(Cap(r.Text, 160))
              .Append(" — ").Append(local.ToString("ddd MMM d h:mm tt", CultureInfo.InvariantCulture))
              .Append(" for ").Append(Name(names, r.TargetUserId));
            if (!string.Equals(r.Recurrence, "none", StringComparison.OrdinalIgnoreCase))
                sb.Append(" (").Append(r.Recurrence).Append(')');
            sb.Append('\n');
        }
    }

    private static async Task AppendChoresAsync(
        StringBuilder sb, UsageDbContext db, int householdId, CancellationToken ct)
    {
        var rows = await db.FamilyChores.AsNoTracking()
            .Where(c => c.HouseholdId == householdId && !c.Done)
            .OrderBy(c => c.AssignedToUserId == null ? 1 : 0)
            .ThenBy(c => c.Title)
            .Take(MaxSnapshotChores)
            .Select(c => new { c.Title, c.AssignedToUserId, c.Points, c.Recurrence })
            .ToListAsync(ct);
        if (rows.Count == 0) return;

        var names = await NamesAsync(db, rows.Where(c => c.AssignedToUserId != null).Select(c => c.AssignedToUserId!.Value), ct);
        sb.Append("OPEN_CHORES:\n");
        foreach (var c in rows)
        {
            sb.Append("- ").Append(Cap(c.Title, 120))
              .Append(" — ").Append(c.AssignedToUserId is int a ? Name(names, a) : "unassigned")
              .Append(", ").Append(c.Points).Append(c.Points == 1 ? " star" : " stars");
            if (!string.Equals(c.Recurrence, "none", StringComparison.OrdinalIgnoreCase))
                sb.Append(" (").Append(c.Recurrence).Append(')');
            sb.Append('\n');
        }
    }

    private static async Task AppendListsAsync(
        StringBuilder sb, UsageDbContext db, int householdId, CancellationToken ct)
    {
        var lists = await db.FamilyLists.AsNoTracking()
            .Where(l => l.HouseholdId == householdId)
            .OrderBy(l => l.Name)
            .Take(MaxSnapshotLists)
            .Select(l => new { l.Id, l.Name, l.Kind })
            .ToListAsync(ct);
        if (lists.Count == 0) return;

        var ids = lists.Select(l => l.Id).ToArray();
        var openByList = (await db.FamilyListItems.AsNoTracking()
                .Where(i => ids.Contains(i.ListId) && !i.Done)
                .GroupBy(i => i.ListId)
                .Select(grp => new { ListId = grp.Key, Open = grp.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => x.ListId, x => x.Open);

        sb.Append("LISTS:\n");
        foreach (var l in lists)
            sb.Append("- ").Append(Cap(l.Name, 120)).Append(" (").Append(l.Kind).Append("): ")
              .Append(openByList.GetValueOrDefault(l.Id)).Append(" open\n");
    }

    private static async Task AppendMealsAsync(
        StringBuilder sb, UsageDbContext db, int householdId, TimeZoneInfo tz, CancellationToken ct)
    {
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var today = DateOnly.FromDateTime(localNow);
        var offset = ((int)today.DayOfWeek + 6) % 7; // ISO week start = Monday
        var weekStart = today.AddDays(-offset);
        var weekEnd = weekStart.AddDays(7); // exclusive

        var meals = await db.FamilyMeals.AsNoTracking()
            .Where(m => m.HouseholdId == householdId && m.LocalDate >= weekStart && m.LocalDate < weekEnd)
            .OrderBy(m => m.LocalDate).ThenBy(m => m.Id)
            .Take(MaxSnapshotMeals)
            .Select(m => new { m.LocalDate, m.Slot, m.Title })
            .ToListAsync(ct);
        if (meals.Count == 0) return;

        sb.Append("THIS_WEEK_MEALS:\n");
        foreach (var m in meals)
            sb.Append("- ").Append(m.LocalDate.ToString("ddd MMM d", CultureInfo.InvariantCulture))
              .Append(' ').Append(m.Slot).Append(": ").Append(Cap(m.Title, 120)).Append('\n');
    }

    /// <summary>Append the current month's finance totals, computed with the SAME expense-only math as
    /// GET /finance/summary (transfers excluded). Owners are his/hers/joint/unassigned; amounts are the
    /// authoritative server totals. No email is involved in finance at all.</summary>
    private static async Task AppendFinanceAsync(
        StringBuilder sb, UsageDbContext db, int householdId, CancellationToken ct)
    {
        // The summary month: the most recent month with data, else now (mirrors GET /summary's default).
        var maxDate = await db.FinanceTransactions.AsNoTracking()
            .Where(t => t.HouseholdId == householdId)
            .OrderByDescending(t => t.Date)
            .Select(t => (DateOnly?)t.Date)
            .FirstOrDefaultAsync(ct);
        var anchor = maxDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var from = new DateOnly(anchor.Year, anchor.Month, 1);
        var toExclusive = from.AddMonths(1);
        var monthLabel = from.ToString("yyyy-MM", CultureInfo.InvariantCulture);

        var monthTxns = await db.FinanceTransactions.AsNoTracking()
            .Where(t => t.HouseholdId == householdId && t.Date >= from && t.Date < toExclusive)
            .Select(t => new { t.Magnitude, t.Kind, t.Category })
            .ToListAsync(ct);

        if (monthTxns.Count == 0)
        {
            sb.Append("FINANCE: no spending recorded for ").Append(monthLabel).Append(".\n");
            return;
        }

        var expenses = monthTxns.Where(t => t.Kind == "expense").ToList();
        var totalSpent = expenses.Sum(t => t.Magnitude);
        var totalIncome = monthTxns.Where(t => t.Kind == "income").Sum(t => t.Magnitude);

        var topCategories = expenses
            .GroupBy(t => string.IsNullOrWhiteSpace(t.Category) ? "Uncategorized" : t.Category!)
            .Select(grp => new { Category = grp.Key, Amount = grp.Sum(x => x.Magnitude) })
            .OrderByDescending(x => x.Amount).ThenBy(x => x.Category, StringComparer.OrdinalIgnoreCase)
            .Take(MaxSnapshotFinanceCategories)
            .ToList();

        sb.Append("FINANCE (").Append(monthLabel).Append("):\n");
        sb.Append("- total_spent: ").Append(Money(totalSpent)).Append('\n');
        sb.Append("- total_income: ").Append(Money(totalIncome)).Append('\n');
        if (topCategories.Count > 0)
            sb.Append("- top_categories: ")
              .Append(string.Join("; ", topCategories.Select(c => $"{c.Category} {Money(c.Amount)}")))
              .Append('\n');
    }

    // =====================================================================================
    // HELPERS
    // =====================================================================================

    private static string Money(decimal amount) => "$" + amount.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Cap(string? s, int max)
    {
        s = (s ?? "").Trim();
        return s.Length > max ? s[..max] : s;
    }

    /// <summary>Resolve a set of userIds to display names (email is never read). Missing → "Unknown user".</summary>
    private static async Task<Dictionary<int, string>> NamesAsync(
        UsageDbContext db, IEnumerable<int> userIds, CancellationToken ct)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<int, string>();
        return await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(
                u => u.Id,
                u => string.IsNullOrEmpty(u.Name) ? "Unknown user" : u.Name, ct);
    }

    private static string Name(Dictionary<int, string> names, int userId) =>
        names.TryGetValue(userId, out var n) ? n : "Unknown user";

    /// <summary>503 (never 500) when the assistant can't run — Gemini unconfigured or the call failed. One
    /// consistent degraded path the frontend shows as "the assistant isn't available right now".</summary>
    private static IResult AiUnavailable() => Results.Problem(
        title: "The family assistant is not available.",
        detail: "The family assistant is not available right now. You can do this manually.",
        statusCode: StatusCodes.Status503ServiceUnavailable);
}
