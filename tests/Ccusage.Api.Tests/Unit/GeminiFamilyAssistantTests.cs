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
/// The Family Assistant model-output handling in <see cref="GeminiService.FamilyAssistantAsync"/>, exercised
/// against a stubbed Gemini HTTP response so the SERVER-SIDE drop/clamp logic is covered without a live key:
///
/// <list type="bullet">
///   <item>actions whose <c>type</c> is OUTSIDE the closed enum (list_add/reminder/timer/calendar_event/
///   chore/meal) are DROPPED;</item>
///   <item>actions whose REQUIRED params are missing/empty are DROPPED (e.g. list_add with no items, reminder
///   with no text, calendar_event with no title/start, chore/meal with no title);</item>
///   <item>numeric clamps hold (timer durationSeconds 5..86400, chore points 0..1000) and counts are capped
///   (&lt;=6 actions, list items de-duped/capped);</item>
///   <item>recurrence is normalised to the chore vocabulary; a blank timer label defaults to "Timer";</item>
///   <item>the answer is carried through, and an empty message returns null without calling the model.</item>
/// </list>
/// </summary>
public class GeminiFamilyAssistantTests
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

    private static readonly TimeZoneInfo Tz = TimeZoneInfo.Utc;
    private static readonly DateTime Ref = new(2026, 6, 21, 9, 0, 0, DateTimeKind.Unspecified);

    private static T? Get<T>(IReadOnlyDictionary<string, object?> p, string key) =>
        p.TryGetValue(key, out var v) && v is T t ? t : default;

    // =====================================================================================
    // EMPTY MESSAGE → null without calling the model
    // =====================================================================================

    [Fact]
    public async Task FamilyAssistant_returns_null_for_an_empty_message_without_calling_the_model()
    {
        (await ServiceThatMustNotCall().FamilyAssistantAsync("   ", "(snapshot)", Ref, Tz))
            .Should().BeNull();
    }

    // =====================================================================================
    // OUT-OF-ENUM action types are DROPPED
    // =====================================================================================

    [Fact]
    public async Task Out_of_enum_action_types_are_dropped()
    {
        var modelJson = JsonSerializer.Serialize(new
        {
            answer = "Sure.",
            actions = new object[]
            {
                new { type = "finance_write", title = "Pay a bill", @params = new { amount = 50 } }, // not in the set
                new { type = "delete_everything", title = "boom", @params = new { } },               // not in the set
                new { type = "TIMER", title = "Oven", @params = new { label = "Oven", durationSeconds = 600 } }, // valid (case-insensitive)
            },
        });

        var result = await ServiceReturning(modelJson).FamilyAssistantAsync("x", "(s)", Ref, Tz);

        result.Should().NotBeNull();
        result!.Answer.Should().Be("Sure.");
        result.Actions.Should().HaveCount(1);
        result.Actions[0].Type.Should().Be("timer");
    }

    // =====================================================================================
    // MISSING required params → the action is DROPPED
    // =====================================================================================

    [Fact]
    public async Task Actions_missing_required_params_are_dropped()
    {
        var modelJson = JsonSerializer.Serialize(new
        {
            answer = "",
            actions = new object[]
            {
                new { type = "list_add", title = "no items", @params = new { listName = "Groceries", items = Array.Empty<string>() } }, // empty items -> drop
                new { type = "list_add", title = "no list", @params = new { listName = "", items = new[] { "Milk" } } },               // blank listName -> drop
                new { type = "reminder", title = "no text", @params = new { text = "  ", whenLocal = "" } },                            // blank text -> drop
                new { type = "calendar_event", title = "no start", @params = new { title = "Dentist", startLocal = "" } },             // no start -> drop
                new { type = "chore", title = "no title", @params = new { title = "", points = 3 } },                                   // blank title -> drop
                new { type = "meal", title = "no title", @params = new { title = "", ingredients = "x" } },                             // blank title -> drop
            },
        });

        var result = await ServiceReturning(modelJson).FamilyAssistantAsync("x", "(s)", Ref, Tz);

        result.Should().NotBeNull();
        result!.Actions.Should().BeEmpty();
    }

    // =====================================================================================
    // VALID actions of every type map + clamp correctly
    // =====================================================================================

    [Fact]
    public async Task Each_valid_action_type_maps_and_clamps()
    {
        var modelJson = JsonSerializer.Serialize(new
        {
            answer = "On it.",
            actions = new object[]
            {
                new { type = "list_add", title = "Add to Groceries", @params = new { listName = "Groceries", items = new[] { "Milk", "milk", "Eggs" } } }, // dupe "milk" dropped
                new { type = "reminder", title = "Remind", @params = new { text = "Call mom", whenLocal = "2026-06-22T09:00:00" } },
                new { type = "timer", title = "Pasta", @params = new { label = "", durationSeconds = 1 } },           // below floor -> 5; blank label -> "Timer"
                new { type = "calendar_event", title = "Dentist", @params = new { title = "Dentist", startLocal = "2026-06-23T16:00:00", endLocal = "2026-06-23T17:00:00", allDay = false } },
                new { type = "chore", title = "Trash", @params = new { title = "Trash", points = 99999, recurrence = "WEEKLY", assigneeName = "Leo" } }, // points clamp; recurrence normalise
                new { type = "meal", title = "Tacos", @params = new { title = "Tacos", ingredients = "tortillas\nbeef", mealDateLocal = "2026-06-26" } },
            },
        });

        var result = await ServiceReturning(modelJson).FamilyAssistantAsync("x", "(s)", Ref, Tz);

        result.Should().NotBeNull();
        result!.Answer.Should().Be("On it.");
        result.Actions.Should().HaveCount(6);

        var listAdd = result.Actions.Single(a => a.Type == "list_add");
        Get<string>(listAdd.Params, "listName").Should().Be("Groceries");
        ((System.Collections.IEnumerable)listAdd.Params["items"]!).Cast<string>()
            .Should().Equal("Milk", "Eggs"); // case-insensitive dedupe

        var timer = result.Actions.Single(a => a.Type == "timer");
        Convert.ToInt32(timer.Params["durationSeconds"]).Should().Be(5);     // clamped up to the 5s floor
        Get<string>(timer.Params, "label").Should().Be("Timer");             // blank -> default

        var chore = result.Actions.Single(a => a.Type == "chore");
        Convert.ToInt32(chore.Params["points"]).Should().Be(1000);           // clamped to the 1000 ceiling
        Get<string>(chore.Params, "recurrence").Should().Be("weekly");       // normalised
        Get<string>(chore.Params, "assigneeName").Should().Be("Leo");

        var ev = result.Actions.Single(a => a.Type == "calendar_event");
        Get<string>(ev.Params, "startLocal").Should().Be("2026-06-23T16:00:00");

        var meal = result.Actions.Single(a => a.Type == "meal");
        Get<string>(meal.Params, "mealDateLocal").Should().Be("2026-06-26"); // bare date kept as a date
    }

    // =====================================================================================
    // At most 6 actions are kept
    // =====================================================================================

    [Fact]
    public async Task At_most_six_actions_are_kept()
    {
        var actions = Enumerable.Range(0, 10).Select(i => (object)new
        {
            type = "timer",
            title = $"Timer {i}",
            @params = new { label = $"T{i}", durationSeconds = 300 },
        }).ToArray();
        var modelJson = JsonSerializer.Serialize(new { answer = "", actions });

        var result = await ServiceReturning(modelJson).FamilyAssistantAsync("x", "(s)", Ref, Tz);

        result.Should().NotBeNull();
        result!.Actions.Should().HaveCount(6);
    }

    // =====================================================================================
    // A bad/blank ISO local time is normalised to "" (no time implied)
    // =====================================================================================

    [Fact]
    public async Task A_garbage_local_time_param_is_dropped_to_empty()
    {
        var modelJson = JsonSerializer.Serialize(new
        {
            answer = "",
            actions = new object[]
            {
                new { type = "reminder", title = "Remind", @params = new { text = "Water plants", whenLocal = "not-a-date" } },
            },
        });

        var result = await ServiceReturning(modelJson).FamilyAssistantAsync("x", "(s)", Ref, Tz);

        result.Should().NotBeNull();
        var reminder = result!.Actions.Single();
        Get<string>(reminder.Params, "whenLocal").Should().Be(""); // unparseable -> ""
    }
}
