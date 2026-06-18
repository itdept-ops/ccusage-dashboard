using System.Text.Json;
using Ccusage.Api.Services;
using FluentAssertions;

namespace Ccusage.Api.Tests.Unit;

/// <summary>
/// The pure WorkoutX helpers: normalizing a <c>{ total, count, data: [...] }</c> exercises payload into
/// <see cref="Ccusage.Api.Dtos.WorkoutXExerciseDto"/>s (the gifUrl is dropped — the client uses the
/// key-authorized proxy), the calorie estimate (round(caloriesPerMinute * minutes)), and the gif-id
/// digit validation that keeps the proxied id from traversing the upstream path. Malformed input never
/// throws and yields an empty page.
/// </summary>
public class WorkoutXServiceTests
{
    // A trimmed sample mirroring the live /v1/exercises shape (probed against the real API).
    private const string SamplePayload = """
    {
      "total": 1327,
      "count": 2,
      "data": [
        {
          "id": "0001",
          "name": "3/4 Sit-up",
          "bodyPart": "Waist",
          "equipment": "Body Weight",
          "target": "Abs",
          "secondaryMuscles": ["Hip Flexors", "Lower Back"],
          "instructions": ["Lie flat on your back.", "Lift your upper body.", "Lower back down."],
          "gifUrl": "https://api.workoutxapp.com/v1/gifs/0001.gif",
          "category": "strength",
          "difficulty": "beginner",
          "met": 3.5,
          "caloriesPerMinute": 4.3,
          "description": "A beginner core exercise.",
          "recommendedSets": "3",
          "recommendedReps": "10-15"
        },
        {
          "id": "0314",
          "name": "Barbell Bench Press",
          "bodyPart": "Chest",
          "equipment": "Barbell",
          "target": "Pectorals",
          "secondaryMuscles": ["Triceps", "Shoulders"],
          "instructions": ["Lie on the bench.", "Press the bar up."],
          "gifUrl": "https://api.workoutxapp.com/v1/gifs/0314.gif",
          "category": "strength",
          "difficulty": "intermediate",
          "met": 6.0,
          "caloriesPerMinute": 7.1,
          "description": "A compound chest press.",
          "recommendedSets": "4",
          "recommendedReps": "8-12"
        }
      ]
    }
    """;

    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Normalizes_a_sample_payload_into_dtos_with_the_catalog_total()
    {
        var result = WorkoutXService.ParseSearch(Root(SamplePayload));

        result.Total.Should().Be(1327); // catalog-wide total drives pagination, not the page count
        result.Data.Should().HaveCount(2);

        var first = result.Data[0];
        first.Id.Should().Be("0001");
        first.Name.Should().Be("3/4 Sit-up");
        first.BodyPart.Should().Be("Waist");
        first.Equipment.Should().Be("Body Weight");
        first.Target.Should().Be("Abs");
        first.SecondaryMuscles.Should().Equal("Hip Flexors", "Lower Back");
        first.Instructions.Should().HaveCount(3);
        first.Category.Should().Be("strength");
        first.Difficulty.Should().Be("beginner");
        first.Met.Should().Be(3.5);
        first.CaloriesPerMinute.Should().Be(4.3);
        first.Description.Should().Be("A beginner core exercise.");
        first.RecommendedSets.Should().Be("3");
        first.RecommendedReps.Should().Be("10-15");

        result.Data[1].Id.Should().Be("0314");
        result.Data[1].Target.Should().Be("Pectorals");
    }

    [Fact]
    public void Falls_back_to_data_length_when_total_is_missing()
    {
        var result = WorkoutXService.ParseSearch(Root(
            """{ "data": [ { "id": "0001", "name": "Sit-up" } ] }"""));
        result.Total.Should().Be(1);
        result.Data.Should().ContainSingle();
    }

    [Fact]
    public void Skips_rows_missing_an_id_or_name_and_defaults_optional_fields()
    {
        var result = WorkoutXService.ParseSearch(Root("""
        {
          "total": 3,
          "data": [
            { "id": "0001", "name": "Valid" },
            { "id": "0002" },
            { "name": "No id" }
          ]
        }
        """));

        result.Data.Should().ContainSingle();
        var dto = result.Data[0];
        dto.Name.Should().Be("Valid");
        dto.SecondaryMuscles.Should().BeEmpty();
        dto.Instructions.Should().BeEmpty();
        dto.CaloriesPerMinute.Should().Be(0);
        dto.Description.Should().BeNull();
        dto.RecommendedSets.Should().BeNull();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "data": null }""")]
    [InlineData("""{ "data": "oops" }""")]
    [InlineData("[]")]
    public void Malformed_or_empty_payloads_yield_an_empty_page_and_never_throw(string json)
    {
        var act = () => WorkoutXService.ParseSearch(Root(json));
        var result = act.Should().NotThrow().Subject;
        result.Data.Should().BeEmpty();
        result.Total.Should().Be(0);
    }

    [Theory]
    [InlineData(4.3, 30, 129)]   // 4.3 * 30 = 129
    [InlineData(7.1, 45, 320)]   // 7.1 * 45 = 319.5 -> 320 (away from zero)
    [InlineData(4.3, 0, 0)]      // no duration -> 0
    [InlineData(0, 30, 0)]       // no per-minute rate -> 0
    [InlineData(-5, 30, 0)]      // negative rate -> 0
    [InlineData(4.3, -10, 0)]    // negative duration -> 0
    public void EstimateCalories_is_round_of_perMinute_times_minutes(double perMin, int minutes, int expected)
    {
        WorkoutXService.EstimateCalories(perMin, minutes).Should().Be(expected);
    }

    [Theory]
    [InlineData("0001", true)]
    [InlineData("1", true)]
    [InlineData("99999999", true)]   // 8 digits, the cap
    [InlineData("999999999", false)] // 9 digits, over the cap
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("00a1", false)]
    [InlineData("../etc", false)]
    [InlineData("0001.gif", false)]
    [InlineData("-1", false)]
    public void IsValidGifId_accepts_only_short_digit_strings(string? id, bool expected)
    {
        WorkoutXService.IsValidGifId(id).Should().Be(expected);
    }
}
