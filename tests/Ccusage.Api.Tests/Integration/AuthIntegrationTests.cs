using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Ccusage.Api.Tests.Integration;

public class AuthIntegrationTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    private const string Summary = "/api/usage/summary?groupBy=day";

    private HttpClient Client(string? email = null)
    {
        var c = factory.CreateClient();
        if (email is not null)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestJwt.For(email));
        return c;
    }

    [Fact]
    public async Task Health_is_public()
        => (await Client().GetAsync("/api/health")).StatusCode.Should().Be(HttpStatusCode.OK);

    [Fact]
    public async Task Data_endpoints_require_authentication()
        => (await Client().GetAsync(Summary)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

    [Fact]
    public async Task Tampered_token_is_unauthorized()
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestJwt.For(WebAppFactory.AdminEmail, "a-totally-different-wrong-key-32-bytes-minimum!"));
        (await c.GetAsync(Summary)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Valid_token_for_unknown_user_is_forbidden()
        => (await Client("ghost@test.local").GetAsync(Summary)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

    [Fact]
    public async Task Seeded_admin_can_read_and_manage()
    {
        var c = Client(WebAppFactory.AdminEmail);
        (await c.GetAsync(Summary)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await c.GetAsync("/api/users")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Viewer_is_denied_users_and_sync()
    {
        var email = $"viewer-{Guid.NewGuid():N}@test.local";
        await Client(WebAppFactory.AdminEmail).PostAsJsonAsync("/api/users",
            new { email, isEnabled = true, permissions = new[] { "dashboard.view" } });

        var viewer = Client(email);
        (await viewer.GetAsync(Summary)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.GetAsync("/api/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.PostAsync("/api/sync", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Disabling_a_user_revokes_access_on_the_next_request()
    {
        var admin = Client(WebAppFactory.AdminEmail);
        var email = $"revoke-{Guid.NewGuid():N}@test.local";
        var created = await admin.PostAsJsonAsync("/api/users",
            new { email, isEnabled = true, permissions = new[] { "dashboard.view" } });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var viewer = Client(email);
        (await viewer.GetAsync(Summary)).StatusCode.Should().Be(HttpStatusCode.OK);

        await admin.PutAsJsonAsync($"/api/users/{id}",
            new { isEnabled = false, permissions = new[] { "dashboard.view" } });

        // Same (still-valid) token, but the DB now says disabled -> denied immediately.
        (await viewer.GetAsync(Summary)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Cannot_remove_the_last_administrator()
    {
        var admin = Client(WebAppFactory.AdminEmail);
        var users = await (await admin.GetAsync("/api/users")).Content.ReadFromJsonAsync<JsonElement>();
        var adminId = users.EnumerateArray()
            .First(u => u.GetProperty("email").GetString() == WebAppFactory.AdminEmail)
            .GetProperty("id").GetInt32();

        var res = await admin.PutAsJsonAsync($"/api/users/{adminId}",
            new { isEnabled = true, permissions = new[] { "dashboard.view" } }); // drops users.manage
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Csv_export_requires_authentication()
        => (await Client().GetAsync("/api/usage/records.csv")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

    [Fact]
    public async Task Admin_can_export_records_as_csv_with_header()
    {
        var res = await Client(WebAppFactory.AdminEmail).GetAsync("/api/usage/records.csv");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        var body = await res.Content.ReadAsStringAsync();
        body.Should().StartWith("date,source,model,project,type,input,output,cache_read,cache_5m,cache_1h,total,cost_usd");
    }

    [Fact]
    public async Task User_management_actions_are_written_to_the_audit_log()
    {
        var admin = Client(WebAppFactory.AdminEmail);
        var email = $"audited-{Guid.NewGuid():N}@test.local";
        await admin.PostAsJsonAsync("/api/users",
            new { email, isEnabled = true, permissions = new[] { "dashboard.view" } });

        var audit = await (await admin.GetAsync("/api/audit")).Content.ReadFromJsonAsync<JsonElement>();
        audit.EnumerateArray().ToList().Should().Contain(e =>
            e.GetProperty("action").GetString() == "user.created" &&
            e.GetProperty("targetEmail").GetString() == email &&
            e.GetProperty("actorEmail").GetString() == WebAppFactory.AdminEmail);
    }

    [Fact]
    public async Task Audit_log_is_gated_by_users_manage()
    {
        var email = $"viewer-audit-{Guid.NewGuid():N}@test.local";
        await Client(WebAppFactory.AdminEmail).PostAsJsonAsync("/api/users",
            new { email, isEnabled = true, permissions = new[] { "dashboard.view" } });

        (await Client(email).GetAsync("/api/audit")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- Google sign-in: identity verification + subject pinning ----
    // (FakeGoogleTokenValidator reads the posted idToken as "email|subject".)

    private Task<HttpResponseMessage> GoogleLogin(string idToken)
        => factory.CreateClient().PostAsJsonAsync("/api/auth/google", new { idToken });

    [Fact]
    public async Task Google_login_with_an_invalid_token_is_unauthorized()
        => (await GoogleLogin("invalid")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

    [Fact]
    public async Task Google_login_for_an_unprovisioned_email_is_forbidden()
        => (await GoogleLogin($"ghost-{Guid.NewGuid():N}@test.local|sub-x"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

    [Fact]
    public async Task Google_login_pins_the_account_to_its_google_subject()
    {
        var email = $"glogin-{Guid.NewGuid():N}@test.local";
        await Client(WebAppFactory.AdminEmail).PostAsJsonAsync("/api/users",
            new { email, isEnabled = true, permissions = new[] { "dashboard.view" } });

        // First login binds the Google subject and returns an app token.
        var first = await GoogleLogin($"{email}|sub-A");
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString().Should().NotBeNullOrEmpty();

        // The same Google account logs in again fine.
        (await GoogleLogin($"{email}|sub-A")).StatusCode.Should().Be(HttpStatusCode.OK);

        // A different Google account presenting the same (now-bound) email is rejected.
        (await GoogleLogin($"{email}|sub-B")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
