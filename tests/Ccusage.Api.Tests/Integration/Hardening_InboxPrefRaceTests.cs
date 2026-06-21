using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;

namespace Ccusage.Api.Tests.Integration;

/// <summary>
/// Hardening regression (inbox-pref-race): the first GET /api/inbox/preferences for a user creates a
/// defaults <c>NotificationPreference</c> row via read-modify-write. Under concurrency two callers can
/// both miss the row and both try to INSERT; the unique index on <c>UserEmail</c> makes one INSERT
/// fail. Before the fix that surfaced as a 500; the endpoint now catches the unique violation, re-reads
/// the winning row, and returns it. Every concurrent caller must therefore get 200 + the defaults.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class Hardening_InboxPrefRaceTests(WebAppFactory factory)
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

    [Fact]
    public async Task Concurrent_first_reads_of_preferences_all_succeed_without_unique_violation_500()
    {
        // Fresh user with no preferences row yet (chat.read gates the inbox).
        var email = $"prefrace-{Guid.NewGuid():N}@test.local";
        (await Admin().PostAsJsonAsync("/api/users", new { email, isEnabled = true, permissions = new[] { "chat.read" } }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        // Fire many simultaneous first-reads on independent clients so several race the insert.
        var clients = Enumerable.Range(0, 8).Select(_ => Client(email)).ToArray();
        var responses = await Task.WhenAll(clients.Select(c => c.GetAsync("/api/inbox/preferences")));

        // None may 500: the losing inserter must fall back to the winning row, not bubble a unique violation.
        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);

        // And every caller sees the same canonical defaults.
        foreach (var r in responses)
        {
            var dto = await r.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            dto.GetProperty("notifyDirectMessages").GetBoolean().Should().BeTrue();
            dto.GetProperty("notifyChannelMessages").GetBoolean().Should().BeFalse();
            dto.GetProperty("surfaceBrowser").GetBoolean().Should().BeFalse();
        }
    }
}
