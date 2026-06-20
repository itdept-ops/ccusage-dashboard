using Ccusage.Api.Services;
using FluentAssertions;

namespace Ccusage.Api.Tests.Unit;

/// <summary>
/// The pure "find a time" engine <see cref="SlotFinder.FindFreeSlots"/> (Family Hub F6b). Covers: the
/// intersection of multiple members' free time; clipping to the local workday window; respecting the
/// requested duration; the earliest-first ordering + the ~12-slot cap; and degenerate inputs (all-busy →
/// none, bad duration/window → none). Times are UTC; the workday clipping runs in a fixed UTC timezone so
/// the assertions stay clock-independent.
/// </summary>
public class SlotFinderTests
{
    // Use UTC as the household zone so local workday hours == UTC hours (no DST to reason about in unit math).
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static DateTime At(int day, int hour, int min = 0) =>
        new(2026, 6, day, hour, min, 0, DateTimeKind.Utc);

    private static SlotFinder.MemberBusy Busy(params (DateTime, DateTime)[] blocks) =>
        new(blocks.ToList());

    private static SlotFinder.MemberBusy Free() => new(Array.Empty<(DateTime, DateTime)>());

    // =====================================================================================
    // Basic free/busy within a single workday
    // =====================================================================================

    [Fact]
    public void Single_free_member_gets_back_to_back_slots_across_the_workday()
    {
        // One member, no busy blocks, a 9–17 workday on June 1, 60-minute slots.
        var slots = SlotFinder.FindFreeSlots(
            new[] { Free() }, At(1, 0), At(2, 0), durationMinutes: 60,
            workdayStartHourLocal: 9, workdayEndHourLocal: 17, Utc);

        // 9–17 is 8 hours → 8 one-hour slots, earliest first, starting at 09:00.
        slots.Should().HaveCount(8);
        slots[0].StartUtc.Should().Be(At(1, 9));
        slots[0].EndUtc.Should().Be(At(1, 10));
        slots[^1].StartUtc.Should().Be(At(1, 16));
        slots[^1].EndUtc.Should().Be(At(1, 17));
        // Every slot is exactly 60 minutes and ordered.
        slots.Should().BeInAscendingOrder(s => s.StartUtc);
        slots.Should().OnlyContain(s => s.EndUtc - s.StartUtc == TimeSpan.FromMinutes(60));
    }

    [Fact]
    public void Intersection_of_two_members_busy_blocks_only_yields_commonly_free_time()
    {
        // Workday 9–17 on June 1. Alice busy 9–12; Bob busy 14–17. Common free = 12–14 → two 60-min slots.
        var alice = Busy((At(1, 9), At(1, 12)));
        var bob = Busy((At(1, 14), At(1, 17)));

        var slots = SlotFinder.FindFreeSlots(
            new[] { alice, bob }, At(1, 0), At(2, 0), durationMinutes: 60,
            workdayStartHourLocal: 9, workdayEndHourLocal: 17, Utc);

        slots.Should().HaveCount(2);
        slots[0].StartUtc.Should().Be(At(1, 12));
        slots[0].EndUtc.Should().Be(At(1, 13));
        slots[1].StartUtc.Should().Be(At(1, 13));
        slots[1].EndUtc.Should().Be(At(1, 14));
    }

    [Fact]
    public void Overlapping_busy_blocks_merge_so_a_gap_must_fit_the_duration()
    {
        // Busy 9–11 and 10:30–13 overlap → merged busy 9–13. Free 13–17 → 90-min slots: 13–14:30, 14:30–16.
        var member = Busy((At(1, 9), At(1, 11)), (At(1, 10, 30), At(1, 13)));

        var slots = SlotFinder.FindFreeSlots(
            new[] { member }, At(1, 0), At(2, 0), durationMinutes: 90,
            workdayStartHourLocal: 9, workdayEndHourLocal: 17, Utc);

        slots.Should().HaveCount(2);
        slots[0].StartUtc.Should().Be(At(1, 13));
        slots[0].EndUtc.Should().Be(At(1, 14, 30));
        slots[1].StartUtc.Should().Be(At(1, 14, 30));
        slots[1].EndUtc.Should().Be(At(1, 16));
    }

    // =====================================================================================
    // Workday clipping
    // =====================================================================================

    [Fact]
    public void Slots_are_clipped_to_the_workday_window_even_when_the_search_window_is_wider()
    {
        // The search window is the whole day (00–24) but the workday is 13–15 → only 13–14, 14–15 emit.
        var slots = SlotFinder.FindFreeSlots(
            new[] { Free() }, At(1, 0), At(2, 0), durationMinutes: 60,
            workdayStartHourLocal: 13, workdayEndHourLocal: 15, Utc);

        slots.Should().HaveCount(2);
        var lower = At(1, 13);
        var upper = At(1, 15);
        slots.Should().OnlyContain(s => s.StartUtc >= lower && s.EndUtc <= upper);
    }

    [Fact]
    public void Busy_block_spanning_the_workday_boundary_clips_to_within_the_workday()
    {
        // Workday 9–17; member busy 8–10 (starts before the workday). Free 10–17 → 7 hourly slots from 10:00.
        var member = Busy((At(1, 8), At(1, 10)));

        var slots = SlotFinder.FindFreeSlots(
            new[] { member }, At(1, 0), At(2, 0), durationMinutes: 60,
            workdayStartHourLocal: 9, workdayEndHourLocal: 17, Utc);

        slots.Should().HaveCount(7);
        slots[0].StartUtc.Should().Be(At(1, 10));
    }

    [Fact]
    public void Multi_day_window_emits_slots_inside_each_days_workday_only()
    {
        // Two full days, workday 9–11 (two hourly slots/day) → 4 slots total, none outside 9–11 local.
        var slots = SlotFinder.FindFreeSlots(
            new[] { Free() }, At(1, 0), At(3, 0), durationMinutes: 60,
            workdayStartHourLocal: 9, workdayEndHourLocal: 11, Utc);

        slots.Should().HaveCount(4);
        slots[0].StartUtc.Should().Be(At(1, 9));
        slots[1].StartUtc.Should().Be(At(1, 10));
        slots[2].StartUtc.Should().Be(At(2, 9));
        slots[3].StartUtc.Should().Be(At(2, 10));
    }

    // =====================================================================================
    // Duration
    // =====================================================================================

    [Fact]
    public void A_gap_smaller_than_the_duration_yields_no_slot()
    {
        // Workday 9–17, busy 10–16 → only free gaps are 9–10 and 16–17 (one hour each). A 90-min ask fits none.
        var member = Busy((At(1, 10), At(1, 16)));

        var slots = SlotFinder.FindFreeSlots(
            new[] { member }, At(1, 0), At(2, 0), durationMinutes: 90,
            workdayStartHourLocal: 9, workdayEndHourLocal: 17, Utc);

        slots.Should().BeEmpty();
    }

    [Fact]
    public void A_long_duration_uses_the_whole_free_gap()
    {
        // Workday 9–17 free → a single 8-hour (480-min) slot fills it exactly.
        var slots = SlotFinder.FindFreeSlots(
            new[] { Free() }, At(1, 0), At(2, 0), durationMinutes: 480,
            workdayStartHourLocal: 9, workdayEndHourLocal: 17, Utc);

        slots.Should().HaveCount(1);
        slots[0].StartUtc.Should().Be(At(1, 9));
        slots[0].EndUtc.Should().Be(At(1, 17));
    }

    // =====================================================================================
    // Caps + degenerate inputs
    // =====================================================================================

    [Fact]
    public void Result_is_capped_at_twelve_slots()
    {
        // A 24h workday over 2 days with 30-min slots would be 96 slots; the engine caps at 12.
        var slots = SlotFinder.FindFreeSlots(
            new[] { Free() }, At(1, 0), At(3, 0), durationMinutes: 30,
            workdayStartHourLocal: 0, workdayEndHourLocal: 24, Utc);

        slots.Should().HaveCount(SlotFinder.MaxSlots);
        slots.Should().BeInAscendingOrder(s => s.StartUtc);
    }

    [Fact]
    public void All_busy_across_the_workday_yields_no_slots()
    {
        // Two members between them cover the entire 9–17 workday → no common free time.
        var alice = Busy((At(1, 9), At(1, 13)));
        var bob = Busy((At(1, 13), At(1, 17)));

        var slots = SlotFinder.FindFreeSlots(
            new[] { alice, bob }, At(1, 0), At(2, 0), durationMinutes: 30,
            workdayStartHourLocal: 9, workdayEndHourLocal: 17, Utc);

        slots.Should().BeEmpty();
    }

    [Fact]
    public void Non_positive_duration_or_inverted_window_yields_no_slots()
    {
        SlotFinder.FindFreeSlots(new[] { Free() }, At(1, 0), At(2, 0), 0, 9, 17, Utc).Should().BeEmpty();
        SlotFinder.FindFreeSlots(new[] { Free() }, At(1, 0), At(2, 0), -30, 9, 17, Utc).Should().BeEmpty();
        // Inverted window (end <= start).
        SlotFinder.FindFreeSlots(new[] { Free() }, At(2, 0), At(1, 0), 60, 9, 17, Utc).Should().BeEmpty();
        // Empty workday (start >= end).
        SlotFinder.FindFreeSlots(new[] { Free() }, At(1, 0), At(2, 0), 60, 17, 9, Utc).Should().BeEmpty();
    }

    [Fact]
    public void No_members_means_the_whole_workday_is_free()
    {
        // An empty member set => no busy constraints => the workday is fully free.
        var slots = SlotFinder.FindFreeSlots(
            Array.Empty<SlotFinder.MemberBusy>(), At(1, 0), At(2, 0), durationMinutes: 60,
            workdayStartHourLocal: 9, workdayEndHourLocal: 12, Utc);

        slots.Should().HaveCount(3);
    }
}
