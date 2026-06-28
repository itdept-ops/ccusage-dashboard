using Ccusage.Api.Auth;
using Ccusage.Api.Data;
using Ccusage.Api.Data.Entities;
using Ccusage.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Ccusage.Api.Endpoints;

/// <summary>
/// Journal & Mood (<c>/api/journal</c>) — a private owner day-log, a near-exact sibling of the cycle day-log
/// (<see cref="CycleEndpoints"/>). Gated by <see cref="Permissions.TrackerSelf"/> (NO dedicated permission) and
/// OWNER-SCOPED: a caller only ever reads or edits their OWN entries (rows keyed by the caller's lower-cased
/// email; the per-date WHERE binds email+date).
///
/// <para>FREE-TEXT PRIVACY (the core invariant): the gratitude + reflection free-text is owner-only and is
/// NEVER put on the wire for any other viewer and NEVER sent to (or logged by) the AI. The optional weekly
/// reflection (<c>GET /reflection</c>) narrates ONLY an AGGREGATE projection — mood/energy/tag FREQUENCIES +
/// counts — and ALWAYS 200s with a deterministic plain floor when <see cref="Permissions.TrackerAi"/> is absent
/// or Gemini is off.</para>
/// </summary>
public static class JournalEndpoints
{
    // ---- Request DTOs ----

    /// <summary>A PARTIAL upsert of one day's journal entry. The date is required; every other field is optional
    /// and a field left null/absent is PRESERVED on an existing row (not cleared). Tags (when present) REPLACE
    /// the stored set. To clear a whole day use <c>DELETE /day?date=</c>.</summary>
    public sealed record DayRequest(
        DateOnly Date, string? Mood, int? Energy, IReadOnlyList<string>? Tags,
        string? GratitudeText, string? ReflectionText);

    // ---- Response DTOs ----

    /// <summary>One day's journal entry (owner-only; mirrors <see cref="JournalEntry"/>). Returned ONLY to the
    /// owner on their own GET — never to any other viewer, never to the AI.</summary>
    public sealed record EntryDto(
        DateOnly Date, string? Mood, int? Energy, IReadOnlyList<string> Tags,
        string? GratitudeText, string? ReflectionText, DateTime UpdatedUtc);

    /// <summary>A deterministic aggregate summary of the recent window (counts/frequencies only — no free-text).</summary>
    public sealed record SummaryDto(
        int DaysLogged, string? TopMood, string? TopTag, double? AvgEnergy);

    /// <summary>The main GET payload: the owner's recent entries (newest-date-first) + a deterministic summary.</summary>
    public sealed record JournalDto(IReadOnlyList<EntryDto> Entries, SummaryDto Summary);

    /// <summary>The gentle weekly reflection: the one-liner + whether it fell back to the deterministic plain
    /// floor (true when tracker.ai is absent or Gemini is off). ALWAYS 200.</summary>
    public sealed record ReflectionDto(string Note, bool FellBackToPlain);

    /// <summary>How many recent entries the GET returns (a generous window for the calendar + pattern note).</summary>
    private const int RecentCap = 120;
    /// <summary>Max tags persisted per day (the vocabulary is small; this caps a hostile payload).</summary>
    private const int MaxTagsPerDay = 12;
    private const int MaxGratitudeLen = 500;
    private const int MaxReflectionLen = 2000;

    /// <summary>The accepted MOOD vocabulary; a 1..5 numeric maps onto it (1=rough .. 5=great).</summary>
    private static readonly string[] MoodScale = { "rough", "low", "ok", "good", "great" };
    private static readonly IReadOnlySet<string> MoodVocab =
        new HashSet<string>(MoodScale, StringComparer.OrdinalIgnoreCase);

    /// <summary>The accepted TAG vocabulary (anything outside it is dropped on write).</summary>
    private static readonly IReadOnlySet<string> TagVocab = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "work", "family", "health", "sleep", "exercise", "social",
        "rest", "stress", "creative", "nature", "learning", "money",
    };

    public static void MapJournalEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/journal")
            .RequireAuthorization()
            .RequirePermission(Permissions.TrackerSelf);

        // ---- GET / : the owner's recent entries (newest-first) + a deterministic aggregate summary ----
        g.MapGet("/", async (CurrentUserAccessor me, UsageDbContext db, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;
            var entries = await db.JournalEntries.AsNoTracking()
                .Where(j => j.UserEmail == caller.Email)
                .OrderByDescending(j => j.LocalDate)
                .Take(RecentCap)
                .ToListAsync(ct);

            return Results.Ok(new JournalDto(
                entries.Select(ToDto).ToList(),
                BuildSummary(entries)));
        });

        // ---- PUT /day : PARTIAL upsert of one day's entry (owner-scoped) ----
        // Unspecified fields are PRESERVED on an existing row; tags, when present, REPLACE the stored set.
        g.MapPut("/day", async (
            DayRequest req, CurrentUserAccessor me, UsageDbContext db, CancellationToken ct) =>
        {
            if (ValidateDate(req.Date) is { } bad) return bad;
            var caller = (await me.GetUserAsync(ct))!;

            var now = DateTime.UtcNow;
            var row = await db.JournalEntries
                .FirstOrDefaultAsync(j => j.UserEmail == caller.Email && j.LocalDate == req.Date, ct);

            if (row is null)
            {
                row = new JournalEntry
                {
                    UserEmail = caller.Email, // already lower-cased in CurrentUserAccessor
                    UserId = caller.Id,
                    LocalDate = req.Date,
                    CreatedUtc = now,
                };
                db.JournalEntries.Add(row);
            }

            // PARTIAL: only overwrite a field the request actually carried; an absent field is preserved.
            if (req.Mood is not null) row.Mood = NormalizeMood(req.Mood);
            if (req.Energy is { } en) row.Energy = Math.Clamp(en, 1, 5);
            if (req.Tags is not null) row.Tags = NormalizeTags(req.Tags);
            if (req.GratitudeText is not null) row.GratitudeText = NormalizeText(req.GratitudeText, MaxGratitudeLen);
            if (req.ReflectionText is not null) row.ReflectionText = NormalizeText(req.ReflectionText, MaxReflectionLen);

            row.UpdatedUtc = now;
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (TrackerVisibility.IsUniqueViolation(ex))
            {
                // A concurrent insert raced the same (email, date); reload + re-apply onto the winner.
                db.ChangeTracker.Clear();
                row = await db.JournalEntries
                    .FirstAsync(j => j.UserEmail == caller.Email && j.LocalDate == req.Date, ct);
                if (req.Mood is not null) row.Mood = NormalizeMood(req.Mood);
                if (req.Energy is { } en2) row.Energy = Math.Clamp(en2, 1, 5);
                if (req.Tags is not null) row.Tags = NormalizeTags(req.Tags);
                if (req.GratitudeText is not null) row.GratitudeText = NormalizeText(req.GratitudeText, MaxGratitudeLen);
                if (req.ReflectionText is not null) row.ReflectionText = NormalizeText(req.ReflectionText, MaxReflectionLen);
                row.UpdatedUtc = now;
                await db.SaveChangesAsync(ct);
            }
            return Results.Ok(ToDto(row));
        });

        // ---- DELETE /day?date= : clear one whole day's entry (owner-scoped) ----
        g.MapDelete("/day", async (
            DateOnly? date, CurrentUserAccessor me, UsageDbContext db, CancellationToken ct) =>
        {
            if (date is not { } d) return Results.BadRequest(new { message = "A date is required." });
            if (ValidateDate(d) is { } bad) return bad;
            var caller = (await me.GetUserAsync(ct))!;

            // Owner-scoped: the WHERE binds both the date AND the caller's email.
            var deleted = await db.JournalEntries
                .Where(x => x.UserEmail == caller.Email && x.LocalDate == d)
                .ExecuteDeleteAsync(ct);
            return deleted == 0 ? Results.NotFound() : Results.NoContent();
        });

        // ---- GET /reflection : a gentle floored-AI WEEKLY reflection over an AGGREGATE projection ----
        // Gated by tracker.self (the group). ALWAYS 200: the deterministic plain floor when tracker.ai is absent
        // or Gemini is off. ONLY mood/energy/tag FREQUENCIES reach the model — NEVER the raw reflection/gratitude.
        g.MapGet("/reflection", async (
            CurrentUserAccessor me, UsageDbContext db, GeminiService gemini, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;
            var today = await TrackerVisibility.DisplayTzTodayAsync(db, ct);
            var weekStart = today.AddDays(-6);

            var week = await db.JournalEntries.AsNoTracking()
                .Where(j => j.UserEmail == caller.Email && j.LocalDate >= weekStart && j.LocalDate <= today)
                .ToListAsync(ct);

            var plain = ComposePlainReflection(week);

            // Plain reflection is the floor. The warm AI upgrade is only when the caller holds tracker.ai AND
            // Gemini is configured (a tracker.self-only caller never spends tokens). ALWAYS 200.
            if (!caller.Permissions.Contains(Permissions.TrackerAi) || !gemini.IsConfigured)
                return Results.Ok(new ReflectionDto(plain, true));

            string? ai = null;
            // ONLY the AGGREGATE projection (mood/energy/tag frequencies + counts) goes to the model — never a
            // raw reflection/gratitude entry, never a per-day row.
            try { ai = await gemini.JournalReflectionAsync(AggregateFacts(week), ct); }
            catch { ai = null; } // AI must never fail this endpoint — fall back to the plain floor.

            return string.IsNullOrWhiteSpace(ai)
                ? Results.Ok(new ReflectionDto(plain, true))
                : Results.Ok(new ReflectionDto(ai!, false));
        }).RequireRateLimiting("ai");
    }

    // =====================================================================================
    // Helpers
    // =====================================================================================

    private static IResult? ValidateDate(DateOnly date)
    {
        if (date == default)
            return Results.BadRequest(new { message = "A date is required." });
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (date < today.AddYears(-5) || date > today.AddYears(1))
            return Results.BadRequest(new { message = "That date is out of range." });
        return null;
    }

    /// <summary>Normalise a mood to the small vocabulary: a known word stays; a 1..5 numeric maps onto the
    /// scale (1=rough .. 5=great); an empty value clears; an unknown word is truncated + kept as-is.</summary>
    private static string? NormalizeMood(string? mood)
    {
        var m = mood?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(m)) return null;
        if (MoodVocab.Contains(m)) return m;
        if (int.TryParse(m, out var n) && n is >= 1 and <= 5) return MoodScale[n - 1];
        return m.Length > 32 ? m[..32] : m;
    }

    /// <summary>Normalise tags to the known vocabulary, de-duplicated + capped; unknown values dropped.</summary>
    private static List<string> NormalizeTags(IReadOnlyList<string> tags)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outList = new List<string>();
        foreach (var t in tags)
        {
            var v = t?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(v) || !TagVocab.Contains(v)) continue;
            if (!seen.Add(v)) continue;
            outList.Add(v);
            if (outList.Count >= MaxTagsPerDay) break;
        }
        return outList;
    }

    private static string? NormalizeText(string? text, int max)
    {
        var t = text?.Trim();
        return string.IsNullOrEmpty(t) ? null : (t.Length > max ? t[..max] : t);
    }

    private static EntryDto ToDto(JournalEntry j) => new(
        j.LocalDate, j.Mood, j.Energy, j.Tags, j.GratitudeText, j.ReflectionText, j.UpdatedUtc);

    /// <summary>The deterministic aggregate summary of the recent window — counts/frequencies only (the same
    /// non-free-text aggregates the AI may narrate). Empty-ish when there's little to say.</summary>
    private static SummaryDto BuildSummary(IReadOnlyList<JournalEntry> entries)
    {
        var topMood = entries
            .Where(e => !string.IsNullOrEmpty(e.Mood))
            .GroupBy(e => e.Mood!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(grp => grp.Count())
            .Select(grp => grp.Key)
            .FirstOrDefault();
        var topTag = entries
            .SelectMany(e => e.Tags)
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(grp => grp.Count())
            .Select(grp => grp.Key)
            .FirstOrDefault();
        var energies = entries.Where(e => e.Energy is { } en && en is >= 1 and <= 5).Select(e => e.Energy!.Value).ToList();
        double? avgEnergy = energies.Count > 0 ? Math.Round(energies.Average(), 1) : null;
        return new SummaryDto(entries.Count, topMood, topTag, avgEnergy);
    }

    /// <summary>The GUARANTEED deterministic plain reflection (the AI floor + the no-AI fallback). Gentle.</summary>
    private static string ComposePlainReflection(IReadOnlyList<JournalEntry> week)
    {
        if (week.Count == 0)
            return "Log a few days this week to see a gentle reflection here.";
        var s = BuildSummary(week);
        var parts = new List<string> { $"You journaled {s.DaysLogged} day{(s.DaysLogged == 1 ? "" : "s")} this week" };
        if (s.TopMood is not null) parts.Add($"most often feeling {s.TopMood}");
        if (s.AvgEnergy is { } e) parts.Add($"with an average energy of {e:0.#}/5");
        var sentence = string.Join(", ", parts) + ".";
        if (s.TopTag is not null) sentence += $" Your most-tagged theme was {s.TopTag}.";
        return sentence;
    }

    /// <summary>
    /// The compact AGGREGATE facts the model narrates (it invents nothing). STRICTLY counts/frequencies over
    /// moods/tags/energy — it deliberately NEVER emits the raw gratitude/reflection free-text, and never a
    /// single dated row. This is the ONLY journal data that ever reaches the AI.
    /// </summary>
    private static string AggregateFacts(IReadOnlyList<JournalEntry> week)
    {
        var lines = new List<string> { $"DAYS_LOGGED: {week.Count}" };

        foreach (var grp in week
                     .Where(e => !string.IsNullOrEmpty(e.Mood))
                     .GroupBy(e => e.Mood!, StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(g => g.Count()))
            lines.Add($"MOOD {grp.Key}: {grp.Count()}");

        foreach (var grp in week
                     .SelectMany(e => e.Tags)
                     .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(g => g.Count())
                     .Take(5))
            lines.Add($"TAG {grp.Key}: {grp.Count()}");

        var energies = week.Where(e => e.Energy is { } en && en is >= 1 and <= 5).Select(e => e.Energy!.Value).ToList();
        if (energies.Count > 0)
            lines.Add($"AVG_ENERGY_1TO5: {energies.Average():0.#}");

        return string.Join("\n", lines);
    }
}
