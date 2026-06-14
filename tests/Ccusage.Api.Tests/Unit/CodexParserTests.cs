using Ccusage.Api.Ingestion;
using FluentAssertions;

namespace Ccusage.Api.Tests.Unit;

public class CodexParserTests
{
    // The UUID embedded in this filename is what the parser derives as the session id
    // when no session_meta record supplies an explicit payload.id.
    private const string FileWithUuid =
        "rollout-2026-06-10T12-00-00-aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee.jsonl";
    private const string DerivedSessionId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    private static List<ParsedUsage> Parse(string jsonl, string file = FileWithUuid) =>
        new CodexParser().Parse(new StringReader(jsonl), file).ToList();

    private static string TokenCount(
        long input, long cached, long output, string ts = "2026-06-10T12:00:00Z") =>
        "{\"type\":\"event_msg\",\"timestamp\":\"" + ts +
        "\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{" +
        "\"input_tokens\":" + input +
        ",\"cached_input_tokens\":" + cached +
        ",\"output_tokens\":" + output + "}}}}";

    [Fact]
    public void Kind_is_codex()
    {
        new CodexParser().Kind.Should().Be("codex");
    }

    // ---- MatchesFile ----

    [Fact]
    public void MatchesFile_true_for_rollout_jsonl()
    {
        new CodexParser().MatchesFile(FileWithUuid).Should().BeTrue();
    }

    [Fact]
    public void MatchesFile_false_for_wrong_extension()
    {
        new CodexParser().MatchesFile("rollout-x.txt").Should().BeFalse();
    }

    [Fact]
    public void MatchesFile_false_without_rollout_prefix()
    {
        new CodexParser().MatchesFile("other.jsonl").Should().BeFalse();
    }

    [Fact]
    public void MatchesFile_prefix_and_extension_are_case_insensitive()
    {
        new CodexParser().MatchesFile("ROLLOUT-abc.JSONL").Should().BeTrue();
    }

    // ---- Core token_count mapping ----

    [Fact]
    public void Token_count_event_yields_one_row()
    {
        Parse(TokenCount(1000, 200, 300)).Should().HaveCount(1);
    }

    [Fact]
    public void Input_is_non_cached_portion()
    {
        // input_tokens - cached_input_tokens = 1000 - 200
        Parse(TokenCount(1000, 200, 300)).Single().Input.Should().Be(800);
    }

    [Fact]
    public void Input_clamps_at_zero_when_cached_exceeds_input()
    {
        Parse(TokenCount(100, 500, 300)).Single().Input.Should().Be(0);
    }

    [Fact]
    public void Cache_read_is_cached_input_tokens()
    {
        Parse(TokenCount(1000, 200, 300)).Single().CacheRead.Should().Be(200);
    }

    [Fact]
    public void Output_is_output_tokens()
    {
        Parse(TokenCount(1000, 200, 300)).Single().Output.Should().Be(300);
    }

    [Fact]
    public void Cache_write_tiers_are_zero()
    {
        var row = Parse(TokenCount(1000, 200, 300)).Single();
        row.Cache5m.Should().Be(0);
        row.Cache1h.Should().Be(0);
    }

    [Fact]
    public void Row_is_not_sidechain_and_has_no_agent()
    {
        var row = Parse(TokenCount(1000, 200, 300)).Single();
        row.IsSidechain.Should().BeFalse();
        row.AgentId.Should().BeNull();
    }

    [Fact]
    public void Absent_token_fields_default_to_zero()
    {
        // last_token_usage object present but missing all numeric fields.
        var line = """{"type":"event_msg","timestamp":"2026-06-10T12:00:00Z","payload":{"type":"token_count","info":{"last_token_usage":{"something_else":1}}}}""";
        // input==0 && output==0 => skipped row, but the object existed.
        Parse(line).Should().BeEmpty();
    }

    [Fact]
    public void Missing_input_field_defaults_to_zero_input_but_keeps_output()
    {
        var line = """{"type":"event_msg","timestamp":"2026-06-10T12:00:00Z","payload":{"type":"token_count","info":{"last_token_usage":{"output_tokens":50}}}}""";
        var row = Parse(line).Single();
        row.Input.Should().Be(0);
        row.CacheRead.Should().Be(0);
        row.Output.Should().Be(50);
    }

    [Fact]
    public void Token_numbers_provided_as_doubles_are_tolerated()
    {
        var line = """{"type":"event_msg","timestamp":"2026-06-10T12:00:00Z","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":1000.0,"cached_input_tokens":200.0,"output_tokens":300.0}}}}""";
        var row = Parse(line).Single();
        row.Input.Should().Be(800);
        row.CacheRead.Should().Be(200);
        row.Output.Should().Be(300);
    }

    // ---- Model resolution ----

    [Fact]
    public void Model_is_unknown_when_no_meta_or_context_seen()
    {
        Parse(TokenCount(1000, 200, 300)).Single().Model.Should().Be("(unknown)");
    }

    [Fact]
    public void Model_comes_from_session_meta()
    {
        var meta = """{"type":"session_meta","timestamp":"2026-06-10T12:00:00Z","payload":{"id":"sess-1","cwd":"C:/work/proj","cli_version":"0.9.0","model":"gpt-5","git":{"branch":"main"}}}""";
        Parse(meta + "\n" + TokenCount(1000, 200, 300)).Single().Model.Should().Be("gpt-5");
    }

    [Fact]
    public void Model_comes_from_turn_context()
    {
        var ctx = """{"type":"turn_context","payload":{"model":"gpt-5-codex","cwd":"C:/work/other"}}""";
        Parse(ctx + "\n" + TokenCount(1000, 200, 300)).Single().Model.Should().Be("gpt-5-codex");
    }

    [Fact]
    public void Turn_context_model_overrides_earlier_session_meta()
    {
        var meta = """{"type":"session_meta","timestamp":"2026-06-10T12:00:00Z","payload":{"id":"sess-1","model":"gpt-5"}}""";
        var ctx = """{"type":"turn_context","payload":{"model":"gpt-5-codex"}}""";
        var rows = Parse(meta + "\n" + ctx + "\n" + TokenCount(1000, 200, 300));
        rows.Single().Model.Should().Be("gpt-5-codex");
    }

    // ---- cwd / gitBranch / version flow ----

    [Fact]
    public void Context_fields_flow_from_session_meta_into_following_rows()
    {
        var meta = """{"type":"session_meta","timestamp":"2026-06-10T12:00:00Z","payload":{"id":"sess-1","cwd":"C:/work/proj","cli_version":"0.9.0","model":"gpt-5","git":{"branch":"main"}}}""";
        var row = Parse(meta + "\n" + TokenCount(1000, 200, 300)).Single();
        row.Cwd.Should().Be("C:/work/proj");
        row.GitBranch.Should().Be("main");
        row.Version.Should().Be("0.9.0");
    }

    [Fact]
    public void Cwd_can_come_from_turn_context()
    {
        var ctx = """{"type":"turn_context","payload":{"model":"gpt-5-codex","cwd":"C:/work/other"}}""";
        Parse(ctx + "\n" + TokenCount(1000, 200, 300)).Single().Cwd.Should().Be("C:/work/other");
    }

    // ---- SessionId ----

    [Fact]
    public void Session_id_comes_from_session_meta_payload_id()
    {
        var meta = """{"type":"session_meta","timestamp":"2026-06-10T12:00:00Z","payload":{"id":"explicit-session","model":"gpt-5"}}""";
        Parse(meta + "\n" + TokenCount(1000, 200, 300)).Single().SessionId.Should().Be("explicit-session");
    }

    [Fact]
    public void Session_id_is_derived_from_uuid_in_filename_when_no_meta()
    {
        Parse(TokenCount(1000, 200, 300)).Single().SessionId.Should().Be(DerivedSessionId);
    }

    [Fact]
    public void Session_id_falls_back_to_filename_without_extension()
    {
        // No session_meta id and no UUID in the name => file name sans extension.
        var rows = Parse(TokenCount(1000, 200, 300), file: "rollout-plain.jsonl");
        rows.Single().SessionId.Should().Be("rollout-plain");
    }

    // ---- DedupKey & ordinal ----

    [Fact]
    public void Dedup_key_uses_session_and_ordinal()
    {
        var meta = """{"type":"session_meta","timestamp":"2026-06-10T12:00:00Z","payload":{"id":"sess-1","model":"gpt-5"}}""";
        Parse(meta + "\n" + TokenCount(1000, 200, 300)).Single().DedupKey.Should().Be("codex|sess-1|1");
    }

    [Fact]
    public void Ordinal_increments_across_multiple_real_events()
    {
        var jsonl = TokenCount(1000, 200, 300) + "\n" + TokenCount(2000, 0, 400);
        var rows = Parse(jsonl);
        rows.Should().HaveCount(2);
        rows[0].DedupKey.Should().Be($"codex|{DerivedSessionId}|1");
        rows[1].DedupKey.Should().Be($"codex|{DerivedSessionId}|2");
    }

    [Fact]
    public void Zero_spend_event_is_skipped_but_still_consumes_an_ordinal()
    {
        // First event is input=0/output=0 (ordinal 1, skipped); the real event is ordinal 2.
        var jsonl = TokenCount(0, 0, 0) + "\n" + TokenCount(1000, 200, 300);
        var rows = Parse(jsonl);
        rows.Should().HaveCount(1);
        rows.Single().DedupKey.Should().Be($"codex|{DerivedSessionId}|2");
    }

    // ---- Timestamp ----

    [Fact]
    public void Timestamp_is_parsed_to_utc()
    {
        var row = Parse(TokenCount(1000, 200, 300, ts: "2026-06-10T12:00:00Z")).Single();
        row.TimestampUtc.Should().Be(new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Missing_or_unparseable_timestamp_falls_back_to_unix_epoch()
    {
        var line = """{"type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":1000,"cached_input_tokens":0,"output_tokens":300}}}}""";
        Parse(line).Single().TimestampUtc.Should().Be(DateTimeOffset.UnixEpoch.UtcDateTime);
    }

    // ---- Ignored / malformed input ----

    [Fact]
    public void Non_token_count_event_payloads_are_ignored()
    {
        var line = """{"type":"event_msg","timestamp":"2026-06-10T12:00:00Z","payload":{"type":"agent_message","text":"hi"}}""";
        Parse(line).Should().BeEmpty();
    }

    [Fact]
    public void Unknown_top_level_types_are_ignored()
    {
        var line = """{"type":"something_new","payload":{"foo":"bar"}}""";
        Parse(line).Should().BeEmpty();
    }

    [Fact]
    public void Malformed_json_lines_are_skipped()
    {
        var jsonl = "this is not json\n" + TokenCount(1000, 200, 300);
        Parse(jsonl).Should().HaveCount(1);
    }

    [Fact]
    public void Blank_lines_are_skipped()
    {
        var jsonl = "\n" + TokenCount(1000, 200, 300) + "\n";
        Parse(jsonl).Should().HaveCount(1);
    }
}
