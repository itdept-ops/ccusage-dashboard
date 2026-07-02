using Ccusage.Api.Data;
using Ccusage.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Ccusage.Api.Services;

/// <summary>
/// The single source of truth for household-membership / role authorization queries. Every "is this
/// user a member of this household?", "what role do they hold?", and "which household does this user
/// belong to?" check routes through here so the predicate (and therefore the allow/deny decision) lives
/// in exactly one place instead of being copy-pasted across the Family/Presence/Search/Nudge endpoints.
///
/// These are read-only, <see cref="EntityFrameworkQueryableExtensions.AsNoTracking{TEntity}"/> lookups
/// against <see cref="HouseholdMember"/>. Role comparisons are ORDINAL (case-sensitive) — the roles are
/// the fixed lower-case set "owner" | "adult" | "child", matching how they're written on insert. The
/// methods take a <see cref="UsageDbContext"/> so they can be called from endpoint lambdas, private
/// static helpers, and background services alike without extra DI plumbing;
/// <see cref="CurrentHouseholdAccessor"/> exposes instance shims over them for the request path.
/// </summary>
public static class HouseholdMembership
{
    /// <summary>
    /// Whether <paramref name="userId"/> is a member of <paramref name="householdId"/> (any role).
    /// This is the canonical household-co-membership authorization check.
    /// </summary>
    public static Task<bool> IsMemberAsync(
        UsageDbContext db, int householdId, int userId, CancellationToken ct = default) =>
        db.HouseholdMembers.AsNoTracking()
            .AnyAsync(m => m.HouseholdId == householdId && m.UserId == userId, ct);

    /// <summary>
    /// The role <paramref name="userId"/> holds in <paramref name="householdId"/> ("owner" | "adult" |
    /// "child"), or null if they aren't a member. Callers compare the value with ordinal semantics.
    /// </summary>
    public static Task<string?> RoleInHouseholdAsync(
        UsageDbContext db, int householdId, int userId, CancellationToken ct = default) =>
        db.HouseholdMembers.AsNoTracking()
            .Where(m => m.HouseholdId == householdId && m.UserId == userId)
            .Select(m => m.Role)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// The id of the household <paramref name="userId"/> belongs to, or null if they aren't in one. A
    /// user belongs to at most one household (UNIQUE index on <see cref="HouseholdMember.UserId"/>), so
    /// this is the membership-graph way to resolve someone's household without a create.
    /// </summary>
    public static Task<int?> HouseholdIdForUserAsync(
        UsageDbContext db, int userId, CancellationToken ct = default) =>
        db.HouseholdMembers.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => (int?)m.HouseholdId)
            .FirstOrDefaultAsync(ct);
}
