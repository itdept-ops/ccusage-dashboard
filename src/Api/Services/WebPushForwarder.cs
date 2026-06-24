using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace Ccusage.Api.Services;

/// <summary>One queued background push: the recipient + the minimal title/body/link. NO secrets ever.</summary>
public readonly record struct WebPushItem(string RecipientEmail, string Title, string Body, string? Link);

/// <summary>
/// Off-request-path, fire-and-forget driver for <see cref="WebPushSender"/> — the exact pattern of
/// <see cref="DiscordForwarder"/>. Registered as a singleton hosted service. The notification dispatch
/// (<see cref="ChatNotificationService"/>) enqueues an item and returns immediately; the worker resolves a
/// fresh scope per item and calls the (scoped) <see cref="WebPushSender"/>, which is itself a no-op without
/// VAPID keys and never throws. So a push NEVER blocks, slows, or fails notification creation.
///
/// <para>When web-push is unconfigured the enqueue is dropped at the door (cheap), so an unconfigured
/// deployment does no per-notification work at all.</para>
/// </summary>
public sealed class WebPushForwarder(
    IServiceScopeFactory scopeFactory, IOptions<WebPushOptions> options, ILogger<WebPushForwarder> logger)
    : BackgroundService
{
    private readonly WebPushOptions _options = options.Value;

    // Bounded + DropOldest so a flood can never grow unbounded; the in-app notification is already persisted,
    // so a dropped item only misses the background push mirror.
    private readonly Channel<WebPushItem> _channel =
        Channel.CreateBounded<WebPushItem>(new BoundedChannelOptions(4096)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    /// <summary>
    /// Enqueue a fire-and-forget push. Returns immediately; never throws. Dropped cheaply when web-push is
    /// unconfigured (no keys) so an unconfigured deployment does zero per-notification work.
    /// </summary>
    public void Enqueue(WebPushItem item)
    {
        if (!_options.IsConfigured) return; // no keys ⇒ nothing to do; don't even queue.
        _channel.Writer.TryWrite(item);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var sender = scope.ServiceProvider.GetRequiredService<WebPushSender>();
                await sender.SendToUserAsync(item.RecipientEmail, item.Title, item.Body, item.Link, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // The sender already swallows its own errors; this is belt-and-suspenders for scope failures.
                logger.LogWarning(ex, "Web push forward failed for a recipient.");
            }
        }
    }
}
