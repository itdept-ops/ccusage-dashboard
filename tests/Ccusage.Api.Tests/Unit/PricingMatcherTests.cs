using Ccusage.Api.Data.Entities;
using Ccusage.Api.Ingestion;
using FluentAssertions;

namespace Ccusage.Api.Tests.Unit;

public class PricingMatcherTests
{
    private static PricingMatcher Build() => new(new[]
    {
        new ModelPricing { ModelPattern = "claude-opus-4-8", InputPerMTok = 15m, OutputPerMTok = 75m, CacheReadPerMTok = 1.5m, CacheWrite5mPerMTok = 18.75m, CacheWrite1hPerMTok = 30m },
        new ModelPricing { ModelPattern = "claude-haiku-4-5", InputPerMTok = 1m, OutputPerMTok = 5m, CacheReadPerMTok = 0.1m },
        new ModelPricing { ModelPattern = "*", IsPlaceholder = true },
    });

    [Fact]
    public void Resolves_exact_match()
    {
        Build().Resolve("claude-opus-4-8").ModelPattern.Should().Be("claude-opus-4-8");
    }

    [Fact]
    public void Resolves_longest_prefix_for_date_suffixed_model()
    {
        Build().Resolve("claude-haiku-4-5-20251001").ModelPattern.Should().Be("claude-haiku-4-5");
    }

    [Fact]
    public void Falls_back_to_star_and_records_unpriced_model()
    {
        var m = Build();
        m.Resolve("gpt-9000").ModelPattern.Should().Be("*");
        m.UnpricedModels.Should().Contain("gpt-9000");
    }

    [Fact]
    public void Cost_sums_each_token_tier_at_its_rate()
    {
        // 1M input @15 + 1M output @75 + 1M read @1.5 = 91.5
        var cost = Build().Cost("claude-opus-4-8", input: 1_000_000, output: 1_000_000, read: 1_000_000, write5m: 0, write1h: 0);
        cost.Should().Be(91.5m);
    }

    [Fact]
    public void Cost_is_zero_for_unpriced_fallback()
    {
        Build().Cost("totally-unknown", 5_000_000, 5_000_000, 5_000_000, 5_000_000, 5_000_000).Should().Be(0m);
    }
}
