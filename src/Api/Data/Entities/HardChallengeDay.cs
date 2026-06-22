namespace Ccusage.Api.Data.Entities;

/// <summary>
/// One calendar day of a <see cref="HardChallenge"/>. The row persists ONLY the manual attestations + the
/// auto-override + the cheat flag; the auto-scored task bits (diet/water/workout1/workout2) are recomputed LIVE
/// from the tracker on every read and stored here purely as a denormalized cache (never read as truth). One row
/// per (UserEmail, LocalDate) — unique.
///
/// <para>PHOTO PRIVACY: <see cref="PhotoTaken"/> is a BOOLEAN attestation only. There is deliberately NO image
/// column here, EVER — the backend never stores or accepts a progress-photo image.</para>
/// </summary>
public class HardChallengeDay
{
    public long Id { get; set; }

    /// <summary>FK to the owning <see cref="HardChallenge"/> (cascade-deleted with it).</summary>
    public int ChallengeId { get; set; }
    public HardChallenge? Challenge { get; set; }

    /// <summary>Owner email, denormalized + stored lower-cased (unique with <see cref="LocalDate"/>).</summary>
    public string UserEmail { get; set; } = "";

    /// <summary>The challenge day, in the app's display timezone.</summary>
    public DateOnly LocalDate { get; set; }

    // ---- AUTO-scored (recomputed live from the tracker; cached here, never read as truth) ----

    /// <summary>AUTO: calories-in within the daily calorie goal AND within every SET macro goal.</summary>
    public bool DietOk { get; set; }

    /// <summary>Manual override of the diet result: true/false WINS over the auto computation; null = use auto.</summary>
    public bool? DietOverride { get; set; }

    /// <summary>AUTO: the day's hydration sum is at least one US gallon (3785 ml).</summary>
    public bool WaterGallonOk { get; set; }

    /// <summary>AUTO: at least one logged exercise of >= 45 minutes that day.</summary>
    public bool Workout1Ok { get; set; }

    /// <summary>AUTO: at least two logged exercises of >= 45 minutes that day.</summary>
    public bool Workout2Ok { get; set; }

    // ---- Manual attestations + flags (the ONLY persisted truth) ----

    /// <summary>User attestation that the second workout was outdoors.</summary>
    public bool Workout2Outdoor { get; set; }

    /// <summary>Manual: the day's reading (10 pages) was done.</summary>
    public bool ReadOk { get; set; }

    /// <summary>Manual BOOLEAN attestation that a progress photo was taken. NO image is ever stored.</summary>
    public bool PhotoTaken { get; set; }

    /// <summary>Whether the user kept the no-alcohol rule that day. Defaults true.</summary>
    public bool NoAlcohol { get; set; } = true;

    /// <summary>Optional Relaxed-ruleset confession (&lt;= 280 chars): keeps the run counted on a missed day.</summary>
    public string? Confession { get; set; }

    /// <summary>Whether this day was pre-declared a cheat day (keeps the run counted without completing).</summary>
    public bool IsCheatDay { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
