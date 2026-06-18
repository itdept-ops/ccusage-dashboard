using Ccusage.Api.Services;
using FluentAssertions;

namespace Ccusage.Api.Tests.Unit;

/// <summary>
/// The pure FatSecret <c>food_description</c> parser (<see cref="FatSecretFoodService.ParseDescription"/>):
/// it pulls kcal + fat/carb/protein grams out of strings like
/// "Per 100g - Calories: 89kcal | Fat: 0.33g | Carbs: 22.84g | Protein: 1.09g" and derives the basis
/// (per100g when the "Per X" phrase mentions 100g/100ml, else perServing). Malformed input never throws.
/// </summary>
public class FatSecretParserTests
{
    [Fact]
    public void Parses_a_per_100g_description()
    {
        var n = FatSecretFoodService.ParseDescription(
            "Per 100g - Calories: 89kcal | Fat: 0.33g | Carbs: 22.84g | Protein: 1.09g");

        n.Calories.Should().Be(89);
        n.FatG.Should().Be(0.3);    // rounded to 1dp
        n.CarbG.Should().Be(22.8);
        n.ProteinG.Should().Be(1.1);
        n.Basis.Should().Be("per100g");
        n.ServingUnit.Should().Be("100g");
    }

    [Fact]
    public void Parses_a_per_serving_description_as_perServing()
    {
        var n = FatSecretFoodService.ParseDescription(
            "Per 1 cup (240 g) - Calories: 150kcal | Fat: 8.00g | Carbs: 12.00g | Protein: 8.00g");

        n.Calories.Should().Be(150);
        n.FatG.Should().Be(8.0);
        n.CarbG.Should().Be(12.0);
        n.ProteinG.Should().Be(8.0);
        n.Basis.Should().Be("perServing");
        n.ServingUnit.Should().Be("1 cup (240 g)");
    }

    [Fact]
    public void Treats_100ml_as_per100g_basis()
    {
        var n = FatSecretFoodService.ParseDescription(
            "Per 100ml - Calories: 42kcal | Fat: 1.00g | Carbs: 5.00g | Protein: 3.40g");
        n.Basis.Should().Be("per100g");
        n.Calories.Should().Be(42);
    }

    [Fact]
    public void Rounds_calories_to_a_whole_number()
    {
        var n = FatSecretFoodService.ParseDescription(
            "Per 1 serving - Calories: 88.6kcal | Fat: 0.00g | Carbs: 0.00g | Protein: 0.00g");
        n.Calories.Should().Be(89);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("totally unparseable text")]
    public void Malformed_or_empty_input_yields_zeros_and_never_throws(string? input)
    {
        var n = FatSecretFoodService.ParseDescription(input);
        n.Calories.Should().Be(0);
        n.FatG.Should().Be(0);
        n.CarbG.Should().Be(0);
        n.ProteinG.Should().Be(0);
        // No "Per X -" prefix → defaults to perServing.
        n.Basis.Should().Be("perServing");
    }
}
