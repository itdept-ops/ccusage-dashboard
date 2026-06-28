using Ccusage.Api.Data.Entities;

namespace Ccusage.Api.Services;

/// <summary>
/// PURE habit scoring — no I/O, fully unit-testable. The habit verticals DELEGATE all the day-level points/
/// progress math to the live <see cref="HardChallengeScoring"/> (a habit is treated as a single
/// <see cref="HardChallengeTask"/> via the <see cref="ToTask"/> shim — nothing is extracted or refactored from
/// the 75-Hard scorer). The ONE genuinely net-new piece is the CADENCE-AWARE streak fold: it reuses the same
/// "kept / paused, never reset" relaxed-streak shape as <see cref="HardChallengeScoring.RelaxedStreak"/> but
/// only counts the days/periods the habit's <see cref="HabitCadence"/> actually EXPECTS — a non-expected day is
/// neither a miss nor an advance.
/// </summary>
public static class HabitScoring
{
    /// <summary>The default minimum logged-exercise duration for a Workout-auto habit (reuses the 75-Hard floor).</summary>
    public const int WorkoutMinMinutes = HardChallengeScoring.WorkoutMinMinutes;

    /// <summary>
    /// Shim a <see cref="Habit"/> into the single <see cref="HardChallengeTask"/> the 75-Hard scorer expects so
    /// the day progress math (binary vs measurable, partial credit, auto-source against the habit's own target)
    /// is reused VERBATIM. PointValue is a constant 1 — habit "points" are irrelevant; we only use the
    /// progress fraction + completeness from the scorer.
    /// </summary>
    public static HardChallengeTask ToTask(Habit habit) => new()
    {
        Id = habit.Id,
        Key = "habit",
        Label = "habit",
        AutoSource = habit.AutoSource,
        TargetValue = habit.TargetValue,
        MinMinutes = habit.MinMinutes,
        Unit = habit.Unit,
        PointValue = 1,
        PartialCredit = habit.PartialCredit,
        Enabled = true,
    };

    /// <summary>
    /// The completion FRACTION (0..1) of a habit on one day, drawing auto-source progress from the tracker
    /// <paramref name="input"/> and manual progress from <paramref name="value"/>/<paramref name="done"/>.
    /// Delegates entirely to <see cref="HardChallengeScoring.TaskProgress"/>.
    /// </summary>
    public static double DayProgress(Habit habit, HardChallengeScoring.HardDayInput input, decimal? value, bool? done)
        => HardChallengeScoring.TaskProgress(ToTask(habit), input, value, done);

    /// <summary>Whether the habit is COMPLETE on one day (progress reaches 100%).</summary>
    public static bool IsComplete(Habit habit, HardChallengeScoring.HardDayInput input, decimal? value, bool? done)
        => DayProgress(habit, input, value, done) >= 1.0;

    // ===================================================================================
    // Cadence — which days/periods does a habit EXPECT?
    // ===================================================================================

    /// <summary>
    /// Whether <paramref name="date"/> is an EXPECTED unit-day for a <see cref="HabitCadence.Daily"/> or
    /// <see cref="HabitCadence.CustomDaysOfWeek"/> habit (Daily: always; CustomDaysOfWeek: the weekday bit is
    /// set in <see cref="Habit.DaysOfWeekMask"/>). Not meaningful for the period-based cadences (Weekly /
    /// XTimesPerPeriod) which fold over PERIODS rather than days.
    /// </summary>
    public static bool ExpectsDay(Habit habit, DateOnly date) => habit.Cadence switch
    {
        HabitCadence.Daily => true,
        HabitCadence.CustomDaysOfWeek => (habit.DaysOfWeekMask & (1 << (int)date.DayOfWeek)) != 0,
        _ => false, // period cadences don't use per-day expectation
    };

    // ===================================================================================
    // The cadence-aware streak (the ONLY net-new logic)
    // ===================================================================================

    /// <summary>One day's completion facts, used to build the cadence-aware streak.</summary>
    public readonly record struct DayFact(DateOnly Date, bool Complete, bool Skip);

    /// <summary>The current + longest cadence-aware streak. Shares the relaxed-streak shape (kept advances,
    /// paused holds, never resets) but the UNITS are cadence-defined.</summary>
    public readonly record struct StreakResult(int CurrentStreak, int LongestStreak);

    /// <summary>
    /// The CADENCE-AWARE streak over <paramref name="days"/> (the habit's at-or-before-today day facts, any
    /// order — sorted internally). The streak UNIT depends on the cadence:
    /// <list type="bullet">
    ///   <item><b>Daily</b>: every calendar day in [StartDate, today] is a unit. A complete OR skipped day is
    ///   KEPT (advances); an incomplete non-skip day PAUSES (holds, no reset). Identical to the relaxed streak.</item>
    ///   <item><b>CustomDaysOfWeek</b>: only EXPECTED weekdays are units; a non-expected weekday is skipped
    ///   entirely (neither miss nor advance). An expected day that is complete or skipped is KEPT; an expected
    ///   incomplete non-skip day PAUSES.</item>
    ///   <item><b>Weekly</b>: each ISO week (Mon–Sun) touching [StartDate, today] is a unit. A week is KEPT
    ///   when the habit was complete on at least one of its days OR any day in the week was skipped; an
    ///   all-incomplete, no-skip week PAUSES.</item>
    ///   <item><b>XTimesPerPeriod</b>: each rolling <see cref="Habit.PeriodDays"/>-day period from StartDate is a
    ///   unit (only periods that have BEGUN by today count). A period is KEPT when its complete-day count meets
    ///   <see cref="Habit.TimesPerPeriod"/> OR any day in it was skipped; otherwise it PAUSES. The CURRENT
    ///   (still-open) period is only counted once it is fully complete (met its target) so an in-progress
    ///   period never prematurely pauses the run.</item>
    /// </list>
    /// In every case the fold is the SAME relaxed shape — kept ⇒ +1 (track longest), paused ⇒ hold — applied to
    /// the cadence's UNITS via <see cref="HardChallengeScoring.RelaxedStreak"/>.
    /// </summary>
    public static StreakResult CadenceStreak(Habit habit, IReadOnlyList<DayFact> days, DateOnly today)
    {
        var units = BuildUnits(habit, days, today);
        var relaxed = HardChallengeScoring.RelaxedStreak(units);
        return new StreakResult(relaxed.CurrentStreak, relaxed.LongestStreak);
    }

    /// <summary>
    /// Reduce the habit's day facts to the ordered (oldest-first) list of <see cref="HardChallengeScoring.StreakDay"/>
    /// UNITS the cadence expects, so the existing relaxed fold scores them. A kept unit maps to
    /// Complete=true; a paused unit maps to all-false; a skip maps to IsCheatDay=true (the "keeps the run"
    /// lever). Non-expected days never produce a unit.
    /// </summary>
    private static List<HardChallengeScoring.StreakDay> BuildUnits(
        Habit habit, IReadOnlyList<DayFact> days, DateOnly today)
    {
        var byDate = days
            .Where(d => d.Date >= habit.StartDate && d.Date <= today)
            .GroupBy(d => d.Date)
            .ToDictionary(g => g.Key, g => g.First());

        var endCal = habit.EndDate is { } ed && ed < today ? ed : today;
        if (endCal < habit.StartDate) return new List<HardChallengeScoring.StreakDay>();

        return habit.Cadence switch
        {
            HabitCadence.Daily => DailyUnits(habit, byDate, endCal),
            HabitCadence.CustomDaysOfWeek => CustomDayUnits(habit, byDate, endCal),
            HabitCadence.Weekly => WeeklyUnits(habit, byDate, endCal),
            HabitCadence.XTimesPerPeriod => PeriodUnits(habit, byDate, endCal, today),
            _ => DailyUnits(habit, byDate, endCal),
        };
    }

    /// <summary>A KEPT unit (complete) or, if skipped, a cheat-day unit; an incomplete non-skip unit pauses.</summary>
    private static HardChallengeScoring.StreakDay Unit(bool complete, bool skip)
        => new(Complete: complete, IsCheatDay: skip, HasConfession: false);

    private static List<HardChallengeScoring.StreakDay> DailyUnits(
        Habit habit, IReadOnlyDictionary<DateOnly, DayFact> byDate, DateOnly endCal)
    {
        var list = new List<HardChallengeScoring.StreakDay>();
        for (var d = habit.StartDate; d <= endCal; d = d.AddDays(1))
        {
            byDate.TryGetValue(d, out var f);
            list.Add(Unit(f.Complete, f.Skip));
        }
        return list;
    }

    private static List<HardChallengeScoring.StreakDay> CustomDayUnits(
        Habit habit, IReadOnlyDictionary<DateOnly, DayFact> byDate, DateOnly endCal)
    {
        var list = new List<HardChallengeScoring.StreakDay>();
        for (var d = habit.StartDate; d <= endCal; d = d.AddDays(1))
        {
            if (!ExpectsDay(habit, d)) continue; // non-expected weekday: neither miss nor advance
            byDate.TryGetValue(d, out var f);
            list.Add(Unit(f.Complete, f.Skip));
        }
        return list;
    }

    private static List<HardChallengeScoring.StreakDay> WeeklyUnits(
        Habit habit, IReadOnlyDictionary<DateOnly, DayFact> byDate, DateOnly endCal)
    {
        // Anchor each week to the Monday on/before StartDate; a week is a unit once it has begun.
        var firstMonday = MondayOnOrBefore(habit.StartDate);
        var list = new List<HardChallengeScoring.StreakDay>();
        for (var weekStart = firstMonday; weekStart <= endCal; weekStart = weekStart.AddDays(7))
        {
            var weekEnd = weekStart.AddDays(6);
            var anyComplete = false;
            var anySkip = false;
            for (var d = weekStart; d <= weekEnd && d <= endCal; d = d.AddDays(1))
            {
                if (d < habit.StartDate) continue;
                if (!byDate.TryGetValue(d, out var f)) continue;
                anyComplete |= f.Complete;
                anySkip |= f.Skip;
            }
            list.Add(Unit(anyComplete, anySkip));
        }
        return list;
    }

    private static List<HardChallengeScoring.StreakDay> PeriodUnits(
        Habit habit, IReadOnlyDictionary<DateOnly, DayFact> byDate, DateOnly endCal, DateOnly today)
    {
        var periodDays = Math.Max(1, habit.PeriodDays);
        var target = Math.Max(1, habit.TimesPerPeriod);
        var list = new List<HardChallengeScoring.StreakDay>();
        for (var periodStart = habit.StartDate; periodStart <= endCal; periodStart = periodStart.AddDays(periodDays))
        {
            var periodEnd = periodStart.AddDays(periodDays - 1);
            var completeCount = 0;
            var anySkip = false;
            for (var d = periodStart; d <= periodEnd && d <= endCal; d = d.AddDays(1))
            {
                if (!byDate.TryGetValue(d, out var f)) continue;
                if (f.Complete) completeCount++;
                anySkip |= f.Skip;
            }
            var met = completeCount >= target;
            var isCurrentOpenPeriod = periodEnd >= today;
            // A still-open current period is only KEPT once it has met its target; until then it neither
            // advances nor pauses the run (it is not yet a finished unit).
            if (isCurrentOpenPeriod && !met) continue;
            list.Add(Unit(met, anySkip));
        }
        return list;
    }

    /// <summary>The Monday on or before <paramref name="d"/> (ISO week anchor).</summary>
    private static DateOnly MondayOnOrBefore(DateOnly d)
    {
        var delta = ((int)d.DayOfWeek + 6) % 7; // Sun=0 → 6, Mon=1 → 0, …
        return d.AddDays(-delta);
    }
}
