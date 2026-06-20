using System.Text.RegularExpressions;
using Ccusage.Api.Auth;
using Ccusage.Api.Data;
using Ccusage.Api.Data.Entities;
using Ccusage.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Ccusage.Api.Endpoints;

/// <summary>
/// Family Hub F7 — QUICK-ADD (<c>POST /api/family/quick-add</c>). One tiny endpoint that captures a line
/// of text and files it as the right family item (a list item, a reminder, or a note) for the acting
/// user's household. It is the shared backend for two front doors:
///
/// <list type="bullet">
///   <item>the WEB app's global quick-add box (a normal user JWT), and</item>
///   <item>the desktop tray AGENT (the same <c>X-Ingest-Key</c> credential it already uses for
///   <c>/api/ingest</c>), so you can jot "remind me to call the dentist" from the tray without opening a
///   browser.</item>
/// </list>
///
/// AUTH is dual and resolved in-handler (the endpoint is <c>AllowAnonymous</c> so the ingest-key path
/// works without a JWT):
/// <list type="bullet">
///   <item>If the <c>X-Ingest-Key</c> header is present it is hashed + matched to a non-revoked
///   <see cref="IngestKey"/>, and the acting user is that key's OWNER (server-derived — never a client
///   value). This path is deliberately NARROW: it may ONLY create these family items for its owner's
///   household; it grants no other access and never reaches another household.</item>
///   <item>Otherwise the caller's JWT identifies the acting user.</item>
/// </list>
///
/// The acting user MUST hold <see cref="Permissions.FamilyUse"/> (else 403, on either auth path); their
/// household is resolved (auto-provisioned on first use). Reuses the F1 list/note + F2 reminder entities,
/// so no schema change is needed. PRIVACY: people are referenced by AppUser id only — no email is ever
/// stored or put on the wire.
/// </summary>
public static class FamilyQuickAddEndpoints
{
    /// <summary>Quick-add request. <c>kind</c> defaults to "auto" (route by the text's leading keyword).</summary>
    public sealed record QuickAddRequest(string? Text, string? Kind, string? ListName);

    /// <summary>What was captured: the resolved kind, the new item's id, and a warm one-line summary.</summary>
    public sealed record QuickAddResult(string Kind, long CreatedId, string Summary);

    private const int MaxTextLength = 1000;
    private const string DefaultListName = "Quick Capture";

    public static void MapFamilyQuickAddEndpoints(this WebApplication app)
    {
        // AllowAnonymous: the ingest-key path has no JWT. Both auth schemes are resolved in-handler, and
        // family.use is enforced there too (so this is no weaker than the JWT-gated /api/family group).
        app.MapPost("/api/family/quick-add", async (
            QuickAddRequest req, HttpContext http, CurrentUserAccessor me,
            CurrentHouseholdAccessor households, UsageDbContext db, CancellationToken ct) =>
        {
            // ---- Resolve the acting user from EITHER the ingest key OR the JWT ----
            var caller = await ResolveActingUserAsync(http, me, db, ct);
            if (caller is null)
                return Results.Json(new { message = "Sign in or supply a valid ingest key to quick-add." },
                    statusCode: StatusCodes.Status401Unauthorized);
            if (!caller.IsEnabled || !caller.Permissions.Contains(Permissions.FamilyUse))
                return Results.Json(new { message = "You don't have permission: family.use" },
                    statusCode: StatusCodes.Status403Forbidden);

            var household = await households.GetOrCreateForCallerAsync(caller, ct);
            if (household is null)
                return Results.Json(new { message = "You don't have permission: family.use" },
                    statusCode: StatusCodes.Status403Forbidden);

            // ---- Validate + cap the text ----
            var text = (req.Text ?? "").Trim();
            if (string.IsNullOrEmpty(text))
                return Results.BadRequest(new { message = "There's nothing to add — type a quick note first." });
            if (text.Length > MaxTextLength) text = text[..MaxTextLength];

            // ---- Route to the right kind ----
            var kind = ResolveKind(req.Kind, text);
            return kind switch
            {
                "reminder" => await AddReminderAsync(db, household, caller, text, ct),
                "note" => await AddNoteAsync(db, household, caller, text, ct),
                _ => await AddListItemAsync(db, household, caller, text, req.ListName, ct),
            };
        })
        .AllowAnonymous()
        .RequireRateLimiting("ingest");
    }

    // =====================================================================================
    // AUTH — resolve the acting user from the ingest key (preferred when present) or the JWT
    // =====================================================================================

    /// <summary>
    /// The acting user, resolved from the <c>X-Ingest-Key</c> header (its OWNER) when present and valid,
    /// otherwise from the request's JWT. Returns null when neither yields a user (the handler maps that to
    /// 401). The ingest-key branch resolves the key owner server-side — a client never names the user.
    /// </summary>
    private static async Task<CurrentUserAccessor.CurrentUser?> ResolveActingUserAsync(
        HttpContext http, CurrentUserAccessor me, UsageDbContext db, CancellationToken ct)
    {
        var rawKey = http.Request.Headers[IngestKeyFilter.HeaderName].ToString().Trim();
        if (!string.IsNullOrEmpty(rawKey))
        {
            // Machine credential: hash + match a live key, then load its owner with permissions. A revoked
            // or unknown key yields null (→ 401); an owner who lacks family.use is rejected by the caller.
            var hash = IngestKeyFilter.Hash(rawKey);
            var ownerId = await db.IngestKeys.AsNoTracking()
                .Where(k => k.KeyHash == hash && k.RevokedUtc == null)
                .Select(k => k.UserId)
                .FirstOrDefaultAsync(ct);
            if (ownerId is null) return null; // unknown/revoked key, or a legacy key with no owner

            var owner = await db.Users.AsNoTracking()
                .Include(u => u.Permissions)
                .FirstOrDefaultAsync(u => u.Id == ownerId.Value, ct);
            if (owner is null) return null;

            // Best-effort last-used stamp, mirroring the ingest filter (never fail quick-add over it).
            try
            {
                var now = DateTime.UtcNow;
                var ip = http.Connection.RemoteIpAddress?.ToString();
                await db.IngestKeys.Where(k => k.KeyHash == hash && k.RevokedUtc == null)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.LastUsedUtc, now)
                        .SetProperty(x => x.LastUsedIp, ip), ct);
            }
            catch { /* informational only */ }

            return new CurrentUserAccessor.CurrentUser(
                owner.Id, owner.Email, owner.Name, owner.IsEnabled,
                owner.Permissions.Select(p => p.Permission).ToHashSet(StringComparer.Ordinal));
        }

        // No machine key → fall back to the signed-in user (null when there is no valid JWT).
        return await me.GetUserAsync(ct);
    }

    // =====================================================================================
    // KIND ROUTING
    // =====================================================================================

    /// <summary>
    /// Resolve the effective kind. An explicit "list"/"reminder"/"note" wins; "auto" (the default, and any
    /// unknown value) routes by the text: a leading "remind"/"remember to" or a recognizable time phrase →
    /// reminder; a leading "note:"/"note " → note; otherwise → list.
    /// </summary>
    private static string ResolveKind(string? requested, string text)
    {
        var k = (requested ?? "auto").Trim().ToLowerInvariant();
        if (k is "list" or "reminder" or "note") return k;

        // auto
        var lower = text.TrimStart().ToLowerInvariant();
        if (lower.StartsWith("remind") || lower.StartsWith("remember to"))
            return "reminder";
        if (lower.StartsWith("note:") || lower.StartsWith("note "))
            return "note";
        if (NaturalTime.HasTimePhrase(text))
            return "reminder";
        return "list";
    }

    // =====================================================================================
    // LIST  — append to a named list (find-or-create), else the default "Quick Capture" list
    // =====================================================================================

    private static async Task<IResult> AddListItemAsync(
        UsageDbContext db, Household household, CurrentUserAccessor.CurrentUser caller,
        string text, string? listName, CancellationToken ct)
    {
        var name = (listName ?? "").Trim();
        if (string.IsNullOrEmpty(name)) name = DefaultListName;
        if (name.Length > 200) name = name[..200];

        // Find-or-create a household list by (case-insensitive) name. A default "Quick Capture" list is a
        // shopping list (the common groceries case); a named one defaults to a to-do list.
        var list = await db.FamilyLists
            .FirstOrDefaultAsync(l => l.HouseholdId == household.Id && l.Name.ToLower() == name.ToLower(), ct);
        if (list is null)
        {
            var now0 = DateTime.UtcNow;
            list = new FamilyList
            {
                HouseholdId = household.Id,
                CreatedByUserId = caller.Id,
                Name = name,
                Kind = name == DefaultListName ? "shopping" : "todo",
                CreatedUtc = now0,
                UpdatedUtc = now0,
            };
            db.FamilyLists.Add(list);
            await db.SaveChangesAsync(ct); // assign list.Id
        }

        var itemText = text.Length > 500 ? text[..500] : text;
        var maxSort = await db.FamilyListItems.Where(i => i.ListId == list.Id)
            .Select(i => (int?)i.SortOrder).MaxAsync(ct) ?? -1;
        var item = new FamilyListItem
        {
            ListId = list.Id,
            Text = itemText,
            SortOrder = maxSort + 1,
            CreatedUtc = DateTime.UtcNow,
        };
        db.FamilyListItems.Add(item);
        list.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new QuickAddResult("list", item.Id, $"Added “{itemText}” to {list.Name}."));
    }

    // =====================================================================================
    // REMINDER — light natural-time parse in the household timezone; strip the time phrase
    // =====================================================================================

    private static async Task<IResult> AddReminderAsync(
        UsageDbContext db, Household household, CurrentUserAccessor.CurrentUser caller,
        string text, CancellationToken ct)
    {
        // Drop a leading "remind me to / remember to / remind me" lead-in so the saved text reads cleanly.
        var body = NaturalTime.StripReminderLeadIn(text);

        // Parse a time phrase in the household's local zone; strip the phrase from the saved text. With no
        // recognizable time we default to one hour out (a gentle "later today" nudge).
        var (dueUtc, stripped) = NaturalTime.Parse(body, household.TimeZone, DateTime.UtcNow);
        var reminderText = stripped.Trim();
        if (string.IsNullOrEmpty(reminderText)) reminderText = body.Trim();
        if (string.IsNullOrEmpty(reminderText)) reminderText = "Reminder";
        if (reminderText.Length > 500) reminderText = reminderText[..500];

        var reminder = new FamilyReminder
        {
            HouseholdId = household.Id,
            CreatedByUserId = caller.Id,
            TargetUserId = caller.Id, // quick-add reminders target the acting user
            Text = reminderText,
            DueUtc = DateTime.SpecifyKind(dueUtc, DateTimeKind.Utc),
            Recurrence = "none",
            Active = true,
            CreatedUtc = DateTime.UtcNow,
        };
        db.FamilyReminders.Add(reminder);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new QuickAddResult("reminder", reminder.Id,
            $"I'll remind you to {reminderText}."));
    }

    // =====================================================================================
    // NOTE — first line → Title, the rest → Body
    // =====================================================================================

    private static async Task<IResult> AddNoteAsync(
        UsageDbContext db, Household household, CurrentUserAccessor.CurrentUser caller,
        string text, CancellationToken ct)
    {
        // A leading "note:" / "note " marker is the routing keyword, not part of the content — strip it.
        var content = Regex.Replace(text.TrimStart(), @"^note\s*:?\s*", "", RegexOptions.IgnoreCase).Trim();
        if (string.IsNullOrEmpty(content)) content = text.Trim();

        var firstBreak = content.IndexOf('\n');
        string title, body;
        if (firstBreak >= 0)
        {
            title = content[..firstBreak].Trim();
            body = content[(firstBreak + 1)..].Trim();
        }
        else
        {
            title = content;
            body = "";
        }
        if (title.Length > 200) title = title[..200];

        var now = DateTime.UtcNow;
        var note = new FamilyNote
        {
            HouseholdId = household.Id,
            CreatedByUserId = caller.Id,
            Title = title,
            Body = body,
            Pinned = false,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        db.FamilyNotes.Add(note);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new QuickAddResult("note", note.Id, $"Saved a note: “{title}”."));
    }
}
