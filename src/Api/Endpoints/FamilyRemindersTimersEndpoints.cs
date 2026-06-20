using Ccusage.Api.Auth;
using Ccusage.Api.Data;
using Ccusage.Api.Data.Entities;
using Ccusage.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Ccusage.Api.Endpoints;

/// <summary>
/// Family Hub F2 — shared REMINDERS and TIMERS (/api/family/reminders, /api/family/timers). Everything
/// is gated by <see cref="Permissions.FamilyUse"/> on top of <c>.RequireAuthorization()</c> and obeys
/// the Family Hub privacy rules:
///
/// <list type="bullet">
///   <item>Items are private to the owning HOUSEHOLD; every member can see and manage them. A caller
///   only ever addresses their OWN household — there is no way to reach another household's items, and a
///   cross-household id is a 404 (existence is never leaked).</item>
///   <item>People are exposed by AppUser id + display name ONLY — an email is NEVER put on the wire.</item>
/// </list>
///
/// Delivery of a fired reminder / finished timer is the background <see cref="FamilyReminderService"/>'s
/// job; these endpoints only own the CRUD. A reminder's target must be a member of the caller's
/// household (default: the caller themselves).
/// </summary>
public static class FamilyRemindersTimersEndpoints
{
    // ---- DTOs (people by userId + name; never email) ----

    public sealed record ReminderDto(
        long Id, string Text, DateTime DueUtc, string Recurrence, bool Active,
        int TargetUserId, string TargetName, int CreatedByUserId, string CreatedByName);

    public sealed record TimerDto(
        long Id, string Label, DateTime EndsUtc, bool Done,
        int StartedByUserId, string StartedByName);

    public sealed record ReminderCreateRequest(string? Text, DateTime? DueUtc, string? Recurrence, int? TargetUserId);
    public sealed record ReminderUpdateRequest(string? Text, DateTime? DueUtc, string? Recurrence, int? TargetUserId);
    public sealed record SnoozeRequest(int? Minutes);
    public sealed record TimerCreateRequest(string? Label, int? DurationSeconds);

    private static readonly string[] Recurrences = { "none", "daily", "weekly", "weekdays" };

    public static void MapFamilyRemindersTimersEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/family")
            .RequireAuthorization()
            .RequirePermission(Permissions.FamilyUse);

        MapReminders(g);
        MapTimers(g);
    }

    // =====================================================================================
    // REMINDERS
    // =====================================================================================

    private static void MapReminders(RouteGroupBuilder g)
    {
        // ---- GET /reminders : the household's reminders ----
        g.MapGet("/reminders", async (
            CurrentUserAccessor me, CurrentHouseholdAccessor households, UsageDbContext db, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;
            var household = (await households.GetOrCreateForCallerAsync(caller, ct))!;

            var reminders = await db.FamilyReminders.AsNoTracking()
                .Where(r => r.HouseholdId == household.Id)
                .OrderBy(r => r.Active ? 0 : 1).ThenBy(r => r.DueUtc)
                .ToListAsync(ct);

            var names = await NamesAsync(db,
                reminders.Select(r => r.TargetUserId).Concat(reminders.Select(r => r.CreatedByUserId)), ct);
            return Results.Ok(reminders.Select(r => ToReminderDto(r, names)).ToList());
        });

        // ---- POST /reminders ----
        g.MapPost("/reminders", async (
            ReminderCreateRequest req, CurrentUserAccessor me, CurrentHouseholdAccessor households,
            UsageDbContext db, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;
            var household = (await households.GetOrCreateForCallerAsync(caller, ct))!;

            var text = (req.Text ?? "").Trim();
            if (string.IsNullOrEmpty(text)) return Results.BadRequest(new { message = "Reminder text is required." });
            if (text.Length > 500) text = text[..500];

            if (req.DueUtc is not DateTime due)
                return Results.BadRequest(new { message = "A due time is required." });
            if (!TryNormalizeRecurrence(req.Recurrence, out var recurrence))
                return Results.BadRequest(new { message = "Recurrence must be none, daily, weekly, or weekdays." });

            // The target defaults to the caller; if given, it must be a member of the caller's household.
            var targetId = req.TargetUserId ?? caller.Id;
            if (!await IsHouseholdMemberAsync(db, household.Id, targetId, ct))
                return Results.BadRequest(new { message = "The reminder target must be a member of your family." });

            var reminder = new FamilyReminder
            {
                HouseholdId = household.Id,
                CreatedByUserId = caller.Id,
                TargetUserId = targetId,
                Text = text,
                DueUtc = DateTime.SpecifyKind(due, DateTimeKind.Utc),
                Recurrence = recurrence,
                Active = true,
                CreatedUtc = DateTime.UtcNow,
            };
            db.FamilyReminders.Add(reminder);
            await db.SaveChangesAsync(ct);

            return Results.Ok(await SingleReminderDtoAsync(db, reminder, ct));
        });

        // ---- PUT /reminders/{id} ----
        g.MapPut("/reminders/{id:long}", async (
            long id, ReminderUpdateRequest req, CurrentUserAccessor me, CurrentHouseholdAccessor households,
            UsageDbContext db, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;
            var household = (await households.GetOrCreateForCallerAsync(caller, ct))!;

            var reminder = await db.FamilyReminders.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (reminder is null || reminder.HouseholdId != household.Id) return NotFound();

            if (req.Text is not null)
            {
                var text = req.Text.Trim();
                if (string.IsNullOrEmpty(text)) return Results.BadRequest(new { message = "Reminder text is required." });
                reminder.Text = text.Length > 500 ? text[..500] : text;
            }
            if (req.DueUtc is DateTime due)
            {
                reminder.DueUtc = DateTime.SpecifyKind(due, DateTimeKind.Utc);
                reminder.Active = true; // re-scheduling revives a fired one-shot
            }
            if (req.Recurrence is not null)
            {
                if (!TryNormalizeRecurrence(req.Recurrence, out var recurrence))
                    return Results.BadRequest(new { message = "Recurrence must be none, daily, weekly, or weekdays." });
                reminder.Recurrence = recurrence;
            }
            if (req.TargetUserId is int targetId)
            {
                if (!await IsHouseholdMemberAsync(db, household.Id, targetId, ct))
                    return Results.BadRequest(new { message = "The reminder target must be a member of your family." });
                reminder.TargetUserId = targetId;
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(await SingleReminderDtoAsync(db, reminder, ct));
        });

        // ---- POST /reminders/{id}/snooze ----
        g.MapPost("/reminders/{id:long}/snooze", async (
            long id, SnoozeRequest req, CurrentUserAccessor me, CurrentHouseholdAccessor households,
            UsageDbContext db, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;
            var household = (await households.GetOrCreateForCallerAsync(caller, ct))!;

            var reminder = await db.FamilyReminders.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (reminder is null || reminder.HouseholdId != household.Id) return NotFound();

            var minutes = req.Minutes ?? 10;
            if (minutes < 1) minutes = 1;
            if (minutes > 7 * 24 * 60) minutes = 7 * 24 * 60; // cap snooze at a week

            // Snooze pushes the next fire out from now and re-activates a fired reminder.
            reminder.DueUtc = DateTime.UtcNow.AddMinutes(minutes);
            reminder.Active = true;
            await db.SaveChangesAsync(ct);

            return Results.Ok(await SingleReminderDtoAsync(db, reminder, ct));
        });

        // ---- DELETE /reminders/{id} ----
        g.MapDelete("/reminders/{id:long}", async (
            long id, CurrentUserAccessor me, CurrentHouseholdAccessor households,
            UsageDbContext db, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;
            var household = (await households.GetOrCreateForCallerAsync(caller, ct))!;

            var reminder = await db.FamilyReminders.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (reminder is null || reminder.HouseholdId != household.Id) return NotFound();

            db.FamilyReminders.Remove(reminder);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    // =====================================================================================
    // TIMERS
    // =====================================================================================

    private static void MapTimers(RouteGroupBuilder g)
    {
        // ---- GET /timers : the household's active/recent timers ----
        g.MapGet("/timers", async (
            CurrentUserAccessor me, CurrentHouseholdAccessor households, UsageDbContext db, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;
            var household = (await households.GetOrCreateForCallerAsync(caller, ct))!;

            // Active timers first (soonest-ending), then recently-finished ones.
            var timers = await db.FamilyTimers.AsNoTracking()
                .Where(t => t.HouseholdId == household.Id)
                .OrderBy(t => t.Done ? 1 : 0).ThenBy(t => t.EndsUtc)
                .Take(50)
                .ToListAsync(ct);

            var names = await NamesAsync(db, timers.Select(t => t.StartedByUserId), ct);
            return Results.Ok(timers.Select(t => ToTimerDto(t, names)).ToList());
        });

        // ---- POST /timers ----
        g.MapPost("/timers", async (
            TimerCreateRequest req, CurrentUserAccessor me, CurrentHouseholdAccessor households,
            UsageDbContext db, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;
            var household = (await households.GetOrCreateForCallerAsync(caller, ct))!;

            var label = (req.Label ?? "").Trim();
            if (string.IsNullOrEmpty(label)) label = "Timer";
            if (label.Length > 120) label = label[..120];

            var seconds = req.DurationSeconds ?? 0;
            if (seconds < 1) return Results.BadRequest(new { message = "A timer duration (in seconds) is required." });
            if (seconds > 24 * 60 * 60) seconds = 24 * 60 * 60; // cap a shared countdown at a day

            var now = DateTime.UtcNow;
            var timer = new FamilyTimer
            {
                HouseholdId = household.Id,
                StartedByUserId = caller.Id,
                Label = label,
                EndsUtc = now.AddSeconds(seconds),
                Done = false,
                CreatedUtc = now,
            };
            db.FamilyTimers.Add(timer);
            await db.SaveChangesAsync(ct);

            var names = await NamesAsync(db, new[] { timer.StartedByUserId }, ct);
            return Results.Ok(ToTimerDto(timer, names));
        });

        // ---- DELETE /timers/{id} : cancel ----
        g.MapDelete("/timers/{id:long}", async (
            long id, CurrentUserAccessor me, CurrentHouseholdAccessor households,
            UsageDbContext db, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;
            var household = (await households.GetOrCreateForCallerAsync(caller, ct))!;

            var timer = await db.FamilyTimers.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (timer is null || timer.HouseholdId != household.Id) return NotFound();

            db.FamilyTimers.Remove(timer);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    // =====================================================================================
    // HELPERS
    // =====================================================================================

    private static bool TryNormalizeRecurrence(string? raw, out string recurrence)
    {
        recurrence = string.IsNullOrWhiteSpace(raw) ? "none" : raw.Trim().ToLowerInvariant();
        return Recurrences.Contains(recurrence);
    }

    private static async Task<bool> IsHouseholdMemberAsync(
        UsageDbContext db, int householdId, int userId, CancellationToken ct) =>
        await db.HouseholdMembers.AsNoTracking()
            .AnyAsync(m => m.HouseholdId == householdId && m.UserId == userId, ct);

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

    private static ReminderDto ToReminderDto(FamilyReminder r, Dictionary<int, string> names) =>
        new(r.Id, r.Text, r.DueUtc, r.Recurrence, r.Active,
            r.TargetUserId, Name(names, r.TargetUserId),
            r.CreatedByUserId, Name(names, r.CreatedByUserId));

    private static TimerDto ToTimerDto(FamilyTimer t, Dictionary<int, string> names) =>
        new(t.Id, t.Label, t.EndsUtc, t.Done, t.StartedByUserId, Name(names, t.StartedByUserId));

    private static async Task<ReminderDto> SingleReminderDtoAsync(
        UsageDbContext db, FamilyReminder reminder, CancellationToken ct)
    {
        var names = await NamesAsync(db, new[] { reminder.TargetUserId, reminder.CreatedByUserId }, ct);
        return ToReminderDto(reminder, names);
    }

    private static IResult NotFound() =>
        Results.NotFound(new { message = "That item doesn't exist." });
}
