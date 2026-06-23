using Ccusage.Api.Data;
using Ccusage.Api.Data.Entities;
using Ccusage.Api.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace Ccusage.Api.Services;

/// <summary>
/// Composes and sends a user's PERSONAL weekly recap to their OWN Discord webhook. The recap is the user
/// about THEMSELVES (their own week, to their own webhook), so it rides the per-user webhook path
/// (<see cref="NotificationPreference"/> + <see cref="TokenProtector"/> + the SSRF allowlist re-checked at
/// send time) — never the admin/global webhook. Only stats that actually exist are reported (tracker
/// totals/averages, workouts, hydration goal hits, 75-Hard, open/settled bills); nothing here is a
/// secret and the user's email is never put in the embed.
///
/// <para>The DATA AGGREGATION is a PURE function (<see cref="Aggregate"/>) over already-loaded rows so it
/// can be unit-tested without a database; <see cref="SendRecapAsync"/> does the DB loads + the gated send.</para>
/// </summary>
public sealed class WeeklyRecapComposer(
    IServiceScopeFactory scopeFactory, ILogger<WeeklyRecapComposer> logger)
{
    /// <summary>The fully-aggregated, display-ready numbers for a recap window. A pure value — no DB, no I/O.</summary>
    public sealed record RecapStats(
        int Days,
        int CaloriesInTotal, int CaloriesInAvg,
        int CaloriesOutTotal,
        double ProteinAvgG,
        int Workouts, int WorkoutMinutes,
        long StepsTotal,
        int HydrationGoalHits,
        int CoffeeCups,
        HardChallengeEndpoints.WeeklyHardStats? Hard,
        int BillsOpen, int BillsSettled);

    /// <summary>
    /// PURE aggregation of a recap window from already-loaded rows. Mirrors the tracker's day roll-up
    /// (food + supplements = calories-in/macros; exercises + watch active-calories = calories-out; hydration
    /// goal = profile goal or the 2000 ml default). Days = the inclusive span length. Averages are over the
    /// window's day count (a missing day counts as a zero day, matching "per day this week").
    /// </summary>
    public static RecapStats Aggregate(
        DateOnly from, DateOnly to,
        IReadOnlyList<FoodEntry> foods,
        IReadOnlyList<SupplementEntry> supplements,
        IReadOnlyList<ExerciseEntry> exercises,
        IReadOnlyList<HydrationEntry> hydration,
        IReadOnlyList<CoffeeEntry> coffee,
        IReadOnlyList<DailyActivity> activities,
        TrackerProfile? profile,
        HardChallengeEndpoints.WeeklyHardStats? hard,
        int billsOpen, int billsSettled)
    {
        var days = to.DayNumber - from.DayNumber + 1;
        if (days < 1) days = 1;

        var foodCals = foods.Sum(f => f.Calories);
        var suppCals = supplements.Sum(s => s.Calories);
        var caloriesIn = foodCals + suppCals;

        var foodProtein = foods.Sum(f => f.ProteinG);
        var suppProtein = supplements.Sum(s => (double)s.ProteinG);
        var proteinTotal = foodProtein + suppProtein;

        // Calories OUT, per day: logged-exercise sum, then the watch's active-calories ADD/OVERRIDE
        // (mirrors TrackerEndpoints.ResolveCaloriesOut). A day with no exercise/activity contributes 0.
        var exerciseByDate = exercises.GroupBy(x => x.LocalDate)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.CaloriesBurned));
        var caloriesOut = 0;
        foreach (var a in activities)
        {
            exerciseByDate.TryGetValue(a.LocalDate, out var ex);
            caloriesOut += ResolveCaloriesOut(ex, a);
        }
        // Days that had exercise but no watch activity row still count their logged burn.
        foreach (var (date, ex) in exerciseByDate)
            if (!activities.Any(a => a.LocalDate == date))
                caloriesOut += ex;

        var stepsTotal = activities.Sum(a => (long)(a.Steps ?? 0));

        // Hydration goal hits: count distinct days whose total hydration met the resolved goal.
        var goalMl = profile?.HydrationGoalMl ?? DefaultHydrationGoalMl;
        var hydrationByDate = hydration.GroupBy(h => h.LocalDate)
            .Select(g => g.Sum(h => h.AmountMl));
        var hydrationGoalHits = hydrationByDate.Count(ml => ml >= goalMl);

        var coffeeCups = coffee.Sum(c => c.Cups);

        return new RecapStats(
            Days: days,
            CaloriesInTotal: caloriesIn,
            CaloriesInAvg: (int)Math.Round((double)caloriesIn / days),
            CaloriesOutTotal: caloriesOut,
            ProteinAvgG: Math.Round(proteinTotal / days, 1),
            Workouts: exercises.Count,
            WorkoutMinutes: exercises.Sum(x => x.DurationMin ?? 0),
            StepsTotal: stepsTotal,
            HydrationGoalHits: hydrationGoalHits,
            CoffeeCups: coffeeCups,
            Hard: hard,
            BillsOpen: billsOpen,
            BillsSettled: billsSettled);
    }

    /// <summary>Mirror of TrackerEndpoints.ResolveCaloriesOut (kept local so the recap doesn't depend on a
    /// private tracker method): watch active-calories ADD on top of, or OVERRIDE, the logged-exercise burn.</summary>
    private static int ResolveCaloriesOut(int exerciseCalories, DailyActivity activity)
    {
        if (activity.ActiveCalories is not { } active) return exerciseCalories;
        return activity.CalorieMode == ActivityCalorieMode.Override ? active : exerciseCalories + active;
    }

    private const int DefaultHydrationGoalMl = 2000;

    /// <summary>
    /// Format the aggregated stats into the recap embed's metric fields (the headline + fields the
    /// <see cref="DiscordNotifier"/> renders). PURE — no DB, no secrets, no email. Always returns at least
    /// the nutrition/movement fields; the 75-Hard field appears only when there's an active challenge.
    /// </summary>
    public static (string Headline, List<DiscordNotifier.RecapField> Fields) BuildEmbed(RecapStats s)
    {
        var headline = $"Here's how your last {s.Days} days went 💪";

        var fields = new List<DiscordNotifier.RecapField>
        {
            new("🍽️ Calories in", $"**{s.CaloriesInTotal:N0}** total · ~{s.CaloriesInAvg:N0}/day", true),
            new("🔥 Calories out", $"**{s.CaloriesOutTotal:N0}** burned", true),
            new("🥩 Protein", $"~{s.ProteinAvgG:N0} g/day", true),
            new("🏋️ Workouts", s.WorkoutMinutes > 0
                ? $"**{s.Workouts}** · {s.WorkoutMinutes:N0} min"
                : $"**{s.Workouts}**", true),
            new("💧 Hydration goal", $"**{s.HydrationGoalHits}** / {s.Days} days", true),
        };

        if (s.StepsTotal > 0)
            fields.Add(new("👟 Steps", $"**{s.StepsTotal:N0}**", true));
        if (s.CoffeeCups > 0)
            fields.Add(new("☕ Coffee", $"**{s.CoffeeCups}** cups", true));

        if (s.Hard is { } h)
            fields.Add(new("🏆 75 Hard",
                $"🔥 {h.CurrentStreak}-day streak · **{h.WeekPoints:0.#}** pts this week · {h.WeekCompletedDays}/{s.Days} days complete",
                false));

        if (s.BillsOpen > 0 || s.BillsSettled > 0)
            fields.Add(new("🧾 Bills", $"**{s.BillsSettled}** settled · {s.BillsOpen} still open", false));

        return (headline, fields);
    }

    /// <summary>
    /// Compose + send the recap for <paramref name="email"/>'s window [from, to] to their OWN webhook.
    /// Gated: requires <see cref="NotificationPreference.WeeklyRecapEnabled"/> AND a stored, decryptable,
    /// still-valid webhook. The webhook is decrypted ONLY in memory at send time and never logged. Returns
    /// true only on a confirmed Discord 2xx (so the caller can advance its last-sent guard). All errors are
    /// swallowed + logged metadata-only (recipient + ok/failed) — never the URL, never the user's data.
    /// </summary>
    public async Task<bool> SendRecapAsync(string email, DateOnly from, DateOnly to, DateOnly today, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<TokenProtector>();
        var notifier = scope.ServiceProvider.GetRequiredService<DiscordNotifier>();

        var pref = await db.NotificationPreferences.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserEmail == email, ct);
        if (pref is null || !pref.WeeklyRecapEnabled || string.IsNullOrEmpty(pref.DiscordWebhookEnc))
            return false; // opted out or no webhook — nothing to send.

        var url = protector.Unprotect(pref.DiscordWebhookEnc);
        if (string.IsNullOrEmpty(url) || !DiscordWebhookValidator.IsValid(url))
            return false; // undecryptable / corrupt / no-longer-valid — drop quietly.

        var stats = await LoadStatsAsync(db, email, from, to, today, ct);
        var (headline, fields) = BuildEmbed(stats);
        var period = $"{from:MMM d}–{to:MMM d}";

        var result = await notifier.SendWeeklyRecapAsync(url!, period, headline, fields, ct);
        logger.LogInformation("Weekly recap to {Recipient}: {Outcome}.", MaskEmail(email), result.Ok ? "sent" : "failed");
        return result.Ok;
    }

    /// <summary>The outcome of a send-now request: the gate the caller hit (so the endpoint maps the status).</summary>
    public enum SendNowResult { NoWebhook, DiscordRejected, Sent }

    /// <summary>
    /// SEND-NOW / preview-send: compose the caller's last-7-days recap and send it to their OWN webhook
    /// IMMEDIATELY, IGNORING both the opt-in toggle and the LastRecapSent guard (this is an explicit user
    /// action). Still requires a saved, decryptable, valid webhook. Does NOT touch LastRecapSent (so a manual
    /// test never suppresses the next scheduled Sunday recap). Returns a result the endpoint maps to a status.
    /// </summary>
    public async Task<SendNowResult> SendNowAsync(string email, DateOnly from, DateOnly to, DateOnly today, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<TokenProtector>();
        var notifier = scope.ServiceProvider.GetRequiredService<DiscordNotifier>();

        var pref = await db.NotificationPreferences.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserEmail == email, ct);
        if (pref is null || string.IsNullOrEmpty(pref.DiscordWebhookEnc))
            return SendNowResult.NoWebhook;

        var url = protector.Unprotect(pref.DiscordWebhookEnc);
        if (string.IsNullOrEmpty(url) || !DiscordWebhookValidator.IsValid(url))
            return SendNowResult.NoWebhook;

        var stats = await LoadStatsAsync(db, email, from, to, today, ct);
        var (headline, fields) = BuildEmbed(stats);
        var result = await notifier.SendWeeklyRecapAsync(url!, $"{from:MMM d}–{to:MMM d}", headline, fields, ct);
        logger.LogInformation("Weekly recap (send-now) to {Recipient}: {Outcome}.", MaskEmail(email), result.Ok ? "sent" : "failed");
        return result.Ok ? SendNowResult.Sent : SendNowResult.DiscordRejected;
    }

    // Mask an email for logs (PII hygiene) — first char + domain, e.g. "j***@example.com".
    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        return at <= 0 ? "***" : $"{email[0]}***{email[at..]}";
    }

    /// <summary>
    /// PREVIEW: compose the caller's last-7-days recap embed WITHOUT sending — returns the period, headline,
    /// and the metric fields so the UI can show exactly what would be posted. No webhook required; no secrets.
    /// </summary>
    public async Task<(string Period, string Headline, IReadOnlyList<DiscordNotifier.RecapField> Fields)> PreviewAsync(
        UsageDbContext db, string email, DateOnly from, DateOnly to, DateOnly today, CancellationToken ct)
    {
        var stats = await LoadStatsAsync(db, email, from, to, today, ct);
        var (headline, fields) = BuildEmbed(stats);
        return ($"{from:MMM d}–{to:MMM d}", headline, fields);
    }

    /// <summary>
    /// Load + aggregate the user's recap window (DB-backed; calls the PURE <see cref="Aggregate"/>). Exposed
    /// so the send-now/preview endpoint can compose the SAME embed JSON without sending. Scoped queries are
    /// all keyed by the lower-cased email; only the user's own rows are read.
    /// </summary>
    public async Task<RecapStats> LoadStatsAsync(
        UsageDbContext db, string email, DateOnly from, DateOnly to, DateOnly today, CancellationToken ct)
    {
        var foods = await db.FoodEntries.AsNoTracking()
            .Where(f => f.UserEmail == email && f.LocalDate >= from && f.LocalDate <= to).ToListAsync(ct);
        var supplements = await db.SupplementEntries.AsNoTracking()
            .Where(s => s.UserEmail == email && s.LocalDate >= from && s.LocalDate <= to).ToListAsync(ct);
        var exercises = await db.ExerciseEntries.AsNoTracking()
            .Where(x => x.UserEmail == email && x.LocalDate >= from && x.LocalDate <= to).ToListAsync(ct);
        var hydration = await db.HydrationEntries.AsNoTracking()
            .Where(h => h.UserEmail == email && h.LocalDate >= from && h.LocalDate <= to).ToListAsync(ct);
        var coffee = await db.CoffeeEntries.AsNoTracking()
            .Where(c => c.UserEmail == email && c.LocalDate >= from && c.LocalDate <= to).ToListAsync(ct);
        var activities = await db.DailyActivities.AsNoTracking()
            .Where(a => a.UserEmail == email && a.LocalDate >= from && a.LocalDate <= to).ToListAsync(ct);
        var profile = await db.TrackerProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserEmail == email, ct);

        var hard = await HardChallengeEndpoints.ComputeWeeklyRecapStatsAsync(db, email, from, to, today, ct);

        // Bills have no settled-timestamp, so report the owner's current open/settled COUNTS (not time-bound).
        var billCounts = await db.Bills.AsNoTracking()
            .Where(b => b.OwnerEmail == email)
            .GroupBy(b => b.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var billsSettled = billCounts.FirstOrDefault(x => x.Status == "settled")?.Count ?? 0;
        var billsOpen = billCounts.FirstOrDefault(x => x.Status == "open")?.Count ?? 0;

        return Aggregate(from, to, foods, supplements, exercises, hydration, coffee, activities,
            profile, hard, billsOpen, billsSettled);
    }
}
