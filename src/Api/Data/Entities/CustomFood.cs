namespace Ccusage.Api.Data.Entities;

/// <summary>
/// One of a user's saved "My foods" — a per-user library auto-built from MANUALLY logged foods. Each
/// time the user logs a manual food (no provider source and no FdcId) it is upserted here keyed by
/// (UserEmail, Description, Brand, ServingDesc): re-logging the same food bumps <see cref="UseCount"/>
/// and <see cref="LastUsedUtc"/> and refreshes the snapshot macros, rather than inserting a duplicate.
///
/// To make that upsert/dedup work with a unique index, <see cref="Brand"/> and <see cref="ServingDesc"/>
/// are normalized to the EMPTY string (never null) so two logs that differ only by a null-vs-empty
/// brand collapse to the same row.
/// </summary>
public class CustomFood
{
    public long Id { get; set; }

    /// <summary>Owner email, stored lower-cased.</summary>
    public string UserEmail { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>Brand, normalized to "" (never null) so the dedup key is stable.</summary>
    public string Brand { get; set; } = "";

    /// <summary>Serving description, normalized to "" (never null) so the dedup key is stable.</summary>
    public string ServingDesc { get; set; } = "";

    public int Calories { get; set; }
    public double ProteinG { get; set; }
    public double CarbG { get; set; }
    public double FatG { get; set; }

    /// <summary>How many times this food has been logged (drives the "frequent" ordering).</summary>
    public int UseCount { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime LastUsedUtc { get; set; }
}
