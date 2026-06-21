using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Ccusage.Api.Tests.Integration;

/// <summary>
/// Hardening regression for <c>SavedViewsEndpoints</c>: over-length / over-count payloads must be
/// rejected (400) or clamped before they reach the DB, and unknown GroupBy values must collapse to
/// the "day" default (mirroring ShareEndpoints.Normalize) rather than persisting arbitrary input.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class Hardening_SavedViewsTests(WebAppFactory factory)
{
    private HttpClient Admin()
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestJwt.For(WebAppFactory.AdminEmail));
        return c;
    }

    private async Task<HttpClient> ProvisionUserAsync()
    {
        var email = $"sv-harden-{Guid.NewGuid():N}@test.local";
        var res = await Admin().PostAsJsonAsync("/api/users", new { email, isEnabled = true, permissions = new[] { "dashboard.view" } });
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestJwt.For(email));
        return c;
    }

    [Fact]
    public async Task Create_with_over_length_name_returns_400_not_500()
    {
        var c = await ProvisionUserAsync();
        var body = new { name = new string('x', 200), groupBy = "day" };

        var resp = await c.PostAsJsonAsync("/api/saved-views", body);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_with_over_length_name_returns_400()
    {
        var c = await ProvisionUserAsync();
        var id = (await (await c.PostAsJsonAsync("/api/saved-views", new { name = "ok", groupBy = "day" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var resp = await c.PutAsJsonAsync($"/api/saved-views/{id}", new { name = new string('y', 200), groupBy = "day" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Oversized_arrays_and_long_items_are_clamped_not_500()
    {
        var c = await ProvisionUserAsync();
        var body = new
        {
            name = "clamp-me",
            groupBy = "day",
            projectId = Enumerable.Range(1, 500).ToArray(),                          // > 200 cap
            model = Enumerable.Range(1, 500).Select(_ => "m").ToArray(),             // > 200 cap
            source = new[] { new string('s', 300) },                                 // per-item > 128
        };

        var created = await (await c.PostAsJsonAsync("/api/saved-views", body))
            .Content.ReadFromJsonAsync<JsonElement>();

        created.GetProperty("projectId").GetArrayLength().Should().Be(200);
        created.GetProperty("model").GetArrayLength().Should().Be(200);
        created.GetProperty("source").EnumerateArray().Single().GetString()!.Length.Should().Be(128);
    }

    [Fact]
    public async Task Unknown_groupBy_collapses_to_day()
    {
        var c = await ProvisionUserAsync();

        var created = await (await c.PostAsJsonAsync("/api/saved-views", new { name = "gb", groupBy = "totally-bogus" }))
            .Content.ReadFromJsonAsync<JsonElement>();

        created.GetProperty("groupBy").GetString().Should().Be("day");
    }
}
