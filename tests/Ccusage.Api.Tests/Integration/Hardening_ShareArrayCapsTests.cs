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
/// Hardening: a share's stored filter arrays are attacker-supplied via the create body and are
/// replayed on every public read. ShareEndpoints caps them before persisting (mirror of the ingest
/// sanitization): <= 200 project ids, <= 100 model/source labels, each label clamped to <= 128 chars,
/// and de-duplicated. Legitimate (small) filters are stored verbatim.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class Hardening_ShareArrayCapsTests(WebAppFactory factory)
{
    private HttpClient Admin()
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestJwt.For(WebAppFactory.AdminEmail));
        return c;
    }

    [Fact]
    public async Task Oversized_filter_arrays_are_capped_and_clamped_before_persisting()
    {
        var admin = Admin();

        var longModel = new string('m', 300); // > 128 chars -> clamped to 128
        var req = new
        {
            label = "Hardening",
            expiresInHours = 24,
            groupBy = "day",
            projectId = Enumerable.Range(1, 300).ToArray(),                // 300 -> 200
            model = Enumerable.Range(0, 250).Select(i => $"model-{i}").Append(longModel).ToArray(), // 251 -> 100
            source = Enumerable.Range(0, 250).Select(i => $"source-{i}").ToArray(),                 // 250 -> 100
        };

        var created = await (await admin.PostAsJsonAsync("/api/shares", req))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetInt32();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
        var share = await db.ShareLinks.AsNoTracking().FirstAsync(s => s.Id == id);

        share.ProjectIds.Length.Should().Be(200);
        share.Models.Length.Should().Be(100);
        share.Sources.Length.Should().Be(100);
        share.Models.Should().OnlyContain(m => m.Length <= 128);
    }

    [Fact]
    public async Task Duplicate_filter_labels_are_de_duplicated()
    {
        var admin = Admin();

        var req = new
        {
            label = "Dedup",
            expiresInHours = 24,
            groupBy = "day",
            source = new[] { "codex", "codex", "codex", "claude" },
        };

        var created = await (await admin.PostAsJsonAsync("/api/shares", req))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetInt32();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
        var share = await db.ShareLinks.AsNoTracking().FirstAsync(s => s.Id == id);

        share.Sources.Should().BeEquivalentTo(new[] { "codex", "claude" });
    }

    [Fact]
    public async Task Small_legitimate_filter_is_stored_verbatim()
    {
        var admin = Admin();

        var req = new
        {
            label = "Legit",
            expiresInHours = 24,
            groupBy = "day",
            projectId = new[] { 7, 9 },
            source = new[] { "codex" },
        };

        var created = await (await admin.PostAsJsonAsync("/api/shares", req))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetInt32();

        // The baked scope still reflects the (untouched) source for legitimate callers.
        var token = created.GetProperty("token").GetString()!;
        var pub = await (await factory.CreateClient().GetAsync($"/api/share/{token}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        pub.GetProperty("scope").GetString().Should().Contain("codex");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
        var share = await db.ShareLinks.AsNoTracking().FirstAsync(s => s.Id == id);

        share.ProjectIds.Should().BeEquivalentTo(new[] { 7, 9 });
        share.Sources.Should().BeEquivalentTo(new[] { "codex" });
    }
}
