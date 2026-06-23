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
/// End-to-end coverage for the display-name preference + presence prefs: a user controls how they appear
/// to everyone (default "First L."), AppearOffline truly removes them from the roster others see, the
/// opt-in status broadcasts, and no payload ever leaks an email.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class DisplayNamePresenceTests(WebAppFactory factory)
{
    private HttpClient Client(string? email = null)
    {
        var c = factory.CreateClient();
        if (email is not null)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestJwt.For(email));
        return c;
    }

    private async Task<(int Id, string Email)> CreateUser(string name)
    {
        var email = $"dn-{Guid.NewGuid():N}@test.local";
        var created = await Client(WebAppFactory.AdminEmail).PostAsJsonAsync("/api/users",
            new { email, name, isEnabled = true, permissions = new[] { "dashboard.view" } });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        return (id, email);
    }

    private async Task<JsonElement> Presence(string asEmail)
        => await (await Client(asEmail).GetAsync("/api/presence")).Content.ReadFromJsonAsync<JsonElement>();

    private static JsonElement? RowFor(JsonElement list, int userId)
        => list.EnumerateArray().Cast<JsonElement?>().FirstOrDefault(e =>
            e!.Value.TryGetProperty("userId", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt32() == userId);

    [Fact]
    public async Task Default_mode_is_first_initial_in_presence()
    {
        var (id, email) = await CreateUser("Jane Smith");
        (await Client(email).GetAsync("/api/auth/me")).EnsureSuccessStatusCode(); // go online

        var row = RowFor(await Presence(WebAppFactory.AdminEmail), id);
        row.Should().NotBeNull();
        row!.Value.GetProperty("name").GetString().Should().Be("Jane S.");
    }

    [Fact]
    public async Task Me_reports_default_prefs_and_patch_changes_how_others_see_the_name()
    {
        var (id, email) = await CreateUser("Jane Smith");

        // /me carries the caller's own prefs; default mode is firstInitial.
        var me = await (await Client(email).GetAsync("/api/auth/me")).Content.ReadFromJsonAsync<JsonElement>();
        me.GetProperty("displayNameMode").GetString().Should().Be("firstInitial");
        me.GetProperty("appearOffline").GetBoolean().Should().BeFalse();

        // Switch to full-name; others now see the full name.
        var patch = await Client(email).PatchAsJsonAsync("/api/auth/profile", new { displayNameMode = "full" });
        patch.StatusCode.Should().Be(HttpStatusCode.OK);

        var row = RowFor(await Presence(WebAppFactory.AdminEmail), id);
        row!.Value.GetProperty("name").GetString().Should().Be("Jane Smith");

        // And firstName.
        await Client(email).PatchAsJsonAsync("/api/auth/profile", new { displayNameMode = "firstName" });
        row = RowFor(await Presence(WebAppFactory.AdminEmail), id);
        row!.Value.GetProperty("name").GetString().Should().Be("Jane");
    }

    [Fact]
    public async Task Nickname_mode_shows_the_sanitized_nickname_to_others()
    {
        var (id, email) = await CreateUser("Jane Smith");
        await Client(email).PatchAsJsonAsync("/api/auth/profile",
            new { displayNameMode = "nickname", nickname = "J@J cool" });

        var row = RowFor(await Presence(WebAppFactory.AdminEmail), id);
        row!.Value.GetProperty("name").GetString().Should().Be("JJ cool"); // '@' stripped
    }

    [Fact]
    public async Task AppearOffline_removes_the_user_from_others_roster_but_not_their_own()
    {
        var (id, email) = await CreateUser("Hidden Person");
        (await Client(email).GetAsync("/api/auth/me")).EnsureSuccessStatusCode();

        await Client(email).PatchAsJsonAsync("/api/auth/profile", new { appearOffline = true });

        // The user makes another request to stay online, then we check both viewpoints.
        (await Client(email).GetAsync("/api/auth/me")).EnsureSuccessStatusCode();

        // Others (admin) do NOT see the hidden user.
        RowFor(await Presence(WebAppFactory.AdminEmail), id).Should().BeNull();

        // The hidden user still sees themselves (the app works for them).
        var ownView = await Presence(email);
        RowFor(ownView, id).Should().NotBeNull();
        RowFor(ownView, id)!.Value.GetProperty("isSelf").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Presence_status_broadcasts_and_is_sanitized()
    {
        var (id, email) = await CreateUser("Status Person");
        await Client(email).PatchAsJsonAsync("/api/auth/profile",
            new { presenceStatus = "heads-down ping me@x.com" });
        (await Client(email).GetAsync("/api/auth/me")).EnsureSuccessStatusCode();

        var row = RowFor(await Presence(WebAppFactory.AdminEmail), id);
        var status = row!.Value.GetProperty("status").GetString();
        status.Should().NotContain("@");
        status.Should().Contain("heads-down");
    }

    [Fact]
    public async Task Invalid_display_name_mode_is_rejected()
    {
        var (_, email) = await CreateUser("Jane Smith");
        var res = await Client(email).PatchAsJsonAsync("/api/auth/profile", new { displayNameMode = "sideways" });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Presence_payload_never_contains_an_email_even_with_status()
    {
        var (_, email) = await CreateUser("Jane Smith");
        await Client(email).PatchAsJsonAsync("/api/auth/profile",
            new { presenceStatus = "reach me at jane@corp.com" });
        (await Client(email).GetAsync("/api/auth/me")).EnsureSuccessStatusCode();

        var raw = await (await Client(WebAppFactory.AdminEmail).GetAsync("/api/presence")).Content.ReadAsStringAsync();
        raw.Should().NotContain("@");
        raw.Should().NotContain("jane@corp.com");
    }
}
