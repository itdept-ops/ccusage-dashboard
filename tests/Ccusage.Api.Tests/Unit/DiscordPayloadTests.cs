using System.Net;
using System.Text.Json;
using Ccusage.Api.Data;
using Ccusage.Api.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ccusage.Api.Tests.Unit;

/// <summary>Verifies the Discord payload is a well-formed rich embed without contacting Discord.</summary>
public class DiscordPayloadTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Body = req.Content is null ? null : await req.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.NoContent) { RequestMessage = req };
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    [Fact]
    public async Task Test_message_is_a_rich_embed_with_icon_author_and_footer()
    {
        var handler = new CapturingHandler();
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = "x".PadRight(40, 'k') }).Build();
        // SendTestAsync never queries the DB, so an unconfigured context is fine.
        var db = new UsageDbContext(new DbContextOptionsBuilder<UsageDbContext>().Options);
        var notifier = new DiscordNotifier(new StubFactory(handler), db, NullLogger<DiscordNotifier>.Instance);

        var ok = await notifier.SendTestAsync("https://discord.com/api/webhooks/1/abc", CancellationToken.None);

        ok.Should().BeTrue();
        handler.Body.Should().NotBeNull();
        var json = JsonDocument.Parse(handler.Body!).RootElement;
        json.GetProperty("username").GetString().Should().Be("Usage IQ");
        json.GetProperty("avatar_url").GetString().Should().Contain("usage-iq-icon.png");

        var embed = json.GetProperty("embeds")[0];
        embed.GetProperty("author").GetProperty("name").GetString().Should().Be("Connection test");
        embed.GetProperty("author").GetProperty("icon_url").GetString().Should().Contain("usage-iq-icon.png");
        embed.GetProperty("footer").GetProperty("icon_url").GetString().Should().Contain("usage-iq-icon.png");
        embed.GetProperty("color").GetInt32().Should().BeGreaterThan(0);
        embed.TryGetProperty("timestamp", out _).Should().BeTrue();
    }
}
