namespace Ccusage.Api.Data.Entities;

/// <summary>
/// One Rocket Money CSV import batch on a <see cref="Household"/> — an audit row recording the file name,
/// how many rows the file held, and how many were imported vs skipped (skips = unparseable rows + dedup
/// hits). The importer is recorded by AppUser id (and resolved to a display name on the wire) — an email is
/// never stored here or returned.
/// </summary>
public class FinanceImport
{
    public long Id { get; set; }

    /// <summary>The owning household — imports are private to its members.</summary>
    public int HouseholdId { get; set; }

    /// <summary>The uploaded file's name (display only).</summary>
    public string FileName { get; set; } = "";

    /// <summary>Total data rows seen in the file (excludes the header).</summary>
    public int RowCount { get; set; }

    /// <summary>How many new transactions were inserted.</summary>
    public int ImportedCount { get; set; }

    /// <summary>How many rows were skipped (unparseable + already-present duplicates).</summary>
    public int SkippedCount { get; set; }

    /// <summary>AppUser id of whoever ran the import (identity is by id, never email).</summary>
    public int ImportedByUserId { get; set; }

    /// <summary>
    /// The lifecycle state of this batch in the parse→review→commit flow:
    /// <c>"staged"</c> (parsed + held for review, nothing in the live ledger yet — the default),
    /// <c>"committed"</c> (its transactions were inserted into the ledger; immutable thereafter),
    /// <c>"discarded"</c> (a staged batch the user threw away). Committed batches are never re-edited.
    /// </summary>
    public string Status { get; set; } = "staged";

    /// <summary>The source format this batch was parsed from: <c>"rocketmoney"</c> | <c>"csv"</c> (a
    /// generic, column-mapped bank CSV) | <c>"ofx"</c> (an OFX/QFX statement). Display/diagnostics only.</summary>
    public string Format { get; set; } = "rocketmoney";

    /// <summary>When this batch was committed to the live ledger (null while still staged/discarded).</summary>
    public DateTime? CommittedUtc { get; set; }

    public DateTime CreatedUtc { get; set; }
}
