using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ccusage.Api.Data;
using Ccusage.Api.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ccusage.Api.Tests.Integration;

/// <summary>
/// Journal & Mood (/api/journal) — a private owner day-log. Covers:
/// <list type="bullet">
///   <item>GATING: tracker.self required (403 without), auth required (401).</item>
///   <item>OWNER-SCOPE: a caller only sees/edits their OWN entries; another user's never appear / can't be cleared.</item>
///   <item>PARTIAL upsert preserves unspecified fields; one row per day.</item>
///   <item>FREE-TEXT PRIVACY: the weekly reflection (always 200, plain floor when AI off) NEVER echoes the raw
///   reflection/gratitude text — only aggregate mood/energy/tag frequencies.</item>
/// </list>
/// Each test provisions fresh users so they're order-independent.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class JournalTests(WebAppFactory factory)
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
        var email = $"journal-{Guid.NewGuid():N}@test.local";
        var res = await Admin().PostAsJsonAsync("/api/users", new { email, isEnabled = true, permissions });
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        return (email, Client(email));
    }

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        await resp.Content.ReadFromJsonAsync<JsonElement>();

    private async Task<int> EntryCountFor(string email)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
        var lower = email.ToLowerInvariant();
        return await db.JournalEntries.AsNoTracking().CountAsync(j => j.UserEmail == lower);
    }

    private static readonly string Today = DateTime.UtcNow.ToString("yyyy-MM-dd");

    // ---- Gating ----

    [Fact]
    public async Task Journal_requires_tracker_self()
    {
        var (_, plain) = await ProvisionUser("dashboard.view");
        (await plain.GetAsync("/api/journal/")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await plain.PutAsJsonAsync("/api/journal/day", new { date = Today, mood = "good" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await plain.GetAsync("/api/journal/reflection")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Journal_requires_authentication()
    {
        var anon = factory.CreateClient();
        (await anon.GetAsync("/api/journal/")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anon.PutAsJsonAsync("/api/journal/day", new { date = Today })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- Upsert + partial preserve ----

    [Fact]
    public async Task Day_upsert_preserves_unspecified_fields()
    {
        var (email, owner) = await ProvisionUser("tracker.self");

        var first = await owner.PutAsJsonAsync("/api/journal/day", new
        {
            date = "2026-06-10",
            mood = "good",
            energy = 4,
            tags = new[] { "work", "exercise" },
            gratitudeText = "grateful for coffee",
            reflectionText = "a solid day overall",
        });
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var b1 = await Json(first);
        b1.GetProperty("mood").GetString().Should().Be("good");
        b1.GetProperty("energy").GetInt32().Should().Be(4);
        b1.GetProperty("tags").GetArrayLength().Should().Be(2);
        b1.GetProperty("gratitudeText").GetString().Should().Be("grateful for coffee");

        // PARTIAL: change ONLY the mood — everything else preserved.
        var second = await owner.PutAsJsonAsync("/api/journal/day", new { date = "2026-06-10", mood = "great" });
        var b2 = await Json(second);
        b2.GetProperty("mood").GetString().Should().Be("great");
        b2.GetProperty("energy").GetInt32().Should().Be(4);                       // preserved
        b2.GetProperty("tags").GetArrayLength().Should().Be(2);                   // preserved
        b2.GetProperty("reflectionText").GetString().Should().Be("a solid day overall"); // preserved

        (await EntryCountFor(email)).Should().Be(1); // upsert, not insert
    }

    [Fact]
    public async Task Mood_numeric_maps_onto_the_vocabulary_and_unknown_tags_drop()
    {
        var (_, owner) = await ProvisionUser("tracker.self");
        var res = await owner.PutAsJsonAsync("/api/journal/day", new
        {
            date = "2026-06-11",
            mood = "5", // numeric → "great"
            tags = new[] { "work", "not-a-real-tag", "work" }, // unknown dropped, dup removed
        });
        var b = await Json(res);
        b.GetProperty("mood").GetString().Should().Be("great");
        b.GetProperty("tags").GetArrayLength().Should().Be(1);
        b.GetProperty("tags")[0].GetString().Should().Be("work");
    }

    [Fact]
    public async Task Day_rejects_bad_date_and_delete_without_date()
    {
        var (_, owner) = await ProvisionUser("tracker.self");
        (await owner.PutAsJsonAsync("/api/journal/day", new { date = "1990-01-01", mood = "ok" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await owner.DeleteAsync("/api/journal/day")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Delete_clears_the_day()
    {
        var (email, owner) = await ProvisionUser("tracker.self");
        await owner.PutAsJsonAsync("/api/journal/day", new { date = "2026-06-14", mood = "low" });
        (await EntryCountFor(email)).Should().Be(1);

        (await owner.DeleteAsync("/api/journal/day?date=2026-06-14")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await EntryCountFor(email)).Should().Be(0);
        (await owner.DeleteAsync("/api/journal/day?date=2026-06-14")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- Owner-scope ----

    [Fact]
    public async Task A_caller_cannot_read_or_clear_another_users_entries()
    {
        var (aliceEmail, alice) = await ProvisionUser("tracker.self");
        var (_, bob) = await ProvisionUser("tracker.self");

        await alice.PutAsJsonAsync("/api/journal/day",
            new { date = "2026-06-15", mood = "low", reflectionText = "alice-private-secret" });

        // Bob's own GET never contains Alice's row.
        var bobBody = await Json(await bob.GetAsync("/api/journal/"));
        bobBody.GetProperty("entries").GetArrayLength().Should().Be(0);
        var bobRaw = await (await bob.GetAsync("/api/journal/")).Content.ReadAsStringAsync();
        bobRaw.Should().NotContain("alice-private-secret");

        // Bob upserting the same date writes only to HIS row; Alice's stays untouched.
        await bob.PutAsJsonAsync("/api/journal/day", new { date = "2026-06-15", mood = "great" });
        var aliceBody = await Json(await alice.GetAsync("/api/journal/"));
        aliceBody.GetProperty("entries")[0].GetProperty("mood").GetString().Should().Be("low");
        aliceBody.GetProperty("entries")[0].GetProperty("reflectionText").GetString().Should().Be("alice-private-secret");

        // Bob DELETE clears only his row; Alice's remains.
        (await bob.DeleteAsync("/api/journal/day?date=2026-06-15")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await EntryCountFor(aliceEmail)).Should().Be(1);
    }

    // ---- Copy an entry to another day (POST /journal/{id}/copy; COPY not MOVE; owner-only; IDOR-guarded) ----

    /// <summary>Today in the app's display timezone — the SAME "today" the journal endpoints use.</summary>
    private async Task<DateOnly> DisplayTzToday()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
        return await Ccusage.Api.Services.TrackerVisibility.DisplayTzTodayAsync(db, CancellationToken.None);
    }

    [Fact]
    public async Task Copy_entry_recreates_mood_tags_and_text_on_the_target_day_and_leaves_the_source_untouched()
    {
        var (email, owner) = await ProvisionUser("tracker.self");
        var today = await DisplayTzToday();
        var from = today.AddDays(-2).ToString("yyyy-MM-dd");
        var target = today.AddDays(-1).ToString("yyyy-MM-dd");

        var src = await Json(await owner.PutAsJsonAsync("/api/journal/day", new
        {
            date = from,
            mood = "good",
            energy = 4,
            tags = new[] { "work", "exercise" },
            gratitudeText = "grateful-text",
            reflectionText = "reflection-text",
        }));
        var srcId = src.GetProperty("id").GetInt32();

        var res = await owner.PostAsJsonAsync($"/api/journal/{srcId}/copy", new { targetDate = target });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var copy = await Json(res);
        copy.GetProperty("date").GetString().Should().Be(target);
        copy.GetProperty("mood").GetString().Should().Be("good");
        copy.GetProperty("energy").GetInt32().Should().Be(4);
        copy.GetProperty("tags").GetArrayLength().Should().Be(2);
        copy.GetProperty("gratitudeText").GetString().Should().Be("grateful-text");
        copy.GetProperty("reflectionText").GetString().Should().Be("reflection-text");

        // COPY not MOVE: the source day still exists, unchanged — and there are now TWO rows.
        (await EntryCountFor(email)).Should().Be(2);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
        var srcRow = await db.JournalEntries.AsNoTracking().FirstAsync(j => j.Id == srcId);
        srcRow.Mood.Should().Be("good");
        srcRow.GratitudeText.Should().Be("grateful-text");
    }

    [Fact]
    public async Task Copy_entry_with_a_foreign_id_is_a_404_no_op_and_never_writes_to_the_callers_day()
    {
        var (aliceEmail, alice) = await ProvisionUser("tracker.self");
        var (bobEmail, bob) = await ProvisionUser("tracker.self");
        var today = await DisplayTzToday();
        var aliceDate = today.AddDays(-3).ToString("yyyy-MM-dd");

        var aliceEntry = await Json(await alice.PutAsJsonAsync("/api/journal/day",
            new { date = aliceDate, mood = "low", reflectionText = "alice-secret" }));
        var aliceId = aliceEntry.GetProperty("id").GetInt32();

        // Bob tries to copy Alice's entry onto his own day — the owner-scoped WHERE never matches → 404.
        var target = today.AddDays(-1).ToString("yyyy-MM-dd");
        (await bob.PostAsJsonAsync($"/api/journal/{aliceId}/copy", new { targetDate = target }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Bob's day was never written; Alice's entry is untouched.
        (await EntryCountFor(bobEmail)).Should().Be(0);
        (await EntryCountFor(aliceEmail)).Should().Be(1);
        var bobRaw = await (await bob.GetAsync("/api/journal/")).Content.ReadAsStringAsync();
        bobRaw.Should().NotContain("alice-secret");
    }

    [Fact]
    public async Task Copy_entry_onto_an_existing_day_overwrites_in_place_not_duplicates()
    {
        var (email, owner) = await ProvisionUser("tracker.self");
        var today = await DisplayTzToday();
        var from = today.AddDays(-2).ToString("yyyy-MM-dd");
        var target = today.AddDays(-1).ToString("yyyy-MM-dd");

        var src = await Json(await owner.PutAsJsonAsync("/api/journal/day",
            new { date = from, mood = "great", tags = new[] { "rest" } }));
        var srcId = src.GetProperty("id").GetInt32();
        await owner.PutAsJsonAsync("/api/journal/day", new { date = target, mood = "rough" }); // pre-existing target

        var copy = await Json(await owner.PostAsJsonAsync($"/api/journal/{srcId}/copy", new { targetDate = target }));
        copy.GetProperty("mood").GetString().Should().Be("great"); // overwritten from the source

        (await EntryCountFor(email)).Should().Be(2); // source + the one (upserted) target — no duplicate
    }

    [Fact]
    public async Task Copy_entry_requires_tracker_self_and_auth_and_rejects_copy_onto_same_day()
    {
        var (_, plain) = await ProvisionUser("dashboard.view");
        (await plain.PostAsJsonAsync("/api/journal/1/copy", new { targetDate = Today }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var anon = factory.CreateClient();
        (await anon.PostAsJsonAsync("/api/journal/1/copy", new { targetDate = Today }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Copying an entry onto its OWN day is a 400 (pick a different day).
        var (_, owner) = await ProvisionUser("tracker.self");
        var entry = await Json(await owner.PutAsJsonAsync("/api/journal/day", new { date = Today, mood = "ok" }));
        var entryId = entry.GetProperty("id").GetInt32();
        (await owner.PostAsJsonAsync($"/api/journal/{entryId}/copy", new { targetDate = Today }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- Free-text privacy: reflection never echoes raw gratitude/reflection text ----

    [Fact]
    public async Task Reflection_falls_back_to_plain_and_never_echoes_raw_free_text()
    {
        var (_, owner) = await ProvisionUser("tracker.self", "tracker.ai");

        // Log a few recent days with SECRET free-text + aggregable mood/energy/tags.
        for (var i = 0; i < 3; i++)
        {
            var date = DateTime.UtcNow.AddDays(-i).ToString("yyyy-MM-dd");
            await owner.PutAsJsonAsync("/api/journal/day", new
            {
                date,
                mood = "good",
                energy = 4,
                tags = new[] { "work" },
                gratitudeText = $"SECRET-GRATITUDE-{i}-do-not-leak",
                reflectionText = $"SECRET-REFLECTION-{i}-do-not-leak",
            });
        }

        // Gemini is unconfigured in the test host → ALWAYS 200 with the deterministic plain floor.
        var res = await owner.GetAsync("/api/journal/reflection");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(res);
        body.GetProperty("fellBackToPlain").GetBoolean().Should().BeTrue();

        var note = body.GetProperty("note").GetString();
        note.Should().NotBeNullOrWhiteSpace();
        // The reflection (the ONLY journal-AI surface) must NEVER carry the raw free-text — only aggregates.
        note!.Should().NotContain("SECRET-GRATITUDE");
        note.Should().NotContain("SECRET-REFLECTION");
        note.Should().NotContain("do-not-leak");

        var raw = await res.Content.ReadAsStringAsync();
        raw.Should().NotContain("SECRET-GRATITUDE");
        raw.Should().NotContain("SECRET-REFLECTION");
    }

    [Fact]
    public async Task Reflection_is_200_with_a_floor_even_with_no_entries()
    {
        var (_, owner) = await ProvisionUser("tracker.self");
        var res = await owner.GetAsync("/api/journal/reflection");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(res)).GetProperty("note").GetString().Should().NotBeNullOrWhiteSpace();
    }
}
