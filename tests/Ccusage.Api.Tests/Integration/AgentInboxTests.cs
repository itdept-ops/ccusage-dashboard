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
/// The AGENT INBOX / "Overnight" surface (<c>/api/agents/inbox</c>). Covers: agents.use gating; the inbox
/// returns ONLY the caller's OWN agent deliveries (owner scope — another user's items never appear); ONLY
/// agent-produced (AgentNudge) types are included (a chat/family notification on the same bell is excluded);
/// period grouping is correct (overnight / today / earlier) in the display timezone; mark-handled flips the
/// EXISTING read flag and is owner-guarded (a foreign id is a no-op); and no email is ever on the wire.
/// Each test provisions fresh users so they're order-independent.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AgentInboxTests(WebAppFactory factory)
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
        var email = $"inbox-{Guid.NewGuid():N}@test.local";
        var res = await Admin().PostAsJsonAsync("/api/users", new { email, isEnabled = true, permissions });
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        return (email, Client(email));
    }

    /// <summary>Insert a raw notification row directly (the scheduler/composer path is exercised elsewhere; here
    /// we drive the inbox over a controlled set of rows). <paramref name="createdUtc"/> defaults to now.</summary>
    private async Task<long> SeedNotification(
        string email, NotificationType type, string text, string? link, DateTime? createdUtc = null, bool isRead = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
        var n = new Notification
        {
            RecipientEmail = email.ToLowerInvariant(),
            Type = type,
            Text = text,
            Link = link,
            IsRead = isRead,
            CreatedUtc = createdUtc ?? DateTime.UtcNow,
        };
        db.Notifications.Add(n);
        await db.SaveChangesAsync();
        return n.Id;
    }

    private async Task<JsonElement> GetInbox(HttpClient client, string? query = null)
    {
        var resp = await client.GetAsync("/api/agents/inbox" + (query ?? ""));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static IEnumerable<JsonElement> AllItems(JsonElement inbox) =>
        inbox.GetProperty("groups").EnumerateArray()
            .SelectMany(g => g.GetProperty("items").EnumerateArray());

    // =====================================================================================
    // GATING
    // =====================================================================================

    [Fact]
    public async Task Inbox_requires_agents_use()
    {
        var (_, plain) = await ProvisionUser("dashboard.view");
        (await plain.GetAsync("/api/agents/inbox")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await plain.PostAsJsonAsync("/api/agents/inbox/handle", new { ids = new long[] { 1 } }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await plain.PostAsync("/api/agents/inbox/handle-all", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_holder_with_no_agent_deliveries_gets_an_empty_inbox()
    {
        var (_, user) = await ProvisionUser("agents.use");
        var inbox = await GetInbox(user);
        inbox.GetProperty("unhandledCount").GetInt32().Should().Be(0);
        inbox.GetProperty("groups").GetArrayLength().Should().Be(0);
    }

    // =====================================================================================
    // OWNER SCOPE + TYPE FILTER
    // =====================================================================================

    [Fact]
    public async Task Inbox_returns_only_the_callers_own_agent_items()
    {
        var (mine, me) = await ProvisionUser("agents.use");
        var (_, _) = await ProvisionUser("agents.use"); // another holder
        var theirs = $"inbox-other-{Guid.NewGuid():N}@test.local";

        var myId = await SeedNotification(mine, NotificationType.AgentNudge, "Your shopping list has 2 items", "/grocery");
        await SeedNotification(theirs, NotificationType.AgentNudge, "Their budget alert", "/family/finance");

        var inbox = await GetInbox(me);
        var ids = AllItems(inbox).Select(i => i.GetProperty("id").GetInt64()).ToList();
        ids.Should().ContainSingle().Which.Should().Be(myId);
        AllItems(inbox).Single().GetProperty("summary").GetString().Should().Contain("shopping list");
    }

    [Fact]
    public async Task Inbox_includes_only_agent_produced_types()
    {
        var (email, user) = await ProvisionUser("agents.use");
        var agentId = await SeedNotification(email, NotificationType.AgentNudge, "Streak rescue", "/challenge");
        // Non-agent notifications on the SAME bell must NOT appear in the agent inbox.
        await SeedNotification(email, NotificationType.DirectMessage, "Someone: hi", "/chat?c=1");
        await SeedNotification(email, NotificationType.FamilyReminder, "Reminder due", "/family/calendar");
        await SeedNotification(email, NotificationType.Cheer, "X cheered your run", "/feed");

        var inbox = await GetInbox(user);
        var items = AllItems(inbox).ToList();
        items.Should().ContainSingle();
        items[0].GetProperty("id").GetInt64().Should().Be(agentId);
        items[0].GetProperty("agentKind").GetString().Should().Be("streakRescue");
        items[0].GetProperty("agentLabel").GetString().Should().Be("Streak Rescue");
    }

    [Fact]
    public async Task Inbox_never_exposes_an_email()
    {
        var (email, user) = await ProvisionUser("agents.use");
        await SeedNotification(email, NotificationType.AgentNudge, "Budget alert this month", "/family/finance");

        var raw = await (await user.GetAsync("/api/agents/inbox")).Content.ReadAsStringAsync();
        raw.Should().NotContain(email);
        raw.Should().NotContain("@");
    }

    // =====================================================================================
    // PERIOD GROUPING
    // =====================================================================================

    [Fact]
    public async Task Inbox_groups_items_by_period_overnight_today_earlier()
    {
        var (email, user) = await ProvisionUser("agents.use");

        // Resolve the display timezone the endpoint uses, then craft local times that land in each bucket.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
            var tz = await Ccusage.Api.Services.TrackerVisibility.DisplayTzAsync(db, CancellationToken.None);
            var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
            var today = DateOnly.FromDateTime(nowLocal.DateTime);

            // "today" = a delivery today at/after 6am local; "earlier" = a prior local day. Use fixed local
            // anchors (2am today for overnight, noon today for today, noon yesterday for earlier) → UTC.
            DateTime LocalToUtc(DateOnly d, int hour) =>
                TimeZoneInfo.ConvertTimeToUtc(d.ToDateTime(new TimeOnly(hour, 0)), tz);

            var overnightUtc = LocalToUtc(today, 2);
            var todayUtc = LocalToUtc(today, 12);
            var earlierUtc = LocalToUtc(today.AddDays(-1), 12);

            db.Notifications.AddRange(
                new Notification { RecipientEmail = email, Type = NotificationType.AgentNudge, Text = "overnight", Link = "/grocery", CreatedUtc = overnightUtc },
                new Notification { RecipientEmail = email, Type = NotificationType.AgentNudge, Text = "today", Link = "/grocery", CreatedUtc = todayUtc },
                new Notification { RecipientEmail = email, Type = NotificationType.AgentNudge, Text = "earlier", Link = "/grocery", CreatedUtc = earlierUtc });
            await db.SaveChangesAsync();
        }

        var inbox = await GetInbox(user);
        var byPeriod = inbox.GetProperty("groups").EnumerateArray()
            .ToDictionary(
                g => g.GetProperty("period").GetString()!,
                g => g.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("summary").GetString()).ToList());

        byPeriod.Should().ContainKeys("overnight", "today", "earlier");
        byPeriod["overnight"].Should().ContainSingle().Which.Should().Be("overnight");
        byPeriod["today"].Should().ContainSingle().Which.Should().Be("today");
        byPeriod["earlier"].Should().ContainSingle().Which.Should().Be("earlier");

        // Groups are ordered overnight → today → earlier.
        var order = inbox.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("period").GetString()).ToList();
        order.Should().Equal("overnight", "today", "earlier");
    }

    // =====================================================================================
    // TRIAGE (mark handled) — flips the EXISTING read flag, owner-guarded
    // =====================================================================================

    [Fact]
    public async Task Handle_flips_the_read_flag_and_is_owner_guarded()
    {
        var (mine, me) = await ProvisionUser("agents.use");
        var theirs = $"inbox-victim-{Guid.NewGuid():N}@test.local";

        var myId = await SeedNotification(mine, NotificationType.AgentNudge, "mine", "/grocery");
        var foreignId = await SeedNotification(theirs, NotificationType.AgentNudge, "theirs", "/grocery");

        // Marking MY id + a FOREIGN id handled: only mine flips; the foreign row is a silent no-op.
        var resp = await me.PostAsJsonAsync("/api/agents/inbox/handle", new { ids = new[] { myId, foreignId } });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("unhandledCount").GetInt32().Should().Be(0);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
        (await db.Notifications.AsNoTracking().FirstAsync(n => n.Id == myId)).IsRead.Should().BeTrue();
        // The foreign user's row was NEVER touched (owner guard).
        (await db.Notifications.AsNoTracking().FirstAsync(n => n.Id == foreignId)).IsRead.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_does_not_flip_a_non_agent_notification()
    {
        var (email, user) = await ProvisionUser("agents.use");
        // A chat notification the caller OWNS, but it isn't an agent item — handle must not touch it.
        var dmId = await SeedNotification(email, NotificationType.DirectMessage, "hi", "/chat?c=1");

        await user.PostAsJsonAsync("/api/agents/inbox/handle", new { ids = new[] { dmId } });

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
        (await db.Notifications.AsNoTracking().FirstAsync(n => n.Id == dmId)).IsRead.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_all_marks_every_un_triaged_agent_item_handled()
    {
        var (email, user) = await ProvisionUser("agents.use");
        await SeedNotification(email, NotificationType.AgentNudge, "a", "/grocery");
        await SeedNotification(email, NotificationType.AgentNudge, "b", "/challenge");
        await SeedNotification(email, NotificationType.AgentNudge, "c", "/family/finance", isRead: true); // already handled

        var resp = await user.PostAsync("/api/agents/inbox/handle-all", null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("unhandledCount").GetInt32().Should().Be(0);

        // unreadOnly view (?handled=false) is now empty; the full inbox still shows all three.
        AllItems(await GetInbox(user, "?handled=false")).Should().BeEmpty();
        AllItems(await GetInbox(user)).Should().HaveCount(3);
    }

    [Fact]
    public async Task Unhandled_count_reflects_only_un_triaged_agent_items()
    {
        var (email, user) = await ProvisionUser("agents.use");
        await SeedNotification(email, NotificationType.AgentNudge, "unread1", "/grocery");
        await SeedNotification(email, NotificationType.AgentNudge, "read1", "/challenge", isRead: true);
        // A non-agent unread row must NOT count toward the agent inbox badge.
        await SeedNotification(email, NotificationType.DirectMessage, "dm", "/chat?c=1");

        (await GetInbox(user)).GetProperty("unhandledCount").GetInt32().Should().Be(1);
    }
}
