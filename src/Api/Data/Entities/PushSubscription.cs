namespace Ccusage.Api.Data.Entities;

/// <summary>
/// One browser/device Web Push subscription owned by a single user. Created when the user opts in to
/// background ("offline / installable") notifications and the service worker registers a push
/// subscription; the rows are the fan-out targets the <see cref="Ccusage.Api.Services.WebPushSender"/>
/// posts to. One row per device <see cref="Endpoint"/> (unique); a user may have several (phone +
/// laptop). The <see cref="P256dh"/>/<see cref="Auth"/> keys are the subscription's PUBLIC encryption
/// material the push protocol needs to encrypt a payload — they are NOT app secrets (they live only on
/// the subscriber's device and the push service), so they are stored as-is and only ever used to send a
/// push to THAT device. Pruned when the push service reports the subscription is gone (404/410).
/// </summary>
public class PushSubscription
{
    public int Id { get; set; }

    /// <summary>Owner email, stored lower-cased. Indexed for the per-recipient fan-out lookup.</summary>
    public string OwnerEmail { get; set; } = "";

    /// <summary>
    /// The push service URL the browser handed us (e.g. <c>https://fcm.googleapis.com/fcm/send/…</c>).
    /// Unique across all rows — re-subscribing the same device upserts onto this row rather than
    /// duplicating. This is the address the sender POSTs the encrypted payload to.
    /// </summary>
    public string Endpoint { get; set; } = "";

    /// <summary>The subscription's PUBLIC P-256 ECDH key (base64url) — encryption material, not a secret.</summary>
    public string P256dh { get; set; } = "";

    /// <summary>The subscription's auth secret (base64url) — encryption material, not an app secret.</summary>
    public string Auth { get; set; } = "";

    /// <summary>Optional User-Agent captured at subscribe time, purely to help the owner recognise a device.</summary>
    public string? UserAgent { get; set; }

    public DateTime CreatedUtc { get; set; }

    /// <summary>Last time a push to this subscription succeeded — bumped by the sender; null = never sent yet.</summary>
    public DateTime? LastUsedUtc { get; set; }
}
