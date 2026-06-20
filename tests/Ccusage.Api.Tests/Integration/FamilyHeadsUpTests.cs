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
/// Family Hub F6b — calendar EVENT HEADS-UPS: the settings round-trip on /api/family/settings
/// (EventHeadsUpEnabled + EventHeadsUpLeadMinutes) and the announce-once core
/// (<see cref="FamilyReminderService.AnnounceEventsForHouseholdAsync"/>). The real Google Calendar is NEVER
/// called here — the announce logic is driven directly with a FAKED upcoming-event list and a seeded
/// <see cref="FamilyEventAnnouncement"/>, asserting: an upcoming event posts ONE familyHeadsUp bell per
/// member + records the announcement; a second run for the SAME event announces nothing more (announce-once);
/// and an already-seeded announcement is skipped. Each test provisions fresh users so they're
/// order-independent.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class FamilyHeadsUpTests(WebAppFactory factory)
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

    private async Task<(string email, HttpClient client, int id)> ProvisionUser(params string[] permissions)
    {
        var email = $"famhu-{Guid.NewGuid():N}@test.local";
        var res = await Admin().PostAsJsonAsync("/api/users", new { email, isEnabled = true, permissions });
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await Json(res)).GetProperty("id").GetInt32();
        return (email, Client(email), id);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        await resp.Content.ReadFromJsonAsync<JsonElement>();

    private async Task<int> HouseholdIdFor(int userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
        return await db.HouseholdMembers.AsNoTracking().Where(m => m.UserId == userId)
            .Select(m => m.HouseholdId).FirstAsync();
    }

    private async Task<int> NotificationCountFor(string email, NotificationType type)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
        return await db.Notifications.CountAsync(n => n.RecipientEmail == email.ToLowerInvariant() && n.Type == type);
    }

    private async Task<int> AnnouncementCountFor(int householdId, string googleEventId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
        return await db.FamilyEventAnnouncements
            .CountAsync(a => a.HouseholdId == householdId && a.GoogleEventId == googleEventId);
    }

    /// <summary>
    /// Drive the Google-free announce core directly with a faked upcoming-event list (no real Google). Mirrors
    /// what the background tick would do for one household after listing a connected member's events.
    /// </summary>
    private async Task<int> AnnounceFaked(
        int householdId, IReadOnlyList<int> memberIds,
        IEnumerable<FamilyReminderService.UpcomingEvent> events, DateTime now, DateTime windowEnd)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
        var notifier = scope.ServiceProvider.GetRequiredService<ChatNotificationService>();
        var briefing = scope.ServiceProvider.GetRequiredService<FamilyBriefingService>();
        return await FamilyReminderService.AnnounceEventsForHouseholdAsync(
            db, notifier, briefing, householdId, memberIds, events, now, windowEnd, default);
    }

    // =====================================================================================
    // SETTINGS round-trip
    // =====================================================================================

    [Fact]
    public async Task Heads_up_settings_round_trip_on_settings_endpoint()
    {
        var (_, owner, _) = await ProvisionUser("family.use");
        await owner.GetAsync("/api/family/household");

        // Defaults: off, 15-minute lead.
        var initial = await Json(await owner.GetAsync("/api/family/settings"));
        initial.GetProperty("eventHeadsUpEnabled").GetBoolean().Should().BeFalse();
        initial.GetProperty("eventHeadsUpLeadMinutes").GetInt32().Should().Be(15);

        // Enable + change the lead.
        var put = await owner.PutAsJsonAsync("/api/family/settings", new
        {
            eventHeadsUpEnabled = true, eventHeadsUpLeadMinutes = 30,
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var saved = await Json(put);
        saved.GetProperty("eventHeadsUpEnabled").GetBoolean().Should().BeTrue();
        saved.GetProperty("eventHeadsUpLeadMinutes").GetInt32().Should().Be(30);

        // And it persists on a fresh read.
        var reread = await Json(await owner.GetAsync("/api/family/settings"));
        reread.GetProperty("eventHeadsUpEnabled").GetBoolean().Should().BeTrue();
        reread.GetProperty("eventHeadsUpLeadMinutes").GetInt32().Should().Be(30);
    }

    [Fact]
    public async Task Heads_up_lead_minutes_is_validated()
    {
        var (_, owner, _) = await ProvisionUser("family.use");
        await owner.GetAsync("/api/family/household");

        (await owner.PutAsJsonAsync("/api/family/settings", new { eventHeadsUpLeadMinutes = 0 }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await owner.PutAsJsonAsync("/api/family/settings", new { eventHeadsUpLeadMinutes = 999 }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // =====================================================================================
    // ANNOUNCE-ONCE — drive the core with a faked event list (no real Google)
    // =====================================================================================

    [Fact]
    public async Task Upcoming_event_announces_once_bells_every_member_and_records_it()
    {
        var (ownerEmail, owner, ownerId) = await ProvisionUser("family.use");
        var (bobEmail, _, bobId) = await ProvisionUser("family.use");
        await owner.GetAsync("/api/family/household");
        await owner.PostAsJsonAsync("/api/family/household/members", new { userId = bobId });
        var householdId = await HouseholdIdFor(ownerId);
        var memberIds = new[] { ownerId, bobId };

        var now = DateTime.UtcNow;
        var eventId = $"evt-{Guid.NewGuid():N}";
        var events = new[]
        {
            new FamilyReminderService.UpcomingEvent(eventId, "Soccer practice", now.AddMinutes(10)),
        };

        var ownerBefore = await NotificationCountFor(ownerEmail, NotificationType.FamilyHeadsUp);
        var bobBefore = await NotificationCountFor(bobEmail, NotificationType.FamilyHeadsUp);

        // First run: announce exactly one event.
        var announced = await AnnounceFaked(householdId, memberIds, events, now, now.AddMinutes(15));
        announced.Should().Be(1);

        // Every member got exactly one heads-up bell, and the announcement was recorded once.
        (await NotificationCountFor(ownerEmail, NotificationType.FamilyHeadsUp)).Should().Be(ownerBefore + 1);
        (await NotificationCountFor(bobEmail, NotificationType.FamilyHeadsUp)).Should().Be(bobBefore + 1);
        (await AnnouncementCountFor(householdId, eventId)).Should().Be(1);

        // Second run for the SAME event announces nothing more (announce-once) — no extra bells, no extra row.
        var again = await AnnounceFaked(householdId, memberIds, events, now, now.AddMinutes(15));
        again.Should().Be(0);
        (await NotificationCountFor(ownerEmail, NotificationType.FamilyHeadsUp)).Should().Be(ownerBefore + 1);
        (await AnnouncementCountFor(householdId, eventId)).Should().Be(1);
    }

    [Fact]
    public async Task A_seeded_announcement_is_skipped()
    {
        var (ownerEmail, owner, ownerId) = await ProvisionUser("family.use");
        await owner.GetAsync("/api/family/household");
        var householdId = await HouseholdIdFor(ownerId);
        var now = DateTime.UtcNow;
        var eventId = $"evt-{Guid.NewGuid():N}";

        // Seed the announcement BEFORE the run — the core must skip it (announce-once).
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
            db.FamilyEventAnnouncements.Add(new FamilyEventAnnouncement
            {
                HouseholdId = householdId, GoogleEventId = eventId,
                EventStartUtc = now.AddMinutes(10), AnnouncedUtc = now.AddMinutes(-1),
            });
            await db.SaveChangesAsync();
        }

        var before = await NotificationCountFor(ownerEmail, NotificationType.FamilyHeadsUp);
        var events = new[] { new FamilyReminderService.UpcomingEvent(eventId, "Dentist", now.AddMinutes(10)) };

        var announced = await AnnounceFaked(householdId, new[] { ownerId }, events, now, now.AddMinutes(15));
        announced.Should().Be(0);
        (await NotificationCountFor(ownerEmail, NotificationType.FamilyHeadsUp)).Should().Be(before);
        (await AnnouncementCountFor(householdId, eventId)).Should().Be(1);
    }

    [Fact]
    public async Task An_event_outside_the_lead_window_or_already_started_is_not_announced()
    {
        var (ownerEmail, owner, ownerId) = await ProvisionUser("family.use");
        await owner.GetAsync("/api/family/household");
        var householdId = await HouseholdIdFor(ownerId);
        var now = DateTime.UtcNow;

        var farFuture = new FamilyReminderService.UpcomingEvent($"far-{Guid.NewGuid():N}", "Next week", now.AddMinutes(60));
        var alreadyStarted = new FamilyReminderService.UpcomingEvent($"past-{Guid.NewGuid():N}", "In progress", now.AddMinutes(-5));

        var before = await NotificationCountFor(ownerEmail, NotificationType.FamilyHeadsUp);

        // windowEnd is now+15; both candidates are outside [now, now+15] → nothing announced.
        var announced = await AnnounceFaked(householdId, new[] { ownerId },
            new[] { farFuture, alreadyStarted }, now, now.AddMinutes(15));
        announced.Should().Be(0);
        (await NotificationCountFor(ownerEmail, NotificationType.FamilyHeadsUp)).Should().Be(before);
    }
}
