using Ccusage.Api.Auth;
using Ccusage.Api.Data;
using Ccusage.Api.Data.Entities;
using Ccusage.Api.Dtos;
using Ccusage.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Ccusage.Api.Endpoints;

/// <summary>
/// AI-assist endpoints (<c>/api/ai</c>) backed by Google Gemini: estimate food macros, suggest a daily
/// calorie/macro goal, and estimate calories burned for an exercise. Identity comes from the JWT
/// (<c>.RequireAuthorization()</c>); capability from <see cref="Permissions.TrackerSelf"/> (DB-checked).
/// Every call is rate-limited (the "ai" policy) because AI calls cost tokens.
///
/// CONTRACT/SECURITY:
/// <list type="bullet">
///   <item>When Gemini is unconfigured (blank <c>Gemini:ApiKey</c>), every endpoint returns 503 so the
///   frontend can show "AI estimate unavailable, enter manually". The same 503 is returned on a
///   quota/parse failure (the service returns null), so the frontend has ONE consistent degraded path.</item>
///   <item>All user input is free text treated strictly as DATA in the model prompt (see
///   <see cref="GeminiService"/>); we only ever parse + clamp the model's JSON.</item>
///   <item><c>suggest-goal</c> reads the CALLER's own <c>TrackerProfile</c> server-side and never trusts
///   client-sent stats.</item>
/// </list>
/// </summary>
public static class AiEndpoints
{
    public const string RateLimitPolicy = "ai";

    public static void MapAiEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/ai")
            .RequireAuthorization()
            .RequirePermission(Permissions.TrackerSelf)
            .RequireRateLimiting(RateLimitPolicy);

        // ---- Estimate macros for a free-text food description ----
        g.MapPost("/estimate-macros", async (
            EstimateMacrosRequest body, GeminiService gemini, CancellationToken ct) =>
        {
            if (!gemini.IsConfigured) return Unconfigured();
            var result = await gemini.EstimateMacrosAsync(body?.Description, body?.Quantity, ct);
            return result is null ? Unavailable() : Results.Ok(result);
        });

        // ---- Suggest a daily goal from the caller's OWN profile (read server-side) ----
        g.MapPost("/suggest-goal", async (
            SuggestGoalRequest _, CurrentUserAccessor me, GeminiService gemini, UsageDbContext db,
            CancellationToken ct) =>
        {
            if (!gemini.IsConfigured) return Unconfigured();

            var caller = (await me.GetUserAsync(ct))!; // tracker.self filter guarantees non-null
            // Read the caller's own profile; if they have none yet, suggest from an empty/default profile
            // rather than trusting any client-sent stats.
            var profile = await db.TrackerProfiles.AsNoTracking()
                .FirstOrDefaultAsync(p => p.UserEmail == caller.Email, ct)
                ?? new TrackerProfile { UserEmail = caller.Email };

            var result = await gemini.SuggestGoalAsync(profile, ct);
            return result is null ? Unavailable() : Results.Ok(result);
        });

        // ---- Estimate calories burned for a free-text exercise ----
        g.MapPost("/estimate-exercise", async (
            EstimateExerciseRequest body, GeminiService gemini, CancellationToken ct) =>
        {
            if (!gemini.IsConfigured) return Unconfigured();
            var result = await gemini.EstimateExerciseCaloriesAsync(body?.Name, body?.DurationMin ?? 0, ct);
            return result is null ? Unavailable() : Results.Ok(result);
        });
    }

    /// <summary>503 when no API key is configured (the test host + an un-keyed deploy hit this).</summary>
    private static IResult Unconfigured() => Results.Problem(
        title: "AI assistance is not configured.",
        detail: "AI assistance is not configured.",
        statusCode: StatusCodes.Status503ServiceUnavailable);

    /// <summary>503 when the model is configured but the call failed (quota/parse) — same degraded path.</summary>
    private static IResult Unavailable() => Results.Problem(
        title: "AI estimate unavailable, enter manually.",
        detail: "AI estimate unavailable, enter manually.",
        statusCode: StatusCodes.Status503ServiceUnavailable);
}
