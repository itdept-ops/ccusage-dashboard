using Ccusage.Api.Ingestion;
using FluentAssertions;

namespace Ccusage.Api.Tests.Unit;

public class ClaudeParserTests
{
    private static List<ParsedUsage> Parse(string jsonl) =>
        new ClaudeParser().Parse(new StringReader(jsonl), "session.jsonl").ToList();

    private static string AssistantLine(string id = "msg_1", string body = "") =>
        $"{{\"type\":\"assistant\",\"message\":{{\"id\":\"{id}\",\"usage\":{{\"input_tokens\":1{body}}}}}}}";

    [Fact]
    public void Kind_is_claude()
    {
        new ClaudeParser().Kind.Should().Be("claude");
    }

    [Fact]
    public void MatchesFile_is_true_for_jsonl_extension_case_insensitive()
    {
        var p = new ClaudeParser();
        p.MatchesFile("a.jsonl").Should().BeTrue();
        p.MatchesFile("A.JSONL").Should().BeTrue();
        p.MatchesFile("path/to/Session.JsonL").Should().BeTrue();
    }

    [Fact]
    public void MatchesFile_is_false_for_other_extensions()
    {
        var p = new ClaudeParser();
        p.MatchesFile("a.json").Should().BeFalse();
        p.MatchesFile("a.jsonl.txt").Should().BeFalse();
        p.MatchesFile("jsonl").Should().BeFalse();
    }

    [Fact]
    public void Assistant_line_with_usage_and_id_produces_one_row()
    {
        var rows = Parse(AssistantLine());
        rows.Should().HaveCount(1);
        rows[0].Input.Should().Be(1);
    }

    [Fact]
    public void User_lines_are_skipped()
    {
        var jsonl = "{\"type\":\"user\",\"message\":{\"id\":\"u1\",\"usage\":{\"input_tokens\":5}}}";
        Parse(jsonl).Should().BeEmpty();
    }

    [Fact]
    public void Summary_lines_are_skipped()
    {
        var jsonl = "{\"type\":\"summary\",\"summary\":\"a recap\",\"leafUuid\":\"x\"}";
        Parse(jsonl).Should().BeEmpty();
    }

    [Fact]
    public void Assistant_line_without_usage_is_skipped()
    {
        var jsonl = "{\"type\":\"assistant\",\"message\":{\"id\":\"msg_1\",\"model\":\"claude-opus-4-8\"}}";
        Parse(jsonl).Should().BeEmpty();
    }

    [Fact]
    public void Assistant_line_with_empty_id_is_skipped()
    {
        var jsonl = "{\"type\":\"assistant\",\"message\":{\"id\":\"\",\"usage\":{\"input_tokens\":5}}}";
        Parse(jsonl).Should().BeEmpty();
    }

    [Fact]
    public void Assistant_line_with_missing_id_is_skipped()
    {
        var jsonl = "{\"type\":\"assistant\",\"message\":{\"usage\":{\"input_tokens\":5}}}";
        Parse(jsonl).Should().BeEmpty();
    }

    [Fact]
    public void DedupKey_is_message_id_pipe_request_id()
    {
        var jsonl = "{\"type\":\"assistant\",\"requestId\":\"req_42\",\"message\":{\"id\":\"msg_1\",\"usage\":{\"input_tokens\":1}}}";
        Parse(jsonl)[0].DedupKey.Should().Be("msg_1|req_42");
    }

    [Fact]
    public void DedupKey_has_trailing_pipe_when_request_id_missing()
    {
        Parse(AssistantLine(id: "msg_7"))[0].DedupKey.Should().Be("msg_7|");
    }

    [Fact]
    public void Two_identical_lines_yield_two_rows_with_same_dedup_key()
    {
        var line = "{\"type\":\"assistant\",\"requestId\":\"req_1\",\"message\":{\"id\":\"msg_1\",\"usage\":{\"input_tokens\":1}}}";
        var rows = Parse(line + "\n" + line);
        rows.Should().HaveCount(2);
        rows[0].DedupKey.Should().Be("msg_1|req_1");
        rows[1].DedupKey.Should().Be("msg_1|req_1");
    }

    [Fact]
    public void Model_defaults_to_unknown_when_missing()
    {
        Parse(AssistantLine())[0].Model.Should().Be("(unknown)");
    }

    [Fact]
    public void Model_defaults_to_unknown_when_empty()
    {
        var jsonl = "{\"type\":\"assistant\",\"message\":{\"id\":\"msg_1\",\"model\":\"\",\"usage\":{\"input_tokens\":1}}}";
        Parse(jsonl)[0].Model.Should().Be("(unknown)");
    }

    [Fact]
    public void Model_passes_through_when_present()
    {
        var jsonl = "{\"type\":\"assistant\",\"message\":{\"id\":\"msg_1\",\"model\":\"claude-opus-4-8\",\"usage\":{\"input_tokens\":1}}}";
        Parse(jsonl)[0].Model.Should().Be("claude-opus-4-8");
    }

    [Fact]
    public void Token_fields_are_read_from_usage()
    {
        var jsonl = "{\"type\":\"assistant\",\"message\":{\"id\":\"msg_1\",\"usage\":{\"input_tokens\":11,\"output_tokens\":22,\"cache_read_input_tokens\":33}}}";
        var r = Parse(jsonl)[0];
        r.Input.Should().Be(11);
        r.Output.Should().Be(22);
        r.CacheRead.Should().Be(33);
    }

    [Fact]
    public void Token_fields_default_to_zero_when_missing()
    {
        var jsonl = "{\"type\":\"assistant\",\"message\":{\"id\":\"msg_1\",\"usage\":{}}}";
        var r = Parse(jsonl)[0];
        r.Input.Should().Be(0);
        r.Output.Should().Be(0);
        r.CacheRead.Should().Be(0);
        r.Cache5m.Should().Be(0);
        r.Cache1h.Should().Be(0);
    }

    [Fact]
    public void Cache_writes_read_from_cache_creation_object()
    {
        var jsonl = "{\"type\":\"assistant\",\"message\":{\"id\":\"msg_1\",\"usage\":{\"cache_creation\":{\"ephemeral_5m_input_tokens\":100,\"ephemeral_1h_input_tokens\":200}}}}";
        var r = Parse(jsonl)[0];
        r.Cache5m.Should().Be(100);
        r.Cache1h.Should().Be(200);
    }

    [Fact]
    public void Flat_cache_creation_input_tokens_goes_into_cache5m_and_cache1h_stays_zero()
    {
        var jsonl = "{\"type\":\"assistant\",\"message\":{\"id\":\"msg_1\",\"usage\":{\"cache_creation_input_tokens\":500}}}";
        var r = Parse(jsonl)[0];
        r.Cache5m.Should().Be(500);
        r.Cache1h.Should().Be(0);
    }

    [Fact]
    public void Cache_creation_object_takes_precedence_over_flat_field()
    {
        var jsonl = "{\"type\":\"assistant\",\"message\":{\"id\":\"msg_1\",\"usage\":{\"cache_creation_input_tokens\":500,\"cache_creation\":{\"ephemeral_5m_input_tokens\":7,\"ephemeral_1h_input_tokens\":9}}}}";
        var r = Parse(jsonl)[0];
        r.Cache5m.Should().Be(7);
        r.Cache1h.Should().Be(9);
    }

    [Fact]
    public void Timestamp_is_parsed_as_utc()
    {
        var jsonl = "{\"type\":\"assistant\",\"timestamp\":\"2026-06-14T12:34:56Z\",\"message\":{\"id\":\"msg_1\",\"usage\":{\"input_tokens\":1}}}";
        var r = Parse(jsonl)[0];
        r.TimestampUtc.Should().Be(new DateTime(2026, 6, 14, 12, 34, 56, DateTimeKind.Utc));
        r.TimestampUtc.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void Timestamp_defaults_to_unix_epoch_when_missing()
    {
        var r = Parse(AssistantLine())[0];
        r.TimestampUtc.Should().Be(DateTimeOffset.UnixEpoch.UtcDateTime);
    }

    [Fact]
    public void Passthrough_fields_are_mapped()
    {
        var jsonl = "{\"type\":\"assistant\",\"sessionId\":\"sess_1\",\"cwd\":\"/home/app\",\"gitBranch\":\"main\",\"isSidechain\":true,\"agentId\":\"agent_x\",\"version\":\"1.2.3\",\"message\":{\"id\":\"msg_1\",\"usage\":{\"input_tokens\":1}}}";
        var r = Parse(jsonl)[0];
        r.SessionId.Should().Be("sess_1");
        r.Cwd.Should().Be("/home/app");
        r.GitBranch.Should().Be("main");
        r.IsSidechain.Should().BeTrue();
        r.AgentId.Should().Be("agent_x");
        r.Version.Should().Be("1.2.3");
    }

    [Fact]
    public void Passthrough_fields_default_when_missing()
    {
        var r = Parse(AssistantLine())[0];
        r.SessionId.Should().Be("");
        r.IsSidechain.Should().BeFalse();
        r.Cwd.Should().BeNull();
        r.GitBranch.Should().BeNull();
        r.AgentId.Should().BeNull();
        r.Version.Should().BeNull();
    }

    [Fact]
    public void Numeric_fields_accept_string_values()
    {
        var jsonl = "{\"type\":\"assistant\",\"message\":{\"id\":\"msg_1\",\"usage\":{\"input_tokens\":\"123\"}}}";
        Parse(jsonl)[0].Input.Should().Be(123);
    }

    [Fact]
    public void Malformed_json_lines_are_skipped_silently()
    {
        var jsonl = "{ this is not valid json";
        Parse(jsonl).Should().BeEmpty();
    }

    [Fact]
    public void Blank_lines_are_skipped()
    {
        var jsonl = "\n\n" + AssistantLine() + "\n\n";
        Parse(jsonl).Should().HaveCount(1);
    }

    [Fact]
    public void Malformed_line_in_middle_does_not_stop_later_valid_lines()
    {
        var jsonl = string.Join("\n",
            AssistantLine(id: "msg_a"),
            "{ broken json here",
            AssistantLine(id: "msg_b"));
        var rows = Parse(jsonl);
        rows.Should().HaveCount(2);
        rows.Select(r => r.DedupKey).Should().Contain(new[] { "msg_a|", "msg_b|" });
    }
}
