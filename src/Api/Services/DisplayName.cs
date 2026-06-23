using Ccusage.Api.Data;
using Ccusage.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Ccusage.Api.Services;

/// <summary>
/// The SINGLE place that turns a user's stored identity (real full name + their own
/// <see cref="DisplayNameMode"/> + optional nickname) into the string OTHER users see. Applied at every
/// name-resolution site so the user's chosen form shows consistently on presence, chat, family, fleet,
/// the 75-Hard leaderboard — everywhere a name reaches another person.
///
/// Invariants:
/// - The user controls how THEY appear to everyone (the formatter takes the TARGET user's preference).
/// - The result is NEVER an email: an email-shaped full name is reduced to its local part, and a nickname
///   is sanitized of '@' so it can't carry an address (email-privacy, defense in depth).
/// - A blank/missing name yields "Unknown user".
/// - The admin Users table deliberately does NOT route through this (admins manage accounts and need the
///   real full name).
/// </summary>
public static class DisplayName
{
    public const string Unknown = "Unknown user";

    /// <summary>Max characters kept for a sanitized nickname.</summary>
    public const int MaxNicknameLength = 40;

    /// <summary>Max characters kept for a sanitized presence status.</summary>
    public const int MaxStatusLength = 80;

    /// <summary>
    /// Format the wire-facing display name for a target user. <paramref name="fullName"/> is the raw
    /// <see cref="AppUser.Name"/>; <paramref name="mode"/> and <paramref name="nickname"/> are that same
    /// user's own preferences.
    /// </summary>
    public static string Format(string? fullName, DisplayNameMode mode, string? nickname)
    {
        // Nickname mode wins when a usable nickname exists; otherwise fall through to a name-derived form.
        if (mode == DisplayNameMode.Nickname)
        {
            var nick = SanitizeNickname(nickname);
            if (!string.IsNullOrEmpty(nick)) return nick;
        }

        var clean = Scrub(fullName);
        if (string.IsNullOrWhiteSpace(clean)) return Unknown;

        var tokens = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return Unknown;

        return mode switch
        {
            DisplayNameMode.FirstName => tokens[0],
            DisplayNameMode.FirstInitial => tokens.Length == 1
                ? tokens[0]
                : $"{tokens[0]} {char.ToUpperInvariant(tokens[^1][0])}.",
            // Full, and Nickname with no usable nickname, both fall back to the full (scrubbed) name.
            _ => clean,
        };
    }

    /// <summary>Convenience overload: format directly from an <see cref="AppUser"/>.</summary>
    public static string Format(AppUser u) => Format(u.Name, u.DisplayNameMode, u.Nickname);

    /// <summary>
    /// Resolve a set of AppUser ids to their wire-facing display name in ONE query, applying each TARGET
    /// user's own <see cref="DisplayNameMode"/>/<see cref="AppUser.Nickname"/>. Ids with no row are absent
    /// (callers default to <see cref="Unknown"/>). Drop-in replacement for the per-domain
    /// <c>ResolveUserNamesAsync</c>/<c>NamesAsync</c> helpers — the name is NEVER an email.
    /// </summary>
    public static async Task<Dictionary<int, string>> ResolveNamesByIdAsync(
        UsageDbContext db, IEnumerable<int> userIds, CancellationToken ct = default)
    {
        var ids = userIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<int, string>();

        var rows = await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.Name, u.DisplayNameMode, u.Nickname })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.Id, r => Format(r.Name, r.DisplayNameMode, r.Nickname));
    }

    /// <summary>
    /// Resolve a set of emails to their wire-facing display name in ONE query (lower-cased match), applying
    /// each TARGET user's own preference. Emails with no AppUser row are absent (callers default to
    /// <see cref="Unknown"/>). The key is the lower-cased email; the value is never an email.
    /// </summary>
    public static async Task<Dictionary<string, string>> ResolveNamesByEmailAsync(
        UsageDbContext db, IEnumerable<string> emails, CancellationToken ct = default)
    {
        var distinct = emails
            .Where(e => !string.IsNullOrEmpty(e))
            .Select(e => e.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (distinct.Length == 0) return new Dictionary<string, string>(StringComparer.Ordinal);

        var rows = await db.Users.AsNoTracking()
            .Where(u => distinct.Contains(u.Email))
            .Select(u => new { u.Email, u.Name, u.DisplayNameMode, u.Nickname })
            .ToListAsync(ct);

        return rows.ToDictionary(
            r => r.Email, r => Format(r.Name, r.DisplayNameMode, r.Nickname), StringComparer.Ordinal);
    }

    /// <summary>
    /// Sanitize a user-supplied nickname for storage: trim, strip control chars, remove '@' (so it can
    /// never carry an email), collapse whitespace, and cap length. Returns null for an empty result.
    /// </summary>
    public static string? SanitizeNickname(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // Control chars (incl. tabs/newlines) become spaces, '@' is dropped, then whitespace is collapsed —
        // so "a\tb" reads as two words rather than running together.
        var s = new string(raw.Select(c => char.IsControl(c) ? ' ' : c).Where(c => c != '@').ToArray());
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        if (s.Length == 0) return null;
        return s.Length > MaxNicknameLength ? s[..MaxNicknameLength] : s;
    }

    /// <summary>
    /// Sanitize a user-supplied presence status for storage: trim, strip control chars, collapse
    /// whitespace, mask any email-shaped substring, and cap length. Returns null for an empty result.
    /// </summary>
    public static string? SanitizeStatus(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = new string(raw.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        // Defense in depth: never let a status carry an address.
        s = System.Text.RegularExpressions.Regex.Replace(
            s, @"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", "•••");
        s = s.Trim();
        if (s.Length == 0) return null;
        return s.Length > MaxStatusLength ? s[..MaxStatusLength] : s;
    }

    /// <summary>The camelCase wire token for a <see cref="DisplayNameMode"/> (what the SPA sends/receives).</summary>
    public static string ModeToWire(DisplayNameMode mode) => mode switch
    {
        DisplayNameMode.Full => "full",
        DisplayNameMode.FirstName => "firstName",
        DisplayNameMode.FirstInitial => "firstInitial",
        DisplayNameMode.Nickname => "nickname",
        _ => "firstInitial",
    };

    /// <summary>Parse a wire token to a <see cref="DisplayNameMode"/>. Returns false for an unknown token.</summary>
    public static bool TryParseMode(string? wire, out DisplayNameMode mode)
    {
        switch ((wire ?? "").Trim().ToLowerInvariant())
        {
            case "full": mode = DisplayNameMode.Full; return true;
            case "firstname": mode = DisplayNameMode.FirstName; return true;
            case "firstinitial": mode = DisplayNameMode.FirstInitial; return true;
            case "nickname": mode = DisplayNameMode.Nickname; return true;
            default: mode = DisplayNameMode.FirstInitial; return false;
        }
    }

    /// <summary>Reduce an email-shaped name to its local part so a name field can never carry an address.</summary>
    private static string Scrub(string? name)
    {
        if (name is null) return "";
        var trimmed = name.Trim();
        var at = trimmed.IndexOf('@');
        return at < 0 ? trimmed : trimmed[..at];
    }
}
