using Ccusage.Api.Data.Entities;

namespace Ccusage.Api.Services;

/// <summary>
/// PURE 75 Hard scoring — no I/O, fully unit-testable. Two responsibilities:
///
/// <list type="bullet">
///   <item>The per-task day scoring (diet / water / workouts) that MUST stay consistent with the tracker day
///   roll-up (<c>TrackerEndpoints.BuildDayAsync</c>): diet is calories-in within the calorie goal AND within
///   every SET macro goal (unset macros are skipped), with a non-null <see cref="HardDayInput.DietOverride"/>
///   winning; water is the day's hydration sum >= one US gallon (3785 ml); a workout counts when its duration is
///   >= 45 minutes (>= 1 such workout ⇒ workout 1, >= 2 ⇒ workout 2). A day is COMPLETE when all six tasks pass
///   AND no-alcohol holds.</item>
///   <item>The RELAXED streak: a re-derivable fold over the ordered day rows. A PAST day that is incomplete AND
///   not a cheat day AND has no confession PAUSES the run (the streak does not advance) but does NOT reset to 0;
///   a confession or a cheat day KEEPS the run counted (advances it). The longest streak is the max contiguous
///   kept-run length.</item>
/// </list>
/// </summary>
public static class HardChallengeScoring
{
    /// <summary>One US gallon in millilitres — the daily water target.</summary>
    public const int WaterGallonMl = 3785;

    /// <summary>The minimum logged-exercise duration (minutes) that counts as a 75-Hard workout.</summary>
    public const int WorkoutMinMinutes = 45;

    /// <summary>The tracker-derived inputs to a day's auto scoring (mirrors the tracker day roll-up fields).</summary>
    public readonly record struct HardDayInput(
        int CaloriesIn,
        double ProteinG,
        double CarbG,
        double FatG,
        int? CalorieGoal,
        int? ProteinGoalG,
        int? CarbGoalG,
        int? FatGoalG,
        int HydrationMl,
        int WorkoutCount,
        bool? DietOverride);

    /// <summary>The six auto/manual task results for a day + whether it is fully complete.</summary>
    public readonly record struct HardDayScore(
        bool DietOk,
        bool WaterGallonOk,
        bool Workout1Ok,
        bool Workout2Ok,
        bool ReadOk,
        bool PhotoTaken,
        bool NoAlcohol,
        bool Complete);

    /// <summary>
    /// AUTO diet result: calories-in is within the daily calorie goal AND within every SET macro goal
    /// (an unset goal is skipped). "Within" means at or under the target. With NO calorie goal set, diet cannot
    /// auto-pass (there is nothing to measure against) — the user can still attest via the override. A non-null
    /// <paramref name="dietOverride"/> WINS over the computed result.
    /// </summary>
    public static bool ScoreDiet(
        int caloriesIn, double proteinG, double carbG, double fatG,
        int? calorieGoal, int? proteinGoalG, int? carbGoalG, int? fatGoalG, bool? dietOverride)
    {
        if (dietOverride is { } o) return o;
        if (calorieGoal is not { } cal) return false; // nothing to measure against → not auto-passable
        if (caloriesIn > cal) return false;
        if (proteinGoalG is { } pg && proteinG > pg) return false;
        if (carbGoalG is { } cg && carbG > cg) return false;
        if (fatGoalG is { } fg && fatG > fg) return false;
        return true;
    }

    /// <summary>Score the six tasks for a day from the tracker inputs + the day's manual fields.</summary>
    public static HardDayScore Score(HardDayInput input, bool readOk, bool photoTaken, bool noAlcohol)
    {
        var dietOk = ScoreDiet(
            input.CaloriesIn, input.ProteinG, input.CarbG, input.FatG,
            input.CalorieGoal, input.ProteinGoalG, input.CarbGoalG, input.FatGoalG, input.DietOverride);
        var waterOk = input.HydrationMl >= WaterGallonMl;
        var w1 = input.WorkoutCount >= 1;
        var w2 = input.WorkoutCount >= 2;
        var complete = dietOk && waterOk && w1 && w2 && readOk && photoTaken && noAlcohol;
        return new HardDayScore(dietOk, waterOk, w1, w2, readOk, photoTaken, noAlcohol, complete);
    }

    /// <summary>A single past/current day's contribution to the Relaxed streak.</summary>
    public readonly record struct StreakDay(bool Complete, bool IsCheatDay, bool HasConfession);

    /// <summary>The current + longest Relaxed streak over an ordered (oldest-first) run of days.</summary>
    public readonly record struct StreakResult(int CurrentStreak, int LongestStreak);

    /// <summary>
    /// The RELAXED streak fold over <paramref name="days"/> (MUST be oldest-first). For each day:
    /// <list type="bullet">
    ///   <item>COMPLETE, or a CHEAT day, or a day with a CONFESSION ⇒ the run is KEPT and ADVANCES by one.</item>
    ///   <item>incomplete with NO confession and NOT a cheat day ⇒ the run PAUSES: the current streak does not
    ///   advance, but it does NOT reset to 0 (the run survives a slip).</item>
    /// </list>
    /// The longest streak is the maximum contiguous kept-run length seen. (A confession/cheat advances the run
    /// exactly like a completed day for streak purposes — that is the whole point of the Relaxed ruleset.)
    /// </summary>
    public static StreakResult RelaxedStreak(IReadOnlyList<StreakDay> days)
    {
        int current = 0, longest = 0;
        foreach (var d in days)
        {
            var kept = d.Complete || d.IsCheatDay || d.HasConfession;
            if (kept)
            {
                current += 1;
                if (current > longest) longest = current;
            }
            // else: PAUSE — leave `current` unchanged (no advance, no reset).
        }
        return new StreakResult(current, longest);
    }
}
