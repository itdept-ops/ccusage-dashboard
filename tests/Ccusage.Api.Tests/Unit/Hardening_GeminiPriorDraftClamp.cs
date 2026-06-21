using System.Net;
using System.Text;
using System.Text.Json;
using Ccusage.Api.Dtos;
using Ccusage.Api.Services;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ccusage.Api.Tests.Unit;

/// <summary>
/// Hardening regression: the build-day refine path re-clamps the (client-supplied) <see cref="DayDraft"/>
/// PRIOR_DRAFT to the SAME ceilings the output mapper enforces BEFORE serializing it into the Gemini prompt,
/// so a hostile/oversized prior draft can never bypass the input caps (count + per-string length).
///
/// We can't see the private SanitizePriorDraft directly, so we exercise it end-to-end through
/// <see cref="GeminiService.BuildDayAsync"/>: the stub handler captures the OUTBOUND request body (which
/// embeds the serialized prior draft) and we assert the oversized content was clamped out of the prompt.
/// </summary>
public class Hardening_GeminiPriorDraftClamp
{
    /// <summary>Captures the last outbound request body, then returns a minimal canned generateContent reply.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequestBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);

            var envelope = new
            {
                candidates = new[]
                {
                    new { content = new { parts = new[] { new { text = "{\"summary\":\"ok\"}" } } } },
                },
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(envelope), Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://generativelanguage.googleapis.com") };
    }

    private static (GeminiService Svc, CapturingHandler Handler) Service()
    {
        var handler = new CapturingHandler();
        var opts = Options.Create(new GeminiOptions { ApiKey = "test-key", Model = "gemini-2.5-flash" });
        var svc = new GeminiService(
            new StubFactory(handler), opts,
            new MemoryCache(new MemoryCacheOptions()), NullLogger<GeminiService>.Instance);
        return (svc, handler);
    }

    [Fact]
    public async Task BuildDay_reclamps_oversized_prior_draft_before_it_reaches_the_prompt()
    {
        // A hostile prior draft that BLOWS PAST every output ceiling: 12 meals (cap 5), 60 foods/meal
        // (cap 25/meal, 50 total), 40 exercises (cap 20), 50 drinks (cap 30), 20 assumptions (cap 8), plus
        // a huge description / quantity / name / label / summary and absurd numbers.
        var bigDesc = new string('D', 4000);
        var bigQty = new string('Q', 4000);
        var bigName = new string('N', 4000);
        var bigLabel = new string('L', 4000);
        var bigSummary = new string('S', 4000);

        var prior = new DayDraft
        {
            Summary = bigSummary,
            Assumptions = Enumerable.Range(0, 20).Select(_ => new string('A', 1000)).ToList(),
            Meals = Enumerable.Range(0, 12).Select(_ => new MealDraft
            {
                Meal = "lunch",
                Items = Enumerable.Range(0, 60).Select(_ => new DraftFood
                {
                    Description = bigDesc,
                    Quantity = bigQty,
                    Calories = 999999,
                    ProteinG = 9999,
                    CarbG = 9999,
                    FatG = 9999,
                    Confidence = 5,
                }).ToList(),
            }).ToList(),
            Exercises = Enumerable.Range(0, 40).Select(_ => new DraftExercise
            {
                Name = bigName, CaloriesBurned = 999999, DurationMin = 999999, Confidence = 5,
            }).ToList(),
            Hydration = Enumerable.Range(0, 50).Select(_ => new DraftDrink
            {
                Label = bigLabel, Ml = 999999,
            }).ToList(),
        };

        var (svc, handler) = Service();

        await svc.BuildDayAsync(
            text: "what did I have", localDate: "2026-06-21", localTimeOfDay: "20:00",
            images: Array.Empty<(string, string)>(), priorDraft: prior,
            answers: Array.Empty<ClarifyAnswer>(), bodyWeightKg: 70);

        handler.LastRequestBody.Should().NotBeNull();
        var body = handler.LastRequestBody!;

        // The serialized prior draft is embedded in the request body. Re-extract the PRIOR_DRAFT JSON object
        // (between the "PRIOR_DRAFT:" marker and the following "ANSWERS:" marker) and assert the clamps held.
        body.Should().Contain("PRIOR_DRAFT:");
        var draftJson = ExtractPriorDraftJson(body);
        var clamped = JsonSerializer.Deserialize<DayDraft>(draftJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        // Count caps (same ceilings as MapDayDraft).
        clamped.Meals.Should().HaveCountLessThanOrEqualTo(5);
        clamped.Meals.Sum(m => m.Items.Count).Should().BeLessThanOrEqualTo(50);
        clamped.Meals.Should().OnlyContain(m => m.Items.Count <= 25);
        clamped.Exercises.Should().HaveCountLessThanOrEqualTo(20);
        clamped.Hydration.Should().HaveCountLessThanOrEqualTo(30);
        clamped.Assumptions.Should().HaveCountLessThanOrEqualTo(8);

        // Per-string length caps.
        clamped.Summary.Length.Should().BeLessThanOrEqualTo(200);
        clamped.Assumptions.Should().OnlyContain(a => a.Length <= 200);
        clamped.Meals.SelectMany(m => m.Items).Should().OnlyContain(i => i.Description.Length <= 256);
        clamped.Meals.SelectMany(m => m.Items).Should().OnlyContain(i => i.Quantity!.Length <= 128);
        clamped.Exercises.Should().OnlyContain(x => x.Name.Length <= 128);
        clamped.Hydration.Should().OnlyContain(h => h.Label!.Length <= 64);

        // Numeric caps (calories 0..5000, macros 0..500, confidence 0..1).
        clamped.Meals.SelectMany(m => m.Items).Should().OnlyContain(i =>
            i.Calories <= 5000 && i.ProteinG <= 500 && i.CarbG <= 500 && i.FatG <= 500 && i.Confidence <= 1);
        clamped.Exercises.Should().OnlyContain(x => x.CaloriesBurned <= 5000 && x.Confidence <= 1);

        // And the oversized raw strings must NOT appear anywhere in the outbound prompt.
        body.Should().NotContain(bigDesc);
        body.Should().NotContain(bigSummary);
    }

    /// <summary>Pull the PRIOR_DRAFT JSON object out of the captured (JSON-escaped) request body.</summary>
    private static string ExtractPriorDraftJson(string body)
    {
        // The whole prompt is a JSON string value, so newlines appear as the escape sequence \n. The prior
        // draft sits between the "PRIOR_DRAFT:\n" marker and the next "\nANSWERS:" marker.
        const string start = "PRIOR_DRAFT:\\n";
        const string end = "\\nANSWERS:";
        var s = body.IndexOf(start, StringComparison.Ordinal);
        s.Should().BeGreaterThan(-1);
        s += start.Length;
        var e = body.IndexOf(end, s, StringComparison.Ordinal);
        e.Should().BeGreaterThan(-1);
        var raw = body[s..e];
        // Un-escape the JSON-string encoding so we get the literal serialized DayDraft back.
        return JsonSerializer.Deserialize<string>("\"" + raw + "\"")!;
    }
}
