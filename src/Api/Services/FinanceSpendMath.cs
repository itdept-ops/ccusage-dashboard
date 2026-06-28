using Ccusage.Api.Data;
using Ccusage.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Ccusage.Api.Services;

/// <summary>
/// The ONE definition of household spend-vs-budget pace, shared by the Family Hub finance BUDGETS endpoint and
/// the proactive BUDGET-ALERT agent (<see cref="AgentComposer"/>) so "over budget" never diverges. Spending is
/// EXPENSE-only (income + transfers — incl. credit-card payments — are excluded, mirroring GET /summary), and
/// the pace projection is the same straight-line month-to-date extrapolation everywhere:
/// <c>projected = spent / dayOfMonth * daysInMonth</c>.
///
/// <para>Pure (no DB) helpers are unit-testable; the DB readers are thin EF queries that load a household's
/// EXPENSE rows for a month window. Nothing here touches the client beyond a month/date the caller resolves.</para>
/// </summary>
public static class FinanceSpendMath
{
    /// <summary>One expense row for the spend math (positive magnitude + its category).</summary>
    public readonly record struct SpendRow(decimal Magnitude, string? Category);

    /// <summary>
    /// Load a household's EXPENSE rows (transfers/income excluded) in the half-open <paramref name="from"/> ..
    /// <paramref name="toExclusive"/> window — the canonical "spend this month" set both budgets and the agent
    /// read. Magnitude + category only.
    /// </summary>
    public static async Task<List<SpendRow>> LoadExpenseRowsAsync(
        UsageDbContext db, int householdId, DateOnly from, DateOnly toExclusive, CancellationToken ct)
    {
        return await db.FinanceTransactions.AsNoTracking()
            .Where(t => t.HouseholdId == householdId && t.Kind == "expense"
                && t.Date >= from && t.Date < toExclusive)
            .Select(t => new SpendRow(t.Magnitude, t.Category))
            .ToListAsync(ct);
    }

    /// <summary>Total EXPENSE spend over the rows (the whole-month / overall figure).</summary>
    public static decimal TotalSpent(IEnumerable<SpendRow> rows) => rows.Sum(r => r.Magnitude);

    /// <summary>EXPENSE spend grouped by the normalized category key (null/blank → "Uncategorized"),
    /// case-insensitive — the per-category figure budgets are checked against.</summary>
    public static Dictionary<string, decimal> SpentByCategory(IEnumerable<SpendRow> rows) =>
        rows.GroupBy(r => CategoryKey(r.Category), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Magnitude), StringComparer.OrdinalIgnoreCase);

    /// <summary>The normalized display/grouping key for a transaction category (null/blank → "Uncategorized").</summary>
    public static string CategoryKey(string? category) =>
        string.IsNullOrWhiteSpace(category) ? "Uncategorized" : category!.Trim();

    /// <summary>
    /// The straight-line month-end pace projection from month-to-date <paramref name="spent"/> as of
    /// <paramref name="dayOfMonth"/> in a <paramref name="daysInMonth"/>-day month:
    /// <c>spent / dayOfMonth * daysInMonth</c>, rounded to cents. The ONE pace definition shared by the budgets
    /// endpoint and the budget-alert agent. For a fully-elapsed month (dayOfMonth &gt;= daysInMonth) the
    /// projection is just the spend (nothing left to extrapolate).
    /// </summary>
    public static decimal ProjectPace(decimal spent, int dayOfMonth, int daysInMonth)
    {
        if (dayOfMonth <= 0) return spent;
        if (dayOfMonth >= daysInMonth) return spent;
        return decimal.Round(spent / dayOfMonth * daysInMonth, 2);
    }

    /// <summary>
    /// The day-of-month to pace against for a month window viewed "as of" <paramref name="asOf"/>: the elapsed
    /// day count, capped at the month length. A PAST month paces over its full length (fully elapsed); the
    /// CURRENT month paces over today's day-of-month; a FUTURE month is day 0 (no pace yet).
    /// </summary>
    public static int ElapsedDayOfMonth(DateOnly monthStart, DateOnly asOf)
    {
        var daysInMonth = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);
        if (asOf < monthStart) return 0;                       // a future month — nothing elapsed
        var monthEnd = monthStart.AddMonths(1);
        if (asOf >= monthEnd) return daysInMonth;              // a past month — fully elapsed
        return asOf.Day;                                       // the current month — today's day-of-month
    }
}
