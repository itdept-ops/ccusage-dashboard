using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ccusage.Api.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ccusage.Api.Tests.Integration;

/// <summary>
/// 75 Hard (Relaxed) backend (/api/challenge): auth (401) + tracker.self (403) gating; the one-active
/// invariant (a second start 409); the ?user visibility gate (sharer visible read-only, non-sharer 404,
/// confession nulled for a viewer, no email on the wire); the manual upsert (PUT persists read/photo-boolean/
/// no-alcohol/confession/outdoor/diet-override and accepts no image); and the AUTO scoring matching a
/// hand-built tracker day (diet within calorie + set-macro goals, water &gt;= 3785 ml, two &gt;=45-minute
/// workouts ⇒ a complete day). Every test provisions fresh users so they're order-independent.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class HardChallengeTests(WebAppFactory factory)
{
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
        var email = $"hard-{Guid.NewGuid():N}@test.local";
        var res = await Admin().PostAsJsonAsync("/api/users", new { email, isEnabled = true, permissions });
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        return (email, Client(email));
    }

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        await resp.Content.ReadFromJsonAsync<JsonElement>();

    private static bool HasProperty(JsonElement e, string name) => e.TryGetProperty(name, out _);

    private async Task MakeContacts(string aEmail, string bEmail)
    {
        var aId = await UserIdFor(aEmail);
        var bId = await UserIdFor(bEmail);
        var res = await Admin().PostAsJsonAsync($"/api/chat/contacts/user/{aId}", new { contactUserId = bId });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<int> UserIdFor(string email)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
        return await db.Users.AsNoTracking().Where(u => u.Email == email).Select(u => u.Id).FirstAsync();
    }

    // The test container has no configured display timezone → "today" is UTC today.
    private static readonly string Today = DateTime.UtcNow.ToString("yyyy-MM-dd");

    // ---- Gating ----

    [Fact]
    public async Task Challenge_endpoints_require_authentication()
    {
        var anon = factory.CreateClient();
        (await anon.GetAsync("/api/challenge")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anon.GetAsync($"/api/challenge/day?date={Today}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anon.GetAsync("/api/challenge/shared")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anon.PostAsJsonAsync("/api/challenge", new { })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Challenge_endpoints_require_tracker_self()
    {
        var (_, noTracker) = await ProvisionUser("dashboard.view");
        (await noTracker.GetAsync("/api/challenge")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await noTracker.GetAsync($"/api/challenge/day?date={Today}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await noTracker.GetAsync("/api/challenge/shared")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await noTracker.PostAsJsonAsync("/api/challenge", new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await noTracker.PutAsJsonAsync("/api/challenge/day", new { date = Today })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await noTracker.PostAsJsonAsync("/api/challenge/cheat-days", new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- Start + one-active invariant ----

    [Fact]
    public async Task Get_returns_null_until_a_challenge_is_started()
    {
        var (_, user) = await ProvisionUser("tracker.self");
        var before = await user.GetAsync("/api/challenge");
        before.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(before)).ValueKind.Should().Be(JsonValueKind.Null);

        var start = await user.PostAsJsonAsync("/api/challenge", new { startDate = Today });
        start.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await Json(start);
        dto.GetProperty("status").GetString().Should().Be("Active");
        dto.GetProperty("ruleset").GetString().Should().Be("Relaxed");
        dto.GetProperty("currentDay").GetInt32().Should().Be(1);
        dto.GetProperty("totalDays").GetInt32().Should().Be(75);
        dto.GetProperty("days").EnumerateArray().Should().HaveCount(75);

        var after = await Json(await user.GetAsync("/api/challenge"));
        after.GetProperty("status").GetString().Should().Be("Active");
    }

    [Fact]
    public async Task A_second_start_while_one_is_active_is_409()
    {
        var (_, user) = await ProvisionUser("tracker.self");
        (await user.PostAsJsonAsync("/api/challenge", new { startDate = Today })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await user.PostAsJsonAsync("/api/challenge", new { startDate = Today })).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ---- ?user visibility ----

    [Fact]
    public async Task A_sharing_users_challenge_is_visible_read_only_with_confession_nulled()
    {
        var (aliceEmail, alice) = await ProvisionUser("tracker.self");
        var (bobEmail, bob) = await ProvisionUser("tracker.self");
        await MakeContacts(aliceEmail, bobEmail);

        await alice.PutAsJsonAsync("/api/tracker/profile", new { goal = "Maintain", shareWithContacts = true });
        await alice.PostAsJsonAsync("/api/challenge", new { startDate = Today });
        await alice.PutAsJsonAsync("/api/challenge/day", new
        {
            date = Today, readOk = true, photoTaken = true, confession = "Slipped on the diet today.",
        });

        var aliceId = await UserIdFor(aliceEmail);
        var viewed = await bob.GetAsync($"/api/challenge?user={aliceId}");
        viewed.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await Json(viewed);
        dto.GetProperty("readOnly").GetBoolean().Should().BeTrue();
        dto.GetProperty("userId").GetInt32().Should().Be(aliceId);
        HasProperty(dto, "userEmail").Should().BeFalse();

        // The viewed day's confession is NULLED (the owner's private narration never leaks to a viewer).
        var day = dto.GetProperty("days").EnumerateArray().First(d => d.GetProperty("date").GetString() == Today);
        day.GetProperty("confession").ValueKind.Should().Be(JsonValueKind.Null);

        // The owner sees her own confession.
        var own = await Json(await alice.GetAsync("/api/challenge"));
        var ownDay = own.GetProperty("days").EnumerateArray().First(d => d.GetProperty("date").GetString() == Today);
        ownDay.GetProperty("confession").GetString().Should().Be("Slipped on the diet today.");
    }

    [Fact]
    public async Task A_non_sharing_users_challenge_is_404_to_a_contact_and_to_a_stranger()
    {
        var (aliceEmail, alice) = await ProvisionUser("tracker.self");
        var (bobEmail, bob) = await ProvisionUser("tracker.self");
        await MakeContacts(aliceEmail, bobEmail);

        await alice.PutAsJsonAsync("/api/tracker/profile", new { goal = "Maintain", shareWithContacts = false });
        await alice.PostAsJsonAsync("/api/challenge", new { startDate = Today });

        var aliceId = await UserIdFor(aliceEmail);
        (await bob.GetAsync($"/api/challenge?user={aliceId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await bob.GetAsync($"/api/challenge/day?date={Today}&user={aliceId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // A non-existent user id is also 404 (never leak existence); a non-positive id is a 400.
        (await alice.GetAsync("/api/challenge?user=999999999")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await alice.GetAsync("/api/challenge?user=0")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Tracker_viewall_can_read_anyones_challenge_read_only()
    {
        var (aliceEmail, alice) = await ProvisionUser("tracker.self");
        var (_, coach) = await ProvisionUser("tracker.self", "tracker.viewall");

        await alice.PutAsJsonAsync("/api/tracker/profile", new { goal = "Maintain", shareWithContacts = false });
        await alice.PostAsJsonAsync("/api/challenge", new { startDate = Today });

        var aliceId = await UserIdFor(aliceEmail);
        var viewed = await coach.GetAsync($"/api/challenge?user={aliceId}");
        viewed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(viewed)).GetProperty("readOnly").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Shared_list_includes_sharing_contacts_and_never_an_email()
    {
        var (aliceEmail, alice) = await ProvisionUser("tracker.self");
        var (bobEmail, bob) = await ProvisionUser("tracker.self");
        await MakeContacts(aliceEmail, bobEmail);

        await alice.PutAsJsonAsync("/api/tracker/profile", new { goal = "Maintain", shareWithContacts = true });

        var aliceId = await UserIdFor(aliceEmail);
        var bobShared = (await Json(await bob.GetAsync("/api/challenge/shared"))).EnumerateArray().ToList();
        bobShared.Select(u => u.GetProperty("userId").GetInt32()).Should().Contain(aliceId);
        bobShared.Should().NotContain(u => HasProperty(u, "email"));
    }

    // ---- Manual upsert ----

    [Fact]
    public async Task Put_upserts_the_manual_fields_and_ignores_any_image_payload()
    {
        var (_, user) = await ProvisionUser("tracker.self");
        await user.PostAsJsonAsync("/api/challenge", new { startDate = Today });

        // An image payload is simply not a field the contract binds — it is ignored; the boolean is what counts.
        var put = await user.PutAsJsonAsync("/api/challenge/day", new
        {
            date = Today, readOk = true, photoTaken = true, noAlcohol = false,
            confession = "Long day.", workout2Outdoor = true, dietOverride = true,
            photo = "data:image/png;base64,AAAA", image = new byte[] { 1, 2, 3 },
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var day = await Json(put);
        day.GetProperty("readOk").GetBoolean().Should().BeTrue();
        day.GetProperty("photoTaken").GetBoolean().Should().BeTrue();
        day.GetProperty("noAlcohol").GetBoolean().Should().BeFalse();
        day.GetProperty("workout2Outdoor").GetBoolean().Should().BeTrue();
        day.GetProperty("dietOverride").GetBoolean().Should().BeTrue();
        day.GetProperty("confession").GetString().Should().Be("Long day.");
        // No image is ever echoed back — the response has no image-bearing field.
        HasProperty(day, "photo").Should().BeFalse();
        HasProperty(day, "image").Should().BeFalse();

        // A second PUT partially updates — unspecified fields keep their value.
        await user.PutAsJsonAsync("/api/challenge/day", new { date = Today, noAlcohol = true });
        var after = await Json(await user.GetAsync($"/api/challenge/day?date={Today}"));
        after.GetProperty("noAlcohol").GetBoolean().Should().BeTrue();
        after.GetProperty("readOk").GetBoolean().Should().BeTrue();        // preserved
        after.GetProperty("dietOverride").GetBoolean().Should().BeTrue();  // preserved
    }

    [Fact]
    public async Task Put_day_without_an_active_challenge_is_404()
    {
        var (_, user) = await ProvisionUser("tracker.self");
        (await user.PutAsJsonAsync("/api/challenge/day", new { date = Today, readOk = true }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- AUTO scoring matches a hand-built tracker day ----

    [Fact]
    public async Task Auto_scoring_matches_a_hand_built_tracker_day_and_completes_it()
    {
        var (_, user) = await ProvisionUser("tracker.self");

        // Goals: 2000 cal, 150 P, 200 C, 60 F. Hydration default goal is irrelevant — the gallon is fixed.
        await user.PutAsJsonAsync("/api/tracker/profile", new
        {
            goal = "Maintain", weightKg = 80.0, shareWithContacts = false,
            dailyCalorieGoal = 2000, proteinGoalG = 150, carbGoalG = 200, fatGoalG = 60,
        });
        await user.PostAsJsonAsync("/api/challenge", new { startDate = Today });

        // Diet within all goals: 1800 cal / 140 P / 180 C / 55 F.
        await user.PostAsJsonAsync("/api/tracker/food", new
        {
            date = Today, meal = "breakfast", description = "Day total", quantity = 1.0,
            calories = 1800, proteinG = 140.0, carbG = 180.0, fatG = 55.0,
        });
        // Water: 4 x 1000 ml = 4000 ml >= 3785 ml (one US gallon).
        for (var i = 0; i < 4; i++)
            await user.PostAsJsonAsync("/api/tracker/hydration", new { date = Today, amountMl = 1000 });
        // Two >= 45-minute workouts.
        await user.PostAsJsonAsync("/api/tracker/exercise", new { date = Today, name = "AM lift", durationMin = 50, caloriesBurned = 300 });
        await user.PostAsJsonAsync("/api/tracker/exercise", new { date = Today, name = "PM run", durationMin = 45, caloriesBurned = 400 });
        // A short workout that must NOT count toward the two.
        await user.PostAsJsonAsync("/api/tracker/exercise", new { date = Today, name = "Stretch", durationMin = 10, caloriesBurned = 20 });

        // The auto bits are now satisfied; attest the two manual tasks (read + photo) and no-alcohol holds.
        await user.PutAsJsonAsync("/api/challenge/day", new { date = Today, readOk = true, photoTaken = true });

        var day = await Json(await user.GetAsync($"/api/challenge/day?date={Today}"));
        day.GetProperty("dietOk").GetBoolean().Should().BeTrue();
        day.GetProperty("waterGallonOk").GetBoolean().Should().BeTrue();
        day.GetProperty("workout1Ok").GetBoolean().Should().BeTrue();
        day.GetProperty("workout2Ok").GetBoolean().Should().BeTrue();
        day.GetProperty("readOk").GetBoolean().Should().BeTrue();
        day.GetProperty("photoTaken").GetBoolean().Should().BeTrue();
        day.GetProperty("noAlcohol").GetBoolean().Should().BeTrue();
        day.GetProperty("complete").GetBoolean().Should().BeTrue();

        // The challenge aggregate reflects the one completed day.
        var ch = await Json(await user.GetAsync("/api/challenge"));
        ch.GetProperty("completedDays").GetInt32().Should().Be(1);
        ch.GetProperty("currentStreak").GetInt32().Should().Be(1);
        ch.GetProperty("longestStreak").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Diet_fails_when_a_set_macro_goal_is_exceeded()
    {
        var (_, user) = await ProvisionUser("tracker.self");
        await user.PutAsJsonAsync("/api/tracker/profile", new
        {
            goal = "Maintain", shareWithContacts = false,
            dailyCalorieGoal = 2000, proteinGoalG = 100,
        });
        await user.PostAsJsonAsync("/api/challenge", new { startDate = Today });

        // Calories fine, but protein over its 100 g goal → diet fails.
        await user.PostAsJsonAsync("/api/tracker/food", new
        {
            date = Today, meal = "lunch", description = "Protein bomb", quantity = 1.0,
            calories = 1500, proteinG = 180.0, carbG = 50.0, fatG = 30.0,
        });

        var day = await Json(await user.GetAsync($"/api/challenge/day?date={Today}"));
        day.GetProperty("dietOk").GetBoolean().Should().BeFalse();

        // A diet override flips it true regardless of the tracker totals.
        await user.PutAsJsonAsync("/api/challenge/day", new { date = Today, dietOverride = true });
        var overridden = await Json(await user.GetAsync($"/api/challenge/day?date={Today}"));
        overridden.GetProperty("dietOk").GetBoolean().Should().BeTrue();
    }

    // ---- Cheat days: future-only, within window, capped ----

    [Fact]
    public async Task Cheat_days_must_be_future_dates_within_the_window()
    {
        var (_, user) = await ProvisionUser("tracker.self");
        await user.PostAsJsonAsync("/api/challenge", new { startDate = Today });

        // Today (not strictly future) is rejected.
        (await user.PostAsJsonAsync("/api/challenge/cheat-days", new { add = new[] { Today } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // A future date within the window is accepted and shows on the grid.
        var future = DateTime.UtcNow.AddDays(5).ToString("yyyy-MM-dd");
        var ok = await user.PostAsJsonAsync("/api/challenge/cheat-days", new { add = new[] { future } });
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await Json(ok);
        var cheat = dto.GetProperty("days").EnumerateArray().First(d => d.GetProperty("date").GetString() == future);
        cheat.GetProperty("isCheatDay").GetBoolean().Should().BeTrue();

        // A date beyond the 75-day window is rejected.
        var beyond = DateTime.UtcNow.AddDays(200).ToString("yyyy-MM-dd");
        (await user.PostAsJsonAsync("/api/challenge/cheat-days", new { add = new[] { beyond } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
