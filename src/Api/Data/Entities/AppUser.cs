namespace Ccusage.Api.Data.Entities;

/// <summary>A person allowed to sign in. Authorization is the set of <see cref="Permissions"/>.</summary>
public class AppUser
{
    public int Id { get; set; }

    /// <summary>Google account email, stored lower-cased; the identity key.</summary>
    public string Email { get; set; } = "";

    /// <summary>
    /// The Google account's immutable subject id (<c>sub</c> claim). Bound on first successful
    /// sign-in; thereafter a login whose email matches but whose Google id differs is rejected,
    /// so a recycled/reassigned email can't inherit another person's access. Null until first login.
    /// </summary>
    public string? GoogleSubject { get; set; }

    public string Name { get; set; } = "";
    public string? Picture { get; set; }

    /// <summary>When false, sign-in and all API access are denied (checked on every request).</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Security stamp for session invalidation. Each issued JWT carries this value in its <c>sv</c>
    /// claim; the request pipeline rejects a token whose <c>sv</c> no longer matches. An admin
    /// "force logout" bumps this (+1), invalidating every outstanding token for the user without
    /// disabling the account (they can sign in again to get a fresh token). A MISSING <c>sv</c> claim
    /// is treated as 0, so tokens minted before this field existed stay valid while SessionVersion is
    /// still its default 0 — i.e. no mass-logout on deploy.
    /// </summary>
    public int SessionVersion { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime? LastLoginUtc { get; set; }

    public List<UserPermission> Permissions { get; set; } = new();
}
