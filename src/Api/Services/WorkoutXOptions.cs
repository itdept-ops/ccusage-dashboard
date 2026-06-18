namespace Ccusage.Api.Services;

/// <summary>
/// Bound from the <c>WorkoutX</c> configuration section. <see cref="ApiKey"/> is a secret (read from the
/// git-ignored appsettings.Local.json locally, or the <c>WorkoutX__ApiKey</c> env var in prod) and is
/// NEVER logged. When it is blank the WorkoutX exercise-browse + gif endpoints return 503; the rest of
/// the tracker still works. <see cref="BaseUrl"/> is a fixed, non-user-controlled host (no SSRF surface).
/// </summary>
public sealed class WorkoutXOptions
{
    public const string SectionName = "WorkoutX";

    /// <summary>WorkoutX API key, sent as the <c>X-WorkoutX-Key</c> header. Blank disables WorkoutX (503).</summary>
    public string? ApiKey { get; set; }

    /// <summary>The WorkoutX API root; defaults to the public host. Never chosen from user input.</summary>
    public string BaseUrl { get; set; } = "https://api.workoutxapp.com";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}
