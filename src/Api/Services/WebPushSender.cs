using System.Net;
using System.Text.Json;
using Ccusage.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebPush;
using LibPushSubscription = WebPush.PushSubscription;

namespace Ccusage.Api.Services;

/// <summary>
/// Sends background Web Push notifications to a recipient's registered browser/device subscriptions — the
/// always-on surface that fires even when no tab is open (vs. the SignalR live path, which needs an open
/// connection). Scoped; resolved off the request path by the notification dispatch's fire-and-forget hook.
///
/// <para>Hard guarantees:
/// <list type="bullet">
/// <item>NO-OP without keys: if <see cref="WebPushOptions.IsConfigured"/> is false it does nothing and logs
/// the "disabled" reason at most ONCE per process — it never queries the DB, never sends.</item>
/// <item>NEVER throws into the caller: every send is wrapped; failures are swallowed + logged (metadata only,
/// never the payload text or endpoint URL). The notification is already persisted/delivered in-app, so a
/// failed push only misses the background mirror.</item>
/// <item>Minimal payload: only <c>title</c> + <c>body</c> + <c>link</c> are sent — never a secret, never the
/// VAPID private key, never an email.</item>
/// <item>Self-pruning: a 404/410 ("gone") from the push service deletes that dead subscription row.</item>
/// </list></para>
/// </summary>
public sealed class WebPushSender
{
    private readonly UsageDbContext _db;
    private readonly WebPushOptions _options;
    private readonly ILogger<WebPushSender> _logger;
    private readonly WebPushClient _client;

    // Logged at most once per process so a permanently-unconfigured deployment doesn't spam the log on
    // every notification. Static so it spans the scoped instances created per dispatch.
    private static int _disabledLogged;

    public WebPushSender(
        UsageDbContext db, IOptions<WebPushOptions> options, ILogger<WebPushSender> logger,
        IHttpClientFactory httpClientFactory)
    {
        _db = db;
        _options = options.Value;
        _logger = logger;
        // Reuse a pooled HttpClient (named "webpush") so tests can reroute it to a capturing handler and prod
        // gets connection pooling; the WebPushClient itself is cheap to new up per scope.
        _client = new WebPushClient(httpClientFactory.CreateClient(HttpClientName));
    }

    /// <summary>The named HttpClient the push POSTs go through (rerouteable in tests).</summary>
    public const string HttpClientName = "webpush";

    /// <summary>Whether web-push is configured (both VAPID keys present). False ⇒ every send is a silent no-op.</summary>
    public bool IsConfigured => _options.IsConfigured;

    /// <summary>
    /// Send a minimal push (title + body + link) to ALL of <paramref name="recipientEmail"/>'s registered
    /// subscriptions. No-op (returns immediately) when web-push is unconfigured or the recipient has no
    /// subscriptions. Prunes any subscription the push service reports as gone (404/410). NEVER throws.
    /// <paramref name="recipientEmail"/> must be lower-cased.
    /// <para><paramref name="actions"/> + <paramref name="actionUrls"/> are OPTIONAL deep-link action buttons.
    /// When non-empty they are added to the payload as <c>actions</c> (array of {action, title}) and
    /// <c>actionUrls</c> (map action→in-app relative url); the SW renders the buttons and navigates the url on
    /// click. When null/empty the payload is byte-for-byte the legacy <c>{ title, body, url }</c> shape, so
    /// existing notifications are unchanged. The action urls are in-app relative paths — never a secret.</para>
    /// </summary>
    public async Task SendToUserAsync(
        string recipientEmail, string title, string body, string? link,
        IReadOnlyList<WebPushAction>? actions = null,
        IReadOnlyDictionary<string, string>? actionUrls = null,
        CancellationToken ct = default)
    {
        if (!_options.IsConfigured)
        {
            // Log once, then stay quiet — an intentionally-unconfigured deployment isn't an error.
            if (Interlocked.Exchange(ref _disabledLogged, 1) == 0)
                _logger.LogInformation("Web push disabled (VAPID keys not configured); skipping all sends.");
            return;
        }

        if (string.IsNullOrEmpty(recipientEmail)) return;

        // The opt-in is authoritative SERVER-SIDE (mirrors DiscordForwarder's SurfaceDiscord gate): even if a
        // client ever fails to tear its subscription down, a recipient with "Browser notifications" OFF must
        // NOT receive background pushes. Load the pref and skip unless SurfaceBrowser is explicitly on (the
        // default is off, so a missing pref row also means no push).
        bool surfaceBrowser;
        try
        {
            surfaceBrowser = await _db.NotificationPreferences.AsNoTracking()
                .Where(p => p.UserEmail == recipientEmail)
                .Select(p => (bool?)p.SurfaceBrowser)
                .FirstOrDefaultAsync(ct) ?? false;
        }
        catch (Exception ex)
        {
            // A DB blip must never bubble into the notification dispatch — swallow + log, and fail CLOSED (skip).
            _logger.LogWarning(ex, "Web push: failed to load notification preference for a recipient; skipping.");
            return;
        }
        if (!surfaceBrowser) return;

        List<Data.Entities.PushSubscription> subs;
        try
        {
            subs = await _db.PushSubscriptions.AsNoTracking()
                .Where(s => s.OwnerEmail == recipientEmail)
                .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            // A DB blip must never bubble into the notification dispatch — swallow + log metadata only.
            _logger.LogWarning(ex, "Web push: failed to load subscriptions for a recipient; skipping.");
            return;
        }
        if (subs.Count == 0) return;

        // Minimal payload — title + body + link only. NO secrets, NO email. The service worker reads these.
        // OPTIONALLY carry deep-link action buttons: include `actions`/`actionUrls` ONLY when both are present
        // and non-empty, so a plain push stays byte-for-byte the legacy { title, body, url } shape (the SW
        // no-ops without them, and existing notifications are unchanged). Action urls are in-app relative paths.
        string payload;
        if (actions is { Count: > 0 } && actionUrls is { Count: > 0 })
        {
            payload = JsonSerializer.Serialize(new
            {
                title,
                body,
                url = link,
                actions = actions.Select(a => new { action = a.Action, title = a.Title }).ToArray(),
                actionUrls,
            });
        }
        else
        {
            payload = JsonSerializer.Serialize(new { title, body, url = link });
        }
        var vapid = new VapidDetails(_options.Subject, _options.PublicKey, _options.PrivateKey);

        var goneEndpoints = new List<string>();
        var nowSent = new List<string>();
        foreach (var s in subs)
        {
            try
            {
                var libSub = new LibPushSubscription(s.Endpoint, s.P256dh, s.Auth);
                await _client.SendNotificationAsync(libSub, payload, vapid, ct);
                nowSent.Add(s.Endpoint);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return; // shutting down — stop quietly.
            }
            catch (WebPushException wpe) when (IsGone(wpe.StatusCode))
            {
                // The push service says this subscription no longer exists — reap it.
                goneEndpoints.Add(s.Endpoint);
                _logger.LogInformation(
                    "Web push: pruning gone subscription ({Status}) for a recipient.", (int)wpe.StatusCode);
            }
            catch (WebPushException wpe)
            {
                // Other push-service errors (429/5xx/etc.): drop this one send, keep the subscription.
                _logger.LogWarning(
                    "Web push: send failed ({Status}) for a recipient; keeping subscription.", (int)wpe.StatusCode);
            }
            catch (Exception ex)
            {
                // Belt-and-suspenders: never let anything escape into the caller.
                _logger.LogWarning(ex, "Web push: unexpected send failure for a recipient.");
            }
        }

        // Prune dead subscriptions in one statement (best-effort; failure here is also swallowed).
        if (goneEndpoints.Count > 0)
        {
            try
            {
                await _db.PushSubscriptions
                    .Where(s => s.OwnerEmail == recipientEmail && goneEndpoints.Contains(s.Endpoint))
                    .ExecuteDeleteAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Web push: failed to prune gone subscriptions.");
            }
        }

        // Bump LastUsedUtc for the ones that succeeded (best-effort; purely informational).
        if (nowSent.Count > 0)
        {
            try
            {
                var now = DateTime.UtcNow;
                await _db.PushSubscriptions
                    .Where(s => s.OwnerEmail == recipientEmail && nowSent.Contains(s.Endpoint))
                    .ExecuteUpdateAsync(u => u.SetProperty(s => s.LastUsedUtc, now), ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Web push: failed to stamp LastUsedUtc.");
            }
        }
    }

    /// <summary>A push-service "this subscription is gone" status: 404 Not Found or 410 Gone.</summary>
    private static bool IsGone(HttpStatusCode status) =>
        status is HttpStatusCode.NotFound or HttpStatusCode.Gone;
}
