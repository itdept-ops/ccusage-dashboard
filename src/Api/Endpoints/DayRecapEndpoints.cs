using System.Globalization;
using System.Text;
using Ccusage.Api.Auth;
using Ccusage.Api.Data;
using Ccusage.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Ccusage.Api.Endpoints;

/// <summary>
/// THE DAY RECAP (<c>GET /api/ai/day-recap</c>): a warm, grounded "here's your day" for the CALLER's OWN
/// chosen local date. It gathers the day's cross-domain snapshot from the EXISTING tables/services (tracker
/// food/exercise/sleep/water/caffeine/coffee, journal mood, habit completions, meds adherence, the social
/// ActivityEvents spine, family reminders/meals, location places, and — optionally — finance spend), each
/// section included ONLY when the caller holds its permission (gated like Insights / Ask-my-life), and
/// produces a DETERMINISTIC result: a chronological TIMELINE of the day's moments + a STATS rollup + a few
/// highlight facts. A floored Gemini step then NARRATES ONLY those facts into a 2–4 sentence recap.
///
/// <para>HARD INVARIANTS (load-bearing):</para>
/// <list type="bullet">
///   <item>OWNER-SCOPED — every read filters to the caller's own rows (<c>UserEmail == caller.Email</c> /
///   <c>ActorEmail == caller.Email</c>); the household reads are scoped to the caller's OWN household and to
///   the date. NO other user's data, ever; no email/PII on the wire.</item>
///   <item>PERMISSION-GATED PER DOMAIN — the route needs <see cref="Permissions.TrackerSelf"/> (an EXISTING
///   perm — no new permission, keeping the Permissions exact-count test stable). Journal/habits/meds ride
///   tracker.self (their own pages do too); family reminders/meals need <see cref="Permissions.FamilyUse"/>;
///   finance spend needs <see cref="Permissions.FamilyFinance"/>; location places need
///   <see cref="Permissions.LocationSelf"/>. Meds + location are OWNER-ONLY.</item>
///   <item>FLOORED AI — the deterministic timeline is the ALWAYS-200 floor. The narrative is added ONLY when
///   the caller holds <see cref="Permissions.TrackerAi"/> AND Gemini is configured; it floors to
///   <c>narrative: null</c> on off/unconfigured/error (NEVER 503). NON-MEDICAL framing for health moments.</item>
///   <item>NO MIGRATION — everything is computed LIVE from existing tables; a short in-memory cache holds the
///   narrative only. Nothing is written. A public PII-safe SHARE is OUT OF SCOPE (in-app only) — follow-up.</item>
/// </list>
/// </summary>
public static class DayRecapEndpoints
{
    private const int DefaultCaffeineMgPerCup = 95; // matches TrackerEndpoints / InsightsEndpoints

    // ---- DTOs (the exact wire contract) ----

    /// <summary>One moment on the day's chronological timeline. <see cref="Time"/> is a local "HH:mm" stamp
    /// (or "" for an all-day/undated moment that sorts to the end), <see cref="Domain"/> is the accent/source
    /// hint (food|exercise|sleep|hydration|coffee|journal|habits|meds|activity|family|location|finance),
    /// <see cref="Icon"/> is a short token, and <see cref="Label"/> is a SHORT factual label
    /// (e.g. "logged a 5k run", "lunch 620 kcal", "mood: focused", "2/3 habits done", "meds: all taken").
    /// Carries NO email / PII.</summary>
    public sealed record DayMomentDto(string Time, string Domain, string Icon, string Label);

    /// <summary>The day's deterministic STATS rollup (only the figures the caller's permitted domains produced;
    /// a null/zero field simply wasn't logged). Carries NO email / PII.</summary>
    public sealed record DayStatsDto(
        int? CaloriesIn, int? CalorieGoal, int? ExerciseCalories, int? ExerciseCount,
        double? ProteinG, int? HydrationMl, int? CaffeineMg,
        double? SleepHours, int? RecoveryScore,
        int? HabitsDone, int? HabitsExpected, int? MedsTaken, int? MedsExpected,
        string? Mood, int? PlacesVisited, double? SpendUsd);

    /// <summary>The <c>GET /api/ai/day-recap</c> response. <see cref="Timeline"/> + <see cref="Stats"/> +
    /// <see cref="Highlights"/> are the deterministic always-200 floor; <see cref="Narrative"/> is null when
    /// AI is off/unconfigured/errored (the UI then shows the timeline with no narration banner).
    /// <see cref="DomainsIncluded"/> lists which permitted domains contributed (for the UI). NO email / PII.</summary>
    public sealed record DayRecapResponse(
        string Date,
        IReadOnlyList<DayMomentDto> Timeline,
        DayStatsDto Stats,
        IReadOnlyList<string> Highlights,
        string? Narrative,
        IReadOnlyList<string> DomainsIncluded);

    public static void MapDayRecapEndpoints(this WebApplication app)
    {
        // The DETERMINISTIC floor is reachable with tracker.self alone (no AI). Mapped OUTSIDE the tracker.ai
        // /api/ai group (mirrors /api/ai/tracker-recap) so a tracker.self user always gets the timeline; still
        // rate-limited under the AI policy because the narration step can spend tokens.
        var g = app.MapGroup("/api/ai")
            .RequireAuthorization()
            .RequirePermission(Permissions.TrackerSelf)
            .RequireRateLimiting(AiEndpoints.RateLimitPolicy);

        // ---- GET /api/ai/day-recap?date=yyyy-MM-dd (default today) ----
        g.MapGet("/day-recap", async (
            string? date, CurrentUserAccessor me, CurrentHouseholdAccessor households,
            GeminiService gemini, IMemoryCache cache, UsageDbContext db, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;
            var tz = await TrackerVisibility.DisplayTzAsync(db, ct);
            var localDate = TrackerService.TryParseDate(date, out var parsed)
                ? parsed
                : DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz));

            var snapshot = await BuildDaySnapshotAsync(db, households, caller, localDate, tz, ct);

            // Plain timeline is the floor. Add the warm AI narrative ONLY when the caller holds the AI perm
            // (tracker.ai is the gated, token-spending capability) AND Gemini is configured. A tracker.self
            // caller without tracker.ai always gets the deterministic floor (never spends tokens).
            string? narrative = null;
            if (snapshot.Timeline.Count > 0
                && caller.Permissions.Contains(Permissions.TrackerAi) && gemini.IsConfigured)
            {
                var cacheKey = $"ai:day-recap:{caller.Email}:{localDate:yyyy-MM-dd}";
                if (cache.TryGetValue(cacheKey, out string? cached))
                {
                    narrative = cached; // may be null (a cached "AI declined" floor) — still a hit
                }
                else
                {
                    TrackerRecapResult? ai;
                    try { ai = await gemini.DayRecapNarrativeAsync(snapshot.Facts, ct); }
                    catch { ai = null; }

                    narrative = string.IsNullOrWhiteSpace(ai?.Narrative) ? null : ai!.Narrative;
                    cache.Set(cacheKey, narrative, TimeSpan.FromHours(6));
                }
            }

            return Results.Ok(new DayRecapResponse(
                localDate.ToString("yyyy-MM-dd"),
                snapshot.Timeline, snapshot.Stats, snapshot.Highlights,
                narrative, snapshot.Domains));
        });
    }

    /// <summary>The deterministic snapshot the endpoint serves (and pre-formats into <see cref="Facts"/> for
    /// the optional narration).</summary>
    private sealed record DaySnapshot(
        IReadOnlyList<DayMomentDto> Timeline, DayStatsDto Stats,
        IReadOnlyList<string> Highlights, IReadOnlyList<string> Domains, string Facts);

    // ===================================================================================
    // Owner-scoped, per-permission cross-domain snapshot for ONE local date (PURE rollups)
    // ===================================================================================

    /// <summary>
    /// Build the caller's OWN cross-domain snapshot for <paramref name="localDate"/>. Every read is OWNER-scoped
    /// (the caller's email / the caller's own household) and date-scoped; each DOMAIN is included ONLY when the
    /// caller holds its permission. Produces the chronological timeline + stats rollup + highlights, plus a tight
    /// DATA block the model narrates (never recomputes). All reads are AsNoTracking; nothing is written.
    /// </summary>
    private static async Task<DaySnapshot> BuildDaySnapshotAsync(
        UsageDbContext db, CurrentHouseholdAccessor households, CurrentUserAccessor.CurrentUser caller,
        DateOnly localDate, TimeZoneInfo tz, CancellationToken ct)
    {
        var email = caller.Email;
        var moments = new List<DayMomentDto>();
        var highlights = new List<string>();
        var domains = new List<string>();

        // A logged row's CreatedUtc -> a local "HH:mm" stamp for ordering/labels ("" sorts to day's end).
        string Stamp(DateTime utc) =>
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz).ToString("HH:mm");

        // ---- TRACKER (owner-scoped; the caller's own — tracker.self gates the whole route) ----
        var foods = await db.FoodEntries.AsNoTracking()
            .Where(f => f.UserEmail == email && f.LocalDate == localDate)
            .Select(f => new { f.Description, f.Calories, f.ProteinG, f.CreatedUtc })
            .ToListAsync(ct);
        var exercises = await db.ExerciseEntries.AsNoTracking()
            .Where(x => x.UserEmail == email && x.LocalDate == localDate)
            .Select(x => new { x.Name, x.DurationMin, x.CaloriesBurned, x.CreatedUtc })
            .ToListAsync(ct);
        var sleeps = await db.SleepEntries.AsNoTracking()
            .Where(s => s.UserEmail == email && s.LocalDate == localDate)
            .Select(s => new { s.Hours, s.Quality, s.WakeTime, s.CreatedUtc })
            .ToListAsync(ct);
        var hydration = await db.HydrationEntries.AsNoTracking()
            .Where(h => h.UserEmail == email && h.LocalDate == localDate)
            .Select(h => new { h.AmountMl })
            .ToListAsync(ct);
        var coffees = await db.CoffeeEntries.AsNoTracking()
            .Where(c => c.UserEmail == email && c.LocalDate == localDate)
            .Select(c => new { c.Cups, c.CaffeineMg, c.CreatedUtc })
            .ToListAsync(ct);
        var profile = await db.TrackerProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserEmail == email, ct);

        var caloriesIn = foods.Sum(f => f.Calories);
        var proteinG = foods.Sum(f => f.ProteinG);
        var exerciseCalories = exercises.Sum(x => x.CaloriesBurned);
        var hydrationMl = hydration.Sum(h => h.AmountMl);
        var caffeineMg = coffees.Sum(c => c.CaffeineMg ?? c.Cups * DefaultCaffeineMgPerCup);
        var sleepHours = sleeps.Sum(s => (double)s.Hours);
        var sleepQuality = sleeps.Count > 0 ? sleeps.Max(s => s.Quality) : 0;

        var anyTracker = foods.Count > 0 || exercises.Count > 0 || sleeps.Count > 0
            || hydration.Count > 0 || coffees.Count > 0;
        if (anyTracker) domains.Add("tracker");

        // The local "HH:mm" stamps of the caller's logged ExerciseEntries (the CANONICAL workout rows). A
        // `workout.logged` ActivityEvent at the SAME minute is the social mirror of one of these — it's dropped
        // below so the timeline doesn't show two near-identical workout rows. Stats are unaffected.
        var exerciseStamps = exercises.Select(x => Stamp(x.CreatedUtc))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var f in foods)
            moments.Add(new DayMomentDto(Stamp(f.CreatedUtc), "food", "utensils",
                $"{Trim(f.Description, 40)} · {f.Calories} kcal"));
        foreach (var x in exercises)
            moments.Add(new DayMomentDto(Stamp(x.CreatedUtc), "exercise", "dumbbell",
                $"logged {Trim(x.Name, 40)}" + (x.DurationMin is { } m ? $" · {m} min" : "")
                + $" · {x.CaloriesBurned} kcal"));
        foreach (var s in sleeps)
            moments.Add(new DayMomentDto(s.WakeTime is { } w ? w.ToString("HH:mm") : "", "sleep", "moon",
                $"slept {(double)s.Hours:0.#}h" + (s.Quality is >= 1 and <= 5 ? $" · quality {s.Quality}/5" : "")));
        foreach (var c in coffees)
            moments.Add(new DayMomentDto(Stamp(c.CreatedUtc), "coffee", "coffee",
                $"{c.Cups} cup{(c.Cups == 1 ? "" : "s")} coffee"));
        if (hydrationMl > 0)
            moments.Add(new DayMomentDto("", "hydration", "droplet", $"{hydrationMl} ml water"));

        // ---- JOURNAL (owner-scoped; rides tracker.self, like its own page) ----
        string? mood = null;
        var journal = await db.JournalEntries.AsNoTracking()
            .Where(j => j.UserEmail == email && j.LocalDate == localDate)
            .Select(j => new { j.Mood, j.Energy, j.CreatedUtc })
            .ToListAsync(ct);
        if (journal.Count > 0)
        {
            domains.Add("journal");
            var j = journal[^1];
            mood = string.IsNullOrWhiteSpace(j.Mood) ? null : Trim(j.Mood!, 30);
            if (mood is not null)
                moments.Add(new DayMomentDto(Stamp(j.CreatedUtc), "journal", "feather", $"mood: {mood}"));
        }

        // ---- HABITS (owner-scoped; rides tracker.self) ----
        int? habitsExpected = null, habitsDone = null;
        var habitDays = await db.HabitDays.AsNoTracking()
            .Where(d => d.UserEmail == email && d.LocalDate == localDate)
            .Select(d => new { d.Done, d.Value })
            .ToListAsync(ct);
        if (habitDays.Count > 0)
        {
            domains.Add("habits");
            habitsExpected = habitDays.Count;
            habitsDone = habitDays.Count(d => d.Done == true || (d.Value is { } v && v > 0));
            moments.Add(new DayMomentDto("", "habits", "check",
                $"{habitsDone}/{habitsExpected} habit{(habitsExpected == 1 ? "" : "s")} done"));
        }

        // ---- MEDS (OWNER-ONLY; rides tracker.self — the caller's private adherence) ----
        int? medsTaken = null, medsExpected = null;
        var medLogs = await db.MedicationLogs.AsNoTracking()
            .Where(l => l.UserEmail == email && l.LocalDate == localDate)
            .Select(l => new { l.Status })
            .ToListAsync(ct);
        if (medLogs.Count > 0)
        {
            domains.Add("meds");
            medsExpected = medLogs.Count;
            medsTaken = medLogs.Count(l => l.Status == Data.Entities.MedicationLogStatus.Taken);
            var label = medsTaken == medsExpected ? "meds: all taken" : $"meds: {medsTaken}/{medsExpected} taken";
            moments.Add(new DayMomentDto("", "meds", "pill", label));
        }

        // ---- ACTIVITY (owner-scoped social spine; the caller's OWN already-shareable events) ----
        var dayStartUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(localDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified), tz);
        var dayEndUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(localDate.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified), tz);
        var events = await db.ActivityEvents.AsNoTracking()
            .Where(a => a.ActorEmail == email && a.CreatedUtc >= dayStartUtc && a.CreatedUtc < dayEndUtc)
            .OrderBy(a => a.CreatedUtc)
            .Select(a => new { a.Kind, a.IntValue, a.Label, a.CreatedUtc })
            .Take(20)
            .ToListAsync(ct);
        if (events.Count > 0)
        {
            // De-dupe: a `workout.logged` activity moment whose local minute matches an ExerciseEntry is the
            // redundant social mirror of that canonical exercise row — drop it (timeline display only; stats
            // already exclude activity events). Non-workout events, and workouts without a matching entry, stay.
            var dedupedEvents = events
                .Where(e => !(e.Kind == "workout.logged" && exerciseStamps.Contains(Stamp(e.CreatedUtc))))
                .ToList();
            if (dedupedEvents.Count > 0)
            {
                domains.Add("activity");
                foreach (var e in dedupedEvents)
                    moments.Add(new DayMomentDto(Stamp(e.CreatedUtc), "activity", "spark",
                        ActivityLabel(e.Kind, e.IntValue, e.Label)));
            }
        }

        // ---- FAMILY (family.use): the caller's OWN household reminders due + meals planned for the date ----
        int placesVisited = 0; // (set later under location)
        var household = await households.GetForCallerAsync(caller, ct);
        if (caller.Permissions.Contains(Permissions.FamilyUse) && household is not null)
        {
            var reminders = await db.FamilyReminders.AsNoTracking()
                .Where(r => r.HouseholdId == household.Id && r.Active
                    && r.DueUtc >= dayStartUtc && r.DueUtc < dayEndUtc)
                .OrderBy(r => r.DueUtc)
                .Select(r => new { r.Text, r.DueUtc })
                .Take(20)
                .ToListAsync(ct);
            var meals = await db.FamilyMeals.AsNoTracking()
                .Where(m => m.HouseholdId == household.Id && m.LocalDate == localDate)
                .OrderBy(m => m.Slot)
                .Select(m => new { m.Slot, m.Title })
                .Take(12)
                .ToListAsync(ct);

            if (reminders.Count > 0 || meals.Count > 0) domains.Add("family");
            foreach (var r in reminders)
                moments.Add(new DayMomentDto(Stamp(r.DueUtc), "family", "bell", $"reminder: {Trim(r.Text, 50)}"));
            foreach (var m in meals)
                moments.Add(new DayMomentDto("", "family", "calendar",
                    $"{Trim(m.Slot, 20)}: {Trim(m.Title, 40)}"));
        }

        // ---- LOCATION (location.self; OWNER-ONLY): distinct named places visited on the date ----
        if (caller.Permissions.Contains(Permissions.LocationSelf))
        {
            var places = await db.UserLocations.AsNoTracking()
                .Where(l => l.UserEmail == email && l.CapturedUtc >= dayStartUtc && l.CapturedUtc < dayEndUtc
                    && l.City != null && l.City != "")
                .OrderBy(l => l.CapturedUtc)
                .Select(l => new { l.City, l.CapturedUtc })
                .ToListAsync(ct);
            var distinct = places.Select(p => p.City!).Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
            if (distinct.Count > 0)
            {
                domains.Add("location");
                placesVisited = distinct.Count;
                moments.Add(new DayMomentDto(places.Count > 0 ? Stamp(places[0].CapturedUtc) : "",
                    "location", "pin", $"places: {string.Join(", ", distinct.Select(c => Trim(c, 24)))}"));
            }
        }

        // ---- FINANCE SPEND (family.finance): the caller's OWN household expense total for the date ----
        double? spendUsd = null;
        if (caller.Permissions.Contains(Permissions.FamilyFinance) && household is not null)
        {
            var spend = await db.FinanceTransactions.AsNoTracking()
                .Where(t => t.HouseholdId == household.Id && t.Date == localDate && t.Kind == "expense")
                .SumAsync(t => (decimal?)t.Magnitude, ct) ?? 0m;
            if (spend > 0)
            {
                domains.Add("finance");
                spendUsd = (double)spend;
                moments.Add(new DayMomentDto("", "finance", "card",
                    $"spent ${spend.ToString("0.00", CultureInfo.InvariantCulture)}"));
            }
        }

        // ---- Recovery (RECOMPUTED, never stored) when the day has sleep ----
        int? recoveryScore = null;
        if (sleeps.Count > 0)
        {
            var rec = TrackerStats.ComputeRecovery(new TrackerStats.RecoveryInputs(
                SleepHours: sleepHours,
                SleepQuality: sleepQuality is >= 1 and <= 5 ? sleepQuality : 3,
                CaffeineMg: caffeineMg,
                ExerciseCalories: exerciseCalories,
                ActiveCalories: 0,
                CaloriesIn: caloriesIn,
                CalorieGoal: profile?.DailyCalorieGoal));
            recoveryScore = rec.Score;
        }

        // ---- Order the timeline chronologically (timed moments first by HH:mm, then all-day moments) ----
        moments.Sort((a, b) =>
        {
            var ak = a.Time.Length == 0 ? "99:99" : a.Time;
            var bk = b.Time.Length == 0 ? "99:99" : b.Time;
            return string.CompareOrdinal(ak, bk);
        });

        // ---- Stats rollup (null fields = nothing logged in that permitted domain) ----
        var stats = new DayStatsDto(
            CaloriesIn: foods.Count > 0 ? caloriesIn : null,
            CalorieGoal: profile?.DailyCalorieGoal,
            ExerciseCalories: exercises.Count > 0 ? exerciseCalories : null,
            ExerciseCount: exercises.Count > 0 ? exercises.Count : null,
            ProteinG: foods.Count > 0 ? Math.Round(proteinG, 1) : null,
            HydrationMl: hydrationMl > 0 ? hydrationMl : null,
            CaffeineMg: caffeineMg > 0 ? caffeineMg : null,
            SleepHours: sleeps.Count > 0 ? Math.Round(sleepHours, 1) : null,
            RecoveryScore: recoveryScore,
            HabitsDone: habitsDone,
            HabitsExpected: habitsExpected,
            MedsTaken: medsTaken,
            MedsExpected: medsExpected,
            Mood: mood,
            PlacesVisited: placesVisited > 0 ? placesVisited : null,
            SpendUsd: spendUsd);

        // ---- A few deterministic highlight facts (glanceable; carry NO PII) ----
        if (foods.Count > 0)
            highlights.Add(profile?.DailyCalorieGoal is { } goal && goal > 0
                ? $"{caloriesIn} / {goal} kcal eaten"
                : $"{caloriesIn} kcal eaten");
        if (exercises.Count > 0)
            highlights.Add($"{exercises.Count} workout{(exercises.Count == 1 ? "" : "s")} · {exerciseCalories} kcal burned");
        if (sleeps.Count > 0)
            highlights.Add($"{sleepHours:0.#}h sleep" + (recoveryScore is { } r ? $" · recovery {r}" : ""));
        if (habitsExpected is { } he && he > 0)
            highlights.Add($"{habitsDone}/{he} habits");
        if (medsExpected is { } me2 && me2 > 0)
            highlights.Add(medsTaken == me2 ? "all meds taken" : $"{medsTaken}/{me2} meds taken");
        if (mood is not null) highlights.Add($"mood: {mood}");

        var facts = FormatDayFacts(localDate, moments, stats, highlights);
        return new DaySnapshot(moments, stats, highlights, domains, facts);
    }

    /// <summary>Map a social-feed event kind + payload to a short factual timeline label. The label/value are
    /// already non-sensitive (the ActivityEvent spine carries only shareable facts).</summary>
    private static string ActivityLabel(string kind, int? intValue, string? label) => kind switch
    {
        "workout.logged" => label is { Length: > 0 } ? $"shared a workout: {Trim(label, 40)}" : "shared a workout",
        "hydration.goalHit" => "hit the hydration goal",
        "challenge.dayComplete" => intValue is { } d ? $"completed challenge day {d}" : "completed a challenge day",
        "challenge.started" => "started a challenge",
        _ => label is { Length: > 0 } ? Trim(label, 50) : Trim(kind, 50),
    };

    /// <summary>Pre-format the ALREADY-computed snapshot into a tight DATA block the model NARRATES (never
    /// recomputes). One line per timeline moment + the stats + highlights. Nothing here comes from the client,
    /// and NO email/PII is emitted.</summary>
    private static string FormatDayFacts(
        DateOnly date, IReadOnlyList<DayMomentDto> timeline, DayStatsDto stats, IReadOnlyList<string> highlights)
    {
        var sb = new StringBuilder();
        sb.Append("date: ").Append(date.ToString("yyyy-MM-dd")).Append('\n');
        sb.Append("weekday: ").Append(date.DayOfWeek).Append('\n');
        sb.Append("timeline:\n");
        foreach (var m in timeline)
            sb.Append("  ").Append(m.Time.Length > 0 ? m.Time : "--:--").Append(" · ")
              .Append(m.Domain).Append(" · ").Append(m.Label).Append('\n');

        sb.Append("stats:\n");
        if (stats.CaloriesIn is { } ci)
            sb.Append("  calories_in: ").Append(ci)
              .Append(stats.CalorieGoal is { } cg ? $" (goal {cg})" : "").Append('\n');
        if (stats.ExerciseCalories is { } ec) sb.Append("  exercise_kcal: ").Append(ec).Append('\n');
        if (stats.ProteinG is { } p) sb.Append("  protein_g: ").Append(p.ToString("0.#", CultureInfo.InvariantCulture)).Append('\n');
        if (stats.HydrationMl is { } hm) sb.Append("  hydration_ml: ").Append(hm).Append('\n');
        if (stats.CaffeineMg is { } cm) sb.Append("  caffeine_mg: ").Append(cm).Append('\n');
        if (stats.SleepHours is { } sh) sb.Append("  sleep_hours: ").Append(sh.ToString("0.#", CultureInfo.InvariantCulture)).Append('\n');
        if (stats.RecoveryScore is { } rs) sb.Append("  recovery_score: ").Append(rs).Append('\n');
        if (stats.HabitsExpected is { } hx) sb.Append("  habits: ").Append(stats.HabitsDone).Append('/').Append(hx).Append('\n');
        if (stats.MedsExpected is { } mx) sb.Append("  meds_taken: ").Append(stats.MedsTaken).Append('/').Append(mx).Append('\n');
        if (stats.Mood is { } md) sb.Append("  mood: ").Append(md).Append('\n');
        if (stats.PlacesVisited is { } pv) sb.Append("  places_visited: ").Append(pv).Append('\n');
        if (stats.SpendUsd is { } su) sb.Append("  spend_usd: ").Append(su.ToString("0.00", CultureInfo.InvariantCulture)).Append('\n');

        if (highlights.Count > 0)
        {
            sb.Append("highlights:\n");
            foreach (var h in highlights) sb.Append("  - ").Append(h).Append('\n');
        }
        return sb.ToString();
    }

    private static string Trim(string s, int max)
    {
        var t = (s ?? "").Trim();
        return t.Length > max ? t[..max] : t;
    }
}
