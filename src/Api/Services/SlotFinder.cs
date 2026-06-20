namespace Ccusage.Api.Services;

/// <summary>
/// A PURE, testable "find a time" engine (Family Hub F6b). Given each selected member's BUSY blocks over a
/// window, it computes the time that is FREE for EVERY member (the intersection of their per-member free
/// time, where free = the complement of busy), clipped to the household's local workday window
/// [workdayStartHourLocal, workdayEndHourLocal) on each day, and returns up to <see cref="MaxSlots"/>
/// candidate slots of the requested duration, earliest first.
///
/// No I/O, no clock, no DB — everything is a function of the inputs, so it unit-tests deterministically.
/// All instants are UTC; the workday clipping is done in the supplied <see cref="TimeZoneInfo"/>.
/// </summary>
public static class SlotFinder
{
    /// <summary>The most candidate slots we ever return (keeps the picker tidy + bounds the work).</summary>
    public const int MaxSlots = 12;

    /// <summary>A free window that fits the requested duration; emitted earliest-first.</summary>
    public readonly record struct Slot(DateTime StartUtc, DateTime EndUtc);

    /// <summary>One member's busy blocks (the member identity is irrelevant to the math — just the blocks).</summary>
    public readonly record struct MemberBusy(IReadOnlyList<(DateTime StartUtc, DateTime EndUtc)> Busy);

    /// <summary>
    /// Find up to <see cref="MaxSlots"/> candidate slots of <paramref name="durationMinutes"/> that are free
    /// for ALL members in <paramref name="busyByMember"/>, within the local workday window on each day of
    /// [<paramref name="windowStartUtc"/>, <paramref name="windowEndUtc"/>), earliest first.
    ///
    /// Algorithm:
    /// <list type="number">
    ///   <item>Build the set of BUSY intervals = the union of every member's busy blocks PLUS the
    ///   "off-hours" outside the workday window each local day (so a candidate never straddles the workday
    ///   bounds). This makes "free for all + inside the workday" a single complement problem.</item>
    ///   <item>Merge those busy intervals and walk the gaps between them inside the window; in each gap, emit
    ///   back-to-back duration-long slots until the gap can't hold another.</item>
    /// </list>
    /// Returns an empty list for a non-positive duration, an empty/inverted window, or when every moment is
    /// busy. A member with NO busy blocks simply contributes nothing (they're free the whole window).
    /// </summary>
    public static IReadOnlyList<Slot> FindFreeSlots(
        IEnumerable<MemberBusy> busyByMember,
        DateTime windowStartUtc,
        DateTime windowEndUtc,
        int durationMinutes,
        int workdayStartHourLocal,
        int workdayEndHourLocal,
        TimeZoneInfo tz)
    {
        var slots = new List<Slot>();
        if (durationMinutes <= 0) return slots;

        var windowStart = DateTime.SpecifyKind(windowStartUtc, DateTimeKind.Utc);
        var windowEnd = DateTime.SpecifyKind(windowEndUtc, DateTimeKind.Utc);
        if (windowEnd <= windowStart) return slots;

        var duration = TimeSpan.FromMinutes(durationMinutes);
        if (duration > windowEnd - windowStart) return slots;

        // Normalize the workday bounds; an invalid/empty window (start >= end) means "no workday" → no slots.
        var startHour = Math.Clamp(workdayStartHourLocal, 0, 24);
        var endHour = Math.Clamp(workdayEndHourLocal, 0, 24);
        if (endHour <= startHour) return slots;

        // ---- 1) Gather BUSY intervals (members' busy ∪ off-hours), each clipped to the window. ----
        var busy = new List<(DateTime Start, DateTime End)>();

        foreach (var member in busyByMember)
        {
            if (member.Busy is null) continue;
            foreach (var (s, e) in member.Busy)
            {
                var bs = DateTime.SpecifyKind(s, DateTimeKind.Utc);
                var be = DateTime.SpecifyKind(e, DateTimeKind.Utc);
                if (be <= bs) continue;
                var cs = bs < windowStart ? windowStart : bs;
                var ce = be > windowEnd ? windowEnd : be;
                if (ce > cs) busy.Add((cs, ce));
            }
        }

        // Off-hours: everything OUTSIDE the local workday window counts as busy, so a candidate stays inside
        // [startHour, endHour) local on its day. Walk local days across the window and add the gaps.
        AddOffHours(busy, windowStart, windowEnd, startHour, endHour, tz);

        // ---- 2) Merge busy intervals, then emit duration-long slots in each free gap. ----
        busy.Sort((a, b) => a.Start.CompareTo(b.Start));

        var cursor = windowStart;
        foreach (var (bStart, bEnd) in busy)
        {
            if (slots.Count >= MaxSlots) return slots;
            if (bStart > cursor)
                EmitGap(slots, cursor, bStart < windowEnd ? bStart : windowEnd, duration);
            if (bEnd > cursor) cursor = bEnd;
            if (cursor >= windowEnd) return slots;
        }
        if (cursor < windowEnd) EmitGap(slots, cursor, windowEnd, duration);

        return slots;
    }

    /// <summary>Emit back-to-back <paramref name="duration"/>-long slots filling [from, to), up to the cap.</summary>
    private static void EmitGap(List<Slot> slots, DateTime from, DateTime to, TimeSpan duration)
    {
        var start = from;
        while (start + duration <= to)
        {
            if (slots.Count >= MaxSlots) return;
            var end = start + duration;
            slots.Add(new Slot(start, end));
            start = end;
        }
    }

    /// <summary>
    /// Mark every instant OUTSIDE the local workday window as busy across the window. For each local calendar
    /// day the window touches, the busy spans are [dayStart, workdayStartLocal) and [workdayEndLocal,
    /// nextDayStart) — i.e. before and after the workday. Each span is converted local→UTC and clipped to the
    /// window. workdayEndHour==24 means "to midnight" (no trailing off-hours that day).
    /// </summary>
    private static void AddOffHours(
        List<(DateTime Start, DateTime End)> busy,
        DateTime windowStartUtc, DateTime windowEndUtc, int startHour, int endHour, TimeZoneInfo tz)
    {
        var localWindowStart = TimeZoneInfo.ConvertTimeFromUtc(windowStartUtc, tz);
        var localWindowEnd = TimeZoneInfo.ConvertTimeFromUtc(windowEndUtc, tz);

        // Iterate local dates from the window's first local day through its last, inclusive.
        var day = localWindowStart.Date;
        var lastDay = localWindowEnd.Date;
        // Guard against an absurd span (clamped window is already <=366d in the endpoint, but be safe).
        var safety = 0;
        while (day <= lastDay && safety++ < 800)
        {
            var workStartLocal = day.AddHours(startHour);
            var workEndLocal = endHour >= 24 ? day.AddDays(1) : day.AddHours(endHour);

            // Off-hours BEFORE the workday: [dayStart, workStartLocal).
            AddLocalBusy(busy, day, workStartLocal, windowStartUtc, windowEndUtc, tz);
            // Off-hours AFTER the workday: [workEndLocal, nextDayStart).
            AddLocalBusy(busy, workEndLocal, day.AddDays(1), windowStartUtc, windowEndUtc, tz);

            day = day.AddDays(1);
        }
    }

    /// <summary>Convert a local [start, end) span to UTC, clip to the window, and add it as busy if non-empty.</summary>
    private static void AddLocalBusy(
        List<(DateTime Start, DateTime End)> busy,
        DateTime localStart, DateTime localEnd, DateTime windowStartUtc, DateTime windowEndUtc, TimeZoneInfo tz)
    {
        if (localEnd <= localStart) return;
        var sUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localStart, DateTimeKind.Unspecified), tz);
        var eUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localEnd, DateTimeKind.Unspecified), tz);
        var cs = sUtc < windowStartUtc ? windowStartUtc : sUtc;
        var ce = eUtc > windowEndUtc ? windowEndUtc : eUtc;
        if (ce > cs) busy.Add((cs, ce));
    }
}
