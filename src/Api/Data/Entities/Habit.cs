namespace Ccusage.Api.Data.Entities;

/// <summary>
/// How often a <see cref="Habit"/> is EXPECTED — the NET-NEW concept that generalises 75-Hard's fixed
/// every-day cadence. The cadence drives the CADENCE-AWARE streak fold: only the periods/days the cadence
/// expects are counted; a day the cadence does not expect is neither a miss nor an advance. Stored by int so
/// future cadences append.
/// </summary>
public enum HabitCadence
{
    /// <summary>Expected EVERY day (like 75-Hard). Each calendar day is a streak unit.</summary>
    Daily = 0,

    /// <summary>Expected ONCE per ISO week — the week is the streak unit (complete = the habit was done at
    /// least once in that week).</summary>
    Weekly = 1,

    /// <summary>Expected only on specific weekdays (see <see cref="Habit.DaysOfWeekMask"/>). Each EXPECTED
    /// weekday is a streak unit; non-expected weekdays are skipped (neither miss nor advance).</summary>
    CustomDaysOfWeek = 2,

    /// <summary>Expected a TARGET COUNT of times per period (see <see cref="Habit.TimesPerPeriod"/> +
    /// <see cref="Habit.PeriodDays"/>) — the rolling period is the streak unit (complete = the done-count in
    /// that period met the target).</summary>
    XTimesPerPeriod = 3,
}

/// <summary>The lifecycle of a habit. Unlike 75-Hard there is NO one-active invariant — a user can run many
/// habits at once — and the window is OPEN-ENDED (no fixed 75-day span). Stored by int.</summary>
public enum HabitStatus
{
    Active = 0,
    Paused = 1,
    Archived = 2,
}

/// <summary>
/// One user's configurable HABIT (the generalised successor to a 75-Hard task). Net-new and INDEPENDENT of the
/// live <see cref="HardChallenge"/> tables (those are untouched): a habit reuses the pure
/// <see cref="Services.HardChallengeScoring"/> task semantics (target/unit/partial-credit/auto-source) via an
/// in-memory shim, but has NO one-active invariant and an OPEN-ENDED window. Gated by <c>tracker.self</c>
/// (no dedicated permission). One owner per row, keyed by the lower-cased <see cref="UserEmail"/>.
/// </summary>
public class Habit
{
    public int Id { get; set; }

    /// <summary>Owner email, stored lower-cased; the identity key (owner-scoped on every endpoint).</summary>
    public string UserEmail { get; set; } = "";

    /// <summary>The owner's AppUser id, kept alongside the email for identity joins / leaderboard.</summary>
    public int UserId { get; set; }

    /// <summary>The user-facing title (e.g. "Read 10 pages"). PRIVATE: never emitted to the activity feed.</summary>
    public string Title { get; set; } = "";

    /// <summary>How often the habit is expected (drives the cadence-aware streak).</summary>
    public HabitCadence Cadence { get; set; } = HabitCadence.Daily;

    /// <summary>For <see cref="HabitCadence.CustomDaysOfWeek"/>: a 7-bit weekday mask, bit 0 = Sunday ..
    /// bit 6 = Saturday. Ignored for other cadences.</summary>
    public int DaysOfWeekMask { get; set; }

    /// <summary>For <see cref="HabitCadence.XTimesPerPeriod"/>: the target number of completions per rolling
    /// period. Ignored for other cadences.</summary>
    public int TimesPerPeriod { get; set; } = 1;

    /// <summary>For <see cref="HabitCadence.XTimesPerPeriod"/>: the rolling-period length in days (e.g. 7 for a
    /// week). Ignored for other cadences.</summary>
    public int PeriodDays { get; set; } = 7;

    /// <summary>The completion target for a MEASURABLE habit (e.g. pages, ml, minutes), or null for a BINARY
    /// (done/not-done) habit. Reuses <see cref="HardChallengeTask.TargetValue"/> semantics.</summary>
    public decimal? TargetValue { get; set; }

    /// <summary>The unit label for a measurable habit ("pages", "ml", "min", …), or "" for a binary habit.</summary>
    public string Unit { get; set; } = "";

    /// <summary>When true, a measurable habit earns PRO-RATED day progress (min(1, value/target)); ignored for
    /// a binary habit. Reuses <see cref="HardChallengeTask.PartialCredit"/> semantics.</summary>
    public bool PartialCredit { get; set; }

    /// <summary>Optional auto-credit source from the tracker (None/Water/Workout) — reuses
    /// <see cref="HardTaskAutoSource"/>. When set, the day's value is recomputed live from the tracker against
    /// this habit's own target (water ml / workout count) rather than hand-entered.</summary>
    public HardTaskAutoSource AutoSource { get; set; } = HardTaskAutoSource.None;

    /// <summary>For an <see cref="HardTaskAutoSource.Workout"/> auto habit: the minimum logged-exercise
    /// duration (minutes) that counts. Null ⇒ the scorer default. Ignored otherwise.</summary>
    public int? MinMinutes { get; set; }

    /// <summary>A short display color token (e.g. a hex or palette name), or "".</summary>
    public string Color { get; set; } = "";

    /// <summary>A short display icon token, or "".</summary>
    public string Icon { get; set; } = "";

    /// <summary>The day the habit began (open-ended: day-one anchor for the streak + calendar).</summary>
    public DateOnly StartDate { get; set; }

    /// <summary>An optional end date (open-ended when null — the habit runs indefinitely).</summary>
    public DateOnly? EndDate { get; set; }

    public HabitStatus Status { get; set; } = HabitStatus.Active;

    /// <summary>Cached current cadence-aware streak length (recomputed on read).</summary>
    public int CurrentStreak { get; set; }

    /// <summary>Cached longest cadence-aware streak length (recomputed on read).</summary>
    public int LongestStreak { get; set; }

    /// <summary>Cached count of completed units (recomputed on read).</summary>
    public int CompletedCount { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    /// <summary>The per-day rows for this habit (cascade-deleted with it).</summary>
    public List<HabitDay> Days { get; set; } = new();
}

/// <summary>
/// One calendar day's progress for a <see cref="Habit"/> (mirrors <see cref="HardChallengeDay"/> +
/// <see cref="HardChallengeDayTask"/> collapsed into one row, since a habit IS a single task). For a measurable
/// habit, <see cref="Value"/> holds the entered amount; for a binary habit, <see cref="Done"/> holds the
/// attestation. <see cref="Skip"/> is the "cheat/skip keeps the streak" lever (reused from 75-Hard's cheat day):
/// a skipped EXPECTED unit neither misses nor breaks the run. One row per (HabitId, LocalDate) — unique.
/// </summary>
public class HabitDay
{
    public long Id { get; set; }

    /// <summary>FK to the owning <see cref="Habit"/> (cascade-deleted with it).</summary>
    public int HabitId { get; set; }
    public Habit? Habit { get; set; }

    /// <summary>Owner email, denormalized + stored lower-cased (unique with <see cref="LocalDate"/> via HabitId).</summary>
    public string UserEmail { get; set; } = "";

    /// <summary>The calendar day, in the app's display timezone.</summary>
    public DateOnly LocalDate { get; set; }

    /// <summary>The entered value for a MEASURABLE habit (e.g. pages read), or null.</summary>
    public decimal? Value { get; set; }

    /// <summary>The attestation for a BINARY habit (done/not), or null.</summary>
    public bool? Done { get; set; }

    /// <summary>The skip/cheat flag — a skipped EXPECTED unit keeps the run counted without completing it
    /// (reuses the 75-Hard cheat-day lever). Defaults false.</summary>
    public bool Skip { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
