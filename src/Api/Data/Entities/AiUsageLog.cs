namespace Ccusage.Api.Data.Entities;

/// <summary>
/// One AI (Gemini) call captured at the GeminiService chokepoint. Records WHO called WHICH feature, the
/// model, how it went, and the token counts — but NEVER any prompt or response CONTENT. The table is
/// pruned to a recent window by <see cref="Ccusage.Api.Infrastructure.AiUsageLogWriter"/>, mirroring the
/// RequestLog queue+writer pattern.
/// </summary>
public class AiUsageLog
{
    public long Id { get; set; }
    public DateTime WhenUtc { get; set; }

    /// <summary>The authenticated caller's email (lower-cased), or null for a background tick (no HttpContext).</summary>
    public string? UserEmail { get; set; }

    /// <summary>The GeminiService "kind" / feature label, e.g. "schedule", "build-day", "money-coach".</summary>
    public string Feature { get; set; } = "";

    /// <summary>The configured Gemini model id used for the call.</summary>
    public string Model { get; set; } = "";

    /// <summary>One of: "ok" | "unavailable" | "rate-limited" | "parse-failed" | "error".</summary>
    public string Outcome { get; set; } = "";

    /// <summary>The upstream HTTP status, or null when no response was received (network error/timeout).</summary>
    public int? HttpStatus { get; set; }

    public int DurationMs { get; set; }

    /// <summary>Gemini usageMetadata.promptTokenCount, when the call succeeded and reported it.</summary>
    public int? PromptTokens { get; set; }

    /// <summary>Gemini usageMetadata.candidatesTokenCount, when the call succeeded and reported it.</summary>
    public int? OutputTokens { get; set; }

    /// <summary>Gemini usageMetadata.totalTokenCount, when the call succeeded and reported it.</summary>
    public int? TotalTokens { get; set; }

    /// <summary>A short status/reason hint (NEVER the response body), e.g. "HTTP 503" or "timeout".</summary>
    public string? ErrorHint { get; set; }
}
