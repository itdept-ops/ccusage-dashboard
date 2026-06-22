using Ccusage.Api.Data.Entities;
using Ccusage.Api.Endpoints;
using FluentAssertions;

namespace Ccusage.Api.Tests.Unit;

/// <summary>
/// The DETERMINISTIC title→role classifier behind calendar import (no AI). Covers: case-insensitive substring
/// match, no-match returns null, priority wins, longer-keyword tiebreak, and empty inputs.
/// </summary>
public class IdentityClassifyTests
{
    private static IdentityRule Rule(int id, string keyword, int roleId, int priority = 0) =>
        new() { Id = id, Keyword = keyword.ToLowerInvariant(), RoleId = roleId, Priority = priority };

    [Fact]
    public void Matches_keyword_as_case_insensitive_substring()
    {
        var rules = new[] { Rule(1, "soccer", roleId: 10), Rule(2, "standup", roleId: 20) };

        IdentityEndpoints.ClassifyTitle("Kids SOCCER practice", rules).Should().Be(10);
        IdentityEndpoints.ClassifyTitle("Daily standup", rules).Should().Be(20);
    }

    [Fact]
    public void Returns_null_when_no_rule_matches()
    {
        var rules = new[] { Rule(1, "soccer", roleId: 10) };
        IdentityEndpoints.ClassifyTitle("Dentist appointment", rules).Should().BeNull();
    }

    [Fact]
    public void Returns_null_for_blank_title_or_no_rules()
    {
        IdentityEndpoints.ClassifyTitle("", new[] { Rule(1, "x", 1) }).Should().BeNull();
        IdentityEndpoints.ClassifyTitle("anything", Array.Empty<IdentityRule>()).Should().BeNull();
    }

    [Fact]
    public void Higher_priority_wins_when_two_keywords_match()
    {
        // Both keywords are substrings of the title; the higher-priority rule wins regardless of order/length.
        var rules = new[]
        {
            Rule(1, "meeting", roleId: 10, priority: 0),
            Rule(2, "1:1", roleId: 20, priority: 5),
        };
        IdentityEndpoints.ClassifyTitle("1:1 meeting with Sam", rules).Should().Be(20);
    }

    [Fact]
    public void Longer_keyword_wins_when_priority_is_tied()
    {
        // Same priority → the more specific (longer) keyword wins.
        var rules = new[]
        {
            Rule(1, "gym", roleId: 10),
            Rule(2, "gymnastics", roleId: 20),
        };
        IdentityEndpoints.ClassifyTitle("Evening gymnastics class", rules).Should().Be(20);
    }
}
