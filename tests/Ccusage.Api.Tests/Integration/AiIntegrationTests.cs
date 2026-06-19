using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;

namespace Ccusage.Api.Tests.Integration;

/// <summary>
/// AI-assist endpoints (<c>/api/ai</c>): they are gated behind <c>tracker.self</c> exactly like the rest
/// of the tracker (401 anonymous, 403 without the permission), and they degrade GRACEFULLY to 503 when
/// Gemini is unconfigured — which the test host always is, because no <c>Gemini__ApiKey</c> is set (and
/// <c>SkipLocalSettings=true</c> keeps the local secrets file out). The real Gemini API is NEVER called
/// from tests: the 503-when-unconfigured branch is reached before any HTTP request is built.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AiIntegrationTests(WebAppFactory factory)
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
        var email = $"ai-{Guid.NewGuid():N}@test.local";
        var res = await Admin().PostAsJsonAsync("/api/users", new { email, isEnabled = true, permissions });
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        return (email, Client(email));
    }

    // ---- Auth gating ----

    [Fact]
    public async Task Ai_endpoints_require_authentication()
    {
        var anon = factory.CreateClient();
        (await anon.PostAsJsonAsync("/api/ai/estimate-macros", new { description = "2 eggs" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anon.PostAsJsonAsync("/api/ai/suggest-goal", new { }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anon.PostAsJsonAsync("/api/ai/estimate-exercise", new { name = "running", durationMin = 30 }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Ai_endpoints_require_tracker_self()
    {
        var (_, noTracker) = await ProvisionUser("dashboard.view");
        (await noTracker.PostAsJsonAsync("/api/ai/estimate-macros", new { description = "2 eggs" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await noTracker.PostAsJsonAsync("/api/ai/suggest-goal", new { }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await noTracker.PostAsJsonAsync("/api/ai/estimate-exercise", new { name = "running", durationMin = 30 }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- Graceful 503 when Gemini is unconfigured (no key in the test env) ----

    [Fact]
    public async Task Ai_endpoints_return_503_when_unconfigured()
    {
        var (_, user) = await ProvisionUser("tracker.self");

        var macros = await user.PostAsJsonAsync("/api/ai/estimate-macros", new { description = "2 scrambled eggs", quantity = "2 eggs" });
        macros.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        var goal = await user.PostAsJsonAsync("/api/ai/suggest-goal", new { });
        goal.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        var exercise = await user.PostAsJsonAsync("/api/ai/estimate-exercise", new { name = "running", durationMin = 30 });
        exercise.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }
}
