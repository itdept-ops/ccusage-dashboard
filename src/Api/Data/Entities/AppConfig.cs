namespace Ccusage.Api.Data.Entities;

/// <summary>
/// Single-row runtime settings, seeded from configuration on first run and
/// editable via the Settings page.
/// </summary>
public class AppConfig
{
    public int Id { get; set; }

    /// <summary>IANA timezone used to bucket usage into days/months.</summary>
    public string DisplayTimeZone { get; set; } = "America/New_York";

    /// <summary>Absolute path to the Claude Code projects directory (legacy; sources now own paths).</summary>
    public string ClaudeProjectsPath { get; set; } = "";

    /// <summary>Whether the background timer runs incremental syncs.</summary>
    public bool AutoSyncEnabled { get; set; } = true;

    /// <summary>Cadence of the background sync, in seconds (min 30).</summary>
    public int AutoSyncIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// When true, any Google account that passes token validation is auto-provisioned a user
    /// (granted <see cref="DefaultPermissionsCsv"/>). When false, only pre-provisioned accounts
    /// may sign in.
    /// </summary>
    public bool OpenSignupEnabled { get; set; } = true;

    /// <summary>
    /// CSV of permission keys granted to auto-provisioned users. Validated against the catalog
    /// on use; empty means new accounts land on /welcome with no access (an approval queue).
    /// </summary>
    public string DefaultPermissionsCsv { get; set; } = "dashboard.view";
}
