using System.Globalization;
using System.Text.RegularExpressions;

namespace Ccusage.Api.Services;

/// <summary>
/// A deliberately LIGHT natural-time parser for Family Hub quick-add (F7). It recognizes the handful of
/// everyday phrases people actually type into a quick box — "tomorrow 9am", "in 30 min", "at 4pm",
/// "friday", "tonight" — resolves them against the household's local timezone, and returns the matching
/// UTC instant plus the input text with the time phrase removed. This is NOT a full date grammar: it's
/// scoped to the quick-add use case, biased toward the next future occurrence, and falls back gracefully
/// (no match → caller decides the default, e.g. +1 hour).
/// </summary>
public static class NaturalTime
{
    // ---- Phrase shapes we recognize (each captures the slice to strip from the saved text) ----

    // "in 30 min", "in 2 hours", "in 1 day", "in 3 weeks"
    private static readonly Regex RelIn = new(
        @"\bin\s+(?<n>\d{1,4})\s*(?<unit>min(?:ute)?s?|h(?:ou)?rs?|days?|weeks?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // a clock time: "9am", "9:30 pm", "at 4 pm", "at 14:00", "at noon", "at midnight"
    private static readonly Regex Clock = new(
        @"\b(?:at\s+)?(?:(?<h>\d{1,2})(?::(?<m>\d{2}))?\s*(?<ap>am|pm)\b|(?<h24>\d{1,2}):(?<m24>\d{2})\b|(?<word>noon|midnight)\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // a day anchor: today / tonight / tomorrow / a weekday name (optionally "this/next friday")
    private static readonly Regex Day = new(
        @"\b(?<day>today|tonight|tomorrow|(?:this\s+|next\s+)?(?:mon|tues?|wed(?:nes)?|thur?s?|fri|sat(?:ur)?|sun)(?:day)?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>True if the text contains any phrase the parser would act on — used for "auto" routing.</summary>
    public static bool HasTimePhrase(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return RelIn.IsMatch(text) || Day.IsMatch(text) || Clock.IsMatch(text);
    }

    /// <summary>
    /// Parse a due time out of <paramref name="text"/>. Returns the resolved <c>DueUtc</c> and the text with
    /// the matched time phrase(s) removed. When nothing is recognized, returns (nowUtc + 1 hour, original
    /// text). All local-time math is done in <paramref name="timeZoneId"/> (IANA); storage is UTC.
    /// </summary>
    public static (DateTime DueUtc, string StrippedText) Parse(string text, string? timeZoneId, DateTime nowUtc)
    {
        var fallback = (nowUtc.AddHours(1), text);
        if (string.IsNullOrWhiteSpace(text)) return fallback;

        var tz = ResolveZone(timeZoneId);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), tz);

        var remaining = text;
        var matched = false;

        // 1) Relative "in N units" — unambiguous, so handle it first and short-circuit.
        var inMatch = RelIn.Match(remaining);
        if (inMatch.Success)
        {
            var n = int.Parse(inMatch.Groups["n"].Value, CultureInfo.InvariantCulture);
            var unit = inMatch.Groups["unit"].Value.ToLowerInvariant();
            var localDue = unit switch
            {
                var u when u.StartsWith("min") => localNow.AddMinutes(n),
                var u when u.StartsWith("h") => localNow.AddHours(n),
                var u when u.StartsWith("day") => localNow.AddDays(n),
                _ => localNow.AddDays(n * 7), // weeks
            };
            if (localDue <= localNow) localDue = localNow.AddMinutes(1); // "in 0 min" → just ahead, never the past
            var stripped = Remove(remaining, inMatch);
            return (ToUtc(localDue, tz), Clean(stripped));
        }

        // 2) Day anchor (today/tonight/tomorrow/weekday) and/or a clock time. Combine whichever appear.
        var localDate = localNow.Date;
        var haveDay = false;

        var dayMatch = Day.Match(remaining);
        if (dayMatch.Success)
        {
            localDate = ResolveDay(dayMatch.Groups["day"].Value, localNow);
            remaining = Remove(remaining, dayMatch);
            matched = true;
            haveDay = true;
        }

        // Default time-of-day: 9am for a bare day, but "tonight" implies the evening (7pm).
        var hour = 9;
        var minute = 0;
        var haveClock = false;
        if (dayMatch.Success && dayMatch.Groups["day"].Value.Trim().ToLowerInvariant() == "tonight")
            hour = 19;

        var clockMatch = Clock.Match(remaining);
        if (clockMatch.Success && TryClock(clockMatch, out var ch, out var cm))
        {
            hour = ch;
            minute = cm;
            remaining = Remove(remaining, clockMatch);
            matched = true;
            haveClock = true;
        }

        if (!matched) return fallback;

        var due = localDate.AddHours(hour).AddMinutes(minute);

        // Bias to the future: ANY resolved time already in the past rolls to the next day, so "at 8am"
        // said at 9am (or "today 8am" this evening) means tomorrow morning, not a past instant that would
        // fire immediately. Weekday anchors already resolve to a future date, so this only nudges
        // today/tonight + bare-clock cases.
        if (due <= localNow)
            due = due.AddDays(1);

        return (ToUtc(due, tz), Clean(remaining));
    }

    /// <summary>
    /// Strip a leading reminder lead-in ("remind me to ", "remember to ", "remind me ") so the stored
    /// reminder text is the action itself, not the command. Case-insensitive; leaves other text untouched.
    /// </summary>
    public static string StripReminderLeadIn(string text) =>
        Regex.Replace((text ?? "").TrimStart(),
            @"^(?:remind\s+me\s+(?:to\s+)?|remember\s+to\s+|remind\s+to\s+)",
            "", RegexOptions.IgnoreCase).TrimStart();

    // ---- internals ----

    private static bool TryClock(Match m, out int hour, out int minute)
    {
        hour = 0; minute = 0;
        if (m.Groups["word"].Success)
        {
            var w = m.Groups["word"].Value.ToLowerInvariant();
            hour = w == "noon" ? 12 : 0; // midnight → 00:00
            return true;
        }
        if (m.Groups["h24"].Success)
        {
            var h = int.Parse(m.Groups["h24"].Value, CultureInfo.InvariantCulture);
            var mm = int.Parse(m.Groups["m24"].Value, CultureInfo.InvariantCulture);
            if (h > 23 || mm > 59) return false;
            hour = h; minute = mm;
            return true;
        }
        if (m.Groups["h"].Success && m.Groups["ap"].Success)
        {
            var h = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
            if (h < 1 || h > 12) return false;
            var mm = m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
            if (mm > 59) return false;
            var pm = m.Groups["ap"].Value.ToLowerInvariant() == "pm";
            hour = (h % 12) + (pm ? 12 : 0); // 12am→0, 12pm→12
            minute = mm;
            return true;
        }
        return false;
    }

    /// <summary>Resolve a day word to a concrete local date (date-only; time is applied by the caller).</summary>
    private static DateTime ResolveDay(string dayRaw, DateTime localNow)
    {
        var d = dayRaw.Trim().ToLowerInvariant();
        if (d is "today" or "tonight") return localNow.Date;
        if (d == "tomorrow") return localNow.Date.AddDays(1);

        var wantNext = d.StartsWith("next");
        var token = d.Replace("this", "").Replace("next", "").Trim();
        var target = WeekdayFromToken(token);
        if (target is null) return localNow.Date;

        // Days ahead to the next occurrence of the target weekday (a plain weekday name means the SOONEST
        // upcoming one, never today; "next friday" pushes a further week out).
        var delta = ((int)target.Value - (int)localNow.DayOfWeek + 7) % 7;
        if (delta == 0) delta = 7;           // same weekday → next week, not today
        if (wantNext && delta <= 7) delta += 7; // "next" → the following week's occurrence
        return localNow.Date.AddDays(delta);
    }

    private static DayOfWeek? WeekdayFromToken(string token) => token switch
    {
        _ when token.StartsWith("mon") => DayOfWeek.Monday,
        _ when token.StartsWith("tue") => DayOfWeek.Tuesday,
        _ when token.StartsWith("wed") => DayOfWeek.Wednesday,
        _ when token.StartsWith("thu") => DayOfWeek.Thursday,
        _ when token.StartsWith("fri") => DayOfWeek.Friday,
        _ when token.StartsWith("sat") => DayOfWeek.Saturday,
        _ when token.StartsWith("sun") => DayOfWeek.Sunday,
        _ => null,
    };

    private static string Remove(string text, Match m) =>
        text.Remove(m.Index, m.Length);

    /// <summary>Tidy leftover whitespace/connector words after pulling a phrase out of the middle.</summary>
    private static string Clean(string text)
    {
        var s = Regex.Replace(text, @"\s{2,}", " ").Trim();
        // Drop a dangling leading/trailing connector left behind (e.g. "call mom on" → "call mom").
        s = Regex.Replace(s, @"\s+(?:on|at|by)\s*$", "", RegexOptions.IgnoreCase).Trim();
        s = Regex.Replace(s, @"^(?:on|at|by)\s+", "", RegexOptions.IgnoreCase).Trim();
        return s;
    }

    private static DateTime ToUtc(DateTime local, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), tz);

    /// <summary>Resolve an IANA id to a <see cref="TimeZoneInfo"/>, falling back to UTC on anything odd.</summary>
    private static TimeZoneInfo ResolveZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return TimeZoneInfo.Utc; }
    }
}
