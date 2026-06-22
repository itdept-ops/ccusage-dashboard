using Ccusage.Api.Auth;
using Ccusage.Api.Data;
using Ccusage.Api.Data.Entities;
using Ccusage.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Ccusage.Api.Endpoints;

/// <summary>
/// 75 Hard (Relaxed ruleset) — a six-task daily challenge layered on the food/fitness tracker
/// (<c>/api/challenge</c>). Gated by the SAME tracker permissions, with NO dedicated permission: own use needs
/// <see cref="Permissions.TrackerSelf"/>; a coach/admin read of someone else needs <see cref="Permissions.TrackerViewAll"/>
/// (or the target's contacts-sharing) via the SAME <c>CanViewAsync</c> gate the tracker uses.
///
/// <para>AUTO scoring (diet / water / workouts) is recomputed LIVE from the tracker on every read by the shared
/// <see cref="HardChallengeScoring"/> helper — never trusted from the stored cache. Only the MANUAL portion
/// (read, photo-boolean, no-alcohol, confession, workout-2-outdoor, diet-override) plus the cheat flag are
/// persisted. There is NEVER an image stored: the progress photo is a boolean attestation only.</para>
///
/// <para>PRIVACY mirrors the tracker: the client never sends an email — it sends <c>?user={userId}</c>, resolved
/// server-side; a non-existent / not-viewable target is 404 (never leak existence); a viewer never sees the
/// owner's confessions (nulled) and never an email on the wire.</para>
/// </summary>
public static class HardChallengeEndpoints
{
    private const int TotalDays = 75;
    private const int MaxCheatDays = 10;

    // ---- Request DTOs ----
    public sealed record StartChallengeRequest(string? StartDate);

    public sealed record UpsertDayRequest(
        string? Date, bool? ReadOk, bool? PhotoTaken, bool? NoAlcohol,
        string? Confession, bool? Workout2Outdoor, bool? DietOverride);

    public sealed record CheatDaysRequest(string[]? Add, string[]? Remove);

    // ---- Response DTOs ----
    /// <summary>One day in the grid: the six task results (auto recomputed live) + manual flags + whether the
    /// day is complete. <see cref="Confession"/> is NULLED for a viewer (never the owner's private narration).</summary>
    public sealed record DayDto(
        string Date, int? DayNumber,
        bool DietOk, bool? DietOverride,
        bool WaterGallonOk, bool Workout1Ok, bool Workout2Ok, bool Workout2Outdoor,
        bool ReadOk, bool PhotoTaken, bool NoAlcohol,
        bool IsCheatDay, bool Complete,
        string? Confession);

    /// <summary>The active challenge with its derived current day, streaks, and the full day grid.</summary>
    public sealed record ChallengeDto(
        int Id, int UserId, string UserName, bool ReadOnly,
        string StartDate, string Ruleset, string Status,
        int CurrentDay, int TotalDays,
        int CompletedDays, int CurrentStreak, int LongestStreak, int ConfessionsUsed,
        IReadOnlyList<DayDto> Days);

    /// <summary>A person whose 75 Hard the caller may view (userId + display name only — NEVER an email).</summary>
    public sealed record SharedPersonDto(int UserId, string Name, string? Picture);

    public static void MapHardChallengeEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/challenge")
            .RequireAuthorization()
            .RequirePermission(Permissions.TrackerSelf);

        // ---- GET / : the active challenge (own, or someone else's read-only when permitted) or null ----
        g.MapGet("/", async (
            int? user, CurrentUserAccessor me, UsageDbContext db, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!; // tracker.self filter guarantees non-null

            var (target, isSelf, resolveError) = await ResolveTargetAsync(user, caller, db, ct);
            if (resolveError is { } err) return err;
            if (!isSelf && !await TrackerVisibility.CanViewAsync(db, caller, target, ct))
                return Results.NotFound(); // never leak that the user / their challenge exists

            var challenge = await db.HardChallenges
                .FirstOrDefaultAsync(c => c.UserEmail == target && c.Status == HardChallengeStatus.Active, ct);
            // Write an explicit JSON `null` body so the client reliably parses "no active challenge".
            // (Results.Ok(null)/Results.Json(null) write an EMPTY body under this serializer, not "null".)
            if (challenge is null) return Results.Content("null", "application/json");

            var dto = await BuildChallengeAsync(db, challenge, target, readOnly: !isSelf, persist: isSelf, ct);
            return Results.Ok(dto);
        });

        // ---- POST / : start a challenge (owner; one active at a time) ----
        g.MapPost("/", async (
            StartChallengeRequest? req, CurrentUserAccessor me, UsageDbContext db, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;

            DateOnly start;
            if (string.IsNullOrWhiteSpace(req?.StartDate))
                start = await TrackerVisibility.DisplayTzTodayAsync(db, ct);
            else if (!TryParseDate(req.StartDate, out start))
                return Results.BadRequest(new { message = "A valid start date (yyyy-MM-dd) is required." });

            // Reject obviously-out-of-range starts so a fat-fingered year can't poison the day math.
            var today = await TrackerVisibility.DisplayTzTodayAsync(db, ct);
            if (start < today.AddYears(-1) || start > today.AddYears(1))
                return Results.BadRequest(new { message = "That start date is out of range." });

            // One-active invariant (filtered-unique on Status=0): fast-path check, then catch the race.
            if (await db.HardChallenges.AnyAsync(
                    c => c.UserEmail == caller.Email && c.Status == HardChallengeStatus.Active, ct))
                return Results.Conflict(new { message = "You already have an active challenge." });

            var now = DateTime.UtcNow;
            var challenge = new HardChallenge
            {
                UserEmail = caller.Email,
                StartDate = start,
                Ruleset = HardRuleset.Relaxed,
                Status = HardChallengeStatus.Active,
                CreatedUtc = now,
                UpdatedUtc = now,
            };
            db.HardChallenges.Add(challenge);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (TrackerVisibility.IsUniqueViolation(ex))
            {
                // A concurrent start won the filtered-unique race — surface the same 409 as the fast-path.
                db.ChangeTracker.Clear();
                return Results.Conflict(new { message = "You already have an active challenge." });
            }

            var dto = await BuildChallengeAsync(db, challenge, caller.Email, readOnly: false, persist: true, ct);
            return Results.Ok(dto);
        });

        // ---- GET /day : one day's six-task breakdown (own, or read-only when permitted) ----
        g.MapGet("/day", async (
            string? date, int? user, CurrentUserAccessor me, UsageDbContext db, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;

            var (target, isSelf, resolveError) = await ResolveTargetAsync(user, caller, db, ct);
            if (resolveError is { } err) return err;
            if (!isSelf && !await TrackerVisibility.CanViewAsync(db, caller, target, ct))
                return Results.NotFound();

            var challenge = await db.HardChallenges.AsNoTracking()
                .FirstOrDefaultAsync(c => c.UserEmail == target && c.Status == HardChallengeStatus.Active, ct);
            if (challenge is null) return Results.NotFound();

            var localDate = TryParseDate(date, out var d)
                ? d : await TrackerVisibility.DisplayTzTodayAsync(db, ct);

            var row = await db.HardChallengeDays.AsNoTracking()
                .FirstOrDefaultAsync(x => x.UserEmail == target && x.LocalDate == localDate, ct);
            var dto = await BuildDayDtoAsync(db, target, challenge, localDate, row, readOnly: !isSelf, ct);
            return Results.Ok(dto);
        });

        // ---- PUT /day : upsert the MANUAL portion of a day (owner only) ----
        // Persists ONLY: ReadOk, PhotoTaken (boolean — any image payload is impossible/ignored), NoAlcohol,
        // Confession, Workout2Outdoor, DietOverride. The auto bits are recomputed live, never written from input.
        g.MapPut("/day", async (
            UpsertDayRequest req, CurrentUserAccessor me, UsageDbContext db, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;

            if (!TryParseDate(req?.Date, out var localDate))
                return Results.BadRequest(new { message = "A valid date (yyyy-MM-dd) is required." });

            var challenge = await db.HardChallenges
                .FirstOrDefaultAsync(c => c.UserEmail == caller.Email && c.Status == HardChallengeStatus.Active, ct);
            if (challenge is null) return Results.NotFound();

            var confession = Trunc(req!.Confession?.Trim(), 280);
            if (string.IsNullOrEmpty(confession)) confession = null;

            var now = DateTime.UtcNow;
            var row = await db.HardChallengeDays
                .FirstOrDefaultAsync(x => x.UserEmail == caller.Email && x.LocalDate == localDate, ct);
            var hadConfession = row?.Confession is not null;
            if (row is null)
            {
                row = new HardChallengeDay
                {
                    ChallengeId = challenge.Id,
                    UserEmail = caller.Email,
                    LocalDate = localDate,
                    CreatedUtc = now,
                    UpdatedUtc = now,
                };
                db.HardChallengeDays.Add(row);
            }
            ApplyManual(row, req, confession, now);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (TrackerVisibility.IsUniqueViolation(ex))
            {
                // A concurrent insert for the same (user, date) won; reload + overwrite the manual fields.
                db.ChangeTracker.Clear();
                challenge = await db.HardChallenges
                    .FirstAsync(c => c.UserEmail == caller.Email && c.Status == HardChallengeStatus.Active, ct);
                row = await db.HardChallengeDays
                    .FirstAsync(x => x.UserEmail == caller.Email && x.LocalDate == localDate, ct);
                hadConfession = row.Confession is not null;
                ApplyManual(row, req, confession, now);
                await db.SaveChangesAsync(ct);
            }

            // ConfessionsUsed counts the act of confessing: bump on a 0→1 transition for this day.
            if (!hadConfession && confession is not null)
            {
                challenge.ConfessionsUsed += 1;
                challenge.UpdatedUtc = now;
                await db.SaveChangesAsync(ct);
            }

            var dto = await BuildDayDtoAsync(db, caller.Email, challenge, localDate, row, readOnly: false, ct);
            return Results.Ok(dto);
        });

        // ---- POST /cheat-days : pre-declare / clear FUTURE-only cheat dates within the window (owner) ----
        g.MapPost("/cheat-days", async (
            CheatDaysRequest? req, CurrentUserAccessor me, UsageDbContext db, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;

            var challenge = await db.HardChallenges
                .FirstOrDefaultAsync(c => c.UserEmail == caller.Email && c.Status == HardChallengeStatus.Active, ct);
            if (challenge is null) return Results.NotFound();

            var today = await TrackerVisibility.DisplayTzTodayAsync(db, ct);
            var windowEnd = challenge.StartDate.AddDays(TotalDays - 1);

            var toAdd = ParseDates(req?.Add);
            var toRemove = ParseDates(req?.Remove);

            // Cheat days are FUTURE-ONLY (strictly after today) and must fall within the challenge window.
            foreach (var dt in toAdd)
                if (dt <= today || dt < challenge.StartDate || dt > windowEnd)
                    return Results.BadRequest(new { message = "Cheat days must be future dates within the challenge window." });

            var now = DateTime.UtcNow;

            // Remove first (clearing the flag; delete the row only if it has no other persisted content).
            foreach (var dt in toRemove)
            {
                var row = await db.HardChallengeDays
                    .FirstOrDefaultAsync(x => x.UserEmail == caller.Email && x.LocalDate == dt, ct);
                if (row is null) continue;
                row.IsCheatDay = false;
                row.UpdatedUtc = now;
            }
            await db.SaveChangesAsync(ct);

            // Enforce the small cap on the resulting set of future cheat days.
            var existingFuture = await db.HardChallengeDays
                .Where(x => x.UserEmail == caller.Email && x.IsCheatDay && x.LocalDate > today)
                .Select(x => x.LocalDate)
                .ToListAsync(ct);
            var resulting = new HashSet<DateOnly>(existingFuture);
            foreach (var dt in toAdd) resulting.Add(dt);
            if (resulting.Count > MaxCheatDays)
                return Results.BadRequest(new { message = $"At most {MaxCheatDays} cheat days may be declared." });

            foreach (var dt in toAdd)
            {
                var row = await db.HardChallengeDays
                    .FirstOrDefaultAsync(x => x.UserEmail == caller.Email && x.LocalDate == dt, ct);
                if (row is null)
                {
                    row = new HardChallengeDay
                    {
                        ChallengeId = challenge.Id,
                        UserEmail = caller.Email,
                        LocalDate = dt,
                        IsCheatDay = true,
                        CreatedUtc = now,
                        UpdatedUtc = now,
                    };
                    db.HardChallengeDays.Add(row);
                }
                else
                {
                    row.IsCheatDay = true;
                    row.UpdatedUtc = now;
                }
            }
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (TrackerVisibility.IsUniqueViolation(ex))
            {
                db.ChangeTracker.Clear(); // a concurrent insert raced one of the dates; re-read below
            }

            var refreshed = await db.HardChallenges
                .FirstAsync(c => c.Id == challenge.Id, ct);
            var dto = await BuildChallengeAsync(db, refreshed, caller.Email, readOnly: false, persist: true, ct);
            return Results.Ok(dto);
        });

        // ---- GET /shared : people whose 75 Hard the caller may view (userId + name only, NEVER email) ----
        // Mirrors GET /api/tracker/shared exactly (TrackerProfile.ShareWithContacts + mutual ChatContact).
        g.MapGet("/shared", async (CurrentUserAccessor me, UsageDbContext db, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;

            IQueryable<AppUser> usersQ;
            if (caller.Permissions.Contains(Permissions.TrackerViewAll))
            {
                usersQ = db.Users.AsNoTracking().Where(u => u.IsEnabled && u.Email != caller.Email);
            }
            else
            {
                var sharingEmails = db.ChatContacts.AsNoTracking()
                    .Where(c => c.ContactEmail == caller.Email)
                    .Join(db.TrackerProfiles.AsNoTracking().Where(p => p.ShareWithContacts),
                        c => c.OwnerEmail, p => p.UserEmail, (c, p) => p.UserEmail);
                usersQ = db.Users.AsNoTracking()
                    .Where(u => u.IsEnabled && u.Email != caller.Email && sharingEmails.Contains(u.Email));
            }

            var people = await usersQ
                .OrderBy(u => u.Name == "" ? u.Email : u.Name)
                .Select(u => new SharedPersonDto(
                    u.Id,
                    string.IsNullOrEmpty(u.Name) ? "Unknown user" : u.Name,
                    u.Picture))
                .ToListAsync(ct);
            return Results.Ok(people);
        });
    }

    // =====================================================================================
    // Building the challenge + day DTOs (auto scoring recomputed LIVE from the tracker)
    // =====================================================================================

    /// <summary>
    /// Build the full challenge DTO: load the day rows, recompute every day's auto scoring live from the
    /// tracker, derive the current day + streaks, and (when <paramref name="persist"/> = owner read) refresh the
    /// cached aggregate counts + auto-complete the challenge when day 75 has passed.
    /// </summary>
    private static async Task<ChallengeDto> BuildChallengeAsync(
        UsageDbContext db, HardChallenge challenge, string email, bool readOnly, bool persist, CancellationToken ct)
    {
        var today = await TrackerVisibility.DisplayTzTodayAsync(db, ct);
        var currentDay = CurrentDay(challenge.StartDate, today);
        var windowEnd = challenge.StartDate.AddDays(TotalDays - 1);

        var rows = await db.HardChallengeDays.AsNoTracking()
            .Where(x => x.UserEmail == email
                && x.LocalDate >= challenge.StartDate && x.LocalDate <= windowEnd)
            .OrderBy(x => x.LocalDate)
            .ToListAsync(ct);
        var rowByDate = rows.ToDictionary(r => r.LocalDate);

        // Pull the whole window's tracker facts in three set-based reads, then score each day in memory.
        var profile = await db.TrackerProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserEmail == email, ct);
        var facts = await LoadTrackerFactsAsync(db, email, challenge.StartDate, windowEnd, ct);

        var days = new List<DayDto>(TotalDays);
        var streakDays = new List<HardChallengeScoring.StreakDay>();
        var completedDays = 0;

        for (var i = 0; i < TotalDays; i++)
        {
            var date = challenge.StartDate.AddDays(i);
            rowByDate.TryGetValue(date, out var row);
            var score = ScoreDay(date, row, profile, facts);

            if (score.Complete) completedDays++;

            // Only PAST and CURRENT days contribute to the streak (a future day hasn't happened yet).
            if (date <= today)
                streakDays.Add(new HardChallengeScoring.StreakDay(
                    score.Complete, row?.IsCheatDay ?? false, row?.Confession is not null));

            days.Add(ToDayDto(date, i + 1, row, score, readOnly));
        }

        var streak = HardChallengeScoring.RelaxedStreak(streakDays);

        // Day 75 complete ⇒ Completed (finisher state). Compute against the recomputed grid.
        var day75 = days.Count == TotalDays && days[TotalDays - 1].Complete;
        var status = challenge.Status;
        if (day75 && status == HardChallengeStatus.Active) status = HardChallengeStatus.Completed;

        if (persist)
        {
            var tracked = await db.HardChallenges.FirstOrDefaultAsync(c => c.Id == challenge.Id, ct);
            if (tracked is not null)
            {
                var dirty = tracked.CompletedDays != completedDays
                    || tracked.CurrentStreak != streak.CurrentStreak
                    || tracked.LongestStreak != streak.LongestStreak
                    || tracked.Status != status;
                if (dirty)
                {
                    tracked.CompletedDays = completedDays;
                    tracked.CurrentStreak = streak.CurrentStreak;
                    tracked.LongestStreak = streak.LongestStreak;
                    tracked.Status = status;
                    tracked.UpdatedUtc = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                }
            }
        }

        var owner = await db.Users.AsNoTracking()
            .Where(u => u.Email == email)
            .Select(u => new { u.Id, u.Name })
            .FirstOrDefaultAsync(ct);

        return new ChallengeDto(
            challenge.Id,
            owner?.Id ?? 0,
            owner is null || string.IsNullOrEmpty(owner.Name) ? "Unknown user" : owner.Name,
            readOnly,
            challenge.StartDate.ToString("yyyy-MM-dd"),
            challenge.Ruleset.ToString(),
            status.ToString(),
            currentDay,
            TotalDays,
            completedDays,
            streak.CurrentStreak,
            streak.LongestStreak,
            challenge.ConfessionsUsed,
            days);
    }

    /// <summary>Build a single day DTO, recomputing its auto scoring live from the tracker.</summary>
    private static async Task<DayDto> BuildDayDtoAsync(
        UsageDbContext db, string email, HardChallenge challenge, DateOnly date,
        HardChallengeDay? row, bool readOnly, CancellationToken ct)
    {
        var profile = await db.TrackerProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserEmail == email, ct);
        var facts = await LoadTrackerFactsAsync(db, email, date, date, ct);
        var score = ScoreDay(date, row, profile, facts);
        var dayNumber = WithinWindow(challenge.StartDate, date)
            ? (int?)CurrentDay(challenge.StartDate, date) : null;
        return ToDayDto(date, dayNumber, row, score, readOnly);
    }

    /// <summary>The tracker facts for one day, used to recompute its auto scoring.</summary>
    private readonly record struct DayFacts(
        int CaloriesIn, double ProteinG, double CarbG, double FatG, int HydrationMl, int WorkoutCount);

    /// <summary>
    /// Load the per-day tracker facts for [from, to] in three set-based queries (food, exercise, hydration).
    /// Mirrors the tracker day roll-up: caloriesIn = sum of food calories; macros = sum of food macros; the
    /// workout count = exercises with DurationMin &gt;= 45 that day; hydration = sum of the day's drink volumes.
    /// </summary>
    private static async Task<Dictionary<DateOnly, DayFacts>> LoadTrackerFactsAsync(
        UsageDbContext db, string email, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var foods = await db.FoodEntries.AsNoTracking()
            .Where(f => f.UserEmail == email && f.LocalDate >= from && f.LocalDate <= to)
            .GroupBy(f => f.LocalDate)
            .Select(grp => new
            {
                Date = grp.Key,
                Calories = grp.Sum(f => f.Calories),
                Protein = grp.Sum(f => f.ProteinG),
                Carb = grp.Sum(f => f.CarbG),
                Fat = grp.Sum(f => f.FatG),
            })
            .ToListAsync(ct);

        // Workout count = exercises of >= 45 minutes that day (>=1 ⇒ workout1, >=2 ⇒ workout2).
        var workouts = await db.ExerciseEntries.AsNoTracking()
            .Where(x => x.UserEmail == email && x.LocalDate >= from && x.LocalDate <= to
                && x.DurationMin != null && x.DurationMin >= HardChallengeScoring.WorkoutMinMinutes)
            .GroupBy(x => x.LocalDate)
            .Select(grp => new { Date = grp.Key, Count = grp.Count() })
            .ToListAsync(ct);

        var hydration = await db.HydrationEntries.AsNoTracking()
            .Where(h => h.UserEmail == email && h.LocalDate >= from && h.LocalDate <= to)
            .GroupBy(h => h.LocalDate)
            .Select(grp => new { Date = grp.Key, Ml = grp.Sum(h => h.AmountMl) })
            .ToListAsync(ct);

        var map = new Dictionary<DateOnly, DayFacts>();
        DayFacts Get(DateOnly d) => map.TryGetValue(d, out var f) ? f : default;
        foreach (var f in foods)
            map[f.Date] = Get(f.Date) with
            {
                CaloriesIn = f.Calories,
                ProteinG = Math.Round(f.Protein, 1),
                CarbG = Math.Round(f.Carb, 1),
                FatG = Math.Round(f.Fat, 1),
            };
        foreach (var w in workouts)
            map[w.Date] = Get(w.Date) with { WorkoutCount = w.Count };
        foreach (var h in hydration)
            map[h.Date] = Get(h.Date) with { HydrationMl = h.Ml };
        return map;
    }

    /// <summary>Score one day's six tasks from its tracker facts, profile goals, and persisted manual fields.</summary>
    private static HardChallengeScoring.HardDayScore ScoreDay(
        DateOnly date, HardChallengeDay? row, TrackerProfile? profile, Dictionary<DateOnly, DayFacts> facts)
    {
        var f = facts.TryGetValue(date, out var v) ? v : default;
        var input = new HardChallengeScoring.HardDayInput(
            f.CaloriesIn, f.ProteinG, f.CarbG, f.FatG,
            profile?.DailyCalorieGoal, profile?.ProteinGoalG, profile?.CarbGoalG, profile?.FatGoalG,
            f.HydrationMl, f.WorkoutCount, row?.DietOverride);
        return HardChallengeScoring.Score(
            input, row?.ReadOk ?? false, row?.PhotoTaken ?? false, row?.NoAlcohol ?? true);
    }

    private static DayDto ToDayDto(
        DateOnly date, int? dayNumber, HardChallengeDay? row,
        HardChallengeScoring.HardDayScore score, bool readOnly)
        => new(
            date.ToString("yyyy-MM-dd"),
            dayNumber,
            score.DietOk,
            row?.DietOverride,
            score.WaterGallonOk,
            score.Workout1Ok,
            score.Workout2Ok,
            row?.Workout2Outdoor ?? false,
            score.ReadOk,
            score.PhotoTaken,
            score.NoAlcohol,
            row?.IsCheatDay ?? false,
            score.Complete,
            // A viewer NEVER sees the owner's private confession narration.
            readOnly ? null : row?.Confession);

    // =====================================================================================
    // Small helpers
    // =====================================================================================

    /// <summary>Apply ONLY the manual fields from the request onto the day row (auto bits stay untouched).</summary>
    private static void ApplyManual(HardChallengeDay row, UpsertDayRequest req, string? confession, DateTime now)
    {
        if (req.ReadOk is { } r) row.ReadOk = r;
        if (req.PhotoTaken is { } p) row.PhotoTaken = p;
        if (req.NoAlcohol is { } na) row.NoAlcohol = na;
        if (req.Workout2Outdoor is { } o) row.Workout2Outdoor = o;
        // DietOverride is tri-state: the request can set true/false; it can't be cleared back to null here
        // (a null in the payload means "leave as-is"). A future explicit clear can be added if needed.
        if (req.DietOverride is { } d) row.DietOverride = d;
        // Confession: a non-empty string sets it; otherwise leave the existing value (PUT is a partial upsert).
        if (confession is not null) row.Confession = confession;
        row.UpdatedUtc = now;
    }

    /// <summary>Derived current day (1..75): clamp((date - start).Days + 1, 1, 75). NEVER stored.</summary>
    private static int CurrentDay(DateOnly start, DateOnly date) =>
        Math.Clamp((date.DayNumber - start.DayNumber) + 1, 1, TotalDays);

    private static bool WithinWindow(DateOnly start, DateOnly date) =>
        date >= start && date <= start.AddDays(TotalDays - 1);

    /// <summary>Resolve the optional <c>?user={id}</c> to a target email (NEVER accept an email). Returns the
    /// target, whether it's self, and a non-null IResult on a validation/visibility-existence failure.</summary>
    private static async Task<(string Target, bool IsSelf, IResult? Error)> ResolveTargetAsync(
        int? user, CurrentUserAccessor.CurrentUser caller, UsageDbContext db, CancellationToken ct)
    {
        if (user is not int targetId)
            return (caller.Email, true, null);
        if (targetId <= 0)
            return ("", false, Results.BadRequest(new { message = "`user` must be a positive user id." }));

        var targetEmail = await db.Users.AsNoTracking()
            .Where(u => u.Id == targetId).Select(u => u.Email).FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(targetEmail))
            return ("", false, Results.NotFound()); // never leak existence

        var isSelf = string.Equals(targetEmail, caller.Email, StringComparison.OrdinalIgnoreCase);
        return (targetEmail, isSelf, null);
    }

    private static List<DateOnly> ParseDates(string[]? raw)
    {
        var list = new List<DateOnly>();
        if (raw is null) return list;
        foreach (var s in raw)
            if (TryParseDate(s, out var d) && !list.Contains(d)) list.Add(d);
        return list;
    }

    private static bool TryParseDate(string? date, out DateOnly result) =>
        DateOnly.TryParseExact((date ?? "").Trim(), "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out result);

    private static string? Trunc(string? s, int max) =>
        s is null ? null : (s.Length > max ? s[..max] : s);
}
