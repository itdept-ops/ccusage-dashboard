using Ccusage.Api.Data.Entities;

namespace Ccusage.Api.Services;

/// <summary>
/// The DETERMINISTIC auto-categorizer that runs at import time, BEFORE any optional AI. Resolution order for a
/// row that has no category from the file itself: (1) a household <see cref="FinanceCategoryRule"/> (its
/// learned/seeded rules), then (2) a built-in default merchant→category map. Only rows STILL Uncategorized
/// after this pass are eligible for the optional Gemini classify. The fixed category set this exposes is also
/// the closed enum the AI classifier is constrained to (no hallucinated categories).
/// </summary>
public static class FinanceCategorizer
{
    /// <summary>
    /// The fixed default category set. This is the closed vocabulary the deterministic map assigns AND the
    /// enum the AI classifier is constrained to — combined at the endpoint with whatever categories already
    /// exist on the household's committed ledger. "Uncategorized" is the implicit floor (never assigned here).
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultCategories = new[]
    {
        "Groceries", "Dining", "Gas", "Shopping", "Entertainment", "Travel",
        "Utilities", "Rent", "Mortgage", "Insurance", "Health", "Fitness",
        "Subscriptions", "Transportation", "Education", "Kids", "Pets",
        "Personal Care", "Home", "Gifts", "Charity", "Fees", "Taxes",
        "Income", "Transfer",
    };

    /// <summary>
    /// The built-in default merchant→category map (substring match on the lower-cased merchant). Conservative
    /// and brand-specific so it never mis-buckets. Used only when neither the file nor a household rule supplies
    /// a category. The VALUES are all members of <see cref="DefaultCategories"/>.
    /// </summary>
    private static readonly (string Needle, string Category)[] DefaultMerchantMap =
    {
        // Groceries
        ("trader joe", "Groceries"), ("whole foods", "Groceries"), ("safeway", "Groceries"),
        ("kroger", "Groceries"), ("aldi", "Groceries"), ("costco", "Groceries"),
        ("publix", "Groceries"), ("wegmans", "Groceries"), ("grocery", "Groceries"),
        // Dining
        ("starbucks", "Dining"), ("chipotle", "Dining"), ("mcdonald", "Dining"),
        ("doordash", "Dining"), ("uber eats", "Dining"), ("grubhub", "Dining"),
        ("restaurant", "Dining"), ("pizza", "Dining"), ("coffee", "Dining"),
        // Gas / transportation
        ("shell", "Gas"), ("chevron", "Gas"), ("exxon", "Gas"), ("bp ", "Gas"),
        ("76 ", "Gas"), ("arco", "Gas"), ("fuel", "Gas"),
        ("uber", "Transportation"), ("lyft", "Transportation"), ("transit", "Transportation"),
        // Shopping
        ("amazon", "Shopping"), ("target", "Shopping"), ("walmart", "Shopping"),
        ("best buy", "Shopping"), ("ebay", "Shopping"), ("etsy", "Shopping"),
        // Entertainment / subscriptions
        ("netflix", "Subscriptions"), ("spotify", "Subscriptions"), ("hulu", "Subscriptions"),
        ("disney+", "Subscriptions"), ("youtube premium", "Subscriptions"), ("hbo", "Subscriptions"),
        ("apple.com/bill", "Subscriptions"), ("prime video", "Subscriptions"),
        ("cinema", "Entertainment"), ("amc ", "Entertainment"),
        // Utilities
        ("comcast", "Utilities"), ("xfinity", "Utilities"), ("at&t", "Utilities"),
        ("verizon", "Utilities"), ("t-mobile", "Utilities"), ("pg&e", "Utilities"),
        ("electric", "Utilities"), ("water district", "Utilities"), ("internet", "Utilities"),
        // Health / fitness
        ("pharmacy", "Health"), ("cvs", "Health"), ("walgreens", "Health"),
        ("planet fitness", "Fitness"), ("gym", "Fitness"), ("peloton", "Fitness"),
        // Travel
        ("airlines", "Travel"), ("delta air", "Travel"), ("united air", "Travel"),
        ("marriott", "Travel"), ("airbnb", "Travel"), ("hotel", "Travel"),
        // Insurance / fees
        ("geico", "Insurance"), ("state farm", "Insurance"), ("insurance", "Insurance"),
        ("overdraft", "Fees"), ("service fee", "Fees"),
    };

    /// <summary>The outcome of categorizing one row: the chosen category (null = still Uncategorized) and the
    /// source label ("file" | "rule" | "none") written to the staged row.</summary>
    public readonly record struct CategoryResult(string? Category, string Source);

    /// <summary>
    /// Deterministically categorize a row. If the file already gave a non-blank <paramref name="fileCategory"/>
    /// that wins (source "file"). Otherwise test the household <paramref name="rules"/> then the default map
    /// against the lower-cased merchant (source "rule"). Else still Uncategorized (source "none").
    /// </summary>
    public static CategoryResult Categorize(
        string? fileCategory, string merchant, IReadOnlyList<FinanceCategoryRule> rules)
    {
        if (!string.IsNullOrWhiteSpace(fileCategory))
            return new CategoryResult(fileCategory.Trim(), "file");

        var m = (merchant ?? "").Trim().ToLowerInvariant();
        if (m.Length == 0) return new CategoryResult(null, "none");

        // Household rules first (learned + seeded). Equals before contains for precision; a contains rule with a
        // longer pattern wins over a shorter one.
        var ruleHit = rules
            .Where(r => Matches(r, m))
            .OrderByDescending(r => r.MatchType == "equals")
            .ThenByDescending(r => r.Pattern.Length)
            .FirstOrDefault();
        if (ruleHit is not null)
            return new CategoryResult(ruleHit.Category, "rule");

        foreach (var (needle, category) in DefaultMerchantMap)
            if (m.Contains(needle, StringComparison.Ordinal))
                return new CategoryResult(category, "rule");

        return new CategoryResult(null, "none");
    }

    private static bool Matches(FinanceCategoryRule r, string lowerMerchant) =>
        r.MatchType == "equals"
            ? string.Equals(lowerMerchant, r.Pattern, StringComparison.Ordinal)
            : r.Pattern.Length > 0 && lowerMerchant.Contains(r.Pattern, StringComparison.Ordinal);
}
