using System.Net;
using System.Text;
using System.Text.Json;
using Ccusage.Api.Services;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ccusage.Api.Tests.Unit;

/// <summary>
/// The "Recipe breakdown" model-output handling in <see cref="GeminiService"/>, exercised against a stubbed
/// Gemini HTTP response so the SERVER-SIDE parse/drop/clamp logic is covered without a live key:
///
/// <list type="bullet">
///   <item>a breakdown returns the title + servings + ingredient rows ({name, quantity}) + PER-SERVING
///   macros (+ optional steps).</item>
///   <item>per-serving macros are CLAMPED to the per-food ceilings (0..5000 cal / 0..500 g each); servings
///   is clamped to 1..50.</item>
///   <item>duplicate ingredient NAMEs are dropped, and a row with no name is skipped.</item>
///   <item>a nested OR flat macros shape is tolerated; empty input never calls the model.</item>
/// </list>
///
/// (Gating/auth/empty-input/503 are covered at the endpoint level in the integration tests.)
/// </summary>
public class GeminiRecipeBreakdownTests
{
    private sealed class StubHandler(string modelJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var envelope = new
            {
                candidates = new[] { new { content = new { parts = new[] { new { text = modelJson } } } } },
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(envelope), Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://generativelanguage.googleapis.com") };
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("the model must not be called for empty input");
    }

    private static GeminiService ServiceReturning(string modelJson) =>
        new(new StubFactory(new StubHandler(modelJson)),
            Options.Create(new GeminiOptions { ApiKey = "test-key", Model = "gemini-2.5-flash" }),
            new MemoryCache(new MemoryCacheOptions()), NullLogger<GeminiService>.Instance);

    [Fact]
    public async Task Breakdown_returns_title_servings_ingredients_and_per_serving_macros()
    {
        var modelJson = JsonSerializer.Serialize(new
        {
            title = "Chicken Alfredo",
            servings = 4,
            ingredients = new object[]
            {
                new { name = "chicken breast", quantity = "1 lb" },
                new { name = "fettuccine", quantity = "12 oz" },
                new { name = "parmesan", quantity = "1 cup" },
            },
            macros_per_serving = new { calories = 620, protein_g = 38.0, carbs_g = 55.0, fat_g = 27.0 },
            steps = new[] { "Cook the pasta.", "Sear the chicken.", "Toss with sauce." },
        });

        var result = await ServiceReturning(modelJson).RecipeBreakdownAsync("chicken alfredo");

        result.Should().NotBeNull();
        result!.Title.Should().Be("Chicken Alfredo");
        result.Servings.Should().Be(4);
        result.Ingredients.Select(i => i.Name).Should().Equal("chicken breast", "fettuccine", "parmesan");
        result.Ingredients[0].Quantity.Should().Be("1 lb");
        result.MacrosPerServing.Calories.Should().Be(620);
        result.MacrosPerServing.ProteinG.Should().Be(38.0);
        result.MacrosPerServing.CarbsG.Should().Be(55.0);
        result.MacrosPerServing.FatG.Should().Be(27.0);
        result.Steps.Should().NotBeNull();
        result.Steps!.Should().HaveCount(3);
    }

    [Fact]
    public async Task Breakdown_clamps_macros_and_servings_and_drops_dup_and_nameless_ingredients()
    {
        var modelJson = JsonSerializer.Serialize(new
        {
            title = "Wild Feast",
            servings = 999, // → clamped to 50
            ingredients = new object[]
            {
                new { name = "rice", quantity = "2 cups" },
                new { name = "rice", quantity = "again" }, // duplicate name → dropped
                new { name = "", quantity = "1" },          // no name → skipped
            },
            macros_per_serving = new { calories = 999999, protein_g = 9999.0, carbs_g = -5.0, fat_g = 9999.0 },
        });

        var result = await ServiceReturning(modelJson).RecipeBreakdownAsync("a huge recipe");

        result.Should().NotBeNull();
        result!.Servings.Should().Be(50);                       // clamped to MaxMealServings
        result.Ingredients.Select(i => i.Name).Should().Equal("rice"); // dup + nameless dropped
        result.MacrosPerServing.Calories.Should().Be(5000);     // per-food ClampCalories
        result.MacrosPerServing.ProteinG.Should().Be(500);      // per-food ClampMacro
        result.MacrosPerServing.CarbsG.Should().Be(0);          // negative → 0
        result.MacrosPerServing.FatG.Should().Be(500);
        result.Steps.Should().BeNull();                         // no steps → null
    }

    [Fact]
    public async Task Breakdown_tolerates_flat_macros_shape()
    {
        // No nested "macros_per_serving" — macros at the top level instead.
        var modelJson = JsonSerializer.Serialize(new
        {
            title = "Oatmeal",
            servings = 1,
            ingredients = new object[] { new { name = "oats", quantity = "1/2 cup" } },
            calories = 300,
            protein_g = 10.0,
            carbs_g = 50.0,
            fat_g = 5.0,
        });

        var result = await ServiceReturning(modelJson).RecipeBreakdownAsync("oatmeal");

        result.Should().NotBeNull();
        result!.MacrosPerServing.Calories.Should().Be(300);
        result.MacrosPerServing.ProteinG.Should().Be(10.0);
    }

    [Fact]
    public async Task Breakdown_returns_null_for_empty_text_without_calling_the_model()
    {
        var svc = new GeminiService(
            new StubFactory(new ThrowingHandler()),
            Options.Create(new GeminiOptions { ApiKey = "test-key" }),
            new MemoryCache(new MemoryCacheOptions()), NullLogger<GeminiService>.Instance);

        (await svc.RecipeBreakdownAsync("   ")).Should().BeNull();
    }
}
