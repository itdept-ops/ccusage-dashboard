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
