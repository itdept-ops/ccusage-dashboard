using Ccusage.Api.Auth;
using Ccusage.Api.Data;
using Ccusage.Api.Data.Entities;
using Ccusage.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Ccusage.Api.Endpoints;

/// <summary>
/// Family Hub — Identity Map (/api/family/identity). PRIVATE + OWNER-SCOPED.
///
/// <para>A visual "web" of the ROLES a user plays (parent, coder, athlete…) and how much TIME goes into each.
/// Every endpoint is gated by <see cref="Permissions.IdentityMap"/> on top of <c>.RequireAuthorization()</c>
/// and is OWNER-SCOPED — a caller only ever reads or edits their OWN roles/time/rules (rows are keyed by the
/// caller's email; ids are owner-checked on mutate). NO email or another user's data ever appears.</para>
///
/// <para>Two data sources: MANUAL time entry (always available, the baseline) and an OPTIONAL "import from
/// calendar" that reuses the already-connected <see cref="GoogleCalendarService"/> to read the caller's OWN
/// primary events and classify them into roles by stored keyword <see cref="IdentityRule"/>s (deterministic
/// substring match — NO AI). Calendar dedup is by the filtered UNIQUE (UserEmail, SourceEventId) index, so
/// re-importing the same window never double-counts. Calendar is NEVER required — when unconfigured/unconnected
/// the import endpoints degrade gracefully (a 200 not-ready body, never a 500), exactly like the family calendar.</para>
/// </summary>
public static class IdentityEndpoints
{
    // ---- Request DTOs ----
    public sealed record RoleRequest(string? Name, string? Color, bool? Archived, int? SortOrder);
    public sealed record TimeRequest(int? RoleId, DateOnly? Date, int? Minutes, string? Note);
    public sealed record RuleRequest(string? Keyword, int? RoleId, int? Priority);
    public sealed record ImportPreviewRequest(DateTime? FromUtc, DateTime? ToUtc);
    public sealed record ImportItem(string SourceEventId, int RoleId, DateOnly Date, int Minutes, string? Note);
    public sealed record NewRule(string Keyword, int RoleId);
    public sealed record ImportCommitRequest(IReadOnlyList<ImportItem>? Items, IReadOnlyList<NewRule>? NewRules);

    // ---- Response DTOs ----
    public sealed record RoleDto(int Id, string Name, string Color, bool Archived, int SortOrder, DateTime CreatedUtc);
    public sealed record TimeEntryDto(
        int Id, int RoleId, DateOnly Date, int Minutes, string Source, string? Note, DateTime CreatedUtc);
    public sealed record RuleDto(int Id, string Keyword, int RoleId, int Priority);

    /// <summary>One slice of the radial web: a role's total minutes over the selected range.</summary>
    public sealed record RoleTotalDto(int RoleId, string RoleName, string Color, int Minutes);

    /// <summary>The main payload: the caller's roles + the per-role minutes aggregate over [from,to) + rules.</summary>
    public sealed record IdentityMapDto(
        IReadOnlyList<RoleDto> Roles, IReadOnlyList<RoleTotalDto> Totals,
        IReadOnlyList<RuleDto> Rules, DateTime FromUtc, DateTime ToUtc);

    /// <summary>Whether the OPTIONAL calendar import is available (configured + the caller connected).</summary>
    public sealed record CalendarStatusDto(bool Configured, bool Connected);

    /// <summary>A matched calendar event the import proposes (its rule already resolved a role).</summary>
    public sealed record ProposedTimeDto(string SourceEventId, string Title, DateOnly Date, int Minutes, int SuggestedRoleId);
    /// <summary>A calendar event no rule matched — the user picks a role (and may save it as a rule) before commit.</summary>
    public sealed record UnclassifiedDto(string SourceEventId, string Title, DateOnly Date, int Minutes);
    /// <summary>The preview: matched + unmatched events, and how many were skipped (already imported / all-day).</summary>
    public sealed record ImportPreviewDto(
        IReadOnlyList<ProposedTimeDto> Matched, IReadOnlyList<UnclassifiedDto> Unmatched,
        int AlreadyImported, int SkippedAllDay);
    public sealed record ImportCommitDto(int Imported, int Skipped);

    // ---- Caps + the allowed colour palette ----
    private const int MaxRoles = 64;
    private const int MaxNameLen = 64;
    private const int MaxKeywordLen = 128;
    private const int MaxNoteLen = 256;
    private const int RangeCapDays = 366;

    /// <summary>Default colours for new roles (mirrors the family calendar OVERLAY_PALETTE). A role colour must
    /// be one of these — free-text hex is rejected so nothing odd can be injected into the chart's CSS.</summary>
    private static readonly IReadOnlyList<string> Palette = new[]
    {
        "#3d8bff", "#22c55e", "#f59e0b", "#ef4444", "#a855f7",
        "#06b6d4", "#ec4899", "#84cc16", "#f97316", "#14b8a6",
        "#6366f1", "#eab308",
    };
    private static readonly IReadOnlySet<string> PaletteSet =
        new HashSet<string>(Palette, StringComparer.OrdinalIgnoreCase);

    public static void MapIdentityEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/family/identity")
            .RequireAuthorization()
            .RequirePermission(Permissions.IdentityMap);

        // ---- GET / : roles + per-role minutes over [from,to) + rules (owner-scoped) ----
        g.MapGet("/", async (
            DateTime? fromUtc, DateTime? toUtc, CurrentUserAccessor me, UsageDbContext db, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!; // identity.map filter guarantees non-null
            var (from, to) = Window(fromUtc, toUtc);
            var fromDate = DateOnly.FromDateTime(from);
            var toDate = DateOnly.FromDateTime(to);

            var roles = await db.IdentityRoles.AsNoTracking()
                .Where(r => r.UserEmail == caller.Email)
                .OrderBy(r => r.Archived).ThenBy(r => r.SortOrder).ThenBy(r => r.Id)
                .ToListAsync(ct);

            // Per-role minutes over the [from,to] day range (owner-scoped GroupBy/Sum).
            var sums = await db.IdentityTimeEntries.AsNoTracking()
                .Where(t => t.UserEmail == caller.Email && t.Date >= fromDate && t.Date <= toDate)
                .GroupBy(t => t.RoleId)
                .Select(grp => new { RoleId = grp.Key, Minutes = grp.Sum(x => x.Minutes) })
                .ToListAsync(ct);
            var sumByRole = sums.ToDictionary(x => x.RoleId, x => x.Minutes);

            var rules = await db.IdentityRules.AsNoTracking()
                .Where(r => r.UserEmail == caller.Email)
                .OrderByDescending(r => r.Priority).ThenBy(r => r.Keyword)
                .ToListAsync(ct);

            // Totals: every role that has time in the window (archived included so history still renders).
            var totals = roles
                .Select(r => new RoleTotalDto(r.Id, r.Name, r.Color, sumByRole.GetValueOrDefault(r.Id, 0)))
                .Where(t => t.Minutes > 0)
                .OrderByDescending(t => t.Minutes)
                .ToList();

            return Results.Ok(new IdentityMapDto(
                roles.Select(ToRoleDto).ToList(), totals, rules.Select(ToRuleDto).ToList(), from, to));
        });

        // ---- POST /roles : create a role (name + colour) ----
        g.MapPost("/roles", async (
            RoleRequest req, CurrentUserAccessor me, UsageDbContext db, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;
            if (NormalizeName(req.Name) is not { } name)
                return Results.BadRequest(new { message = "A role name (1–64 characters) is required." });
            if (NormalizeColor(req.Color) is not { } color)
                return Results.BadRequest(new { message = "That colour isn't an allowed palette colour." });

            // A sane per-user cap so a hostile client can't mint unbounded roles.
            if (await db.IdentityRoles.CountAsync(r => r.UserEmail == caller.Email, ct) >= MaxRoles)
                return Results.BadRequest(new { message = $"You can have at most {MaxRoles} roles." });

            var row = new IdentityRole
            {
                UserEmail = caller.Email, // already lower-cased in CurrentUserAccessor
                UserId = caller.Id,
                Name = name,
                Color = color,
                SortOrder = req.SortOrder ?? 0,
                CreatedUtc = DateTime.UtcNow,
            };
            db.IdentityRoles.Add(row);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                return Results.Conflict(new { message = "You already have a role with that name." });
            }
            return Results.Ok(ToRoleDto(row));
        });

        // ---- PATCH /roles/{id} : rename / recolor / archive / reorder one of the OWNER's roles ----
        g.MapPatch("/roles/{id:int}", async (
            int id, RoleRequest req, CurrentUserAccessor me, UsageDbContext db, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;
            var row = await db.IdentityRoles
                .FirstOrDefaultAsync(r => r.Id == id && r.UserEmail == caller.Email, ct);
            if (row is null) return Results.NotFound();

            if (req.Name is not null)
            {
                if (NormalizeName(req.Name) is not { } name)
                    return Results.BadRequest(new { message = "A role name (1–64 characters) is required." });
                row.Name = name;
            }
            if (req.Color is not null)
            {
                if (NormalizeColor(req.Color) is not { } color)
                    return Results.BadRequest(new { message = "That colour isn't an allowed palette colour." });
                row.Color = color;
            }
            if (req.Archived is { } archived) row.Archived = archived;
            if (req.SortOrder is { } sort) row.SortOrder = sort;

            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                return Results.Conflict(new { message = "You already have a role with that name." });
            }
            return Results.Ok(ToRoleDto(row));
        });

        // ---- DELETE /roles/{id} : delete a role + its time entries + rules (owner-scoped) ----
        g.MapDelete("/roles/{id:int}", async (
            int id, CurrentUserAccessor me, UsageDbContext db, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;
            // Owner-scoped: the WHERE binds both the id AND the caller's email, so a caller can never delete
            // another user's role even by guessing an id.
            var deleted = await db.IdentityRoles
                .Where(r => r.Id == id && r.UserEmail == caller.Email)
                .ExecuteDeleteAsync(ct);
            if (deleted == 0) return Results.NotFound();
            // Cascade the dependent owned rows (no DB FK cascade — keep it explicit + owner-scoped).
            await db.IdentityTimeEntries
                .Where(t => t.RoleId == id && t.UserEmail == caller.Email).ExecuteDeleteAsync(ct);
            await db.IdentityRules
                .Where(r => r.RoleId == id && r.UserEmail == caller.Email).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });

        // ---- POST /time : manual time log (the always-available baseline) ----
        g.MapPost("/time", async (
            TimeRequest req, CurrentUserAccessor me, UsageDbContext db, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;
            if (req.RoleId is not { } roleId)
                return Results.BadRequest(new { message = "A roleId is required." });
            if (req.Date is not { } date || DateOutOfRange(date))
                return Results.BadRequest(new { message = "A valid date is required." });
            if (req.Minutes is not { } mins || mins < 1)
                return Results.BadRequest(new { message = "Minutes must be at least 1." });

            // The role must belong to the caller (owner-scoped) — never attribute time to a foreign role.
            var owns = await db.IdentityRoles
                .AnyAsync(r => r.Id == roleId && r.UserEmail == caller.Email, ct);
            if (!owns) return Results.NotFound(new { message = "That role doesn't exist." });

            var row = new IdentityTimeEntry
            {
                UserEmail = caller.Email,
                UserId = caller.Id,
                RoleId = roleId,
                Date = date,
                Minutes = Math.Clamp(mins, 1, 1440),
                Source = IdentityEntrySource.Manual,
                SourceEventId = null,
                Note = NormalizeNote(req.Note),
                CreatedUtc = DateTime.UtcNow,
            };
            db.IdentityTimeEntries.Add(row);
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToTimeDto(row));
        });

        // ---- PATCH /time/{id} : edit a manual entry (role / date / minutes / note) ----
        g.MapPatch("/time/{id:int}", async (
            int id, TimeRequest req, CurrentUserAccessor me, UsageDbContext db, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;
            var row = await db.IdentityTimeEntries
                .FirstOrDefaultAsync(t => t.Id == id && t.UserEmail == caller.Email, ct);
            if (row is null) return Results.NotFound();

            if (req.RoleId is { } roleId)
            {
                var owns = await db.IdentityRoles
                    .AnyAsync(r => r.Id == roleId && r.UserEmail == caller.Email, ct);
                if (!owns) return Results.NotFound(new { message = "That role doesn't exist." });
                row.RoleId = roleId;
            }
            if (req.Date is { } date)
            {
                if (DateOutOfRange(date)) return Results.BadRequest(new { message = "That date is out of range." });
                row.Date = date;
            }
            if (req.Minutes is { } mins)
            {
                if (mins < 1) return Results.BadRequest(new { message = "Minutes must be at least 1." });
                row.Minutes = Math.Clamp(mins, 1, 1440);
            }
            if (req.Note is not null) row.Note = NormalizeNote(req.Note);

            await db.SaveChangesAsync(ct);
            return Results.Ok(ToTimeDto(row));
        });

        // ---- DELETE /time/{id} : delete one owned time entry (owner-scoped) ----
        g.MapDelete("/time/{id:int}", async (
            int id, CurrentUserAccessor me, UsageDbContext db, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;
            var deleted = await db.IdentityTimeEntries
                .Where(t => t.Id == id && t.UserEmail == caller.Email)
                .ExecuteDeleteAsync(ct);
            return deleted == 0 ? Results.NotFound() : Results.NoContent();
        });

        // ---- POST /rules : create/update a classification rule (UNIQUE keyword upsert) ----
        g.MapPost("/rules", async (
            RuleRequest req, CurrentUserAccessor me, UsageDbContext db, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;
            if (NormalizeKeyword(req.Keyword) is not { } keyword)
                return Results.BadRequest(new { message = "A keyword (1–128 characters) is required." });
            if (req.RoleId is not { } roleId)
                return Results.BadRequest(new { message = "A roleId is required." });
            var owns = await db.IdentityRoles
                .AnyAsync(r => r.Id == roleId && r.UserEmail == caller.Email, ct);
            if (!owns) return Results.NotFound(new { message = "That role doesn't exist." });

            var row = await UpsertRuleAsync(db, caller, keyword, roleId, req.Priority ?? 0, ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToRuleDto(row));
        });

        // ---- DELETE /rules/{id} : delete one owned rule (owner-scoped) ----
        g.MapDelete("/rules/{id:int}", async (
            int id, CurrentUserAccessor me, UsageDbContext db, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;
            var deleted = await db.IdentityRules
                .Where(r => r.Id == id && r.UserEmail == caller.Email)
                .ExecuteDeleteAsync(ct);
            return deleted == 0 ? Results.NotFound() : Results.NoContent();
        });

        // ---- GET /calendar-status : is the OPTIONAL import available (configured + caller connected) ----
        g.MapGet("/calendar-status", async (
            CurrentUserAccessor me, GoogleCalendarService cal, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;
            var connected = cal.IsConfigured && await cal.IsConnectedAsync(caller.Id, ct);
            return Results.Ok(new CalendarStatusDto(cal.IsConfigured, connected));
        });

        // ---- POST /import/preview : read the caller's OWN calendar, classify by rules, propose (creates NOTHING) ----
        g.MapPost("/import/preview", async (
            ImportPreviewRequest req, CurrentUserAccessor me, GoogleCalendarService cal,
            UsageDbContext db, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;
            var (from, to) = Window(req.FromUtc, req.ToUtc);

            var result = await cal.ListEventsAsync(caller.Id, from, to, ct);
            if (!result.Ok) return NotReady(result.Status, cal.LastErrorHint);

            var roleIds = await db.IdentityRoles.AsNoTracking()
                .Where(r => r.UserEmail == caller.Email).Select(r => r.Id).ToListAsync(ct);
            var roleSet = roleIds.ToHashSet();
            var rules = await db.IdentityRules.AsNoTracking()
                .Where(r => r.UserEmail == caller.Email)
                .ToListAsync(ct);
            // Only the ids already imported among THIS window's events count as "already imported".
            var seen = await db.IdentityTimeEntries.AsNoTracking()
                .Where(t => t.UserEmail == caller.Email && t.SourceEventId != null)
                .Select(t => t.SourceEventId!)
                .ToListAsync(ct);
            var seenSet = seen.ToHashSet(StringComparer.Ordinal);

            var matched = new List<ProposedTimeDto>();
            var unmatched = new List<UnclassifiedDto>();
            var alreadyImported = 0;
            var skippedAllDay = 0;

            foreach (var ev in result.Value!)
            {
                if (string.IsNullOrEmpty(ev.Id)) continue;
                if (seenSet.Contains(ev.Id)) { alreadyImported++; continue; }
                // All-day / zero-length events carry no meaningful duration — surface as skipped, never counted.
                if (ev.AllDay || ev.StartUtc is null || ev.EndUtc is null) { skippedAllDay++; continue; }
                var minutes = (int)Math.Round((ev.EndUtc.Value - ev.StartUtc.Value).TotalMinutes);
                if (minutes < 1) { skippedAllDay++; continue; }
                minutes = Math.Clamp(minutes, 1, 1440);
                var date = DateOnly.FromDateTime(ev.StartUtc.Value);
                var title = ev.Title ?? "";

                // Classify deterministically; only propose a role the caller still owns.
                var roleId = ClassifyTitle(title, rules);
                if (roleId is { } rid && roleSet.Contains(rid))
                    matched.Add(new ProposedTimeDto(ev.Id, title, date, minutes, rid));
                else
                    unmatched.Add(new UnclassifiedDto(ev.Id, title, date, minutes));
            }

            return Results.Ok(new ImportPreviewDto(matched, unmatched, alreadyImported, skippedAllDay));
        });

        // ---- POST /import/commit : persist confirmed rows (idempotent) + upsert any new rules ----
        g.MapPost("/import/commit", async (
            ImportCommitRequest req, CurrentUserAccessor me, UsageDbContext db, CancellationToken ct) =>
        {
            var caller = (await me.GetUserAsync(ct))!;
            var items = req.Items ?? Array.Empty<ImportItem>();

            var ownedRoleIds = (await db.IdentityRoles.AsNoTracking()
                .Where(r => r.UserEmail == caller.Email).Select(r => r.Id).ToListAsync(ct)).ToHashSet();
            // Already-imported ids for this owner → skip on commit (the filtered unique index also guards us).
            var seen = (await db.IdentityTimeEntries.AsNoTracking()
                .Where(t => t.UserEmail == caller.Email && t.SourceEventId != null)
                .Select(t => t.SourceEventId!).ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);

            // Upsert any newly-confirmed rules FIRST so the next import auto-classifies these titles.
            foreach (var nr in req.NewRules ?? Array.Empty<NewRule>())
            {
                if (NormalizeKeyword(nr.Keyword) is not { } kw) continue;
                if (!ownedRoleIds.Contains(nr.RoleId)) continue;
                await UpsertRuleAsync(db, caller, kw, nr.RoleId, 0, ct);
            }

            var now = DateTime.UtcNow;
            var imported = 0;
            var skipped = 0;
            // Dedup within THIS batch too (a payload could repeat an id), on top of the DB unique index.
            var batchSeen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var it in items)
            {
                if (string.IsNullOrEmpty(it.SourceEventId)) { skipped++; continue; }
                if (!ownedRoleIds.Contains(it.RoleId)) { skipped++; continue; }
                if (it.Minutes < 1 || DateOutOfRange(it.Date)) { skipped++; continue; }
                // Idempotent: never re-import an id this owner already has (or one repeated in the batch).
                if (seen.Contains(it.SourceEventId) || !batchSeen.Add(it.SourceEventId)) { skipped++; continue; }

                db.IdentityTimeEntries.Add(new IdentityTimeEntry
                {
                    UserEmail = caller.Email,
                    UserId = caller.Id,
                    RoleId = it.RoleId,
                    Date = it.Date,
                    Minutes = Math.Clamp(it.Minutes, 1, 1440),
                    Source = IdentityEntrySource.Calendar,
                    SourceEventId = it.SourceEventId,
                    Note = NormalizeNote(it.Note),
                    CreatedUtc = now,
                });
                imported++;
            }

            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // A racing concurrent import claimed one of the ids; fall back to a per-row insert so the
                // winners still persist and the loser is counted as skipped (the unique index is the source
                // of truth for dedup).
                (imported, skipped) = await CommitOneByOneAsync(db, caller, items, ownedRoleIds, seen, now, ct);
            }

            return Results.Ok(new ImportCommitDto(imported, skipped));
        });
    }

    // =====================================================================================
    // Classification — DETERMINISTIC substring match (no AI). Public + static so it's unit-testable.
    // =====================================================================================

    /// <summary>
    /// Resolve the role a title maps to via the owner's rules: a case-insensitive SUBSTRING match of each
    /// rule's keyword in the title. When several rules match, the highest <see cref="IdentityRule.Priority"/>
    /// wins; ties break to the LONGER keyword (the more specific match), then the lowest id (stable). Returns
    /// null when no rule matches (the event is surfaced as "unclassified" for the user to assign).
    /// </summary>
    public static int? ClassifyTitle(string? title, IReadOnlyList<IdentityRule> rules)
    {
        if (string.IsNullOrWhiteSpace(title) || rules.Count == 0) return null;
        var hay = title.ToLowerInvariant();

        IdentityRule? best = null;
        foreach (var rule in rules)
        {
            if (string.IsNullOrEmpty(rule.Keyword)) continue;
            if (!hay.Contains(rule.Keyword, StringComparison.OrdinalIgnoreCase)) continue;
            if (best is null
                || rule.Priority > best.Priority
                || (rule.Priority == best.Priority && rule.Keyword.Length > best.Keyword.Length)
                || (rule.Priority == best.Priority && rule.Keyword.Length == best.Keyword.Length && rule.Id < best.Id))
                best = rule;
        }
        return best?.RoleId;
    }

    // =====================================================================================
    // Helpers
    // =====================================================================================

    /// <summary>Upsert a rule by (owner, keyword): create a new one or repoint/repriority an existing keyword.
    /// The caller has already validated the role is owned. Does NOT SaveChanges.</summary>
    private static async Task<IdentityRule> UpsertRuleAsync(
        UsageDbContext db, CurrentUserAccessor.CurrentUser caller, string keyword, int roleId, int priority,
        CancellationToken ct)
    {
        var existing = await db.IdentityRules
            .FirstOrDefaultAsync(r => r.UserEmail == caller.Email && r.Keyword == keyword, ct);
        if (existing is not null)
        {
            existing.RoleId = roleId;
            existing.Priority = priority;
            return existing;
        }
        var row = new IdentityRule
        {
            UserEmail = caller.Email,
            UserId = caller.Id,
            Keyword = keyword,
            RoleId = roleId,
            Priority = priority,
            CreatedUtc = DateTime.UtcNow,
        };
        db.IdentityRules.Add(row);
        return row;
    }

    /// <summary>Fallback commit path when a batch insert hit a unique violation (a concurrent import): insert
    /// each row in its own transaction-ish save, skipping the ones the index now rejects. Re-reads the seen
    /// set so it reflects the rows the racer committed.</summary>
    private static async Task<(int Imported, int Skipped)> CommitOneByOneAsync(
        UsageDbContext db, CurrentUserAccessor.CurrentUser caller, IReadOnlyList<ImportItem> items,
        IReadOnlySet<int> ownedRoleIds, HashSet<string> seen, DateTime now, CancellationToken ct)
    {
        // Drop any entities still tracked from the failed batch so we start clean.
        foreach (var e in db.ChangeTracker.Entries<IdentityTimeEntry>().ToList())
            if (e.State == EntityState.Added) e.State = EntityState.Detached;

        var imported = 0;
        var skipped = 0;
        var batchSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var it in items)
        {
            if (string.IsNullOrEmpty(it.SourceEventId) || !ownedRoleIds.Contains(it.RoleId)
                || it.Minutes < 1 || DateOutOfRange(it.Date)
                || seen.Contains(it.SourceEventId) || !batchSeen.Add(it.SourceEventId))
            {
                skipped++;
                continue;
            }
            db.IdentityTimeEntries.Add(new IdentityTimeEntry
            {
                UserEmail = caller.Email,
                UserId = caller.Id,
                RoleId = it.RoleId,
                Date = it.Date,
                Minutes = Math.Clamp(it.Minutes, 1, 1440),
                Source = IdentityEntrySource.Calendar,
                SourceEventId = it.SourceEventId,
                Note = NormalizeNote(it.Note),
                CreatedUtc = now,
            });
            try { await db.SaveChangesAsync(ct); imported++; }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                foreach (var e in db.ChangeTracker.Entries<IdentityTimeEntry>().ToList())
                    if (e.State == EntityState.Added) e.State = EntityState.Detached;
                skipped++;
            }
        }
        return (imported, skipped);
    }

    /// <summary>Normalise a role name: trimmed, 1..64 chars; null when blank/too long.</summary>
    private static string? NormalizeName(string? name)
    {
        var n = name?.Trim();
        return string.IsNullOrEmpty(n) || n.Length > MaxNameLen ? null : n;
    }

    /// <summary>Normalise a colour to the allowed palette (case-insensitive); null when not a palette colour.</summary>
    private static string? NormalizeColor(string? color)
    {
        var c = color?.Trim();
        if (string.IsNullOrEmpty(c) || !PaletteSet.Contains(c)) return null;
        // Canonicalise to the palette's casing so the stored value is stable.
        return Palette.First(p => string.Equals(p, c, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Normalise a keyword: trimmed + lower-cased, 1..128 chars; null when blank/too long.</summary>
    private static string? NormalizeKeyword(string? keyword)
    {
        var k = keyword?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(k) || k.Length > MaxKeywordLen ? null : k;
    }

    private static string? NormalizeNote(string? note)
    {
        var n = note?.Trim();
        return string.IsNullOrEmpty(n) ? null : (n.Length <= MaxNoteLen ? n : n[..MaxNoteLen]);
    }

    /// <summary>Reject a fat-fingered year that would poison the chart (5 years back, 1 forward).</summary>
    private static bool DateOutOfRange(DateOnly date)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return date < today.AddYears(-5) || date > today.AddYears(1);
    }

    /// <summary>Resolve a [start, end) window: defaults to the last 30 days, capped at ~1 year so an open-ended
    /// request can't pull an unbounded range. Mirrors FamilyCalendarEndpoints.Window.</summary>
    private static (DateTime Start, DateTime End) Window(DateTime? fromUtc, DateTime? toUtc)
    {
        var now = DateTime.UtcNow;
        var end = toUtc ?? now;
        var start = fromUtc ?? end.AddDays(-30);
        if (end <= start) end = start.AddDays(1);
        if (end - start > TimeSpan.FromDays(RangeCapDays)) start = end.AddDays(-RangeCapDays);
        return (DateTime.SpecifyKind(start, DateTimeKind.Utc), DateTime.SpecifyKind(end, DateTimeKind.Utc));
    }

    /// <summary>Map a not-Ok calendar status to a graceful, NON-500 response — a 200 not-ready body for the
    /// soft states, a 502 only for a genuine upstream error. Mirrors FamilyCalendarEndpoints.NotReady.</summary>
    private static IResult NotReady(GoogleCalendarService.CalendarStatus status, string? hint) => status switch
    {
        GoogleCalendarService.CalendarStatus.NotConfigured => Results.Ok(new ImportPreviewNotReadyDto(
            false, false, "Google Calendar isn't configured on this server.")),
        GoogleCalendarService.CalendarStatus.NotConnected => Results.Ok(new ImportPreviewNotReadyDto(
            true, false, "Connect your Google Calendar to import time from your events.")),
        _ => Results.Json(new ImportPreviewNotReadyDto(
            true, true, hint ?? "Google Calendar is temporarily unavailable. Please try again."),
            statusCode: StatusCodes.Status502BadGateway),
    };

    /// <summary>The not-ready body for a calendar import (the import is OPTIONAL — never a hard failure).</summary>
    private sealed record ImportPreviewNotReadyDto(bool Configured, bool Connected, string Message);

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation;

    private static RoleDto ToRoleDto(IdentityRole r) =>
        new(r.Id, r.Name, r.Color, r.Archived, r.SortOrder, r.CreatedUtc);

    private static TimeEntryDto ToTimeDto(IdentityTimeEntry t) =>
        new(t.Id, t.RoleId, t.Date, t.Minutes, t.Source == IdentityEntrySource.Calendar ? "calendar" : "manual",
            t.Note, t.CreatedUtc);

    private static RuleDto ToRuleDto(IdentityRule r) => new(r.Id, r.Keyword, r.RoleId, r.Priority);
}
