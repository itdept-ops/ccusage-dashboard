using Ccusage.Api.Data.Entities;
using Ccusage.Api.Dtos;

namespace Ccusage.Api.Services;

/// <summary>
/// Pure, side-effect-free computation of a user's body-metric estimates (age, BMI, BMR, TDEE, and the
/// suggested calorie + macro targets) from their current <see cref="TrackerProfile"/>. Everything is
/// metric (kg + cm). Any field whose inputs are missing stays null — partial stats are intended (e.g.
/// BMI without BMR when sex is Unspecified, or nothing at all when height is missing).
///
/// Formulas (metric):
/// <list type="bullet">
///   <item>Age = whole years from DateOfBirth to <c>today</c>.</item>
///   <item>BMI = kg / (cm/100)^2, 1dp. Category: &lt;18.5 Underweight, &lt;25 Normal, &lt;30 Overweight, else Obese.</item>
///   <item>BMR (Mifflin-St Jeor, needs weight+height+age+sex): Male = 10·kg + 6.25·cm − 5·age + 5;
///   Female = 10·kg + 6.25·cm − 5·age − 161.</item>
///   <item>TDEE = BMR · activity factor (Sedentary 1.2, Light 1.375, Moderate 1.55, Active 1.725, VeryActive 1.9).</item>
///   <item>Suggested calories from TDEE + goal: LoseWeight = TDEE−500; Maintain/Endurance = TDEE; GainMuscle = TDEE+300.</item>
///   <item>Suggested macros (needs weight + a calorie target — the suggestion, else DailyCalorieGoal):
///   protein = round(1.8·kg) g; fat = round(0.8·kg) g; carbs = round((cal − protein·4 − fat·9)/4) g, floored at 0.</item>
/// </list>
/// </summary>
public static class TrackerStats
{
    /// <summary>Activity multiplier applied to BMR to get TDEE.</summary>
    public static double ActivityFactor(ActivityLevel level) => level switch
    {
        ActivityLevel.Sedentary => 1.2,
        ActivityLevel.Light => 1.375,
        ActivityLevel.Moderate => 1.55,
        ActivityLevel.Active => 1.725,
        ActivityLevel.VeryActive => 1.9,
        _ => 1.2,
    };

    /// <summary>Whole years from <paramref name="dob"/> to <paramref name="today"/> (null if no DOB or future).</summary>
    public static int? AgeFrom(DateOnly? dob, DateOnly today)
    {
        if (dob is not { } d || d > today) return null;
        var age = today.Year - d.Year;
        if (today < d.AddYears(age)) age--; // birthday not yet reached this year
        return age < 0 ? null : age;
    }

    /// <summary>BMI category band for a rounded BMI value.</summary>
    public static string CategoryFor(double bmi) =>
        bmi < 18.5 ? "Underweight"
        : bmi < 25 ? "Normal"
        : bmi < 30 ? "Overweight"
        : "Obese";

    /// <summary>Compute the stats for a profile as of <paramref name="today"/> (display-timezone date).</summary>
    public static TrackerStatsDto Compute(TrackerProfile p, DateOnly today)
    {
        var dto = new TrackerStatsDto();

        var weight = p.WeightKg is { } w && w > 0 ? w : (double?)null;
        var height = p.HeightCm is { } h && h > 0 ? h : (double?)null;
        var age = AgeFrom(p.DateOfBirth, today);
        dto.Age = age;

        // --- BMI (needs weight + height) ---
        if (weight is { } kg && height is { } cm)
        {
            var m = cm / 100.0;
            var bmi = Math.Round(kg / (m * m), 1);
            dto.Bmi = bmi;
            dto.BmiCategory = CategoryFor(bmi);
        }

        // --- BMR (Mifflin-St Jeor; needs weight + height + age + a known sex) ---
        int? bmr = null;
        if (weight is { } bkg && height is { } bcm && age is { } a && p.Sex != BiologicalSex.Unspecified)
        {
            var raw = 10 * bkg + 6.25 * bcm - 5 * a + (p.Sex == BiologicalSex.Male ? 5 : -161);
            bmr = (int)Math.Round(raw);
            dto.Bmr = bmr;
        }

        // --- TDEE (needs BMR) ---
        int? tdee = null;
        if (bmr is { } b)
        {
            tdee = (int)Math.Round(b * ActivityFactor(p.ActivityLevel));
            dto.Tdee = tdee;
        }

        // --- Suggested calorie goal (needs TDEE + goal) ---
        int? suggested = null;
        if (tdee is { } t)
        {
            suggested = p.Goal switch
            {
                TrackerGoal.LoseWeight => t - 500,
                TrackerGoal.GainMuscle => t + 300,
                _ => t, // Maintain + Endurance
            };
            dto.SuggestedCalorieGoal = suggested;
        }

        // --- Suggested macros (needs weight + a calorie target: the suggestion, else the set goal) ---
        var calorieTarget = suggested ?? p.DailyCalorieGoal;
        if (weight is { } mkg && calorieTarget is { } cal)
        {
            var protein = (int)Math.Round(1.8 * mkg);
            var fat = (int)Math.Round(0.8 * mkg);
            var carbs = (int)Math.Round((cal - protein * 4 - fat * 9) / 4.0);
            dto.SuggestedProteinG = protein;
            dto.SuggestedFatG = fat;
            dto.SuggestedCarbG = Math.Max(0, carbs);
        }

        return dto;
    }
}
