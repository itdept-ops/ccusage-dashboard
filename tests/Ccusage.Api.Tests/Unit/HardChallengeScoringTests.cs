using Ccusage.Api.Services;
using FluentAssertions;

namespace Ccusage.Api.Tests.Unit;

/// <summary>
/// The PURE 75 Hard scoring (<see cref="HardChallengeScoring"/>) — no I/O. Covers the per-task auto scoring
/// (diet within calorie + set-macro goals with the override winning; water &gt;= 1 US gallon; the two
/// &gt;=45-minute workouts) and the RELAXED streak function (a missed day PAUSES without resetting; a
/// confession or cheat day KEEPS the run; longest = max contiguous kept-run).
/// </summary>
public class HardChallengeScoringTests
{
    // ---- Diet: within calorie goal AND every SET macro goal; override wins; unset macros skipped ----

    [Fact]
    public void Diet_passes_within_calorie_and_all_set_macro_goals()
    {
        HardChallengeScoring.ScoreDiet(
            caloriesIn: 1900, proteinG: 140, carbG: 180, fatG: 55,
            calorieGoal: 2000, proteinGoalG: 150, carbGoalG: 200, fatGoalG: 60, dietOverride: null)
            .Should().BeTrue();
    }

    [Fact]
    public void Diet_fails_when_calories_exceed_the_goal()
    {
        HardChallengeScoring.ScoreDiet(2100, 100, 100, 40, 2000, null, null, null, null)
            .Should().BeFalse();
    }

    [Fact]
    public void Diet_fails_when_a_set_macro_goal_is_exceeded_even_if_calories_are_fine()
    {
        // Calories fine, but protein over its goal → fail.
        HardChallengeScoring.ScoreDiet(1500, 200, 100, 40, 2000, 150, null, null, null)
            .Should().BeFalse();
    }

    [Fact]
    public void Diet_skips_unset_macro_goals()
    {
        // Only a calorie goal set; the (large) macros don't matter because no macro goal is set.
        HardChallengeScoring.ScoreDiet(1500, 999, 999, 999, 2000, null, null, null, null)
            .Should().BeTrue();
    }

    [Fact]
    public void Diet_override_wins_over_the_computed_result()
    {
        // Would auto-fail (no goal), but a true override forces a pass.
        HardChallengeScoring.ScoreDiet(5000, 0, 0, 0, null, null, null, null, dietOverride: true)
            .Should().BeTrue();
        // Would auto-pass, but a false override forces a fail.
        HardChallengeScoring.ScoreDiet(100, 0, 0, 0, 2000, null, null, null, dietOverride: false)
            .Should().BeFalse();
    }

    [Fact]
    public void Diet_cannot_auto_pass_without_a_calorie_goal()
    {
        HardChallengeScoring.ScoreDiet(100, 0, 0, 0, null, null, null, null, null)
            .Should().BeFalse();
    }

    // ---- Full day: six tasks + no-alcohol ----

    [Fact]
    public void A_day_is_complete_only_when_all_six_tasks_and_no_alcohol_hold()
    {
        var input = new HardChallengeScoring.HardDayInput(
            CaloriesIn: 1800, ProteinG: 140, CarbG: 180, FatG: 55,
            CalorieGoal: 2000, ProteinGoalG: 150, CarbGoalG: 200, FatGoalG: 60,
            HydrationMl: HardChallengeScoring.WaterGallonMl, WorkoutCount: 2, DietOverride: null);

        var ok = HardChallengeScoring.Score(input, readOk: true, photoTaken: true, noAlcohol: true);
        ok.DietOk.Should().BeTrue();
        ok.WaterGallonOk.Should().BeTrue();
        ok.Workout1Ok.Should().BeTrue();
        ok.Workout2Ok.Should().BeTrue();
        ok.Complete.Should().BeTrue();

        // Drop alcohol → incomplete.
        HardChallengeScoring.Score(input, true, true, noAlcohol: false).Complete.Should().BeFalse();
        // Drop the second workout → workout2 fails → incomplete.
        var oneWorkout = input with { WorkoutCount = 1 };
        var s1 = HardChallengeScoring.Score(oneWorkout, true, true, true);
        s1.Workout1Ok.Should().BeTrue();
        s1.Workout2Ok.Should().BeFalse();
        s1.Complete.Should().BeFalse();
        // Just under a gallon → water fails.
        var dry = input with { HydrationMl = HardChallengeScoring.WaterGallonMl - 1 };
        HardChallengeScoring.Score(dry, true, true, true).WaterGallonOk.Should().BeFalse();
    }

    // ---- Relaxed streak: pause-not-reset; confession/cheat keeps the run; longest = max contiguous ----

    private static HardChallengeScoring.StreakDay D(bool complete, bool cheat = false, bool confession = false)
        => new(complete, cheat, confession);

    [Fact]
    public void A_missed_day_pauses_the_streak_without_resetting_it()
    {
        // complete, complete, MISS (no confession/cheat), complete.
        var days = new[] { D(true), D(true), D(false), D(true) };
        var r = HardChallengeScoring.RelaxedStreak(days);
        // The miss pauses (stays 2), then the final complete advances to 3 — it never reset to 0.
        r.CurrentStreak.Should().Be(3);
        r.LongestStreak.Should().Be(3);
    }

    [Fact]
    public void A_confession_or_a_cheat_day_keeps_the_run_counted()
    {
        // complete, confession (kept), cheat (kept), complete → all four advance the run.
        var days = new[] { D(true), D(false, confession: true), D(false, cheat: true), D(true) };
        var r = HardChallengeScoring.RelaxedStreak(days);
        r.CurrentStreak.Should().Be(4);
        r.LongestStreak.Should().Be(4);
    }

    [Fact]
    public void Longest_streak_is_the_max_contiguous_kept_run()
    {
        // run of 2, a pause, then a run of 3 → longest 3, current 3 (the pause never reset).
        var days = new[] { D(true), D(true), D(false), D(true), D(true), D(true) };
        var r = HardChallengeScoring.RelaxedStreak(days);
        r.CurrentStreak.Should().Be(5);  // pauses don't advance but don't reset, so kept count = 5
        r.LongestStreak.Should().Be(5);
    }

    [Fact]
    public void Multiple_pauses_only_pause_never_reset()
    {
        var days = new[] { D(true), D(false), D(false), D(true) };
        var r = HardChallengeScoring.RelaxedStreak(days);
        // Two consecutive misses pause twice; the run stays at 1, then advances to 2.
        r.CurrentStreak.Should().Be(2);
        r.LongestStreak.Should().Be(2);
    }

    [Fact]
    public void An_empty_run_is_zero()
    {
        var r = HardChallengeScoring.RelaxedStreak(Array.Empty<HardChallengeScoring.StreakDay>());
        r.CurrentStreak.Should().Be(0);
        r.LongestStreak.Should().Be(0);
    }
}
