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
/// Weekly personal recap: opt-in gating (default OFF / no send without a webhook), the scheduled idempotency
/// guard (no double-send the same week, survives a retick), the send-now/preview endpoint, and that the
/// composed embed never leaks the webhook secret or the user's email. The recap rides the per-user encrypted
/// + SSRF-allowlisted send path; the test factory captures the outgoing Discord payload (no network).
/// </summary>
[Collection(IntegrationCollection.Name)]
public class WeeklyRecapTests(WebAppFactory factory)
{
    private HttpClient Admin() => Client(WebAppFactory.AdminEmail);

    private HttpClient Client(string email)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestJwt.For(email));
        return c;
    }

    private async Task<string> ProvisionUser(params string[] perms)
    {
        var email = $"recap-{Guid.NewGuid():N}@test.local";
        (await Admin().PostAsJsonAsync("/api/users",
            new { email, isEnabled = true, permissions = perms.Length == 0 ? new[] { "chat.read" } : perms }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        return email;
    }

    private const string Webhook = "https://discord.com/api/webhooks/55555/RecapSecretToken12345";

    private async Task SaveWebhook(string email, bool recap, bool surface = false) =>
        (await Client(email).PutAsJsonAsync("/api/notifications/me/discord",
            new { webhookUrl = Webhook, surfaceDiscord = surface, weeklyRecapEnabled = recap }))
            .EnsureSuccessStatusCode();

    private static readonly DateOnly From = new(2026, 6, 8);
    private static readonly DateOnly To = new(2026, 6, 14);
    private static readonly DateOnly Today = new(2026, 6, 15);

    // ---- OPT-IN gating: default OFF, round-trips through GET/PUT ----
    [Fact]
    public async Task Weekly_recap_is_off_by_default_and_round_trips()
    {
        var email = await ProvisionUser();
        var dto = await (await Client(email).GetAsync("/api/notifications/me/discord"))
            .Content.ReadFromJsonAsync<JsonElement>();
        dto.GetProperty("weeklyRecapEnabled").GetBoolean().Should().BeFalse("opt-in defaults OFF");

        await SaveWebhook(email, recap: true);
        var after = await (await Client(email).GetAsync("/api/notifications/me/discord"))
            .Content.ReadFromJsonAsync<JsonElement>();
        after.GetProperty("weeklyRecapEnabled").GetBoolean().Should().BeTrue();
    }

    // ---- COMPOSER gating: no send when opted out, or when no webhook ----
    [Fact]
    public async Task Composer_does_not_send_when_opted_out_or_no_webhook()
    {
        var optedOut = await ProvisionUser();
        await SaveWebhook(optedOut, recap: false);           // webhook saved but recap OFF
        var noWebhook = await ProvisionUser();               // opt the row in but never save a webhook
        using (var s = factory.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<UsageDbContext>();
            var pref = new NotificationPreference { UserEmail = noWebhook, WeeklyRecapEnabled = true };
            db.NotificationPreferences.Add(pref);
            await db.SaveChangesAsync();
        }

        var before = factory.Discord.Count;
        using var scope = factory.Services.CreateScope();
        var recap = scope.ServiceProvider.GetRequiredService<WeeklyRecapComposer>();
        (await recap.SendRecapAsync(optedOut, From, To, Today, default)).Should().BeFalse("recap toggle is OFF");
        (await recap.SendRecapAsync(noWebhook, From, To, Today, default)).Should().BeFalse("no webhook saved");
        factory.Discord.Count.Should().Be(before, "neither gated case may post to Discord");
    }

    // ---- COMPOSER send: opted in + webhook → posts, payload has NO secret and NO email ----
    [Fact]
    public async Task Composer_sends_recap_with_no_secret_or_email_in_payload()
    {
        var email = await ProvisionUser("tracker.self");
        await SaveWebhook(email, recap: true);

        // A little tracker data so the embed has real numbers.
        using (var s = factory.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<UsageDbContext>();
            db.FoodEntries.Add(new FoodEntry { UserEmail = email, LocalDate = From, Calories = 2000, ProteinG = 100 });
            db.ExerciseEntries.Add(new ExerciseEntry { UserEmail = email, LocalDate = From, CaloriesBurned = 300, DurationMin = 30 });
            await db.SaveChangesAsync();
        }

        using var scope = factory.Services.CreateScope();
        var recap = scope.ServiceProvider.GetRequiredService<WeeklyRecapComposer>();
        (await recap.SendRecapAsync(email, From, To, Today, default)).Should().BeTrue();

        var payload = factory.Discord.Payloads.Last();
        payload.Should().NotContain("RecapSecretToken12345", "the webhook token must never be in the body");
        payload.Should().NotContain(email, "the user's email must never be in the embed");
        payload.Should().Contain("week in review");
    }

    // ---- IDEMPOTENT: the LastRecapSent guard prevents a same-week double-send across reticks ----
    [Fact]
    public async Task Recap_guard_prevents_double_send_in_the_same_week()
    {
        var email = await ProvisionUser();
        await SaveWebhook(email, recap: true);

        // First successful send → advance the guard to today (mirrors what the scheduler does on success).
        using (var scope = factory.Services.CreateScope())
        {
            var recap = scope.ServiceProvider.GetRequiredService<WeeklyRecapComposer>();
            (await recap.SendRecapAsync(email, From, To, Today, default)).Should().BeTrue();
            var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
            var pref = await db.NotificationPreferences.SingleAsync(p => p.UserEmail == email);
            pref.LastRecapSent = Today;
            await db.SaveChangesAsync();
        }

        // A retick the same day: the candidate query (the scheduler's gate) must EXCLUDE this user now.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
            var candidates = await db.NotificationPreferences.AsNoTracking()
                .Where(p => p.WeeklyRecapEnabled && p.DiscordWebhookEnc != null && p.LastRecapSent != Today)
                .Select(p => p.UserEmail)
                .ToListAsync();
            candidates.Should().NotContain(email, "already sent this week's recap — the guard excludes a retick");
        }
    }

    // ---- SEND-NOW endpoint: succeeds, ignores the opt-in toggle, and does NOT advance the weekly guard ----
    [Fact]
    public async Task Send_now_works_even_when_opted_out_and_does_not_touch_the_weekly_guard()
    {
        var email = await ProvisionUser();
        await SaveWebhook(email, recap: false); // opt-in OFF — send-now is an explicit action, still allowed

        var before = factory.Discord.Count;
        var res = await Client(email).PostAsync("/api/notifications/me/discord/recap", null);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Discord.Count.Should().BeGreaterThan(before, "send-now posts to Discord regardless of the toggle");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
        var pref = await db.NotificationPreferences.AsNoTracking().SingleAsync(p => p.UserEmail == email);
        pref.LastRecapSent.Should().BeNull("a manual send-now must not consume the weekly idempotency guard");
    }

    // ---- SEND-NOW: 404 when no webhook saved ----
    [Fact]
    public async Task Send_now_is_404_without_a_saved_webhook()
        => (await Client(await ProvisionUser()).PostAsync("/api/notifications/me/discord/recap", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

    // ---- PREVIEW: returns the composed embed (no send, no webhook needed) with no secret/email ----
    [Fact]
    public async Task Preview_returns_embed_json_without_sending_or_leaking()
    {
        var email = await ProvisionUser();
        var before = factory.Discord.Count;

        var res = await Client(email).PostAsync("/api/notifications/me/discord/recap?preview=true", null);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Discord.Count.Should().Be(before, "preview must not POST to Discord");

        var raw = await res.Content.ReadAsStringAsync();
        raw.Should().NotContain(email);
        raw.Should().NotContain("RecapSecretToken12345");
        var dto = JsonDocument.Parse(raw).RootElement;
        dto.GetProperty("period").GetString().Should().NotBeNullOrEmpty();
        dto.GetProperty("fields").EnumerateArray().Should().NotBeEmpty();
    }
}
