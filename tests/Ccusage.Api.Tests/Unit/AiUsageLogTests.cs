using System.Linq;
using Ccusage.Api.Data.Entities;
using Ccusage.Api.Services;
using FluentAssertions;

namespace Ccusage.Api.Tests.Unit;

/// <summary>
/// The AI-usage log entity contract + the GeminiService outcome classification. These guard the two
/// privacy/correctness invariants of the feature: (1) the row NEVER carries prompt or response content,
/// and (2) the upstream HTTP status maps to the right Outcome label.
/// </summary>
public class AiUsageLogTests
{
    [Theory]
    [InlineData(503, "unavailable")]
    [InlineData(429, "rate-limited")]
    [InlineData(500, "error")]
    [InlineData(502, "error")]
    [InlineData(400, "error")]
    [InlineData(403, "error")]
    public void ClassifyOutcome_maps_non_2xx_status_to_the_right_label(int status, string expected)
    {
        GeminiService.ClassifyOutcome(status).Should().Be(expected);
    }

    [Fact]
    public void AiUsageLog_has_no_prompt_or_response_content_column()
    {
        // The entity must never gain a field that could hold prompt/response TEXT. Assert the property set
        // is exactly the metadata columns — nothing named like prompt/response/content/body/text/message.
        var props = typeof(AiUsageLog).GetProperties().Select(p => p.Name).ToHashSet();

        props.Should().BeEquivalentTo(new[]
        {
            "Id", "WhenUtc", "UserEmail", "Feature", "Model", "Outcome", "HttpStatus",
            "DurationMs", "PromptTokens", "OutputTokens", "TotalTokens", "ErrorHint",
        });

        string[] forbidden = { "prompt", "response", "content", "body", "text", "message" };
        foreach (var name in props)
        {
            var lower = name.ToLowerInvariant();
            // PromptTokens/OutputTokens are token COUNTS, not content — allow the "*Tokens" metadata fields.
            if (lower.EndsWith("tokens")) continue;
            forbidden.Should().NotContain(f => lower.Contains(f),
                "an AI-usage row must never store prompt/response content (offending property: {0})", name);
        }
    }
}
