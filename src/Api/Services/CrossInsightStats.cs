namespace Ccusage.Api.Services;

/// <summary>
/// PURE, side-effect-free cross-domain statistics over a user's OWN already-derived per-day series — the
/// deterministic engine behind the Insight Engine (<c>/api/insights</c>). Everything here is a total function
/// of plain numbers (no entity/db types, no I/O): NaN/Infinity-safe, clamped, and never throws. Modeled on
/// <see cref="TrackerStats.ComputeRecovery"/> (the pure-math precedent): the AI only ever NARRATES these
/// numbers — it never produces or recomputes them.
///
/// <para>The catalog computation takes a bag of named <see cref="DailySeries"/> (a sparse date→value map per
/// metric) and emits a set of <see cref="InsightResult"/> cards. Every candidate is DROPPED when its data
/// floor isn't met (e.g. a correlation needs &gt;= <see cref="CorrelationMinPairs"/> paired days), so the
/// result only ever contains statistically honest cards. Correlations are labeled "association, not
/// causation"; forecasts are a BOUNDED estimate (&lt;= ~1 period ahead), never a prediction.</para>
/// </summary>
public static class CrossInsightStats
{
    // ---- Statistical-honesty floors + buckets (the load-bearing contract) ----

    /// <summary>The minimum number of PAIRED days a correlation needs before it is emitted at all.</summary>
    public const int CorrelationMinPairs = 10;

    /// <summary>The minimum number of points a trend (regression) needs.</summary>
    public const int TrendMinPoints = 5;

    /// <summary>The minimum number of points an anomaly (z-score) scan needs.</summary>
    public const int AnomalyMinPoints = 7;

    /// <summary>|z| at/above which a day is flagged an anomaly.</summary>
    public const double AnomalyZThreshold = 2.0;

    /// <summary>The |r| magnitude buckets for a correlation strength label.</summary>
    public static string MagnitudeFor(double r)
    {
        var a = Math.Abs(r);
        return a >= 0.7 ? "strong"
            : a >= 0.4 ? "moderate"
            : a >= 0.2 ? "weak"
            : "negligible";
    }

    /// <summary>The closed set of insight kinds. The frontend groups by these.</summary>
    public static class Kind
    {
        public const string Correlation = "correlation";
        public const string Trend = "trend";
        public const string Streak = "streak";
        public const string Anomaly = "anomaly";
        public const string BestWorst = "bestworst";
    }

    /// <summary>
    /// A named, sparse per-day series for one metric: <paramref name="Key"/> is a stable id (e.g.
    /// "recovery", "sleep_hours", "ai_spend"), <paramref name="Points"/> is the date→value map (only days
    /// the metric was actually recorded). All owner-scoped + already-derived upstream.
    /// </summary>
    public readonly record struct DailySeries(string Key, IReadOnlyDictionary<DateOnly, double> Points);

    /// <summary>
    /// One deterministic insight card. <paramref name="Kind"/> is from the closed <see cref="Kind"/> set;
    /// <paramref name="Title"/> is the human headline; <paramref name="Stat"/> is the compact stat string
    /// ("r=0.61 · moderate"); <paramref name="Magnitude"/> is the bucket/direction label; <paramref
    /// name="Detail"/> is a one-line explanation (carries the "association, not causation" / estimate
    /// microcopy where relevant); <paramref name="Domain"/> is the accent hint ("sleep", "usage", …);
    /// <paramref name="DataPoints"/> is the honesty count (paired days / points behind the stat).
    /// </summary>
    public readonly record struct InsightResult(
        string Kind, string Title, string Stat, string Magnitude, string Detail, string Domain, int DataPoints);

    // ===================================================================================
    // Pure helpers — Pearson r, regression slope + bounded projection, z-score, runs
    // ===================================================================================

    /// <summary>
    /// Pearson correlation coefficient over the PAIRED days of two series (only dates present in BOTH count).
    /// Returns null when fewer than 2 paired points or when either side has zero variance (r is undefined).
    /// NaN/Infinity-safe: a non-finite intermediate yields null. Result is clamped to [-1, 1].
    /// </summary>
    public static double? Pearson(
        IReadOnlyDictionary<DateOnly, double> a, IReadOnlyDictionary<DateOnly, double> b)
    {
        if (a is null || b is null) return null;
        // Walk the smaller map for the intersection.
        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        var xs = new List<double>();
        var ys = new List<double>();
        foreach (var kv in small)
        {
            if (!large.TryGetValue(kv.Key, out var other)) continue;
            var xa = a == small ? kv.Value : other;
            var yb = a == small ? other : kv.Value;
            if (!double.IsFinite(xa) || !double.IsFinite(yb)) continue;
            xs.Add(xa);
            ys.Add(yb);
        }
        var n = xs.Count;
        if (n < 2) return null;

        double mx = xs.Average(), my = ys.Average();
        double sxy = 0, sxx = 0, syy = 0;
        for (var i = 0; i < n; i++)
        {
            var dx = xs[i] - mx;
            var dy = ys[i] - my;
            sxy += dx * dy;
            sxx += dx * dx;
            syy += dy * dy;
        }
        if (sxx <= 0 || syy <= 0) return null; // zero variance ⇒ undefined
        var r = sxy / Math.Sqrt(sxx * syy);
        if (!double.IsFinite(r)) return null;
        return Math.Clamp(r, -1.0, 1.0);
    }

    /// <summary>
    /// Ordinary-least-squares slope of value vs day-index (units of value PER DAY), over the points ordered by
    /// date. The x-axis is the integer day-offset from the earliest point (so the slope is per calendar day,
    /// gaps respected). Returns null with &lt; 2 points or zero x-variance. NaN/Infinity-safe.
    /// </summary>
    public static double? Slope(IReadOnlyDictionary<DateOnly, double> series)
    {
        if (series is null || series.Count < 2) return null;
        var ordered = series.Where(kv => double.IsFinite(kv.Value)).OrderBy(kv => kv.Key).ToList();
        if (ordered.Count < 2) return null;
        var origin = ordered[0].Key;
        var xs = ordered.Select(kv => (double)(kv.Key.DayNumber - origin.DayNumber)).ToList();
        var ys = ordered.Select(kv => kv.Value).ToList();
        double mx = xs.Average(), my = ys.Average();
        double sxy = 0, sxx = 0;
        for (var i = 0; i < xs.Count; i++)
        {
            var dx = xs[i] - mx;
            sxy += dx * (ys[i] - my);
            sxx += dx * dx;
        }
        if (sxx <= 0) return null;
        var slope = sxy / sxx;
        return double.IsFinite(slope) ? slope : (double?)null;
    }

    /// <summary>
    /// A BOUNDED linear projection of <paramref name="series"/> <paramref name="horizonDays"/> ahead of its
    /// last point, using the OLS fit. The horizon is CLAMPED to at most the observed span (&lt;= ~1 period),
    /// so this is an ESTIMATE, never an unbounded prediction. Returns null when the slope is undefined.
    /// </summary>
    public static double? Project(IReadOnlyDictionary<DateOnly, double> series, int horizonDays)
    {
        if (Slope(series) is not { } slope) return null;
        var ordered = series.Where(kv => double.IsFinite(kv.Value)).OrderBy(kv => kv.Key).ToList();
        if (ordered.Count < 2) return null;
        var origin = ordered[0].Key;
        var xs = ordered.Select(kv => (double)(kv.Key.DayNumber - origin.DayNumber)).ToList();
        var ys = ordered.Select(kv => kv.Value).ToList();
        double mx = xs.Average(), my = ys.Average();
        var intercept = my - slope * mx; // y = slope*x + intercept
        var span = xs[^1]; // earliest is 0, so the span IS the last x
        var bounded = Math.Clamp(horizonDays, 0, (int)Math.Max(1, span));
        var projX = xs[^1] + bounded;
        var y = slope * projX + intercept;
        return double.IsFinite(y) ? y : (double?)null;
    }

    /// <summary>
    /// The most extreme z-score day in <paramref name="series"/>: returns the date, its value, and its signed
    /// z-score (value − mean) / stddev (population). Returns null with &lt; 2 points or zero variance.
    /// NaN-safe. The caller decides the |z| threshold for "anomaly".
    /// </summary>
    public static (DateOnly Date, double Value, double Z)? TopZ(IReadOnlyDictionary<DateOnly, double> series)
    {
        if (series is null) return null;
        var pts = series.Where(kv => double.IsFinite(kv.Value)).ToList();
        if (pts.Count < 2) return null;
        var vals = pts.Select(p => p.Value).ToList();
        var mean = vals.Average();
        var variance = vals.Select(v => (v - mean) * (v - mean)).Sum() / vals.Count; // population
        var sd = Math.Sqrt(variance);
        if (!(sd > 0) || !double.IsFinite(sd)) return null;

        (DateOnly Date, double Value, double Z)? best = null;
        foreach (var p in pts)
        {
            var z = (p.Value - mean) / sd;
            if (!double.IsFinite(z)) continue;
            if (best is null || Math.Abs(z) > Math.Abs(best.Value.Z))
                best = (p.Key, p.Value, z);
        }
        return best;
    }

    /// <summary>The best (max) and worst (min) day of a series. Null when empty / all non-finite.</summary>
    public static ((DateOnly Date, double Value) Best, (DateOnly Date, double Value) Worst)? BestWorst(
        IReadOnlyDictionary<DateOnly, double> series)
    {
        if (series is null) return null;
        var pts = series.Where(kv => double.IsFinite(kv.Value))
            .OrderBy(kv => kv.Key).ToList();
        if (pts.Count == 0) return null;
        var best = pts[0];
        var worst = pts[0];
        foreach (var p in pts)
        {
            if (p.Value > best.Value) best = p;
            if (p.Value < worst.Value) worst = p;
        }
        return ((best.Key, best.Value), (worst.Key, worst.Value));
    }

    /// <summary>
    /// The LONGEST and CURRENT consecutive run of qualifying dates in the set. Pure set walk (both 0 when
    /// empty). "Current" = the run ending at <paramref name="asOf"/> (inclusive); 0 when asOf isn't qualifying.
    /// Mirrors <c>WrappedEndpoints.LongestRun</c> (reused for the longest leg) plus a current-run tail walk.
    /// </summary>
    public static (int Longest, int Current) Runs(IReadOnlySet<DateOnly> qualifying, DateOnly asOf)
    {
        if (qualifying.Count == 0) return (0, 0);
        var longest = 0;
        foreach (var d in qualifying)
        {
            if (qualifying.Contains(d.AddDays(-1))) continue; // only start at a run's head
            var len = 0;
            var cursor = d;
            while (qualifying.Contains(cursor)) { len++; cursor = cursor.AddDays(1); }
            if (len > longest) longest = len;
        }

        var current = 0;
        var c = asOf;
        while (qualifying.Contains(c)) { current++; c = c.AddDays(-1); }
        return (longest, current);
    }

    // ===================================================================================
    // Candidate catalog — emit the honest, floored set of cards from the named series
    // ===================================================================================

    /// <summary>One correlation candidate: the two series keys, a title, the domain accent, and the direction
    /// words for a positive vs negative r ("more sleep → higher recovery").</summary>
    public readonly record struct CorrelationSpec(
        string XKey, string YKey, string Title, string Domain, string PosWord, string NegWord);

    /// <summary>One trend candidate: the series key, a title, the unit label (e.g. "kg/wk"), the per-day→unit
    /// multiplier (7 for /wk), the domain accent, and whether to project a bounded estimate.</summary>
    public readonly record struct TrendSpec(
        string Key, string Title, string Unit, double PerDayToUnit, string Domain, bool Project);

    /// <summary>One anomaly candidate: the series key, a HIGH-side and LOW-side title (the surfaced outlier can be
    /// above OR below the mean, so the title is chosen by the sign of z), the unit suffix, and the domain accent.</summary>
    public readonly record struct AnomalySpec(string Key, string TitleHigh, string TitleLow, string Unit, string Domain);

    /// <summary>One best/worst candidate: the series key, a title, the unit suffix, the domain accent, and
    /// whether HIGHER is better (so the "best" label points the right way).</summary>
    public readonly record struct BestWorstSpec(
        string Key, string Title, string Unit, string Domain, bool HigherIsBetter);

    /// <summary>
    /// Compute the full deterministic catalog from the named <paramref name="series"/> bag. Each candidate is
    /// dropped when its floor isn't met. PURE — no I/O, no throw. <paramref name="asOf"/> anchors the current
    /// streak. The streak candidates are passed PRE-RESOLVED (their qualifying date sets) because the
    /// qualification rule (goal-met, recovery-band) lives with the data reader, not here.
    /// </summary>
    public static IReadOnlyList<InsightResult> ComputeCatalog(
        IReadOnlyDictionary<string, IReadOnlyDictionary<DateOnly, double>> series,
        IReadOnlyList<CorrelationSpec> correlations,
        IReadOnlyList<TrendSpec> trends,
        IReadOnlyList<AnomalySpec> anomalies,
        IReadOnlyList<BestWorstSpec> bestWorsts,
        IReadOnlyList<(string Title, string Domain, IReadOnlySet<DateOnly> Qualifying)> streaks,
        DateOnly asOf)
    {
        var cards = new List<InsightResult>();

        // ---- (1) CORRELATIONS — n>=CorrelationMinPairs, bucketed, association-not-causation ----
        foreach (var c in correlations)
        {
            if (!series.TryGetValue(c.XKey, out var xs) || !series.TryGetValue(c.YKey, out var ys)) continue;
            var n = CountPaired(xs, ys);
            if (n < CorrelationMinPairs) continue; // statistical-honesty floor — DROP
            if (Pearson(xs, ys) is not { } r) continue;
            var mag = MagnitudeFor(r);
            if (mag == "negligible") continue; // nothing worth surfacing
            var dir = r >= 0 ? c.PosWord : c.NegWord;
            cards.Add(new InsightResult(
                Kind.Correlation, c.Title,
                $"r={r:0.00} · {mag}", $"{mag} {(r >= 0 ? "positive" : "negative")}",
                $"{dir}. Association, not causation — patterns in your own logs, not medical advice.",
                c.Domain, n));
        }

        // ---- (2) TRENDS — regression slope + bounded projection ----
        foreach (var t in trends)
        {
            if (!series.TryGetValue(t.Key, out var s) || s.Count < TrendMinPoints) continue;
            if (Slope(s) is not { } perDay) continue;
            var perUnit = perDay * t.PerDayToUnit;
            var dir = Math.Abs(perUnit) < 1e-9 ? "flat" : perUnit > 0 ? "up" : "down";
            var stat = $"{(perUnit > 0 ? "+" : "")}{perUnit:0.0#} {t.Unit}";
            var detail = dir == "flat" ? "Holding steady over the window."
                : $"Trending {dir} over the window.";
            if (t.Project && Project(s, 7) is { } proj)
                detail += $" Bounded estimate ~{proj:0.#} if it holds (an estimate, not a prediction).";
            cards.Add(new InsightResult(
                Kind.Trend, t.Title, stat, dir, detail, t.Domain, s.Count));
        }

        // ---- (3) STREAKS — longest + current qualifying run ----
        foreach (var st in streaks)
        {
            if (st.Qualifying.Count == 0) continue;
            var (longest, current) = Runs(st.Qualifying, asOf);
            if (longest <= 0) continue;
            var stat = $"Best run: {longest} day{(longest == 1 ? "" : "s")}";
            var detail = current > 0
                ? $"Currently on a {current}-day run. {st.Qualifying.Count} qualifying days total."
                : $"{st.Qualifying.Count} qualifying days total.";
            cards.Add(new InsightResult(
                Kind.Streak, st.Title, stat, current > 0 ? "active" : "idle", detail, st.Domain, st.Qualifying.Count));
        }

        // ---- (4) ANOMALIES — |z| >= AnomalyZThreshold outlier day ----
        foreach (var a in anomalies)
        {
            if (!series.TryGetValue(a.Key, out var s) || s.Count < AnomalyMinPoints) continue;
            if (TopZ(s) is not { } top || Math.Abs(top.Z) < AnomalyZThreshold) continue;
            var sign = top.Z > 0 ? "+" : "−";
            cards.Add(new InsightResult(
                Kind.Anomaly, top.Z > 0 ? a.TitleHigh : a.TitleLow,
                $"{top.Value:0.#}{a.Unit} on {top.Date:MMM d}", $"{sign}{Math.Abs(top.Z):0.0}σ",
                $"Stood out at {sign}{Math.Abs(top.Z):0.0} standard deviations from your own average.",
                a.Domain, s.Count));
        }

        // ---- (5) BEST / WORST day per metric ----
        foreach (var bw in bestWorsts)
        {
            if (!series.TryGetValue(bw.Key, out var s) || s.Count < 2) continue;
            if (BestWorst(s) is not { } res) continue;
            if (Math.Abs(res.Best.Value - res.Worst.Value) < 1e-9) continue; // flat — nothing to show
            cards.Add(new InsightResult(
                Kind.BestWorst, bw.Title,
                $"Best {res.Best.Value:0.#}{bw.Unit} on {res.Best.Date:MMM d}", "range",
                $"Lowest was {res.Worst.Value:0.#}{bw.Unit} on {res.Worst.Date:MMM d}.",
                bw.Domain, s.Count));
        }

        return cards;
    }

    /// <summary>Count of dates present in BOTH maps (the paired-day n). Pure, total.</summary>
    public static int CountPaired(
        IReadOnlyDictionary<DateOnly, double> a, IReadOnlyDictionary<DateOnly, double> b)
    {
        if (a is null || b is null) return 0;
        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        var n = 0;
        foreach (var k in small.Keys)
        {
            if (!large.TryGetValue(k, out var ov) || !large.ContainsKey(k)) continue;
            if (!double.IsFinite(ov)) continue;
            if (!small.TryGetValue(k, out var sv) || !double.IsFinite(sv)) continue;
            n++;
        }
        return n;
    }
}
