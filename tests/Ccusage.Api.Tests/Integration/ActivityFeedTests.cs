using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ccusage.Api.Data;
using Ccusage.Api.Data.Entities;
using Ccusage.Api.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ccusage.Api.Tests.Integration;

/// <summary>
/// The social activity feed + event spine (<see cref="ActivityEmitter"/> + <c>GET /api/feed</c>):
/// <list type="bullet">
///   <item>OPT-IN gating: no event is written when the actor hasn't opted to share (default OFF).</item>
///   <item>The emitter NEVER throws into the caller (a bad DB/actor is swallowed).</item>
///   <item><c>EmitOnceAsync</c> de-dupes per (actor, kind, intValue) — a re-emit is a no-op.</item>
///   <item>Circle-only visibility: a stranger's events are excluded; a sharing contact's are included.</item>
///   <item>Own events are always visible (even with view-feed OFF + not sharing).</item>
///   <item>The view-feed opt-in: OFF ⇒ only own events; ON ⇒ own + sharing circle.</item>
///   <item>DisplayName is applied and the actor email NEVER appears on the wire (email-privacy).</item>
///   <item>The real emit call sites fire (workout logged, challenge started/day complete, hydration goal).</item>
/// </list>
/// Every test provisions fresh users so they're order-independent.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class ActivityFeedTests(WebAppFactory factory)
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
        var email = $"feed-{Guid.NewGuid():N}@test.local";
        var res = await Admin().PostAsJsonAsync("/api/users", new { email, isEnabled = true, permissions });
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        return (email, Client(email));
    }

    private async Task<int> UserIdFor(string email)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
        return await db.Users.AsNoTracking().Where(u => u.Email == email).Select(u => u.Id).FirstAsync();
    }

    private async Task MakeContacts(string aEmail, string bEmail)
    {
        var aId = await UserIdFor(aEmail);
        var bId = await UserIdFor(bEmail);
        (await Admin().PostAsJsonAsync($"/api/chat/contacts/user/{aId}", new { contactUserId = bId }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>Set the activity share + view-feed opt-ins directly on the user row.</summary>
    private async Task SetActivityPrefs(string email, bool share, bool view)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
        var u = await db.Users.FirstAsync(x => x.Email == email);
        u.ShareActivity = share;
        u.ViewActivityFeed = view;
        await db.SaveChangesAsync();
    }

    private async Task<T> InScopeAsync<T>(Func<UsageDbContext, ActivityEmitter, Task<T>> body)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
        var emitter = scope.ServiceProvider.GetRequiredService<ActivityEmitter>();
        return await body(db, emitter);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        await resp.Content.ReadFromJsonAsync<JsonElement>();

    private static readonly string Today = DateTime.UtcNow.ToString("yyyy-MM-dd");

    // ---- Gating ----

    [Fact]
    public async Task Feed_requires_authentication_and_tracker_self()
    {
        var anon = factory.CreateClient();
        (await anon.GetAsync("/api/feed")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var (_, noTracker) = await ProvisionUser("dashboard.view");
        (await noTracker.GetAsync("/api/feed")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- Emitter: opt-in gating + never-throws + de-dupe ----

    [Fact]
    public async Task Emitter_writes_no_event_when_the_actor_is_not_sharing()
    {
        var (email, _) = await ProvisionUser("tracker.self"); // ShareActivity defaults OFF
        await InScopeAsync(async (db, emitter) =>
        {
            await emitter.EmitAsync(email, ActivityEmitter.Kinds.WorkoutLogged, 30, "Run");
            (await db.ActivityEvents.AsNoTracking().AnyAsync(e => e.ActorEmail == email))
                .Should().BeFalse("a non-sharing actor's action must never become an event");
            return true;
        });
    }

    [Fact]
    public async Task Emitter_writes_one_event_when_the_actor_is_sharing()
    {
        var (email, _) = await ProvisionUser("tracker.self");
        await SetActivityPrefs(email, share: true, view: false);
        await InScopeAsync(async (db, emitter) =>
        {
            await emitter.EmitAsync(email, ActivityEmitter.Kinds.WorkoutLogged, 30, "Run");
            var rows = await db.ActivityEvents.AsNoTracking().Where(e => e.ActorEmail == email).ToListAsync();
            rows.Should().ContainSingle();
            rows[0].Kind.Should().Be("workout.logged");
            rows[0].IntValue.Should().Be(30);
            rows[0].Label.Should().Be("Run");
            return true;
        });
    }

    [Fact]
    public async Task Emitter_never_throws_for_an_unknown_or_blank_actor()
    {
        await InScopeAsync(async (_, emitter) =>
        {
            // Unknown actor, blank actor, blank kind — none must throw (fire-and-forget safety).
            await emitter.EmitAsync("does-not-exist@test.local", ActivityEmitter.Kinds.WorkoutLogged);
            await emitter.EmitAsync("", ActivityEmitter.Kinds.WorkoutLogged);
            await emitter.EmitAsync("x@test.local", "");
            await emitter.EmitOnceAsync("does-not-exist@test.local", ActivityEmitter.Kinds.ChallengeDayComplete, 5);
            return true;
        });
    }

    [Fact]
    public async Task EmitOnce_is_idempotent_per_actor_kind_intValue()
    {
        var (email, _) = await ProvisionUser("tracker.self");
        await SetActivityPrefs(email, share: true, view: false);
        await InScopeAsync(async (db, emitter) =>
        {
            await emitter.EmitOnceAsync(email, ActivityEmitter.Kinds.ChallengeDayComplete, 12);
            await emitter.EmitOnceAsync(email, ActivityEmitter.Kinds.ChallengeDayComplete, 12); // re-emit: no-op
            await emitter.EmitOnceAsync(email, ActivityEmitter.Kinds.ChallengeDayComplete, 13); // different day: writes

            var days = await db.ActivityEvents.AsNoTracking()
                .Where(e => e.ActorEmail == email && e.Kind == "challenge.dayComplete")
                .Select(e => e.IntValue).ToListAsync();
            days.Should().BeEquivalentTo(new int?[] { 12, 13 });
            return true;
        });
    }

    // ---- Feed read: circle-only visibility + own events + view opt-in + privacy ----

    [Fact]
    public async Task Feed_shows_own_events_even_when_not_sharing_and_view_off()
    {
        var (email, client) = await ProvisionUser("tracker.self"); // not sharing, view OFF
        await InScopeAsync(async (db, _) =>
        {
            db.ActivityEvents.Add(new ActivityEvent
            {
                ActorEmail = email, Kind = "workout.logged", IntValue = 20, Label = "Bike", CreatedUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            return true;
        });

        var page = await Json(await client.GetAsync("/api/feed"));
        var items = page.GetProperty("items").EnumerateArray().ToList();
        items.Should().ContainSingle();
        items[0].GetProperty("kind").GetString().Should().Be("workout.logged");
        items[0].GetProperty("intValue").GetInt32().Should().Be(20);
    }

    [Fact]
    public async Task Feed_includes_a_sharing_contacts_events_only_when_viewer_opted_to_view()
    {
        var (viewerEmail, viewer) = await ProvisionUser("tracker.self");
        var (friendEmail, _) = await ProvisionUser("tracker.self");
        await MakeContacts(viewerEmail, friendEmail);
        await SetActivityPrefs(friendEmail, share: true, view: false);

        // The friend (a sharing contact) emits an event.
        await InScopeAsync(async (db, emitter) =>
        {
            await emitter.EmitAsync(friendEmail, ActivityEmitter.Kinds.ChallengeStarted);
            return true;
        });

        // Viewer has NOT opted to view ⇒ the friend's event is withheld (only own events show).
        await SetActivityPrefs(viewerEmail, share: false, view: false);
        var hidden = await Json(await viewer.GetAsync("/api/feed"));
        hidden.GetProperty("items").EnumerateArray().Should().BeEmpty();

        // Viewer opts IN to view ⇒ the sharing contact's event now appears.
        await SetActivityPrefs(viewerEmail, share: false, view: true);
        var shown = await Json(await viewer.GetAsync("/api/feed"));
        var items = shown.GetProperty("items").EnumerateArray().ToList();
        items.Should().ContainSingle();
        items[0].GetProperty("kind").GetString().Should().Be("challenge.started");
    }

    [Fact]
    public async Task Feed_excludes_a_strangers_events_even_when_they_share_and_viewer_views()
    {
        var (viewerEmail, viewer) = await ProvisionUser("tracker.self");
        var (strangerEmail, _) = await ProvisionUser("tracker.self");
        // NO contact edge between them.
        await SetActivityPrefs(strangerEmail, share: true, view: false);
        await SetActivityPrefs(viewerEmail, share: false, view: true);

        await InScopeAsync(async (db, emitter) =>
        {
            await emitter.EmitAsync(strangerEmail, ActivityEmitter.Kinds.WorkoutLogged, 40, "Swim");
            return true;
        });

        var page = await Json(await viewer.GetAsync("/api/feed"));
        page.GetProperty("items").EnumerateArray().Should().BeEmpty("a non-contact's events are never in the circle");
    }

    [Fact]
    public async Task Feed_excludes_a_contact_who_stopped_sharing()
    {
        var (viewerEmail, viewer) = await ProvisionUser("tracker.self");
        var (friendEmail, _) = await ProvisionUser("tracker.self");
        await MakeContacts(viewerEmail, friendEmail);
        await SetActivityPrefs(friendEmail, share: true, view: false);
        await SetActivityPrefs(viewerEmail, share: false, view: true);

        await InScopeAsync(async (db, emitter) =>
        {
            await emitter.EmitAsync(friendEmail, ActivityEmitter.Kinds.WorkoutLogged, 15, "Yoga");
            return true;
        });

        // The friend turns sharing OFF — their existing rows must drop out of the circle feed at read time.
        await SetActivityPrefs(friendEmail, share: false, view: false);
        var page = await Json(await viewer.GetAsync("/api/feed"));
        page.GetProperty("items").EnumerateArray().Should().BeEmpty();
    }

    [Fact]
    public async Task Feed_resolves_actor_to_id_plus_DisplayName_and_never_leaks_email()
    {
        var (viewerEmail, viewer) = await ProvisionUser("tracker.self");
        var (friendEmail, _) = await ProvisionUser("tracker.self");
        await MakeContacts(viewerEmail, friendEmail);
        await SetActivityPrefs(friendEmail, share: true, view: false);
        await SetActivityPrefs(viewerEmail, share: false, view: true);

        // Give the friend a real name + a FirstName display mode so the formatted name is deterministic.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
            var f = await db.Users.FirstAsync(u => u.Email == friendEmail);
            f.Name = "Jordan Rivera";
            f.DisplayNameMode = DisplayNameMode.FirstName;
            await db.SaveChangesAsync();
        }
        var friendId = await UserIdFor(friendEmail);

        await InScopeAsync(async (db, emitter) =>
        {
            await emitter.EmitAsync(friendEmail, ActivityEmitter.Kinds.WorkoutLogged, 30, "Run");
            return true;
        });

        var resp = await viewer.GetAsync("/api/feed");
        var raw = await resp.Content.ReadAsStringAsync();
        // No email anywhere in the payload (defense-in-depth check on the raw JSON).
        raw.Should().NotContain(friendEmail);
        raw.Should().NotContain("@test.local");

        var item = (await Json(resp)).GetProperty("items").EnumerateArray().Single();
        item.GetProperty("actorUserId").GetInt32().Should().Be(friendId);
        item.GetProperty("actorName").GetString().Should().Be("Jordan");        // FirstName mode applied
        item.TryGetProperty("actorEmail", out _).Should().BeFalse();             // never on the wire
    }

    // ---- Real emit call sites ----

    [Fact]
    public async Task Logging_a_workout_emits_a_workout_event_for_a_sharing_user()
    {
        var (email, client) = await ProvisionUser("tracker.self");
        await SetActivityPrefs(email, share: true, view: true);

        var res = await client.PostAsJsonAsync("/api/tracker/exercise", new
        {
            date = Today, name = "Treadmill", durationMin = 25, caloriesBurned = 200,
        });
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var item = (await Json(await client.GetAsync("/api/feed"))).GetProperty("items").EnumerateArray().Single();
        item.GetProperty("kind").GetString().Should().Be("workout.logged");
        item.GetProperty("intValue").GetInt32().Should().Be(25);
        item.GetProperty("label").GetString().Should().Be("Treadmill");
    }

    [Fact]
    public async Task Logging_a_workout_emits_nothing_for_a_non_sharing_user()
    {
        var (email, client) = await ProvisionUser("tracker.self"); // not sharing
        var res = await client.PostAsJsonAsync("/api/tracker/exercise", new
        {
            date = Today, name = "Treadmill", durationMin = 25, caloriesBurned = 200,
        });
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await InScopeAsync(async (db, _) =>
        {
            (await db.ActivityEvents.AsNoTracking().AnyAsync(e => e.ActorEmail == email)).Should().BeFalse();
            return true;
        });
    }

    [Fact]
    public async Task Starting_a_challenge_emits_a_started_event()
    {
        var (email, client) = await ProvisionUser("tracker.self");
        await SetActivityPrefs(email, share: true, view: true);

        (await client.PostAsJsonAsync("/api/challenge", new { startDate = Today })).StatusCode.Should().Be(HttpStatusCode.OK);

        var kinds = (await Json(await client.GetAsync("/api/feed"))).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("kind").GetString()).ToList();
        kinds.Should().Contain("challenge.started");
    }

    [Fact]
    public async Task Hydration_goal_crossing_emits_once()
    {
        var (email, client) = await ProvisionUser("tracker.self");
        await SetActivityPrefs(email, share: true, view: true);

        // Set a small hydration goal so two drinks cross it.
        (await client.PutAsJsonAsync("/api/tracker/profile", new { hydrationGoalMl = 500 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // First drink (300) is below goal — no event yet.
        (await client.PostAsJsonAsync("/api/tracker/hydration", new { date = Today, amountMl = 300 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        // Second drink (300) crosses 500 — one goal-hit event.
        (await client.PostAsJsonAsync("/api/tracker/hydration", new { date = Today, amountMl = 300 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        // Third drink (300) is already over — must NOT re-emit.
        (await client.PostAsJsonAsync("/api/tracker/hydration", new { date = Today, amountMl = 300 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        await InScopeAsync(async (db, _) =>
        {
            var hits = await db.ActivityEvents.AsNoTracking()
                .CountAsync(e => e.ActorEmail == email && e.Kind == "hydration.goalHit");
            hits.Should().Be(1, "the goal-hit event fires only on the crossing, never on every drink");
            return true;
        });
    }
}
