using System.Text.Json;
using Ccusage.Api.Services;
using FluentAssertions;
using static Ccusage.Api.Services.GoogleCalendarService;

namespace Ccusage.Api.Tests.Unit;

/// <summary>
/// Family Hub F6 planner — recurrence rule construction in <see cref="GoogleCalendarService.BuildEventBody"/>
/// + <see cref="GoogleCalendarService.BuildRecurrenceRule"/> (no live Calendar call). Covers: a non-recurring
/// request omits the recurrence array (today's single-event behaviour); weekly repeats on the START weekday;
/// weekdays => MO–FR; daily/monthly; the series is always BOUNDED (a default COUNT when none given, an
/// explicit COUNT clamped, and UNTIL taking precedence and overriding COUNT).
/// </summary>
public class CalendarRecurrenceTests
{
    // A Tuesday (2026-06-23 is a Tuesday) at 16:00 UTC — the "soccer practice every Tuesday at 4pm" case.
    private static readonly DateTime TuesdayStart = new(2026, 6, 23, 16, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime TuesdayEnd = new(2026, 6, 23, 17, 0, 0, DateTimeKind.Utc);

    /// <summary>Serialize the anonymous event body and pull the recurrence array (or null when absent).</summary>
    private static IReadOnlyList<string>? RecurrenceOf(object body)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(body));
        if (!doc.RootElement.TryGetProperty("recurrence", out var rec)) return null;
        if (rec.ValueKind == JsonValueKind.Null) return null;
        rec.ValueKind.Should().Be(JsonValueKind.Array);
        return rec.EnumerateArray().Select(e => e.GetString()!).ToList();
    }

    [Fact]
    public void None_omits_the_recurrence_array_entirely()
    {
        var body = BuildEventBody(
            "Dentist", TuesdayStart, TuesdayEnd, allDay: false, location: null, description: null,
            Recurrence.None);

        RecurrenceOf(body).Should().BeNull();
    }

    [Fact]
    public void Weekly_repeats_on_the_start_weekday_with_a_default_count()
    {
        var body = BuildEventBody(
            "Soccer practice", TuesdayStart, TuesdayEnd, allDay: false, location: null, description: null,
            Recurrence.Weekly);

        var rec = RecurrenceOf(body);
        rec.Should().ContainSingle();
        // The start is a Tuesday => BYDAY=TU; bounded by the default COUNT (52).
        rec![0].Should().Be("RRULE:FREQ=WEEKLY;BYDAY=TU;COUNT=52");
    }

    [Fact]
    public void Weekdays_builds_a_Monday_through_Friday_rule()
    {
        var rule = BuildRecurrenceRule(Recurrence.Weekdays, TuesdayStart);
        rule.Should().Be("RRULE:FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR;COUNT=52");
    }

    [Fact]
    public void Daily_and_monthly_emit_their_frequency()
    {
        BuildRecurrenceRule(Recurrence.Daily, TuesdayStart).Should().Be("RRULE:FREQ=DAILY;COUNT=52");
        BuildRecurrenceRule(Recurrence.Monthly, TuesdayStart).Should().Be("RRULE:FREQ=MONTHLY;COUNT=52");
    }

    [Fact]
    public void None_returns_a_null_rule()
    {
        BuildRecurrenceRule(Recurrence.None, TuesdayStart).Should().BeNull();
    }

    [Fact]
    public void Explicit_count_is_used_and_clamped()
    {
        BuildRecurrenceRule(Recurrence.Daily, TuesdayStart, count: 10)
            .Should().Be("RRULE:FREQ=DAILY;COUNT=10");
        // Clamped to the upper bound (730) — never an unbounded/absurd series.
        BuildRecurrenceRule(Recurrence.Daily, TuesdayStart, count: 100000)
            .Should().Be("RRULE:FREQ=DAILY;COUNT=730");
        // Clamped to at least 1.
        BuildRecurrenceRule(Recurrence.Daily, TuesdayStart, count: 0)
            .Should().Be("RRULE:FREQ=DAILY;COUNT=1");
    }

    [Fact]
    public void Until_takes_precedence_over_count_and_is_a_utc_instant()
    {
        var until = new DateTime(2026, 12, 31, 23, 59, 0, DateTimeKind.Utc);
        var rule = BuildRecurrenceRule(Recurrence.Weekly, TuesdayStart, count: 10, untilUtc: until);

        // UNTIL wins (no COUNT), formatted as an RFC 5545 UTC instant (trailing Z).
        rule.Should().Be("RRULE:FREQ=WEEKLY;BYDAY=TU;UNTIL=20261231T235900Z");
        rule.Should().NotContain("COUNT=");
    }

    [Fact]
    public void All_day_recurring_event_still_carries_the_rule()
    {
        var body = BuildEventBody(
            "Trash day", TuesdayStart, TuesdayStart, allDay: true, location: null, description: null,
            Recurrence.Weekly);

        RecurrenceOf(body).Should().ContainSingle().Which.Should().StartWith("RRULE:FREQ=WEEKLY;BYDAY=TU");
    }
}
