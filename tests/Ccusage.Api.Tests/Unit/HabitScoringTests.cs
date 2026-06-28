using Ccusage.Api.Data.Entities;
using Ccusage.Api.Services;
using FluentAssertions;

namespace Ccusage.Api.Tests.Unit;

/// <summary>
/// The PURE habit scoring (<see cref="HabitScoring"/>) — no I/O. The day-level math is delegated to
/// <see cref="HardChallengeScoring"/> (covered by its own tests via the shim), so these focus on the ONE
/// net-new piece: the CADENCE-AWARE streak fold. Fixtures cover Daily, Weekly, X-times-per-period, and
/// custom-days cadences, plus the skip (cheat) lever and the open-period rule.
/// </summary>
public class HabitScoringTests
{
    private static Habit Habit(
        HabitCadence cadence, string startIso, string? endIso = null,
        int daysMask = 0, int timesPerPeriod = 1, int periodDays = 7)
        => new()
        {
            Id = 1,
            Cadence = cadence,
            StartDate = DateOnly.Parse(startIso),
            EndDate = endIso is null ? null : DateOnly.Parse(endIso),
            DaysOfWeekMask = daysMask,
            TimesPerPeriod = timesPerPeriod,
            PeriodDays = periodDays,
        };

    private static HabitScoring.DayFact F(string iso, bool complete, bool skip = false)
        => new(DateOnly.Parse(iso), complete, skip);

    // ---- Daily cadence (identical to the relaxed streak) ----

    [Fact]
    public void Daily_missed_day_pauses_without_resetting()
    {
        // 2026-06-01 .. 06-04 expected daily; a gap on 06-03 pauses (no reset).
        var habit = Habit(HabitCadence.Daily, "2026-06-01");
        var facts = new[]
        {
            F("2026-06-01", true), F("2026-06-02", true),
            F("2026-06-03", false), F("2026-06-04", true),
        };
        var r = HabitScoring.CadenceStreak(habit, facts, DateOnly.Parse("2026-06-04"));
        r.CurrentStreak.Should().Be(3);
        r.LongestStreak.Should().Be(3);
    }

    [Fact]
    public void Daily_unlogged_day_in_the_middle_counts_as_a_miss_pause()
    {
        // No fact for 06-02 at all → it's an expected-but-incomplete day → pause.
        var habit = Habit(HabitCadence.Daily, "2026-06-01");
        var facts = new[] { F("2026-06-01", true), F("2026-06-03", true) };
        var r = HabitScoring.CadenceStreak(habit, facts, DateOnly.Parse("2026-06-03"));
        // day1 keep(1), day2 pause(1), day3 keep(2)
        r.CurrentStreak.Should().Be(2);
        r.LongestStreak.Should().Be(2);
    }

    [Fact]
    public void A_skipped_day_keeps_the_run_counted()
    {
        var habit = Habit(HabitCadence.Daily, "2026-06-01");
        var facts = new[]
        {
            F("2026-06-01", true), F("2026-06-02", false, skip: true), F("2026-06-03", true),
        };
        var r = HabitScoring.CadenceStreak(habit, facts, DateOnly.Parse("2026-06-03"));
        r.CurrentStreak.Should().Be(3);
    }

    // ---- Custom days of week ----

    [Fact]
    public void Custom_days_only_count_expected_weekdays()
    {
        // Mon/Wed/Fri only (bits 1,3,5). 2026-06-01 is a Monday.
        const int monWedFri = (1 << 1) | (1 << 3) | (1 << 5);
        var habit = Habit(HabitCadence.CustomDaysOfWeek, "2026-06-01", daysMask: monWedFri);
        // Complete on Mon (06-01), Wed (06-03), Fri (06-05); the weekend + Tue/Thu are NOT expected → skipped.
        var facts = new[]
        {
            F("2026-06-01", true),  // Mon - expected
            F("2026-06-02", false), // Tue - NOT expected (ignored even though incomplete)
            F("2026-06-03", true),  // Wed - expected
            F("2026-06-04", false), // Thu - NOT expected
            F("2026-06-05", true),  // Fri - expected
        };
        var r = HabitScoring.CadenceStreak(habit, facts, DateOnly.Parse("2026-06-07"));
        // 3 expected days all complete → streak 3 (the non-expected incomplete days never break it).
        r.CurrentStreak.Should().Be(3);
        r.LongestStreak.Should().Be(3);
    }

    [Fact]
    public void Custom_days_a_missed_expected_day_pauses()
    {
        const int monWedFri = (1 << 1) | (1 << 3) | (1 << 5);
        var habit = Habit(HabitCadence.CustomDaysOfWeek, "2026-06-01", daysMask: monWedFri);
        var facts = new[]
        {
            F("2026-06-01", true),  // Mon
            F("2026-06-03", false), // Wed - expected but missed → pause
            F("2026-06-05", true),  // Fri
        };
        var r = HabitScoring.CadenceStreak(habit, facts, DateOnly.Parse("2026-06-05"));
        r.CurrentStreak.Should().Be(2); // Mon keep(1), Wed pause(1), Fri keep(2)
        r.LongestStreak.Should().Be(2);
    }

    // ---- Weekly cadence ----

    [Fact]
    public void Weekly_a_week_is_kept_if_any_day_was_complete()
    {
        // Two ISO weeks: 2026-06-01 (Mon) .. 06-14 (Sun). Each week needs >=1 complete day.
        var habit = Habit(HabitCadence.Weekly, "2026-06-01");
        var facts = new[]
        {
            F("2026-06-03", true),  // week 1 has a completion
            F("2026-06-10", true),  // week 2 has a completion
        };
        var r = HabitScoring.CadenceStreak(habit, facts, DateOnly.Parse("2026-06-14"));
        r.CurrentStreak.Should().Be(2); // both weeks kept
        r.LongestStreak.Should().Be(2);
    }

    [Fact]
    public void Weekly_an_empty_week_pauses_the_run()
    {
        // Week 1 complete, week 2 empty (no completion), week 3 complete → middle week pauses.
        var habit = Habit(HabitCadence.Weekly, "2026-06-01");
        var facts = new[]
        {
            F("2026-06-02", true),  // week 1 (Mon 06-01..Sun 06-07)
            // week 2 (06-08..06-14): nothing → pause
            F("2026-06-16", true),  // week 3 (06-15..06-21)
        };
        var r = HabitScoring.CadenceStreak(habit, facts, DateOnly.Parse("2026-06-21"));
        r.CurrentStreak.Should().Be(2); // w1 keep(1), w2 pause(1), w3 keep(2)
        r.LongestStreak.Should().Be(2);
    }

    // ---- X times per period ----

    [Fact]
    public void X_times_per_period_keeps_a_period_when_target_met()
    {
        // 3 times per 7-day period; period 1 = 06-01..06-07, period 2 = 06-08..06-14.
        var habit = Habit(HabitCadence.XTimesPerPeriod, "2026-06-01", timesPerPeriod: 3, periodDays: 7);
        var facts = new[]
        {
            F("2026-06-01", true), F("2026-06-02", true), F("2026-06-03", true), // period 1: 3 ✓
            F("2026-06-08", true), F("2026-06-09", true), F("2026-06-10", true), // period 2: 3 ✓
        };
        // today after both periods closed
        var r = HabitScoring.CadenceStreak(habit, facts, DateOnly.Parse("2026-06-15"));
        r.CurrentStreak.Should().Be(2);
        r.LongestStreak.Should().Be(2);
    }

    [Fact]
    public void X_times_per_period_pauses_a_closed_period_that_missed_target()
    {
        var habit = Habit(HabitCadence.XTimesPerPeriod, "2026-06-01", timesPerPeriod: 3, periodDays: 7);
        var facts = new[]
        {
            F("2026-06-01", true), F("2026-06-02", true), F("2026-06-03", true), // period 1: 3 ✓
            F("2026-06-08", true), F("2026-06-09", true),                        // period 2: only 2 ✗
            F("2026-06-15", true), F("2026-06-16", true), F("2026-06-17", true), // period 3: 3 ✓
        };
        var r = HabitScoring.CadenceStreak(habit, facts, DateOnly.Parse("2026-06-22"));
        r.CurrentStreak.Should().Be(2); // p1 keep, p2 pause, p3 keep
        r.LongestStreak.Should().Be(2);
    }

    [Fact]
    public void X_times_per_period_does_not_prematurely_pause_an_open_current_period()
    {
        // Period 1 met (closed). Current period 2 is still open (today = 06-09) with only 1 of 3 so far —
        // it must NOT pause the run yet (an in-progress period isn't a finished unit).
        var habit = Habit(HabitCadence.XTimesPerPeriod, "2026-06-01", timesPerPeriod: 3, periodDays: 7);
        var facts = new[]
        {
            F("2026-06-01", true), F("2026-06-02", true), F("2026-06-03", true), // period 1: 3 ✓
            F("2026-06-08", true),                                               // period 2 (open): 1 so far
        };
        var r = HabitScoring.CadenceStreak(habit, facts, DateOnly.Parse("2026-06-09"));
        r.CurrentStreak.Should().Be(1); // only the closed period 1 counts; the open period is not yet a unit
        r.LongestStreak.Should().Be(1);
    }

    [Fact]
    public void X_times_per_period_counts_an_open_period_once_it_meets_target()
    {
        // Current open period 2 already hit its target of 2 by today → it IS a kept unit.
        var habit = Habit(HabitCadence.XTimesPerPeriod, "2026-06-01", timesPerPeriod: 2, periodDays: 7);
        var facts = new[]
        {
            F("2026-06-01", true), F("2026-06-02", true),  // period 1: 2 ✓
            F("2026-06-08", true), F("2026-06-09", true),  // period 2 (open): 2 ✓ already
        };
        var r = HabitScoring.CadenceStreak(habit, facts, DateOnly.Parse("2026-06-10"));
        r.CurrentStreak.Should().Be(2);
    }

    // ---- Empty / edge ----

    [Fact]
    public void An_empty_history_is_zero()
    {
        var habit = Habit(HabitCadence.Daily, "2026-06-01");
        var r = HabitScoring.CadenceStreak(habit, Array.Empty<HabitScoring.DayFact>(), DateOnly.Parse("2026-06-01"));
        r.CurrentStreak.Should().Be(0);
        r.LongestStreak.Should().Be(0);
    }

    [Fact]
    public void ExpectsDay_respects_the_weekday_mask()
    {
        const int weekendsOnly = (1 << 0) | (1 << 6); // Sun + Sat
        var habit = Habit(HabitCadence.CustomDaysOfWeek, "2026-06-01", daysMask: weekendsOnly);
        HabitScoring.ExpectsDay(habit, DateOnly.Parse("2026-06-06")).Should().BeTrue();  // Saturday
        HabitScoring.ExpectsDay(habit, DateOnly.Parse("2026-06-07")).Should().BeTrue();  // Sunday
        HabitScoring.ExpectsDay(habit, DateOnly.Parse("2026-06-08")).Should().BeFalse(); // Monday
    }

    // ---- The delegated day math (sanity: the shim reuses HardChallengeScoring) ----

    [Fact]
    public void Day_progress_delegates_to_the_75hard_scorer_for_a_measurable_habit()
    {
        var habit = new Habit { Id = 1, TargetValue = 10, PartialCredit = true, AutoSource = HardTaskAutoSource.None };
        var progress = HabitScoring.DayProgress(habit, HabitScoringInput(), value: 5, done: null);
        progress.Should().BeApproximately(0.5, 1e-9);
        HabitScoring.IsComplete(habit, HabitScoringInput(), value: 10, done: null).Should().BeTrue();
    }

    [Fact]
    public void Water_auto_habit_scores_against_its_own_target_via_the_shim()
    {
        var habit = new Habit { Id = 1, TargetValue = 2000, PartialCredit = true, AutoSource = HardTaskAutoSource.Water };
        var input = new HardChallengeScoring.HardDayInput(
            0, 0, 0, 0, null, null, null, null, 1000, Array.Empty<int>(), null, true, null);
        HabitScoring.DayProgress(habit, input, null, null).Should().BeApproximately(0.5, 1e-9);
    }

    private static HardChallengeScoring.HardDayInput HabitScoringInput() =>
        new(0, 0, 0, 0, null, null, null, null, 0, Array.Empty<int>(), null, true, null);
}
