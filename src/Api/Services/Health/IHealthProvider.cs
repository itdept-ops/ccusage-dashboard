using Ccusage.Api.Data.Entities;

namespace Ccusage.Api.Services.Health;

/// <summary>
/// PROGRAM-2 #1 — provider-agnostic wearable abstraction. Fitbit is v1; Oura slots in behind the same
/// interface later. Every method degrades GRACEFULLY (returns a status, never throws into the request /
/// scheduler path), exactly like <see cref="GoogleCalendarService"/>.
///
/// The OAuth model mirrors the calendar service: an auth-code+PKCE <see cref="ConnectAsync"/> exchange
/// stores an ENCRYPTED refresh token, and each pull mints a short-lived access token from it. The ONE
/// deviation (Fitbit-specific, but exposed generically) is that a refresh ROTATES the stored token — the
/// implementation re-persists it on every refresh, so the connection never silently dies.
/// </summary>
public interface IHealthProvider
{
    /// <summary>Which provider this implementation serves.</summary>
    HealthProvider Provider { get; }

    /// <summary>Whether the provider's OAuth client id + secret are configured on this server. When false,
    /// /status reports configured:false and everything degrades gracefully (no connect, no sync).</summary>
    bool IsConfigured { get; }

    /// <summary>The OAuth scopes this provider requests at consent (space-separated, the exact value the
    /// frontend must put in the authorize URL).</summary>
    string Scopes { get; }

    /// <summary>
    /// Exchange a one-time auth CODE (+ the PKCE <paramref name="codeVerifier"/>) for tokens and store the
    /// encrypted (rotating) refresh token for <paramref name="userId"/>/<paramref name="userEmail"/>. Returns
    /// false when unconfigured, when the provider rejects the code, or when no refresh token comes back. Never
    /// throws; never logs the secret/tokens.
    /// </summary>
    Task<bool> ConnectAsync(int userId, string userEmail, string code, string redirectUri, string? codeVerifier, CancellationToken ct = default);

    /// <summary>
    /// Pull the given LOCAL date's signals for a connected user, honouring the per-signal toggles on the
    /// connection. The access token is minted internally from the stored refresh token (re-storing the rotated
    /// token). Returns a graceful outcome: Ok with the day's signals, or NotConfigured / NotConnected /
    /// AuthExpired / RateLimited / Error. <paramref name="tz"/> is the user's timezone (Fitbit returns wall-
    /// clock times the mapper anchors to the local date).
    /// </summary>
    Task<HealthDayResult> PullDayAsync(
        HealthConnection conn, DateOnly localDate, TimeZoneInfo tz, CancellationToken ct = default);
}

/// <summary>The graceful outcome of a provider pull — never an exception.</summary>
public enum HealthPullStatus { Ok, NotConfigured, NotConnected, AuthExpired, RateLimited, Error }

/// <summary>One day's normalized wearable signals, provider-agnostic. Any field may be null when the
/// provider had no data or the signal's toggle is off. The mapper upserts these into the tracker.</summary>
public sealed record HealthDaySignals(
    DateOnly LocalDate,
    DailySummary? Activity,
    IReadOnlyList<SleepRecord> Sleeps,
    IReadOnlyList<WorkoutRecord> Workouts);

/// <summary>Day-keyed activity totals → a single DailyActivity row (keyed by (UserEmail, LocalDate)).</summary>
public sealed record DailySummary(int? Steps, int? DistanceMeters, int? ActiveCalories, int? RestingHeartRate);

/// <summary>One sleep record keyed by the vendor's sleep logId → a SleepEntry on the WAKE date.</summary>
public sealed record SleepRecord(string LogId, decimal Hours, TimeOnly? BedTime, TimeOnly? WakeTime, int Quality);

/// <summary>One workout keyed by the vendor's activity logId → an ExerciseEntry.</summary>
public sealed record WorkoutRecord(string LogId, string Name, int? DurationMin, int CaloriesBurned);

/// <summary>The graceful result of a provider day-pull.</summary>
public sealed record HealthDayResult(HealthPullStatus Status, HealthDaySignals? Signals)
{
    public bool Ok => Status == HealthPullStatus.Ok;
    public static HealthDayResult Of(HealthPullStatus status) => new(status, null);
    public static HealthDayResult Value(HealthDaySignals signals) => new(HealthPullStatus.Ok, signals);

    /// <summary>Map a provider pull status to the persisted connection status (Ok/AuthExpired/RateLimited/Error).</summary>
    public HealthSyncStatus ToSyncStatus() => Status switch
    {
        HealthPullStatus.Ok => HealthSyncStatus.Ok,
        HealthPullStatus.AuthExpired or HealthPullStatus.NotConnected => HealthSyncStatus.AuthExpired,
        HealthPullStatus.RateLimited => HealthSyncStatus.RateLimited,
        _ => HealthSyncStatus.Error,
    };
}
