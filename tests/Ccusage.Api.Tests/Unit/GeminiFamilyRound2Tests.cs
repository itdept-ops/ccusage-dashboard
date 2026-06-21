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
/// The round-2 family-AI model-output handling in <see cref="GeminiService"/>, exercised against a stubbed
/// Gemini HTTP response so the SERVER-SIDE clamp/dedupe/intersect logic is covered without a live key:
///
/// <list type="bullet">
///   <item>ParseTimer CLAMPS durationSeconds to 5..86400 and defaults a blank label to "Timer".</item>
///   <item>AskNotes intersects usedNoteIds with the supplied notes (drops a hallucinated/foreign id) and
///   returns an empty list with no notes.</item>
///   <item>SuggestListAdditions drops items already on the list (case-insensitive) + intra-batch dupes + caps.</item>
///   <item>WhatCanIMake clamps ideas (&lt;=5) + missing items and clamps each idea's ingredient blob.</item>
///   <item>TransformNote rejects an unknown action without calling the model.</item>
/// </list>
///
/// (Gating/auth/empty-input/503/view-access are covered at the endpoint level in the integration tests; an
/// unconfigured key short-circuits to null there, so the clamp/dedup logic itself is unit-tested here.)
/// </summary>
public class GeminiFamilyRound2Tests
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
            throw new InvalidOperationException("the model must not be called");
    }

    private static GeminiService ServiceReturning(string modelJson) => new(
        new StubFactory(new StubHandler(modelJson)),
        Options.Create(new GeminiOptions { ApiKey = "test-key", Model = "gemini-2.5-flash" }),
        new MemoryCache(new MemoryCacheOptions()),
        NullLogger<GeminiService>.Instance);

    private static GeminiService ServiceThatMustNotCall() => new(
        new StubFactory(new ThrowingHandler()),
        Options.Create(new GeminiOptions { ApiKey = "test-key" }),
        new MemoryCache(new MemoryCacheOptions()),
        NullLogger<GeminiService>.Instance);

    // =====================================================================================
    // TIMER — duration clamped to 5..86400, blank label -> "Timer"
    // =====================================================================================

    [Theory]
    [InlineData(1200, 1200)]   // 20 min, in range
    [InlineData(1, 5)]         // below floor -> 5
    [InlineData(0, 5)]         // zero -> 5
    [InlineData(-50, 5)]       // negative -> 5
    [InlineData(999999, 86400)] // above 24h cap -> 86400
    public async Task ParseTimer_clamps_duration_seconds_to_5_to_86400(int modelSeconds, int expected)
    {
        var modelJson = JsonSerializer.Serialize(new { label = "Pasta", duration_seconds = modelSeconds });
        var result = await ServiceReturning(modelJson).ParseTimerAsync("a pasta timer");

        result.Should().NotBeNull();
        result!.DurationSeconds.Should().Be(expected);
        result.Label.Should().Be("Pasta");
    }

    [Fact]
    public async Task ParseTimer_defaults_a_blank_label_to_Timer()
    {
        var modelJson = JsonSerializer.Serialize(new { label = "", duration_seconds = 300 });
        var result = await ServiceReturning(modelJson).ParseTimerAsync("five minutes");

        result.Should().NotBeNull();
        result!.Label.Should().Be("Timer");
        result.DurationSeconds.Should().Be(300);
    }

    [Fact]
    public async Task ParseTimer_returns_null_for_empty_text_without_calling_the_model()
    {
        (await ServiceThatMustNotCall().ParseTimerAsync("   ")).Should().BeNull();
    }

    // =====================================================================================
    // ASK YOUR NOTES — intersect usedNoteIds, "couldn't find" floor, no notes
    // =====================================================================================

    [Fact]
    public async Task AskNotes_intersects_used_ids_with_the_supplied_notes_and_drops_foreign_ids()
    {
        // The model cites id 1 (supplied), id 999 (NOT supplied — must be dropped), and id 1 again (dupe).
        var modelJson = JsonSerializer.Serialize(new
        {
            answer = "The wifi password is hunter2.",
            used_note_ids = new[] { 1, 999, 1 },
        });

        var notes = new List<(long, string, string)>
        {
            (1, "Wifi", "Password is hunter2"),
            (2, "Other", "unrelated"),
        };
        var result = await ServiceReturning(modelJson).AskNotesAsync("what's the wifi password?", notes);

        result.Should().NotBeNull();
        result!.Answer.Should().Contain("hunter2");
        result.UsedNoteIds.Should().Equal(1L); // 999 dropped, dupe collapsed
    }

    [Fact]
    public async Task AskNotes_with_no_notes_returns_the_not_found_floor_without_calling_the_model()
    {
        var result = await ServiceThatMustNotCall()
            .AskNotesAsync("anything?", new List<(long, string, string)>());

        result.Should().NotBeNull();
        result!.Answer.Should().Be("I couldn't find that in your notes.");
        result.UsedNoteIds.Should().BeEmpty();
    }

    [Fact]
    public async Task AskNotes_returns_null_for_an_empty_question_without_calling_the_model()
    {
        var notes = new List<(long, string, string)> { (1, "T", "B") };
        (await ServiceThatMustNotCall().AskNotesAsync("   ", notes)).Should().BeNull();
    }

    // =====================================================================================
    // WHAT AM I MISSING — drop already-present (case-insensitive) + intra-batch dupes + cap
    // =====================================================================================

    [Fact]
    public async Task SuggestListAdditions_drops_existing_items_and_dupes_and_caps()
    {
        // "Balloons" is already on the list (case-insensitive "balloons"); "Cake" appears twice in the batch.
        var items = new[] { "balloons", "Cake", "Cake", "Candles", "Party hats" };
        var modelJson = JsonSerializer.Serialize(new { items });

        var current = new[] { "Balloons", "Plates" };
        var result = await ServiceReturning(modelJson)
            .SuggestListAdditionsAsync("a kids birthday party", "shopping", current);

        result.Should().NotBeNull();
        result!.Items.Should().Equal("Cake", "Candles", "Party hats"); // "balloons" dropped, "Cake" de-duped
    }

    [Fact]
    public async Task SuggestListAdditions_returns_null_for_an_empty_goal_without_calling_the_model()
    {
        (await ServiceThatMustNotCall().SuggestListAdditionsAsync("  ", "todo", Array.Empty<string>()))
            .Should().BeNull();
    }

    // =====================================================================================
    // WHAT CAN I MAKE — cap ideas to 5, clamp missing + ingredients
    // =====================================================================================

    [Fact]
    public async Task WhatCanIMake_caps_ideas_to_five_and_clamps_ingredient_lines()
    {
        var ideas = Enumerable.Range(0, 8).Select(i => (object)new
        {
            title = $"Idea {i}",
            ingredients = "- chicken\n1. rice\n* chicken", // bullets stripped, dup dropped
            missing = new[] { "soy sauce", "soy sauce", "ginger" }, // dup dropped
        }).ToArray();
        var modelJson = JsonSerializer.Serialize(new { ideas });

        var result = await ServiceReturning(modelJson).WhatCanIMakeAsync("chicken, rice", "quick");

        result.Should().NotBeNull();
        result!.Ideas.Should().HaveCountLessThanOrEqualTo(5);
        var first = result.Ideas.First();
        first.Ingredients.Split('\n').Should().Equal("chicken", "rice");
        first.Missing.Should().Equal("soy sauce", "ginger");
    }

    [Fact]
    public async Task WhatCanIMake_returns_null_for_empty_ingredients_without_calling_the_model()
    {
        (await ServiceThatMustNotCall().WhatCanIMakeAsync("   ", null)).Should().BeNull();
    }

    // =====================================================================================
    // TRANSFORM — unknown action is rejected without calling the model
    // =====================================================================================

    [Fact]
    public async Task TransformNote_rejects_an_unknown_action_without_calling_the_model()
    {
        (await ServiceThatMustNotCall().TransformNoteAsync("some real body", "explode", null))
            .Should().BeNull();
    }

    [Fact]
    public async Task TransformNote_returns_the_transformed_body_for_a_known_action()
    {
        var modelJson = JsonSerializer.Serialize(new { body = "- [ ] milk\n- [ ] eggs" });
        var result = await ServiceReturning(modelJson).TransformNoteAsync("milk, eggs", "checklist", null);

        result.Should().NotBeNull();
        result!.Body.Should().Contain("- [ ]");
    }

    [Fact]
    public async Task TransformNote_returns_null_for_an_empty_body_without_calling_the_model()
    {
        (await ServiceThatMustNotCall().TransformNoteAsync("   ", "shorten", null)).Should().BeNull();
    }
}
