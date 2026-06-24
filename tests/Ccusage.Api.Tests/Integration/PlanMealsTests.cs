using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Ccusage.Api.Tests.Integration;

/// <summary>
/// "✨ Plan my day / week" — POST /api/ai/plan-meals (read) + POST /api/ai/plan-meals/to-plan (commit). The robust
/// extension of /what-to-eat: the planner aggregates the CALLER's own context server-side (remaining macros + recent
/// foods + their saved recipes + on-hand groceries + planned meals) and asks Gemini for a per-DAY plan that fits the
/// daily budget. Like /what-to-eat it NEVER 503s: with Gemini unconfigured (the test host always is) it returns 200
/// with a friendly NON-AI plan (<c>aiUsed:false</c>) drawn from the caller's recipes / recent foods / groceries.
///
/// These tests verify: the tracker.ai gate on the planner read, the always-200 floor + per-day shape, the
/// caller-scoping invariant (only the caller's own recipes/meals surface — never another user's, never an email),
/// and the add-to-plan WRITE (gated by meals.use; writes the chosen meals into the household plan via the SAME
/// create path as POST /api/family/meals, so they appear on GET /api/family/meals; the count is clamped).
/// </summary>
[Collection(IntegrationCollection.Name)]
public class PlanMealsTests(WebAppFactory factory)
{
    private static readonly string Today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

    private HttpClient Admin()
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestJwt.For(WebAppFactory.AdminEmail));
        return c;
    }

    private HttpClient Client(string email)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestJwt.For(email));
        return c;
    }

    private async Task<(string email, HttpClient client)> ProvisionUser(params string[] permissions)
    {
        var email = $"plan-{Guid.NewGuid():N}@test.local";
        var res = await Admin().PostAsJsonAsync("/api/users", new { email, isEnabled = true, permissions });
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        return (email, Client(email));
    }

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task EnsureHousehold(HttpClient c) => await c.GetAsync("/api/family/household");

    // =====================================================================================
    // Gating: anonymous → 401; tracker.self alone → 403; tracker.ai → allowed.
    // =====================================================================================

    [Fact]
    public async Task Plan_anonymous_is_401()
    {
        var anon = factory.CreateClient();
        (await anon.PostAsJsonAsync("/api/ai/plan-meals", new { }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Plan_tracker_self_only_is_403()
    {
        var (_, selfOnly) = await ProvisionUser("tracker.self");
        (await selfOnly.PostAsJsonAsync("/api/ai/plan-meals", new { }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Plan_tracker_ai_is_allowed_and_returns_200_even_with_ai_off()
    {
        var (_, user) = await ProvisionUser("tracker.ai");
        var res = await user.PostAsJsonAsync("/api/ai/plan-meals", new { });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(res);
        body.GetProperty("aiUsed").GetBoolean().Should().BeFalse(); // test host has no Gemini key
        body.TryGetProperty("days", out var days).Should().BeTrue();
        days.ValueKind.Should().Be(JsonValueKind.Array);
    }

    // =====================================================================================
    // Floor path (AI off): a per-day plan sourced from the caller's recipes / planned meals.
    // =====================================================================================

    [Fact]
    public async Task Plan_fallback_builds_a_day_per_requested_date_from_the_callers_planned_meals()
    {
        var (_, user) = await ProvisionUser("tracker.ai", "tracker.self", "family.use");
        await EnsureHousehold(user);

        // A planned meal the deterministic floor can seed from (next-7-days window).
        (await user.PostAsJsonAsync("/api/family/meals",
            new { localDate = Today, slot = "dinner", title = "Sheet-pan salmon" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Anchor the plan explicitly so the first day is deterministic regardless of the host clock.
        var body = await Json(await user.PostAsJsonAsync("/api/ai/plan-meals",
            new { days = 2, slots = new[] { "dinner" }, constraints = "high protein", weekStart = Today }));
        body.GetProperty("aiUsed").GetBoolean().Should().BeFalse();

        var days = body.GetProperty("days").EnumerateArray().ToList();
        days.Should().NotBeEmpty();

        var first = days[0];
        first.GetProperty("localDate").GetString().Should().Be(Today);
        var slots = first.GetProperty("slots").EnumerateArray().ToList();
        slots.Should().NotBeEmpty();
        var meal = slots[0];
        meal.GetProperty("slot").GetString().Should().Be("dinner");
        meal.GetProperty("title").GetString().Should().Be("Sheet-pan salmon");
        meal.GetProperty("why").GetString().Should().NotBeNullOrEmpty();
        var macros = meal.GetProperty("macros");
        macros.GetProperty("calories").ValueKind.Should().Be(JsonValueKind.Number);
        macros.TryGetProperty("proteinG", out _).Should().BeTrue();
        meal.GetProperty("ingredients").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task Plan_fallback_seeds_from_the_callers_saved_recipes()
    {
        var (_, user) = await ProvisionUser("tracker.ai", "tracker.self", "recipes.use");
        var recipeTitle = $"Recipe-{Guid.NewGuid():N}";
        (await user.PostAsJsonAsync("/api/recipes", new { title = recipeTitle, servings = 2 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await Json(await user.PostAsJsonAsync("/api/ai/plan-meals",
            new { days = 1, slots = new[] { "dinner" } }));
        body.GetProperty("aiUsed").GetBoolean().Should().BeFalse();

        var titles = body.GetProperty("days").EnumerateArray()
            .SelectMany(d => d.GetProperty("slots").EnumerateArray())
            .Select(s => s.GetProperty("title").GetString())
            .ToList();
        titles.Should().Contain(recipeTitle);
    }

    [Fact]
    public async Task Plan_is_caller_scoped_no_cross_user_recipes_or_email()
    {
        // User A: a uniquely-titled saved recipe.
        var (_, alice) = await ProvisionUser("tracker.ai", "tracker.self", "recipes.use");
        var aliceRecipe = $"Alice-{Guid.NewGuid():N}";
        await alice.PostAsJsonAsync("/api/recipes", new { title = aliceRecipe, servings = 1 });

        // User B: a SEPARATE uniquely-titled recipe that must NEVER surface for A.
        var (_, bob) = await ProvisionUser("tracker.ai", "tracker.self", "recipes.use");
        var bobRecipe = $"Bob-{Guid.NewGuid():N}";
        await bob.PostAsJsonAsync("/api/recipes", new { title = bobRecipe, servings = 1 });

        var raw = await (await alice.PostAsJsonAsync("/api/ai/plan-meals",
            new { days = 1, slots = new[] { "dinner" } })).Content.ReadAsStringAsync();

        raw.Should().Contain(aliceRecipe);   // A sees her own recipe in the fallback
        raw.Should().NotContain(bobRecipe);   // ...but NEVER B's
        raw.Should().NotContain("@");          // emails never on the wire
    }

    [Fact]
    public async Task Plan_clamps_the_requested_day_count_to_seven()
    {
        var (_, user) = await ProvisionUser("tracker.ai", "tracker.self", "family.use");
        await EnsureHousehold(user);
        await user.PostAsJsonAsync("/api/family/meals",
            new { localDate = Today, slot = "dinner", title = "Tacos" });

        var body = await Json(await user.PostAsJsonAsync("/api/ai/plan-meals",
            new { days = 99, slots = new[] { "dinner" } }));
        // Even with a hostile day count, the plan never spans more than the 7-day window.
        body.GetProperty("days").EnumerateArray().Count().Should().BeLessThanOrEqualTo(7);
    }

    // =====================================================================================
    // ADD-TO-PLAN (the write): gated by meals.use; writes into the household meal plan.
    // =====================================================================================

    [Fact]
    public async Task AddToPlan_requires_meals_use()
    {
        // tracker.ai admits the route group, but the WRITE additionally requires meals.use (403 without it).
        var (_, user) = await ProvisionUser("tracker.ai", "family.use");
        (await user.PostAsJsonAsync("/api/ai/plan-meals/to-plan", new
        {
            meals = new[] { new { localDate = Today, slot = "dinner", title = "Chili" } },
        })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AddToPlan_writes_the_chosen_meals_into_the_household_plan()
    {
        var (_, user) = await ProvisionUser("tracker.ai", "family.use", "meals.use");
        await EnsureHousehold(user);

        var title = $"Plan-{Guid.NewGuid():N}";
        var res = await user.PostAsJsonAsync("/api/ai/plan-meals/to-plan", new
        {
            meals = new[]
            {
                new
                {
                    localDate = Today, slot = "dinner", title,
                    ingredients = "chicken\nrice", servings = 2,
                    calories = 800, proteinG = 60.0, carbG = 70.0, fatG = 20.0, macroSource = "ai",
                },
            },
        });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(res)).GetProperty("added").GetInt32().Should().Be(1);

        // It now appears on the meal plan (the SAME store as POST /api/family/meals), with its macros applied.
        var week = await Json(await user.GetAsync($"/api/family/meals?weekStart={Today}"));
        var meals = week.EnumerateArray()
            .SelectMany(d => d.GetProperty("meals").EnumerateArray())
            .ToList();
        var mine = meals.SingleOrDefault(m => m.GetProperty("title").GetString() == title);
        mine.ValueKind.Should().NotBe(JsonValueKind.Undefined);
        mine.GetProperty("slot").GetString().Should().Be("dinner");
        mine.GetProperty("calories").GetInt32().Should().Be(800);
        mine.GetProperty("macroSource").GetString().Should().Be("ai");
    }

    [Fact]
    public async Task AddToPlan_skips_rows_with_a_blank_title_or_invalid_date()
    {
        var (_, user) = await ProvisionUser("tracker.ai", "family.use", "meals.use");
        await EnsureHousehold(user);

        var good = $"Good-{Guid.NewGuid():N}";
        var res = await user.PostAsJsonAsync("/api/ai/plan-meals/to-plan", new
        {
            meals = new object[]
            {
                new { localDate = Today, slot = "dinner", title = good },     // valid
                new { localDate = Today, slot = "lunch", title = "   " },      // blank title -> skipped
                new { localDate = "not-a-date", slot = "dinner", title = "X" }, // bad date -> skipped
            },
        });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(res)).GetProperty("added").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task AddToPlan_with_no_meals_adds_nothing()
    {
        var (_, user) = await ProvisionUser("tracker.ai", "family.use", "meals.use");
        await EnsureHousehold(user);
        var res = await user.PostAsJsonAsync("/api/ai/plan-meals/to-plan", new { meals = Array.Empty<object>() });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(res)).GetProperty("added").GetInt32().Should().Be(0);
    }
}
