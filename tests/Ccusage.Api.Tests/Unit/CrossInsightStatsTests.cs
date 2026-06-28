using Ccusage.Api.Services;
using FluentAssertions;

namespace Ccusage.Api.Tests.Unit;

/// <summary>
/// The pure cross-domain stats engine <see cref="CrossInsightStats"/>: Pearson r on known fixtures (perfect
/// +1 / −1 / zero-variance), OLS slope sign + magnitude, the bounded projection clamp, the top z-score
/// anomaly, longest/current run, the magnitude buckets, and the n&gt;=10 correlation floor in the catalog.
/// Every helper must be NaN-safe + total (no throw on degenerate input).
/// </summary>
public class CrossInsightStatsTests
{
    private static readonly DateOnly D0 = new(2026, 6, 1);
    private static Dictionary<DateOnly, double> Series(params double[] vals)
    {
        var m = new Dictionary<DateOnly, double>();
        for (var i = 0; i < vals.Length; i++) m[D0.AddDays(i)] = vals[i];
        return m;
    }

    // ---- Pearson ----

    [Fact]
    public void Pearson_perfect_positive_is_one()
    {
        var x = Series(1, 2, 3, 4, 5);
        var y = Series(2, 4, 6, 8, 10);
        CrossInsightStats.Pearson(x, y).Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void Pearson_perfect_negative_is_minus_one()
    {
        var x = Series(1, 2, 3, 4, 5);
        var y = Series(10, 8, 6, 4, 2);
        CrossInsightStats.Pearson(x, y).Should().BeApproximately(-1.0, 1e-9);
    }

    [Fact]
    public void Pearson_zero_variance_is_null_not_nan()
    {
        var x = Series(5, 5, 5, 5, 5); // no variance ⇒ r undefined
        var y = Series(1, 2, 3, 4, 5);
        CrossInsightStats.Pearson(x, y).Should().BeNull();
    }

    [Fact]
    public void Pearson_only_pairs_overlapping_days()
    {
        var x = new Dictionary<DateOnly, double> { [D0] = 1, [D0.AddDays(1)] = 2, [D0.AddDays(2)] = 3 };
        var y = new Dictionary<DateOnly, double> { [D0.AddDays(1)] = 2, [D0.AddDays(2)] = 3 }; // only 2 paired
        CrossInsightStats.CountPaired(x, y).Should().Be(2);
        CrossInsightStats.Pearson(x, y).Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void Pearson_fewer_than_two_pairs_is_null()
    {
        var x = new Dictionary<DateOnly, double> { [D0] = 1 };
        var y = new Dictionary<DateOnly, double> { [D0] = 2 };
        CrossInsightStats.Pearson(x, y).Should().BeNull();
    }

    [Fact]
    public void Pearson_handles_known_partial_correlation()
    {
        // Fixture: x=(1..5), y=(2,1,4,3,5) ⇒ r = 0.8 exactly.
        var x = Series(1, 2, 3, 4, 5);
        var y = Series(2, 1, 4, 3, 5);
        CrossInsightStats.Pearson(x, y).Should().BeApproximately(0.8, 1e-9);
    }

    // ---- Slope + projection ----

    [Fact]
    public void Slope_is_per_day_and_signed()
    {
        var s = Series(10, 12, 14, 16); // +2 per day
        CrossInsightStats.Slope(s).Should().BeApproximately(2.0, 1e-9);
    }

    [Fact]
    public void Slope_respects_calendar_gaps()
    {
        // value rises 4 over 2 calendar days (a gap at day 1) ⇒ slope 2/day
        var s = new Dictionary<DateOnly, double> { [D0] = 0, [D0.AddDays(2)] = 4 };
        CrossInsightStats.Slope(s).Should().BeApproximately(2.0, 1e-9);
    }

    [Fact]
    public void Slope_single_point_is_null()
    {
        CrossInsightStats.Slope(Series(5)).Should().BeNull();
    }

    [Fact]
    public void Project_is_bounded_to_the_observed_span()
    {
        var s = Series(0, 1, 2); // span = 2 days, slope +1/day, last value 2
        // Ask for 100 days ahead — bounded to <= span (2) ⇒ projects 2 + 2 = 4 (not 102).
        CrossInsightStats.Project(s, 100).Should().BeApproximately(4.0, 1e-9);
    }

    // ---- z-score anomaly ----

    [Fact]
    public void TopZ_flags_the_outlier_day()
    {
        var s = Series(10, 10, 10, 10, 10, 10, 100); // last day is the spike
        var top = CrossInsightStats.TopZ(s);
        top.Should().NotBeNull();
        top!.Value.Date.Should().Be(D0.AddDays(6));
        top.Value.Z.Should().BeGreaterThan(2.0);
    }

    [Fact]
    public void TopZ_zero_variance_is_null()
    {
        CrossInsightStats.TopZ(Series(7, 7, 7, 7)).Should().BeNull();
    }

    // ---- Runs ----

    [Fact]
    public void Runs_finds_longest_and_current()
    {
        var asOf = D0.AddDays(9);
        var set = new HashSet<DateOnly>
        {
            D0, D0.AddDays(1), D0.AddDays(2),                 // run of 3
            D0.AddDays(5),
            D0.AddDays(8), D0.AddDays(9),                     // current run of 2 ending at asOf
        };
        var (longest, current) = CrossInsightStats.Runs(set, asOf);
        longest.Should().Be(3);
        current.Should().Be(2);
    }

    [Fact]
    public void Runs_current_is_zero_when_asof_not_qualifying()
    {
        var set = new HashSet<DateOnly> { D0, D0.AddDays(1) };
        var (_, current) = CrossInsightStats.Runs(set, D0.AddDays(5));
        current.Should().Be(0);
    }

    // ---- Magnitude buckets ----

    [Theory]
    [InlineData(0.05, "negligible")]
    [InlineData(0.25, "weak")]
    [InlineData(0.5, "moderate")]
    [InlineData(0.85, "strong")]
    [InlineData(-0.85, "strong")]
    public void Magnitude_buckets_by_absolute_r(double r, string expected)
    {
        CrossInsightStats.MagnitudeFor(r).Should().Be(expected);
    }

    // ---- Catalog floors ----

    [Fact]
    public void Catalog_drops_correlation_below_ten_paired_days()
    {
        // 9 perfectly-correlated paired days — under the n>=10 floor ⇒ dropped.
        var x = new Dictionary<DateOnly, double>();
        var y = new Dictionary<DateOnly, double>();
        for (var i = 0; i < 9; i++) { x[D0.AddDays(i)] = i; y[D0.AddDays(i)] = i * 2; }

        var series = new Dictionary<string, IReadOnlyDictionary<DateOnly, double>> { ["x"] = x, ["y"] = y };
        var corr = new List<CrossInsightStats.CorrelationSpec>
        {
            new("x", "y", "X vs Y", "test", "up", "down"),
        };
        var cards = CrossInsightStats.ComputeCatalog(
            series, corr,
            Array.Empty<CrossInsightStats.TrendSpec>(),
            Array.Empty<CrossInsightStats.AnomalySpec>(),
            Array.Empty<CrossInsightStats.BestWorstSpec>(),
            Array.Empty<(string, string, IReadOnlySet<DateOnly>)>(),
            D0.AddDays(8));
        cards.Where(c => c.Kind == CrossInsightStats.Kind.Correlation).Should().BeEmpty();
    }

    [Fact]
    public void Catalog_emits_correlation_at_ten_paired_days()
    {
        var x = new Dictionary<DateOnly, double>();
        var y = new Dictionary<DateOnly, double>();
        for (var i = 0; i < 10; i++) { x[D0.AddDays(i)] = i; y[D0.AddDays(i)] = i * 2; }

        var series = new Dictionary<string, IReadOnlyDictionary<DateOnly, double>> { ["x"] = x, ["y"] = y };
        var corr = new List<CrossInsightStats.CorrelationSpec>
        {
            new("x", "y", "X vs Y", "test", "up", "down"),
        };
        var cards = CrossInsightStats.ComputeCatalog(
            series, corr,
            Array.Empty<CrossInsightStats.TrendSpec>(),
            Array.Empty<CrossInsightStats.AnomalySpec>(),
            Array.Empty<CrossInsightStats.BestWorstSpec>(),
            Array.Empty<(string, string, IReadOnlySet<DateOnly>)>(),
            D0.AddDays(9));
        var card = cards.Single(c => c.Kind == CrossInsightStats.Kind.Correlation);
        card.DataPoints.Should().Be(10);
        card.Stat.Should().Contain("strong");
        card.Detail.Should().Contain("Association, not causation");
    }
}
