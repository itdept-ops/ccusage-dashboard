using Ccusage.Api.Dtos;
using Ccusage.Api.Endpoints;
using FluentAssertions;

namespace Ccusage.Api.Tests.Unit;

/// <summary>
/// The food-search fallback ordering (<see cref="TrackerEndpoints.ChooseSearchResult"/>): USDA wins when
/// it is configured and returns hits; when USDA is empty or unconfigured AND FatSecret is configured,
/// FatSecret's results are used; otherwise an empty list. (The both-unconfigured → 503 case is decided
/// before this helper runs and is covered by the integration tests.)
/// </summary>
public class FoodSearchChainTests
{
    private static IReadOnlyList<FoodSearchItemDto> Some(string source) =>
        new[] { new FoodSearchItemDto { Description = "x", Source = source } };

    private static readonly IReadOnlyList<FoodSearchItemDto> Empty = Array.Empty<FoodSearchItemDto>();

    [Fact]
    public void Usda_nonEmpty_wins_even_when_fatsecret_also_configured()
    {
        var result = TrackerEndpoints.ChooseSearchResult(
            usdaConfigured: true, usdaItems: Some("usda"),
            fatsecretConfigured: true, fatsecretItems: Some("fatsecret"));

        result.Should().ContainSingle();
        result[0].Source.Should().Be("usda");
    }

    [Fact]
    public void Usda_empty_falls_back_to_fatsecret()
    {
        var result = TrackerEndpoints.ChooseSearchResult(
            usdaConfigured: true, usdaItems: Empty,
            fatsecretConfigured: true, fatsecretItems: Some("fatsecret"));

        result.Should().ContainSingle();
        result[0].Source.Should().Be("fatsecret");
    }

    [Fact]
    public void Usda_unconfigured_uses_fatsecret()
    {
        var result = TrackerEndpoints.ChooseSearchResult(
            usdaConfigured: false, usdaItems: null,
            fatsecretConfigured: true, fatsecretItems: Some("fatsecret"));

        result.Should().ContainSingle();
        result[0].Source.Should().Be("fatsecret");
    }

    [Fact]
    public void Both_configured_but_empty_returns_empty_list()
    {
        var result = TrackerEndpoints.ChooseSearchResult(
            usdaConfigured: true, usdaItems: Empty,
            fatsecretConfigured: true, fatsecretItems: Empty);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Usda_only_empty_returns_empty_list()
    {
        var result = TrackerEndpoints.ChooseSearchResult(
            usdaConfigured: true, usdaItems: Empty,
            fatsecretConfigured: false, fatsecretItems: null);

        result.Should().BeEmpty();
    }
}
