using Ccusage.Api.Data.Entities;
using Ccusage.Api.Services;
using FluentAssertions;

namespace Ccusage.Api.Tests.Unit;

/// <summary>
/// The PURE 75 Hard v2 scoring (<see cref="HardChallengeScoring"/>) — no I/O. Covers the configurable per-task
/// points math (partial credit pro-rates + rounds; all-or-nothing otherwise), the auto-source progress against
/// CUSTOM targets (a changed water target changes the water progress), the day completeness threshold (all
/// enabled tasks at 100%), and the unchanged RELAXED streak fold.
/// </summary>
public class HardChallengeScoringTests
{
    private static HardChallengeTask Task(
        string key, HardTaskAutoSource src, decimal? target, int points, bool partial,
        int? minMinutes = null, bool enabled = true, int id = 0, int? activeCalPerWorkout = null)
        => new()
        {
            Id = id == 0 ? key.GetHashCode() & 0x7fffffff : id,
            Key = key,
            Label = key,
            AutoSource = src,
            TargetValue = target,
            MinMinutes = minMinutes,
            ActiveCalPerWorkout = activeCalPerWorkout,
            PointValue = points,
            PartialCredit = partial,
            Enabled = enabled,
        };

    private static HardChallengeScoring.HardDayInput Input(
        int hydrationMl = 0, IReadOnlyList<int>? workouts = null, bool noAlcohol = true,
        int caloriesIn = 0, int? calorieGoal = null, bool? dietOverride = null, int? activeCalories = null)
        => new(caloriesIn, 0, 0, 0, calorieGoal, null, null, null,
            hydrationMl, workouts ?? Array.Empty<int>(), dietOverride, noAlcohol, activeCalories);

    // ---- Diet auto (kept from v1) ----

    [Fact]
    public void Diet_passes_within_calorie_and_all_set_macro_goals()
    {
        HardChallengeScoring.ScoreDiet(1900, 140, 180, 55, 2000, 150, 200, 60, null).Should().BeTrue();
    }

    [Fact]
    public void Diet_override_wins_over_the_computed_result()
    {
        HardChallengeScoring.ScoreDiet(5000, 0, 0, 0, null, null, null, null, dietOverride: true).Should().BeTrue();
        HardChallengeScoring.ScoreDiet(100, 0, 0, 0, 2000, null, null, null, dietOverride: false).Should().BeFalse();
    }

    // ---- Partial-points math ----

    [Fact]
    public void Partial_credit_three_of_four_cups_at_ten_points_is_seven_point_five()
    {
        // A measurable manual "cups" task: target 4, 3 done, partial credit, 10 points → 7.5 (rounds to .5).
        var task = Task("water-cups", HardTaskAutoSource.None, target: 4, points: 10, partial: true);
        var progress = HardChallengeScoring.TaskProgress(task, Input(), manualValue: 3, manualDone: null);
        progress.Should().BeApproximately(0.75, 1e-9);
        HardChallengeScoring.TaskPoints(task, progress).Should().Be(7.5m);
    }

    [Fact]
    public void Partial_credit_one_of_two_workouts_is_half_the_points()
    {
        // 1 of 2 required workouts (each >= 45 min), partial credit, 10 points → 5.
        var task = Task("workout", HardTaskAutoSource.Workout, target: 2, points: 10, partial: true, minMinutes: 45);
        var progress = HardChallengeScoring.TaskProgress(task, Input(workouts: new[] { 50 }), null, null);
        progress.Should().BeApproximately(0.5, 1e-9);
        HardChallengeScoring.TaskPoints(task, progress).Should().Be(5m);
    }

    [Fact]
    public void Non_partial_measurable_task_is_all_or_nothing()
    {
        var task = Task("reading", HardTaskAutoSource.None, target: 10, points: 10, partial: false);
        // 7 of 10 pages, no partial credit → 0 points (not complete).
        HardChallengeScoring.TaskPoints(task, HardChallengeScoring.TaskProgress(task, Input(), 7, null)).Should().Be(0m);
        // 10 of 10 → full points.
        HardChallengeScoring.TaskPoints(task, HardChallengeScoring.TaskProgress(task, Input(), 10, null)).Should().Be(10m);
    }

    [Fact]
    public void Binary_task_is_zero_or_full_regardless_of_partial_flag()
    {
        var task = Task("no-alcohol", HardTaskAutoSource.NoAlcohol, target: null, points: 10, partial: true);
        HardChallengeScoring.TaskPoints(task, HardChallengeScoring.TaskProgress(task, Input(noAlcohol: false), null, null)).Should().Be(0m);
        HardChallengeScoring.TaskPoints(task, HardChallengeScoring.TaskProgress(task, Input(noAlcohol: true), null, null)).Should().Be(10m);
    }

    [Fact]
    public void Points_round_to_the_nearest_half()
    {
        // 1 of 3 @ 10 pts partial = 3.333… → rounds to 3.5.
        var task = Task("t", HardTaskAutoSource.None, target: 3, points: 10, partial: true);
        HardChallengeScoring.TaskPoints(task, HardChallengeScoring.TaskProgress(task, Input(), 1, null)).Should().Be(3.5m);
    }

    // ---- Custom targets change auto progress ----

    [Fact]
    public void A_custom_water_target_changes_the_water_progress()
    {
        var input = Input(hydrationMl: 2000);
        var defaultTarget = Task("water", HardTaskAutoSource.Water, target: 3785, points: 10, partial: true);
        var lowerTarget = Task("water", HardTaskAutoSource.Water, target: 2000, points: 10, partial: true);

        // Same 2000 ml: ~53% against a gallon, but 100% against a 2000 ml custom target.
        HardChallengeScoring.TaskProgress(defaultTarget, input, null, null).Should().BeApproximately(2000.0 / 3785.0, 1e-9);
        HardChallengeScoring.TaskProgress(lowerTarget, input, null, null).Should().Be(1.0);
    }

    [Fact]
    public void A_custom_workout_min_minutes_changes_which_workouts_count()
    {
        // Two workouts of 30 min: count 0 against a 45-min threshold, 2 against a 20-min threshold.
        var input = Input(workouts: new[] { 30, 30 });
        var strict = Task("w", HardTaskAutoSource.Workout, target: 2, points: 10, partial: true, minMinutes: 45);
        var lax = Task("w", HardTaskAutoSource.Workout, target: 2, points: 10, partial: true, minMinutes: 20);
        HardChallengeScoring.TaskProgress(strict, input, null, null).Should().Be(0.0);
        HardChallengeScoring.TaskProgress(lax, input, null, null).Should().Be(1.0);
    }

    // ---- Workout watch credit (smartwatch active calories) ----

    [Fact]
    public void Watch_active_calories_at_or_above_threshold_earn_one_workout_credit()
    {
        // Target 2, default threshold 300 (task ActiveCalPerWorkout null), no logged workouts, 320 active cals →
        // 1 credit → 1/2 = 50% (partial). Never more than one credit.
        var task = Task("workout", HardTaskAutoSource.Workout, target: 2, points: 10, partial: true, minMinutes: 45);
        var bd = HardChallengeScoring.WorkoutBreakdownFor(task, Input(activeCalories: 320));
        bd.LoggedWorkouts.Should().Be(0);
        bd.WatchCredit.Should().Be(1);
        bd.Count.Should().Be(1);
        bd.ActiveCalThreshold.Should().Be(HardChallengeScoring.DefaultActiveCalPerWorkout);
        HardChallengeScoring.TaskProgress(task, Input(activeCalories: 320), null, null).Should().BeApproximately(0.5, 1e-9);

        // Exactly at the threshold also earns the credit.
        HardChallengeScoring.WorkoutBreakdownFor(task, Input(activeCalories: 300)).WatchCredit.Should().Be(1);
    }

    [Fact]
    public void Watch_credit_is_capped_at_one_even_with_huge_active_calories()
    {
        var task = Task("workout", HardTaskAutoSource.Workout, target: 2, points: 10, partial: true, minMinutes: 45);
        HardChallengeScoring.WorkoutBreakdownFor(task, Input(activeCalories: 5000)).WatchCredit.Should().Be(1);
    }

    [Fact]
    public void Watch_active_calories_below_threshold_earn_no_credit()
    {
        var task = Task("workout", HardTaskAutoSource.Workout, target: 2, points: 10, partial: true, minMinutes: 45);
        var bd = HardChallengeScoring.WorkoutBreakdownFor(task, Input(activeCalories: 299));
        bd.WatchCredit.Should().Be(0);
        bd.Count.Should().Be(0);
        HardChallengeScoring.TaskProgress(task, Input(activeCalories: 299), null, null).Should().Be(0.0);
    }

    [Fact]
    public void No_watch_record_earns_no_credit()
    {
        var task = Task("workout", HardTaskAutoSource.Workout, target: 2, points: 10, partial: true, minMinutes: 45);
        var bd = HardChallengeScoring.WorkoutBreakdownFor(task, Input(activeCalories: null));
        bd.WatchCredit.Should().Be(0);
        bd.ActiveCalories.Should().BeNull();
        HardChallengeScoring.TaskProgress(task, Input(activeCalories: null), null, null).Should().Be(0.0);
    }

    [Fact]
    public void One_logged_workout_plus_watch_credit_completes_a_target_two_task()
    {
        // 1 logged (>=45) + 1 watch credit = 2/2 = 100%.
        var task = Task("workout", HardTaskAutoSource.Workout, target: 2, points: 10, partial: true, minMinutes: 45);
        var input = Input(workouts: new[] { 50 }, activeCalories: 400);
        var bd = HardChallengeScoring.WorkoutBreakdownFor(task, input);
        bd.LoggedWorkouts.Should().Be(1);
        bd.WatchCredit.Should().Be(1);
        bd.Count.Should().Be(2);
        HardChallengeScoring.TaskProgress(task, input, null, null).Should().Be(1.0);
        HardChallengeScoring.TaskPoints(task, 1.0).Should().Be(10m);
    }

    [Fact]
    public void Two_logged_workouts_ignore_the_watch_credit_since_count_is_capped_at_target()
    {
        // 2 logged already hits the target; the watch credit cannot push progress above 100%.
        var task = Task("workout", HardTaskAutoSource.Workout, target: 2, points: 10, partial: true, minMinutes: 45);
        var input = Input(workouts: new[] { 50, 60 }, activeCalories: 800);
        // The breakdown still reports the earned credit (count = 3), but progress clamps to 1.0.
        HardChallengeScoring.WorkoutBreakdownFor(task, input).Count.Should().Be(3);
        HardChallengeScoring.TaskProgress(task, input, null, null).Should().Be(1.0);
        HardChallengeScoring.TaskPoints(task, HardChallengeScoring.TaskProgress(task, input, null, null)).Should().Be(10m);
    }

    [Fact]
    public void Just_watch_credit_on_a_target_two_task_is_a_partial_one_half()
    {
        // Watch only (1 credit) on a target-2 task → 1/2 partial → half points.
        var task = Task("workout", HardTaskAutoSource.Workout, target: 2, points: 10, partial: true, minMinutes: 45);
        var input = Input(activeCalories: 350);
        var progress = HardChallengeScoring.TaskProgress(task, input, null, null);
        progress.Should().BeApproximately(0.5, 1e-9);
        HardChallengeScoring.TaskPoints(task, progress).Should().Be(5m);
    }

    [Fact]
    public void A_custom_active_calorie_threshold_is_honored()
    {
        // Custom threshold 500: 400 active cals → no credit; 520 → credit.
        var task = Task("workout", HardTaskAutoSource.Workout, target: 2, points: 10, partial: true,
            minMinutes: 45, activeCalPerWorkout: 500);
        HardChallengeScoring.WorkoutBreakdownFor(task, Input(activeCalories: 400)).WatchCredit.Should().Be(0);
        HardChallengeScoring.WorkoutBreakdownFor(task, Input(activeCalories: 520)).WatchCredit.Should().Be(1);
        HardChallengeScoring.WorkoutBreakdownFor(task, Input(activeCalories: 520)).ActiveCalThreshold.Should().Be(500);
    }

    [Fact]
    public void Active_calories_do_not_affect_non_workout_tasks()
    {
        var input = Input(hydrationMl: 1000, activeCalories: 9999, noAlcohol: true);
        var water = Task("water", HardTaskAutoSource.Water, target: 2000, points: 10, partial: true);
        var diet = Task("diet", HardTaskAutoSource.Diet, target: null, points: 10, partial: false);
        var noAlc = Task("no-alcohol", HardTaskAutoSource.NoAlcohol, target: null, points: 10, partial: false);
        // Water progress is unchanged by active calories (1000/2000 = 0.5).
        HardChallengeScoring.TaskProgress(water, input, null, null).Should().BeApproximately(0.5, 1e-9);
        // Diet has no calorie goal → 0; active calories irrelevant.
        HardChallengeScoring.TaskProgress(diet, input, null, null).Should().Be(0.0);
        HardChallengeScoring.TaskProgress(noAlc, input, null, null).Should().Be(1.0);
    }

    // ---- Day scoring + completeness ----

    [Fact]
    public void Day_points_sum_enabled_tasks_and_complete_needs_all_at_100_percent()
    {
        var tasks = new[]
        {
            Task("diet", HardTaskAutoSource.Diet, null, 10, false, id: 1),
            Task("water", HardTaskAutoSource.Water, 3785, 10, true, id: 2),
            Task("workout", HardTaskAutoSource.Workout, 2, 10, true, minMinutes: 45, id: 3),
            Task("reading", HardTaskAutoSource.None, 10, 10, true, id: 4),
            Task("no-alcohol", HardTaskAutoSource.NoAlcohol, null, 10, false, id: 5),
            Task("disabled", HardTaskAutoSource.None, 10, 50, false, enabled: false, id: 6),
        };
        var input = Input(hydrationMl: 3785, workouts: new[] { 50, 45 }, noAlcohol: true,
            caloriesIn: 1800, calorieGoal: 2000);
        var manual = new Dictionary<int, HardChallengeScoring.DayManual>
        {
            [4] = new(Value: 10, Done: null),
        };

        var full = HardChallengeScoring.ScoreDay(tasks, input, manual);
        full.DayPoints.Should().Be(50m);   // 5 enabled tasks @ 10 (the disabled 50-pt task is ignored)
        full.MaxPoints.Should().Be(50m);
        full.Complete.Should().BeTrue();

        // Drop reading to 5 pages → reading 50% → day incomplete, but partial points still COUNT.
        manual[4] = new(Value: 5, Done: null);
        var partial = HardChallengeScoring.ScoreDay(tasks, input, manual);
        partial.Complete.Should().BeFalse();
        partial.DayPoints.Should().Be(45m); // reading drops 10 → 5
    }

    [Fact]
    public void A_day_with_no_enabled_tasks_is_not_complete()
    {
        var tasks = new[] { Task("x", HardTaskAutoSource.None, 10, 10, false, enabled: false, id: 1) };
        var score = HardChallengeScoring.ScoreDay(tasks, Input(), new Dictionary<int, HardChallengeScoring.DayManual>());
        score.Complete.Should().BeFalse();
        score.DayPoints.Should().Be(0m);
    }

    [Fact]
    public void Default_task_set_is_the_classic_set_minus_the_photo()
    {
        var set = HardChallengeScoring.DefaultTaskSet(challengeId: 7, DateTime.UtcNow);
        set.Select(t => t.Key).Should().BeEquivalentTo(new[] { "diet", "water", "workout", "reading", "no-alcohol" });
        set.Should().NotContain(t => t.Key.Contains("photo"));
        set.Single(t => t.Key == "water").TargetValue.Should().Be(HardChallengeScoring.WaterGallonMl);
        set.Single(t => t.Key == "workout").TargetValue.Should().Be(HardChallengeScoring.WorkoutTargetCount);
        set.Single(t => t.Key == "workout").MinMinutes.Should().Be(HardChallengeScoring.WorkoutMinMinutes);
        set.Single(t => t.Key == "reading").TargetValue.Should().Be(HardChallengeScoring.ReadingTargetPages);
        set.Should().OnlyContain(t => t.ChallengeId == 7);
    }

    // ---- Relaxed streak (unchanged from v1) ----

    private static HardChallengeScoring.StreakDay D(bool complete, bool cheat = false, bool confession = false)
        => new(complete, cheat, confession);

    [Fact]
    public void A_missed_day_pauses_the_streak_without_resetting_it()
    {
        var r = HardChallengeScoring.RelaxedStreak(new[] { D(true), D(true), D(false), D(true) });
        r.CurrentStreak.Should().Be(3);
        r.LongestStreak.Should().Be(3);
    }

    [Fact]
    public void A_confession_or_a_cheat_day_keeps_the_run_counted()
    {
        var r = HardChallengeScoring.RelaxedStreak(new[] { D(true), D(false, confession: true), D(false, cheat: true), D(true) });
        r.CurrentStreak.Should().Be(4);
        r.LongestStreak.Should().Be(4);
    }

    [Fact]
    public void An_empty_run_is_zero()
    {
        var r = HardChallengeScoring.RelaxedStreak(Array.Empty<HardChallengeScoring.StreakDay>());
        r.CurrentStreak.Should().Be(0);
        r.LongestStreak.Should().Be(0);
    }
}
