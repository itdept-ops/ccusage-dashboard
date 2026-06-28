using System.Text;
using Ccusage.Api.Auth;
using Ccusage.Api.Data;
using Ccusage.Api.Dtos;
using Ccusage.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Ccusage.Api.Endpoints;

/// <summary>
/// THE INSIGHT ENGINE (<c>/api/insights</c>): cross-domain correlations / trends / streaks / anomalies /
/// best-worst days NO single page shows, computed DETERMINISTICALLY over the CALLER's OWN already-derived
/// per-day series, with an OPTIONAL floored Gemini narration. NO migration, NO new permission — reuses
/// <see cref="Permissions.TrackerSelf"/> for the data floor and <see cref="Permissions.TrackerAi"/> for the
/// optional narration (mirrors the <c>/api/ai/tracker-recap</c> precedent).
///
/// <para>PRIVACY (load-bearing): STRICTLY OWNER-SCOPED. Every series read filters to the caller's own rows
/// (<c>UserEmail == caller.Email</c>; usage by <c>ReportedByUser == caller.Email</c>) — NO household, NO
/// other user, ever. The cycle/mood signal is read ONLY when the caller actually owns CycleDayLogs.</para>
///
/// <para>STATISTICAL HONESTY: a correlation is emitted ONLY at n &gt;= <see cref="CrossInsightStats.CorrelationMinPairs"/>
/// paired days, bucketed weak/moderate/strong, and labeled "association, not causation"; any forecast is a
/// bounded estimate. The pure math (<see cref="CrossInsightStats"/>) is NaN-safe + total. The deterministic
/// engine is the ALWAYS-200 floor; the AI narrates ONLY the computed numbers, is tracker.ai-gated, 6h-cached,
/// floors to <c>fellBackToPlain</c> on off/unconfigured/error (NEVER 503), and writes NOTHING.</para>
/// </summary>
public static class InsightsEndpoints
{
    private const int DefaultHydrationGoalMl = 2000;
    private const int DefaultCaffeineMgPerCup = 95; // matches TrackerEndpoints.DefaultCaffeineMgPerCup

    // ---- DTOs (the exact wire contract) ----

    /// <summary>One deterministic insight card on the wire. <see cref="Kind"/> is from the closed set
    /// correlation|trend|streak|anomaly|bestworst; the rest mirror <see cref="CrossInsightStats.InsightResult"/>.</summary>
    public sealed record InsightCardDto(
        string Kind, string Title, string Stat, string Magnitude, string Detail, string Domain, int DataPoints);

    /// <summary>The deterministic <c>/api/insights</c> response — the product floor, no AI needed. Cards are
    /// grouped client-side by <see cref="InsightCardDto.Kind"/>. <see cref="HasData"/> is false when the caller
    /// hasn't logged enough yet (the empty/insufficient state). Carries NO email / secret / other-user data.</summary>
    public sealed record InsightsResponse(
        int Window, string FromDate, string ToDate, string GeneratedUtc,
        IReadOnlyList<InsightCardDto> Cards, bool HasData);

    /// <summary>The optional <c>/api/insights/narrate</c> response: the AI <see cref="Narrative"/> + bullets,
    /// or <see cref="FellBackToPlain"/>=true when AI is off/unconfigured/errored (the UI then hides the banner).
    /// ALWAYS 200; narrates ONLY the deterministic numbers; writes NOTHING.</summary>
    public sealed record InsightsNarrateResponse(
        string Narrative, IReadOnlyList<string> Insights, bool FellBackToPlain);

    public static void MapInsightsEndpoints(this WebApplication app)
    {
        // The DETERMINISTIC floor is reachable with tracker.self alone (no AI). Rate-limited under the AI policy
        // because /narrate (same group) can spend tokens.
        var g = app.MapGroup("/api/insights")
            .RequireAuthorization()
            .RequirePermission(Permissions.TrackerSelf)
            .RequireRateLimiting(AiEndpoints.RateLimitPolicy);

        // ---- GET /api/insights?window=30|90|365 : the deterministic, always-200, owner-scoped engine ----
        g.MapGet("/", async (string? window, CurrentUserAccessor me, UsageDbContext db,
            CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;
            var today = await TrackerVisibility.DisplayTzTodayAsync(db, ct);
            var (days, from) = ResolveWindow(window, today);

            var (cards, hasData) = await ComputeInsightsAsync(db, caller.Email, from, today, ct);
            return Results.Ok(new InsightsResponse(
                days, from.ToString("yyyy-MM-dd"), today.ToString("yyyy-MM-dd"), DateTime.UtcNow.ToString("o"),
                cards.Select(ToDto).ToList(), hasData));
        });

        // ---- GET /api/insights/narrate?window=... : tracker.self FLOOR, tracker.ai NARRATION (cached 6h) ----
        g.MapGet("/narrate", async (string? window, CurrentUserAccessor me, GeminiService gemini,
            IMemoryCache cache, UsageDbContext db, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;
            var today = await TrackerVisibility.DisplayTzTodayAsync(db, ct);
            var (days, from) = ResolveWindow(window, today);

            // The plain floor needs no AI: narrate ONLY when the caller holds tracker.ai AND Gemini is configured
            // (copied verbatim from /api/ai/tracker-recap). A tracker.self caller without tracker.ai always gets
            // the deterministic floor and never spends tokens.
            if (!caller.Permissions.Contains(Permissions.TrackerAi) || !gemini.IsConfigured)
                return Results.Ok(new InsightsNarrateResponse("", Array.Empty<string>(), true));

            var cacheKey = $"ai:insights-narrative:{caller.Email}:{days}:{from:yyyy-MM-dd}";
            if (cache.TryGetValue(cacheKey, out InsightsNarrateResponse? cached) && cached is not null)
                return Results.Ok(cached);

            var (cards, _) = await ComputeInsightsAsync(db, caller.Email, from, today, ct);
            var facts = FormatInsightFacts(cards, days);

            TrackerRecapResult? ai;
            try { ai = await gemini.InsightNarrativeAsync(facts, ct); }
            catch { ai = null; }

            if (ai is null || string.IsNullOrWhiteSpace(ai.Narrative))
                return Results.Ok(new InsightsNarrateResponse("", Array.Empty<string>(), true)); // floor

            var dto = new InsightsNarrateResponse(ai.Narrative, ai.Insights, false);
            cache.Set(cacheKey, dto, TimeSpan.FromHours(6));
            return Results.Ok(dto);
        });
    }

    /// <summary>Clamp the window to 30 / 90 / 365 days (default 30) and return (days, inclusive from-date).</summary>
    internal static (int Days, DateOnly From) ResolveWindow(string? window, DateOnly today)
    {
        var days = (window ?? "30").Trim() switch
        {
            "90" => 90,
            "365" => 365,
            _ => 30,
        };
        return (days, today.AddDays(-(days - 1)));
    }

    private static InsightCardDto ToDto(CrossInsightStats.InsightResult r) =>
        new(r.Kind, r.Title, r.Stat, r.Magnitude, r.Detail, r.Domain, r.DataPoints);

    // ===================================================================================
    // Owner-scoped series reads + deterministic catalog computation (PURE math downstream)
    // ===================================================================================

    /// <summary>
    /// Build the per-day series for the caller over [from, to] (OWNER-SCOPED everywhere), then run the pure
    /// <see cref="CrossInsightStats.ComputeCatalog"/>. Returns the cards + whether there was enough data to
    /// surface anything. ALL reads are AsNoTracking, minimal columns, in-memory rollups. The cycle/mood series
    /// is included ONLY when the caller actually owns CycleDayLogs (owner-scoped private data — never household).
    /// </summary>
    internal static async Task<(IReadOnlyList<CrossInsightStats.InsightResult> Cards, bool HasData)>
        ComputeInsightsAsync(
            UsageDbContext db, string email, DateOnly from, DateOnly to, CancellationToken ct)
    {
        // ---- Owner-scoped raw reads (each filtered to the caller's own rows only) ----
        var foods = await db.FoodEntries.AsNoTracking()
            .Where(f => f.UserEmail == email && f.LocalDate >= from && f.LocalDate <= to)
            .Select(f => new { f.LocalDate, f.Calories, f.ProteinG })
            .ToListAsync(ct);
        var exercises = await db.ExerciseEntries.AsNoTracking()
            .Where(x => x.UserEmail == email && x.LocalDate >= from && x.LocalDate <= to)
            .Select(x => new { x.LocalDate, x.CaloriesBurned })
            .ToListAsync(ct);
        var weights = await db.WeightEntries.AsNoTracking()
            .Where(w => w.UserEmail == email && w.LocalDate >= from && w.LocalDate <= to)
            .OrderBy(w => w.LocalDate).ThenBy(w => w.Id)
            .Select(w => new { w.LocalDate, w.WeightKg })
            .ToListAsync(ct);
        var sleeps = await db.SleepEntries.AsNoTracking()
            .Where(s => s.UserEmail == email && s.LocalDate >= from && s.LocalDate <= to)
            .Select(s => new { s.LocalDate, s.Hours, s.Quality })
            .ToListAsync(ct);
        var hydration = await db.HydrationEntries.AsNoTracking()
            .Where(h => h.UserEmail == email && h.LocalDate >= from && h.LocalDate <= to)
            .Select(h => new { h.LocalDate, h.AmountMl, h.Label })
            .ToListAsync(ct);
        var coffees = await db.CoffeeEntries.AsNoTracking()
            .Where(c => c.UserEmail == email && c.LocalDate >= from && c.LocalDate <= to)
            .Select(c => new { c.LocalDate, c.Cups, c.CaffeineMg })
            .ToListAsync(ct);
        var activities = await db.DailyActivities.AsNoTracking()
            .Where(a => a.UserEmail == email && a.LocalDate >= from && a.LocalDate <= to)
            .Select(a => new { a.LocalDate, a.Steps, a.ActiveCalories })
            .ToListAsync(ct);

        var profile = await db.TrackerProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserEmail == email, ct);
        var hydrationGoalMl = profile?.HydrationGoalMl ?? DefaultHydrationGoalMl;

        // CYCLE/MOOD — ONLY when the caller actually owns CycleDayLogs (owner-scoped private data; never household).
        var cycleEnergy = new Dictionary<DateOnly, double>();
        var ownsCycle = await db.CycleDayLogs.AsNoTracking().AnyAsync(c => c.UserEmail == email, ct);
        if (ownsCycle)
        {
            var cycle = await db.CycleDayLogs.AsNoTracking()
                .Where(c => c.UserEmail == email && c.LocalDate >= from && c.LocalDate <= to && c.Energy != null)
                .Select(c => new { c.LocalDate, c.Energy })
                .ToListAsync(ct);
            foreach (var r in cycle)
                if (r.Energy is { } e) cycleEnergy[r.LocalDate] = e;
        }

        // ---- Per-day rollups (sparse maps, owner-only) ----
        var caloriesIn = SumByDate(foods.Select(f => (f.LocalDate, (double)f.Calories)));
        var proteinG = SumByDate(foods.Select(f => (f.LocalDate, f.ProteinG)));
        var exerciseBurn = SumByDate(exercises.Select(x => (x.LocalDate, (double)x.CaloriesBurned)));
        var steps = LastByDate(activities.Where(a => a.Steps is not null).Select(a => (a.LocalDate, (double)a.Steps!.Value)));
        var activeCal = LastByDate(activities.Where(a => a.ActiveCalories is not null).Select(a => (a.LocalDate, (double)a.ActiveCalories!.Value)));
        var sleepHours = SumByDate(sleeps.Select(s => (s.LocalDate, (double)s.Hours)));
        var sleepQuality = MaxByDate(sleeps.Where(s => s.Quality is >= 1 and <= 5).Select(s => (s.LocalDate, (double)s.Quality)));
        var weightByDate = LastByDate(weights.Select(w => (w.LocalDate, w.WeightKg)));
        var hydrationMl = SumByDate(hydration.Select(h => (h.LocalDate, (double)h.AmountMl)));

        // Caffeine per day = coffee caffeine (mg or cups*95) + hydration rows labeled "coffee" * 95 (mirrors recovery).
        var caffeineByDate = SumByDate(coffees.Select(c => (c.LocalDate, (double)(c.CaffeineMg ?? c.Cups * DefaultCaffeineMgPerCup))));
        foreach (var h in hydration.Where(h => string.Equals(h.Label, "coffee", StringComparison.OrdinalIgnoreCase)))
            caffeineByDate[h.LocalDate] = caffeineByDate.GetValueOrDefault(h.LocalDate) + DefaultCaffeineMgPerCup;

        // AI-spend per day — OWNER-scoped to ReportedByUser == caller (the load-bearing usage privacy filter).
        var aiSpend = await LoadUsageSpendByDateAsync(db, email, from, to, ct);

        // ---- Recovery per day — RECOMPUTED (never stored) via TrackerStats.ComputeRecovery, only on sleep days ----
        var calorieGoal = profile?.DailyCalorieGoal;
        var recovery = new Dictionary<DateOnly, double>();
        foreach (var d in sleepHours.Keys)
        {
            var rec = TrackerStats.ComputeRecovery(new TrackerStats.RecoveryInputs(
                SleepHours: sleepHours.GetValueOrDefault(d),
                SleepQuality: (int)Math.Round(sleepQuality.GetValueOrDefault(d)),
                CaffeineMg: (int)Math.Round(caffeineByDate.GetValueOrDefault(d)),
                ExerciseCalories: (int)Math.Round(exerciseBurn.GetValueOrDefault(d)),
                ActiveCalories: (int)Math.Round(activeCal.GetValueOrDefault(d)),
                CaloriesIn: (int)Math.Round(caloriesIn.GetValueOrDefault(d)),
                CalorieGoal: calorieGoal));
            recovery[d] = rec.Score;
        }

        // ---- "Next-day recovery" series: shift recovery back one day so it pairs with TODAY's sleep ----
        // (sleep on day D → recovery on day D is already same-day; for sleep→NEXT-day recovery we map sleep[D]
        // against recovery[D+1]). We build a next-day-recovery map keyed by the PRIOR day.
        var recoveryNextDay = new Dictionary<DateOnly, double>();
        foreach (var kv in recovery)
        {
            var prior = kv.Key.AddDays(-1);
            recoveryNextDay[prior] = kv.Value;
        }

        // ---- 7-day weight trend series (the smoothed line calories-in is correlated against) ----
        var weightTrend = SevenDayTrend(weightByDate);

        // ---- Assemble the named series bag (only non-empty series; pure math downstream) ----
        var series = new Dictionary<string, IReadOnlyDictionary<DateOnly, double>>();
        void Add(string key, IReadOnlyDictionary<DateOnly, double> s) { if (s.Count > 0) series[key] = s; }
        Add("recovery", recovery);
        Add("recovery_next_day", recoveryNextDay);
        Add("sleep_hours", sleepHours);
        Add("sleep_quality", sleepQuality);
        Add("caffeine", caffeineByDate);
        Add("calories_in", caloriesIn);
        Add("protein", proteinG);
        Add("weight", weightByDate);
        Add("weight_trend", weightTrend);
        Add("steps", steps);
        Add("active_cal", activeCal);
        Add("ai_spend", aiSpend);
        Add("hydration_ml", hydrationMl);
        Add("cycle_energy", cycleEnergy);

        // ---- Candidate specs (each dropped by the pure engine when its floor isn't met) ----
        var correlations = new List<CrossInsightStats.CorrelationSpec>
        {
            new("sleep_hours", "recovery_next_day", "Sleep vs next-day recovery", "sleep",
                "More sleep tracked alongside higher next-day recovery", "More sleep tracked alongside lower next-day recovery"),
            new("caffeine", "sleep_hours", "Caffeine vs sleep duration", "coffee",
                "More caffeine tracked alongside longer sleep", "More caffeine tracked alongside shorter sleep"),
            new("calories_in", "weight_trend", "Calories vs 7-day weight trend", "weight",
                "Higher intake tracked alongside a rising weight trend", "Higher intake tracked alongside a falling weight trend"),
            new("ai_spend", "active_cal", "AI spend vs active calories", "usage",
                "Higher AI-spend days tracked alongside more movement", "Higher AI-spend days tracked alongside less movement"),
            new("protein", "recovery", "Protein vs recovery", "food",
                "More protein tracked alongside higher recovery", "More protein tracked alongside lower recovery"),
            new("steps", "sleep_quality", "Steps vs sleep quality", "activity",
                "More steps tracked alongside better-rated sleep", "More steps tracked alongside worse-rated sleep"),
            new("cycle_energy", "recovery", "Cycle energy vs recovery", "cycle",
                "Higher logged energy tracked alongside higher recovery", "Higher logged energy tracked alongside lower recovery"),
        };
        var trends = new List<CrossInsightStats.TrendSpec>
        {
            new("weight", "Weight trend", "kg/wk", 7.0, "weight", true),
            new("sleep_hours", "Sleep drift", "h/wk", 7.0, "sleep", false),
            new("ai_spend", "AI spend trend", "$/wk", 7.0, "usage", true),
            new("hydration_ml", "Hydration trend", "ml/wk", 7.0, "hydration", false),
        };
        var anomalies = new List<CrossInsightStats.AnomalySpec>
        {
            new("ai_spend", "AI-spend spike", "Unusually low AI spend", "", "usage"),
            new("sleep_hours", "Unusually long sleep", "Very short sleep", "h", "sleep"),
            new("calories_in", "Calorie blowout", "Unusually low intake", "", "food"),
        };
        var bestWorsts = new List<CrossInsightStats.BestWorstSpec>
        {
            new("recovery", "Recovery range", "", "sleep", true),
            new("steps", "Steps range", "", "activity", true),
            new("sleep_hours", "Sleep range", "h", "sleep", true),
        };

        // ---- Streak candidates (qualification rules live here, owner-scoped) ----
        var hydrationGoalDays = hydrationMl.Where(kv => kv.Value >= hydrationGoalMl).Select(kv => kv.Key).ToHashSet();
        var steadyRecoveryDays = recovery.Where(kv => kv.Value >= 65).Select(kv => kv.Key).ToHashSet(); // >= "Steady"
        var deficitDays = caloriesIn.Where(kv => calorieGoal is { } g && g > 0 && kv.Value < g).Select(kv => kv.Key).ToHashSet();
        var loggedDays = new HashSet<DateOnly>();
        foreach (var d in caloriesIn.Keys) loggedDays.Add(d);
        foreach (var d in exerciseBurn.Keys) loggedDays.Add(d);
        foreach (var d in sleepHours.Keys) loggedDays.Add(d);
        foreach (var d in hydrationMl.Keys) loggedDays.Add(d);
        foreach (var d in weightByDate.Keys) loggedDays.Add(d);
        var streaks = new List<(string, string, IReadOnlySet<DateOnly>)>
        {
            ("Hydration-goal streak", "hydration", hydrationGoalDays),
            ("Steady-recovery streak", "sleep", steadyRecoveryDays),
            ("Calorie-deficit streak", "food", deficitDays),
            ("Days-logged streak", "primary", loggedDays),
        };

        var cards = CrossInsightStats.ComputeCatalog(
            series, correlations, trends, anomalies, bestWorsts, streaks, to);

        // "Enough data" = at least one card surfaced (correlations need 10 paired days, trends/anomalies need
        // their own floors), so a brand-new user gets the friendly empty state instead of a barren grid.
        return (cards, cards.Count > 0);
    }

    /// <summary>OWNER-scoped daily AI/usage cost ($), filtered to <c>ReportedByUser == email</c> (the load-bearing
    /// usage privacy filter — NEVER another user's spend), grouped by the usage row's LocalDate in [from, to].</summary>
    private static async Task<Dictionary<DateOnly, double>> LoadUsageSpendByDateAsync(
        UsageDbContext db, string email, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var rows = await db.UsageRecords.AsNoTracking()
            .Where(r => r.ReportedByUser == email && r.LocalDate >= from && r.LocalDate <= to)
            .GroupBy(r => r.LocalDate)
            .Select(grp => new { Date = grp.Key, Cost = grp.Sum(r => r.CostUsd) })
            .ToListAsync(ct);
        var map = new Dictionary<DateOnly, double>();
        foreach (var r in rows)
            if (r.Cost > 0) map[r.Date] = (double)r.Cost;
        return map;
    }

    // ---- Per-day rollup helpers (sparse maps) ----

    private static Dictionary<DateOnly, double> SumByDate(IEnumerable<(DateOnly Date, double Value)> rows)
    {
        var map = new Dictionary<DateOnly, double>();
        foreach (var (date, value) in rows)
            map[date] = map.GetValueOrDefault(date) + value;
        return map;
    }

    private static Dictionary<DateOnly, double> MaxByDate(IEnumerable<(DateOnly Date, double Value)> rows)
    {
        var map = new Dictionary<DateOnly, double>();
        foreach (var (date, value) in rows)
            map[date] = map.TryGetValue(date, out var cur) ? Math.Max(cur, value) : value;
        return map;
    }

    private static Dictionary<DateOnly, double> LastByDate(IEnumerable<(DateOnly Date, double Value)> rows)
    {
        // Rows arrive in insertion order; "last wins" gives the latest reading for the day.
        var map = new Dictionary<DateOnly, double>();
        foreach (var (date, value) in rows)
            map[date] = value;
        return map;
    }

    /// <summary>A trailing 7-day moving average of a sparse daily series (only on days that have a raw point),
    /// used to smooth the weight line a calorie correlation is meaningful against. Pure.</summary>
    private static Dictionary<DateOnly, double> SevenDayTrend(IReadOnlyDictionary<DateOnly, double> raw)
    {
        var trend = new Dictionary<DateOnly, double>();
        foreach (var d in raw.Keys)
        {
            double sum = 0;
            var n = 0;
            for (var k = 0; k < 7; k++)
                if (raw.TryGetValue(d.AddDays(-k), out var v)) { sum += v; n++; }
            if (n > 0) trend[d] = sum / n;
        }
        return trend;
    }

    /// <summary>Pre-format the ALREADY-computed cards into a tight DATA block the model NARRATES (never
    /// recomputes). One line per card. Nothing here comes from the client.</summary>
    internal static string FormatInsightFacts(IReadOnlyList<CrossInsightStats.InsightResult> cards, int window)
    {
        var sb = new StringBuilder();
        sb.Append("window_days: ").Append(window).Append('\n');
        sb.Append("card_count: ").Append(cards.Count).Append('\n');
        foreach (var c in cards)
            sb.Append(c.Kind).Append(" | ").Append(c.Title).Append(" | ").Append(c.Stat)
              .Append(" | ").Append(c.Magnitude).Append(" | n=").Append(c.DataPoints).Append('\n');
        return sb.ToString();
    }
}
