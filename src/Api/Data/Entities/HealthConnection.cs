namespace Ccusage.Api.Data.Entities;

/// <summary>The wearable health providers the sync supports. Fitbit is v1; Oura slots in later behind the
/// same <c>IHealthProvider</c> abstraction.</summary>
public enum HealthProvider
{
    Fitbit = 0,
    Oura = 1,
}

/// <summary>The outcome of the most recent sync attempt for a connection — surfaced to the UI so a dead
/// token can prompt a reconnect and a rate-limit shows a "try later" hint.</summary>
public enum HealthSyncStatus
{
    Ok = 0,
    AuthExpired = 1,
    RateLimited = 2,
    Error = 3,
}

/// <summary>
/// PROGRAM-2 #1 — a single user's wearable (Fitbit) connection, established via the OAuth 2.0
/// authorization-CODE + PKCE flow (offline access). Mirrors <see cref="GoogleCalendarConnection"/>: the
/// long-lived REFRESH TOKEN is stored ENCRYPTED at rest (AES-GCM via the app's <c>TokenProtector</c>) and
/// never leaves the server.
///
/// FITBIT DEVIATION (the #1 correctness rule): Fitbit ROTATES the refresh token on EVERY refresh — each
/// token-refresh response carries a NEW refresh token, and the old one is invalidated. The mint path MUST
/// re-store <see cref="EncryptedRefreshToken"/> after every refresh or the connection silently dies.
///
/// At most one connection per (user, provider) — a UNIQUE index enforces it. A row exists only after the
/// user has explicitly connected; deleting it (disconnect) revokes the app's stored access immediately.
/// </summary>
public class HealthConnection
{
    public int Id { get; set; }

    /// <summary>AppUser id this connection belongs to.</summary>
    public int UserId { get; set; }

    /// <summary>Owner email, stored lower-cased — the key the synced tracker rows are written under.</summary>
    public string UserEmail { get; set; } = "";

    /// <summary>Which wearable provider this connection is for.</summary>
    public HealthProvider Provider { get; set; }

    /// <summary>
    /// The provider's OAuth refresh token, ENCRYPTED at rest (AES-GCM via TokenProtector). Decrypted
    /// server-side only, to mint short-lived access tokens. RE-STORED on every refresh (Fitbit rotates it).
    /// Never exposed on the wire or logged.
    /// </summary>
    public string EncryptedRefreshToken { get; set; } = "";

    /// <summary>The OAuth scope(s) granted for this connection.</summary>
    public string Scope { get; set; } = "";

    /// <summary>The provider's opaque user id for this connection (e.g. Fitbit's encoded user id), if known.</summary>
    public string? ProviderUserId { get; set; }

    /// <summary>Whether the background scheduler should auto-sync this connection (default true).</summary>
    public bool AutoSyncEnabled { get; set; } = true;

    /// <summary>Per-signal toggles — a user can pull steps but not sleep, etc. (all default true).</summary>
    public bool SyncSteps { get; set; } = true;
    public bool SyncSleep { get; set; } = true;
    public bool SyncHeartRate { get; set; } = true;
    public bool SyncWorkouts { get; set; } = true;

    /// <summary>The last LOCAL date the sync has advanced through (the cursor). Null until the first sync.
    /// The next sync pulls (cursor .. today-local], capped at a backfill window.</summary>
    public DateOnly? LastSyncCursorDate { get; set; }

    /// <summary>When the connection last completed a sync attempt (success or graceful failure).</summary>
    public DateTime? LastSyncUtc { get; set; }

    /// <summary>The outcome of the most recent sync attempt.</summary>
    public HealthSyncStatus LastSyncStatus { get; set; } = HealthSyncStatus.Ok;

    /// <summary>When the user connected this wearable.</summary>
    public DateTime ConnectedUtc { get; set; }

    /// <summary>When the connection was last used to mint an access token / call the provider API.</summary>
    public DateTime? LastUsedUtc { get; set; }
}
