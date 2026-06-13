namespace Ccusage.Api.Data.Entities;

/// <summary>
/// A place to read usage logs from. <see cref="Kind"/> selects the parser
/// (e.g. <c>claude</c> reads <c>~/.claude/projects</c>, <c>codex</c> reads
/// <c>~/.codex</c> rollout files).
/// </summary>
public class IngestionSource
{
    public int Id { get; set; }

    /// <summary>Stable identifier stamped onto each <see cref="UsageRecord.Source"/> (e.g. <c>claude-code</c>, <c>codex</c>).</summary>
    public string Name { get; set; } = "";

    /// <summary>Parser kind: <c>claude</c> or <c>codex</c>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>Absolute directory scanned for this source's JSONL files.</summary>
    public string RootPath { get; set; } = "";

    public bool Enabled { get; set; } = true;
}
