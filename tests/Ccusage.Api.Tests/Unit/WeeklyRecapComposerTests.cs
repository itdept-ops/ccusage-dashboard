using Ccusage.Api.Data.Entities;
using Ccusage.Api.Endpoints;
using Ccusage.Api.Services;
using FluentAssertions;

namespace Ccusage.Api.Tests.Unit;

/// <summary>
/// The PURE recap aggregation + embed formatting (<see cref="WeeklyRecapComposer.Aggregate"/> /
/// <see cref="WeeklyRecapComposer.BuildEmbed"/>) — no DB, no I/O. Covers: food + supplements summed into
/// calories-in/protein; the watch ADD vs OVERRIDE calories-out resolution; workout count/minutes; hydration
/// goal-hit day counting (profile goal vs the 2000 ml default); per-day averaging over the window length; and
/// that the embed carries the right fields (75-Hard / bills only when present) and NEVER leaks an email.
/// </summary>
public class WeeklyRecapComposerTests
{
    private static readonly DateOnly From = new(2026, 6, 15); // Mon
    private static readonly DateOnly To = new(2026, 6, 21);   // Sun (7-day window)

    private static FoodEntry Food(DateOnly d, int cal, double protein = 0) =>
        new() { LocalDate = d, Calories = cal, ProteinG = protein };

    private static SupplementEntry Supp(DateOnly d, int cal, decimal protein = 0) =>
        new() { LocalDate = d, Calories = cal, ProteinG = protein };

    private static ExerciseEntry Ex(DateOnly d, int burned, int? min = null) =>
        new() { LocalDate = d, CaloriesBurned = burned, DurationMin = min };

    private static HydrationEntry Hyd(DateOnly d, int ml) => new() { LocalDate = d, AmountMl = ml };

    private static WeeklyRecapComposer.RecapStats Agg(
        IReadOnlyList<FoodEntry>? foods = null,
        IReadOnlyList<SupplementEntry>? supps = null,
        IReadOnlyList<ExerciseEntry>? ex = null,
        IReadOnlyList<HydrationEntry>? hyd = null,
        IReadOnlyList<DailyActivity>? acts = null,
        TrackerProfile? profile = null,
        HardChallengeEndpoints.WeeklyHardStats? hard = null,
        int billsOpen = 0, int billsSettled = 0) =>
        WeeklyRecapComposer.Aggregate(From, To,
            foods ?? [], supps ?? [], ex ?? [], hyd ?? [],
            [], acts ?? [], profile, hard, billsOpen, billsSettled);

    [Fact]
    public void Window_length_is_inclusive_day_count()
    {
        Agg().Days.Should().Be(7);
    }

    [Fact]
    public void Calories_in_sums_food_and_supplements_and_averages_over_the_window()
    {
        var s = Agg(
            foods: [Food(From, 2000), Food(From.AddDays(1), 1000)],
            supps: [Supp(From, 120)]);

        s.CaloriesInTotal.Should().Be(3120);
        s.CaloriesInAvg.Should().Be(446); // round(3120 / 7)
    }

    [Fact]
    public void Protein_averages_food_plus_supplement_over_the_window()
    {
        var s = Agg(
            foods: [Food(From, 0, protein: 100)],
            supps: [Supp(From, 0, protein: 40m)]);

        s.ProteinAvgG.Should().Be(20.0); // round((100 + 40) / 7, 1)
    }

    [Fact]
    public void Calories_out_add_mode_adds_watch_active_calories_to_logged_exercise()
    {
        var d = From;
        var s = Agg(
            ex: [Ex(d, 300)],
            acts: [new DailyActivity { LocalDate = d, ActiveCalories = 200, CalorieMode = ActivityCalorieMode.Add }]);

        s.CaloriesOutTotal.Should().Be(500); // 300 logged + 200 watch
    }

    [Fact]
    public void Calories_out_override_mode_replaces_logged_exercise_with_watch()
    {
        var d = From;
        var s = Agg(
            ex: [Ex(d, 300)],
            acts: [new DailyActivity { LocalDate = d, ActiveCalories = 800, CalorieMode = ActivityCalorieMode.Override }]);

        s.CaloriesOutTotal.Should().Be(800); // watch overrides the logged burn
    }

    [Fact]
    public void Calories_out_counts_exercise_days_with_no_watch_row()
    {
        var s = Agg(ex: [Ex(From, 250), Ex(From.AddDays(1), 150)]);
        s.CaloriesOutTotal.Should().Be(400);
    }

    [Fact]
    public void Workouts_count_entries_and_sum_minutes()
    {
        var s = Agg(ex: [Ex(From, 100, min: 30), Ex(From, 100, min: 20), Ex(From.AddDays(1), 100)]);
        s.Workouts.Should().Be(3);
        s.WorkoutMinutes.Should().Be(50);
    }

    [Fact]
    public void Hydration_goal_hits_count_days_meeting_the_default_goal()
    {
        // Default 2000 ml. Day 1: 2 x 1000 = 2000 (hit). Day 2: 500 (miss). Day 3: 2500 (hit).
        var s = Agg(hyd:
        [
            Hyd(From, 1000), Hyd(From, 1000),
            Hyd(From.AddDays(1), 500),
            Hyd(From.AddDays(2), 2500),
        ]);
        s.HydrationGoalHits.Should().Be(2);
    }

    [Fact]
    public void Hydration_goal_hits_use_the_profile_goal_when_set()
    {
        var profile = new TrackerProfile { HydrationGoalMl = 3000 };
        // 2500 ml would HIT the 2000 default but MISS a 3000 profile goal.
        var s = Agg(hyd: [Hyd(From, 2500)], profile: profile);
        s.HydrationGoalHits.Should().Be(0);
    }

    [Fact]
    public void Hard_and_bills_fields_appear_only_when_present()
    {
        var (_, bare) = WeeklyRecapComposer.BuildEmbed(Agg());
        bare.Should().NotContain(f => f.Name.Contains("75 Hard"));
        bare.Should().NotContain(f => f.Name.Contains("Bills"));

        var hard = new HardChallengeEndpoints.WeeklyHardStats(CurrentStreak: 12, TotalPoints: 300m, WeekPoints: 42m, WeekCompletedDays: 5);
        var (_, rich) = WeeklyRecapComposer.BuildEmbed(Agg(hard: hard, billsOpen: 2, billsSettled: 3));
        rich.Should().Contain(f => f.Name.Contains("75 Hard") && f.Value.Contains("12-day"));
        rich.Should().Contain(f => f.Name.Contains("Bills") && f.Value.Contains("3") && f.Value.Contains("2"));
    }

    [Fact]
    public void Embed_always_carries_the_core_nutrition_and_movement_fields()
    {
        var (headline, fields) = WeeklyRecapComposer.BuildEmbed(Agg(foods: [Food(From, 2000, 100)], ex: [Ex(From, 300, 30)]));
        headline.Should().Contain("7 days");
        fields.Should().Contain(f => f.Name.Contains("Calories in"));
        fields.Should().Contain(f => f.Name.Contains("Calories out"));
        fields.Should().Contain(f => f.Name.Contains("Protein"));
        fields.Should().Contain(f => f.Name.Contains("Workouts"));
        fields.Should().Contain(f => f.Name.Contains("Hydration"));
    }

    [Fact]
    public void Embed_never_contains_an_email_address()
    {
        var hard = new HardChallengeEndpoints.WeeklyHardStats(3, 100m, 20m, 2);
        var (headline, fields) = WeeklyRecapComposer.BuildEmbed(
            Agg(foods: [Food(From, 2000, 100)], ex: [Ex(From, 300, 30)], hard: hard, billsOpen: 1, billsSettled: 1));

        headline.Should().NotContain("@");
        fields.Should().NotContain(f => f.Name.Contains("@") || f.Value.Contains("@"));
    }
}
