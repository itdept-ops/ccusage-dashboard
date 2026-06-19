namespace Ccusage.Api.Dtos;

/// <summary>
/// Request/response DTOs for the AI-assist endpoints (<c>/api/ai</c>), which proxy Google Gemini to
/// estimate nutrition macros, suggest a daily calorie/macro goal, and estimate calories burned for an
/// exercise. Every free-text field is treated strictly as DATA in the model prompt: the model only ever
/// returns JSON we parse and CLAMP, so a hostile string can never inject absurd values or be executed.
/// </summary>

// ===================================================================================
// estimate-macros
// ===================================================================================

/// <summary>
/// Estimate nutrition for a free-text food description. <see cref="Quantity"/> is an optional free-text
/// amount/serving (e.g. "2 eggs", "100 g", "1 cup"); when blank the model assumes a single serving.
/// </summary>
public sealed class EstimateMacrosRequest
{
    public string? Description { get; set; }
    public string? Quantity { get; set; }
}

/// <summary>
/// An AI macro estimate. All numbers are model output CLAMPED to sane ranges (calories 0..5000, macros
/// 0..500 g). When the model is unavailable (quota/parse failure) the endpoint returns 503; this DTO is
/// only emitted on success.
/// </summary>
public sealed class EstimateMacrosResponse
{
    public int Calories { get; set; }
    public double ProteinG { get; set; }
    public double CarbsG { get; set; }
    public double FatG { get; set; }

    /// <summary>Optional short model note (e.g. an assumption it made); null when none.</summary>
    public string? Note { get; set; }
}

// ===================================================================================
// suggest-goal
// ===================================================================================

/// <summary>
/// Request body for goal suggestion. Intentionally EMPTY: the endpoint reads the CALLER's own
/// <c>TrackerProfile</c> server-side (age/height/weight/sex/activity/goal direction) and never trusts
/// client-sent stats.
/// </summary>
public sealed class SuggestGoalRequest
{
}

/// <summary>
/// A suggested daily target. Numbers are model output CLAMPED to sane ranges (calories 0..5000, macros
/// 0..500 g).
/// </summary>
public sealed class SuggestGoalResponse
{
    public int CalorieTarget { get; set; }
    public double ProteinG { get; set; }
    public double CarbsG { get; set; }
    public double FatG { get; set; }

    /// <summary>One short sentence explaining the suggestion; null when the model gave none.</summary>
    public string? Rationale { get; set; }
}

// ===================================================================================
// estimate-exercise
// ===================================================================================

/// <summary>Estimate calories burned for a free-text exercise name over a duration in minutes.</summary>
public sealed class EstimateExerciseRequest
{
    public string? Name { get; set; }
    public int? DurationMin { get; set; }
}

/// <summary>
/// An AI exercise-calorie estimate. <see cref="CaloriesBurned"/> is model output CLAMPED to 0..5000.
/// </summary>
public sealed class EstimateExerciseResponse
{
    public int CaloriesBurned { get; set; }

    /// <summary>Optional short model note (e.g. "assumes a 70 kg adult"); null when none.</summary>
    public string? Note { get; set; }
}

// ===================================================================================
// parse-exercise — natural-language exercise log ("3x10 squats", "jogged 2mi")
// ===================================================================================

/// <summary>Free-text exercise description to parse into a structured, loggable exercise.</summary>
public sealed class ParseExerciseRequest
{
    public string? Text { get; set; }
}

/// <summary>
/// A parsed exercise. Numbers are model output CLAMPED to sane ranges (calories 0..5000, duration
/// 0..1440 min, sets/reps 0..1000). Calories are estimated from the CALLER's own body weight (read
/// server-side), defaulting to a typical adult when no weight is on file.
/// </summary>
public sealed class ParseExerciseResponse
{
    public string Name { get; set; } = "";
    public int Calories { get; set; }
    public int? DurationMin { get; set; }
    public int? Sets { get; set; }
    public int? Reps { get; set; }

    /// <summary>Free-text distance the model extracted (e.g. "2 mi"), or null when none.</summary>
    public string? DistanceText { get; set; }
    public string? Note { get; set; }
}

// ===================================================================================
// suggest-workout
// ===================================================================================

/// <summary>Ask for a workout plan for a focus area over a number of minutes with optional equipment.</summary>
public sealed class SuggestWorkoutRequest
{
    public string? Focus { get; set; }
    public int? Minutes { get; set; }
    public string? Equipment { get; set; }
}

/// <summary>A single suggested exercise in a workout plan.</summary>
public sealed class WorkoutItemDto
{
    public string Name { get; set; } = "";
    public string SetsReps { get; set; } = "";
    public string? Note { get; set; }
}

/// <summary>A suggested workout. <see cref="EstCalories"/> is model output CLAMPED to 0..5000.</summary>
public sealed class SuggestWorkoutResponse
{
    public string Title { get; set; } = "";
    public IReadOnlyList<WorkoutItemDto> Items { get; set; } = Array.Empty<WorkoutItemDto>();
    public int EstCalories { get; set; }
}

// ===================================================================================
// parse-meal / photo-meal — multi-item meal parsing
// ===================================================================================

/// <summary>Free-text meal description to parse into individual items ("Big Mac, fries, Coke").</summary>
public sealed class ParseMealRequest
{
    public string? Text { get; set; }
}

/// <summary>One parsed food item; numbers CLAMPED to sane ranges (calories 0..5000, macros 0..500 g).</summary>
public sealed class MealItemDto
{
    public string Description { get; set; } = "";
    public int Calories { get; set; }
    public double ProteinG { get; set; }
    public double CarbsG { get; set; }
    public double FatG { get; set; }
}

/// <summary>A parsed meal: zero or more items, each with clamped macros.</summary>
public sealed class ParseMealResponse
{
    public IReadOnlyList<MealItemDto> Items { get; set; } = Array.Empty<MealItemDto>();
}

/// <summary>
/// A base64-encoded image plus its mime type, for the multimodal photo features. <see cref="MimeType"/>
/// must be one of image/jpeg, image/png, image/webp; the decoded payload must be under ~5 MB (400 otherwise).
/// The bytes are sent to the model as DATA only; we only ever parse + clamp the JSON it returns.
/// </summary>
public sealed class ImageRequest
{
    public string? ImageBase64 { get; set; }
    public string? MimeType { get; set; }
}

// ===================================================================================
// read-label — multimodal nutrition-label read
// ===================================================================================

/// <summary>A single nutrition-label read; numbers CLAMPED (calories 0..5000, macros 0..500 g).</summary>
public sealed class ReadLabelResponse
{
    public string Description { get; set; } = "";
    public int Calories { get; set; }
    public double ProteinG { get; set; }
    public double CarbsG { get; set; }
    public double FatG { get; set; }

    /// <summary>The serving size the label states (e.g. "1 cup (240 ml)"), or null when not read.</summary>
    public string? ServingSize { get; set; }
}

// ===================================================================================
// suggest-foods — from the caller's remaining calories/macros today (read server-side)
// ===================================================================================

/// <summary>Empty: the endpoint reads the caller's OWN remaining calories + macros for today server-side.</summary>
public sealed class SuggestFoodsRequest
{
}

/// <summary>A suggested food to round out the day; numbers CLAMPED (calories 0..5000, protein 0..500 g).</summary>
public sealed class FoodSuggestionDto
{
    public string Food { get; set; } = "";
    public string? Why { get; set; }
    public int Calories { get; set; }
    public double ProteinG { get; set; }
}

/// <summary>Food suggestions to help the caller hit their remaining targets.</summary>
public sealed class SuggestFoodsResponse
{
    public IReadOnlyList<FoodSuggestionDto> Suggestions { get; set; } = Array.Empty<FoodSuggestionDto>();
}

// ===================================================================================
// meal-feedback
// ===================================================================================

/// <summary>A free-text meal to get a quick verdict + healthier swaps for.</summary>
public sealed class MealFeedbackRequest
{
    public string? Description { get; set; }
}

/// <summary>A short verdict on a meal, whether it fits the caller's goal, and up to a few swap ideas.</summary>
public sealed class MealFeedbackResponse
{
    public string Verdict { get; set; } = "";
    public bool GoodForGoal { get; set; }
    public IReadOnlyList<string> Swaps { get; set; } = Array.Empty<string>();
}

// ===================================================================================
// recipe-macros
// ===================================================================================

/// <summary>A free-text recipe + number of servings to compute the per-serving macros for.</summary>
public sealed class RecipeMacrosRequest
{
    public string? Recipe { get; set; }
    public int? Servings { get; set; }
}

/// <summary>Per-serving macros for a recipe; numbers CLAMPED (calories 0..5000, macros 0..500 g).</summary>
public sealed class MacroSet
{
    public int Calories { get; set; }
    public double ProteinG { get; set; }
    public double CarbsG { get; set; }
    public double FatG { get; set; }
}

/// <summary>The result of a recipe-macro calculation: the per-serving macro breakdown.</summary>
public sealed class RecipeMacrosResponse
{
    public MacroSet PerServing { get; set; } = new();
}

// ===================================================================================
// daily-coach (GET, cached) / weekly-review (GET, cached) / weight-insight (GET, cached)
// ===================================================================================

/// <summary>A short daily-coaching insight + a few actionable tips, from the caller's day so far.</summary>
public sealed class DailyCoachResponse
{
    public string Insight { get; set; } = "";
    public IReadOnlyList<string> Tips { get; set; } = Array.Empty<string>();
}

/// <summary>A short weekly review of the caller's last 7 days + one forward-looking suggestion.</summary>
public sealed class WeeklyReviewResponse
{
    public string Summary { get; set; } = "";
    public string Suggestion { get; set; } = "";
}

/// <summary>A short insight on the caller's weight stats + a one-word/phrase trend label.</summary>
public sealed class WeightInsightResponse
{
    public string Insight { get; set; } = "";
    public string Trend { get; set; } = "";
}

// ===================================================================================
// hydration-suggest (reads profile) / parse-hydration / natural-goal
// ===================================================================================

/// <summary>Empty: the endpoint reads the caller's OWN profile server-side to size a hydration target.</summary>
public sealed class HydrationSuggestRequest
{
}

/// <summary>A suggested daily hydration target in ml (CLAMPED 0..10000) + a one-line rationale.</summary>
public sealed class HydrationSuggestResponse
{
    public int TargetMl { get; set; }
    public string? Rationale { get; set; }
}

/// <summary>Free-text drinks to parse into discrete amounts ("2 coffees and a big water").</summary>
public sealed class ParseHydrationRequest
{
    public string? Text { get; set; }
}

/// <summary>One parsed drink; <see cref="Ml"/> is CLAMPED to 0..5000.</summary>
public sealed class HydrationItemDto
{
    public string Label { get; set; } = "";
    public int Ml { get; set; }
}

/// <summary>Parsed drinks from a free-text hydration description.</summary>
public sealed class ParseHydrationResponse
{
    public IReadOnlyList<HydrationItemDto> Items { get; set; } = Array.Empty<HydrationItemDto>();
}

/// <summary>A free-text goal to turn into a concrete plan ("lose 10 lbs in 3 months").</summary>
public sealed class NaturalGoalRequest
{
    public string? Text { get; set; }
}

/// <summary>
/// A structured goal parsed from free text. Calorie/macro numbers are CLAMPED (calories 0..5000, macros
/// 0..500 g); <see cref="Realistic"/> flags whether the model judged the timeline sensible.
/// </summary>
public sealed class NaturalGoalResponse
{
    public int CalorieTarget { get; set; }
    public double ProteinG { get; set; }
    public double CarbsG { get; set; }
    public double FatG { get; set; }
    public string? Timeline { get; set; }
    public bool Realistic { get; set; }
    public string? Rationale { get; set; }
}
