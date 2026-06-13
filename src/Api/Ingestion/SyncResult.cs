namespace Ccusage.Api.Ingestion;

/// <summary>Summary of a sync run, returned by <c>POST /api/sync</c>.</summary>
public sealed class SyncResult
{
    public string TimeZone { get; set; } = "";

    public int FilesScanned { get; set; }
    public int FilesParsed { get; set; }
    public int FilesSkipped { get; set; }

    /// <summary>Newly inserted (de-duplicated) usage rows.</summary>
    public int NewRecords { get; set; }

    /// <summary>New rows broken down by source name (e.g. claude-code, codex).</summary>
    public Dictionary<string, int> NewRecordsBySource { get; set; } = new();

    /// <summary>Per-source problems (e.g. a configured path that doesn't exist).</summary>
    public List<string> SourceWarnings { get; set; } = new();

    /// <summary>Distinct model strings that fell through to the <c>*</c> fallback (need pricing).</summary>
    public List<string> UnpricedModels { get; set; } = new();

    public long DurationMs { get; set; }
    public string? Error { get; set; }
    public string? Warning { get; set; }
}
