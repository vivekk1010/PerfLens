using PerfLens.Core.Configuration;
using PerfLens.Core.Results;
using PerfLens.Core.Statistics;

namespace PerfLens.Analysis;

/// <summary>Page, API and overall scoring.</summary>
public static class Scoring
{
    /// <summary>Lighthouse-style log-normal control points: a value of <see cref="P10"/> scores 0.9, <see cref="Median"/> scores 0.5.</summary>
    public sealed record Curve(string Metric, double P10, double Median, double Weight);

    /// <summary>Desktop-ish control points and weights (renormalized when metrics are missing).</summary>
    public static readonly IReadOnlyList<Curve> PageCurves =
    [
        new(MetricNames.Fcp, 934, 1600, 15),
        new(MetricNames.Lcp, 1200, 2400, 25),
        new(MetricNames.Tbt, 150, 350, 30),
        new(MetricNames.Cls, 0.1, 0.25, 25),
        new(MetricNames.Ttfb, 400, 1000, 5),
    ];

    public const double PageWeight = 0.7;
    public const double ApiWeight = 0.3;

    /// <summary>
    /// Lighthouse log-normal score in [0, 1]: the complementary CDF of a log-normal distribution whose
    /// median is <paramref name="median"/> and whose 10th percentile is <paramref name="p10"/>.
    /// score = erfc(ln(value/median) * erfcInv(0.2) / -ln(p10/median)) / 2.
    /// </summary>
    public static double LogNormalScore(double value, double p10, double median)
    {
        if (double.IsNaN(value)) return 0;
        if (value <= 0) return 1;
        const double inverseErfcOneFifth = 0.9061938024368232;
        double xLogRatio = Math.Log(Math.Max(double.Epsilon, value / median));
        double p10LogRatio = -Math.Log(Math.Max(double.Epsilon, p10 / median));
        double standardizedX = xLogRatio * inverseErfcOneFifth / p10LogRatio;
        return Math.Clamp(MathUtil.Erfc(standardizedX) / 2, 0, 1);
    }

    /// <summary>Weighted 0-100 page score using <paramref name="statistic"/> of each metric. 0 without measured iterations.</summary>
    public static double PageScore(PageSummary? summary, string statistic)
    {
        if (summary is null || summary.MeasuredIterations == 0) return 0;
        double weighted = 0, totalWeight = 0;
        foreach (var curve in PageCurves)
        {
            if (!summary.Metrics.TryGetValue(curve.Metric, out var stats) || stats.Count == 0) continue;
            weighted += curve.Weight * LogNormalScore(StatReader.Get(stats, statistic), curve.P10, curve.Median);
            totalWeight += curve.Weight;
        }
        return totalWeight == 0 ? 0 : Math.Round(weighted / totalWeight * 100, 1);
    }

    /// <summary>
    /// API endpoint score (0-100). Unreachable endpoints (Error set or no successful request) score 0. Otherwise:
    /// <code>
    /// score = 100
    ///       - min(60, errorRatePct * 6)                         // 10% errors costs the full 60
    ///       - min(40, max(0, p95 / p95Limit - 1) * 40)           // 2x the P95 limit costs the full 40
    /// </code>
    /// where p95Limit is the endpoint's P95Ms threshold, else the global one; without a limit there is no latency penalty.
    /// </summary>
    public static double ApiScore(EndpointLoadResult endpoint, ApiThresholds thresholds)
    {
        if (AnalysisContext.IsUnreachable(endpoint)) return 0;
        double score = 100;
        score -= Math.Min(60, Math.Max(0, endpoint.ErrorRatePct) * 6);
        if (thresholds.P95Ms is > 0 and var limit)
            score -= Math.Min(40, Math.Max(0, (endpoint.Latency?.P95 ?? 0) / limit - 1) * 40);
        return Math.Round(Math.Clamp(score, 0, 100), 1);
    }

    /// <summary>70% mean page score + 30% mean API score when both exist; otherwise whichever exists; 0 when neither.</summary>
    public static double Overall(IReadOnlyCollection<double> pageScores, IReadOnlyCollection<double> apiScores)
    {
        double score = (pageScores.Count, apiScores.Count) switch
        {
            (0, 0) => 0,
            (_, 0) => pageScores.Average(),
            (0, _) => apiScores.Average(),
            _ => PageWeight * pageScores.Average() + ApiWeight * apiScores.Average(),
        };
        return Math.Round(score, 1);
    }

    public static string Grade(double score) => score switch
    {
        >= 90 => "A",
        >= 75 => "B",
        >= 60 => "C",
        >= 40 => "D",
        _ => "F",
    };
}

/// <summary>Reads a statistic without throwing; unknown names fall back to p75.</summary>
internal static class StatReader
{
    public const string Default = "p75";

    public static string Normalize(string? statistic)
    {
        if (string.IsNullOrWhiteSpace(statistic)) return Default;
        try { _ = Stats.Get(new MetricStats(), statistic); return statistic.ToLowerInvariant(); }
        catch (ArgumentException) { return Default; }
    }

    public static double Get(MetricStats stats, string statistic) => Stats.Get(stats, Normalize(statistic));
}

internal static class MathUtil
{
    /// <summary>Abramowitz and Stegun 7.1.26 (max abs error 1.5e-7).</summary>
    public static double Erf(double x)
    {
        const double a1 = 0.254829592, a2 = -0.284496736, a3 = 1.421413741, a4 = -1.453152027, a5 = 1.061405429, p = 0.3275911;
        int sign = x < 0 ? -1 : 1;
        x = Math.Abs(x);
        double t = 1 / (1 + p * x);
        double y = 1 - ((((a5 * t + a4) * t + a3) * t + a2) * t + a1) * t * Math.Exp(-x * x);
        return sign * y;
    }

    public static double Erfc(double x) => 1 - Erf(x);
}
