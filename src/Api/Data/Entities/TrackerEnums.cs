namespace Ccusage.Api.Data.Entities;

/// <summary>The fitness objective a user's tracker is oriented around; tags the exercise library too.</summary>
public enum TrackerGoal
{
    LoseWeight = 0,
    Maintain = 1,
    GainMuscle = 2,
    Endurance = 3,
}

/// <summary>Which meal a logged food belongs to, so the day view can group entries.</summary>
public enum MealType
{
    Breakfast = 0,
    Lunch = 1,
    Dinner = 2,
    Snack = 3,
}

/// <summary>
/// Biological sex, used ONLY as an input to the metabolic (BMR/TDEE) estimate. Labelled neutrally
/// "for metabolic estimates" in the UI. <see cref="Unspecified"/> means the user hasn't supplied it,
/// so BMR/TDEE (and anything derived from them) are not computed.
/// </summary>
public enum BiologicalSex
{
    Unspecified = 0,
    Male = 1,
    Female = 2,
}

/// <summary>How active the user is day-to-day; selects the TDEE activity multiplier on top of BMR.</summary>
public enum ActivityLevel
{
    Sedentary = 0,
    Light = 1,
    Moderate = 2,
    Active = 3,
    VeryActive = 4,
}

/// <summary>
/// The user's preferred display units. A DISPLAY preference only — the backend always stores and
/// returns metric (kilograms + centimetres); the client converts for entry/display.
/// </summary>
public enum UnitSystem
{
    Metric = 0,
    Imperial = 1,
}

/// <summary>
/// How a day's watch ACTIVE CALORIES combine with the logged-exercise calorie sum to produce the day's
/// resolved "calories out". <see cref="Add"/> adds the watch total on top of logged exercises; <see
/// cref="Override"/> replaces the logged-exercise sum with the watch total (a watch active-calories
/// figure usually already includes the day's workouts). With no watch entry / no active-calories value,
/// neither applies and calories out is just the logged-exercise sum.
/// </summary>
public enum ActivityCalorieMode
{
    Add = 0,
    Override = 1,
}
