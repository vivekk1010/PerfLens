using PerfLens.Core.Results;
using PerfLens.Core.Statistics;

namespace PerfLens.Reporting.Internal;

/// <summary>Derived, null-safe views over a <see cref="RunResult"/> shared by the writers.</summary>
internal static class ReportFacts
{
    /// <summary>Core Web Vitals / Lighthouse-style bands: value &lt;= Good is good, &gt; Poor is poor.</summary>
    public static IReadOnlyDictionary<string, (double Good, double Poor)> VitalBands => MetricNames.VitalBands;

    public static readonly string[] VitalMetrics = [MetricNames.Ttfb, MetricNames.Fcp, MetricNames.Lcp, MetricNames.Cls, MetricNames.Tbt];

    public static readonly string[] MetricOrder =
    [
        MetricNames.Ttfb, MetricNames.Fcp, MetricNames.Lcp, MetricNames.Cls, MetricNames.Tbt, MetricNames.LongTasks,
        MetricNames.DomInteractive, MetricNames.DomContentLoaded, MetricNames.Load, MetricNames.WallClock,
        MetricNames.Dns, MetricNames.Connect, MetricNames.Tls, MetricNames.Server, MetricNames.Download,
        MetricNames.Requests, MetricNames.TransferKb, MetricNames.DecodedKb, MetricNames.FailedRequests,
        MetricNames.DomNodes, MetricNames.JsHeapMb, MetricNames.ConsoleErrors,
    ];

    public static readonly string[] ResourceTypeOrder = ["document", "script", "stylesheet", "image", "font", "xhr", "fetch", "media", "other"];

    public static string Label(string metric) => MetricNames.Display.TryGetValue(metric, out var d) ? d.Label : metric;
    public static string Unit(string metric) => MetricNames.Display.TryGetValue(metric, out var d) ? d.Unit : "";

    /// <summary>Statistic used for page charts: the page thresholds' statistic, a "statistic" setting, else p75.</summary>
    public static string Statistic(RunResult run)
    {
        if (IsKnownStatistic(run.Analysis?.PageStatistic)) return run.Analysis!.PageStatistic.ToLowerInvariant();
        var fromThresholds = run.Analysis?.Thresholds
            .Where(t => string.Equals(t.Scope, "page", StringComparison.OrdinalIgnoreCase) && IsKnownStatistic(t.Statistic))
            .GroupBy(t => t.Statistic.ToLowerInvariant()).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        if (fromThresholds != null) return fromThresholds;
        foreach (var (k, v) in run.Settings)
            if (k.Contains("statistic", StringComparison.OrdinalIgnoreCase) && IsKnownStatistic(v.Trim()))
                return v.Trim().ToLowerInvariant();
        return "p75";
    }

    private static bool IsKnownStatistic(string? s) =>
        s?.ToLowerInvariant() is "min" or "max" or "mean" or "avg" or "median" or "p50" or "p75" or "p90" or "p95" or "p99";

    public static double StatValue(MetricStats s, string statistic)
    {
        try { return Stats.Get(s, statistic); }
        catch (ArgumentException) { return s.P75; }
    }

    /// <summary>Raw per-iteration metric values keyed by <see cref="MetricNames"/>.</summary>
    public static Dictionary<string, double?> IterationMetrics(PageIteration it) => new()
    {
        [MetricNames.Ttfb] = it.Timing.TtfbMs,
        [MetricNames.Dns] = it.Timing.DnsMs,
        [MetricNames.Connect] = it.Timing.ConnectMs,
        [MetricNames.Tls] = it.Timing.TlsMs,
        [MetricNames.Server] = it.Timing.ServerMs,
        [MetricNames.Download] = it.Timing.DownloadMs,
        [MetricNames.DomInteractive] = it.Timing.DomInteractiveMs,
        [MetricNames.DomContentLoaded] = it.Timing.DomContentLoadedMs,
        [MetricNames.Load] = it.Timing.LoadEventMs,
        [MetricNames.Fcp] = it.Vitals.FcpMs,
        [MetricNames.Lcp] = it.Vitals.LcpMs,
        [MetricNames.Cls] = it.Vitals.Cls,
        [MetricNames.Tbt] = it.Vitals.TbtMs,
        [MetricNames.LongTasks] = it.Vitals.LongTaskCount,
        [MetricNames.WallClock] = it.WallClockMs,
        [MetricNames.Requests] = it.Resources.TotalRequests,
        [MetricNames.TransferKb] = it.Resources.TransferBytes / 1024.0,
        [MetricNames.DecodedKb] = it.Resources.DecodedBytes / 1024.0,
        [MetricNames.FailedRequests] = it.Resources.FailedRequests,
        [MetricNames.DomNodes] = it.DomNodes,
        [MetricNames.JsHeapMb] = it.JsHeapUsedBytes is { } h ? h / 1048576.0 : null,
        [MetricNames.ConsoleErrors] = it.ConsoleErrors.Count,
    };

    /// <summary>The page's summary metrics, or stats computed from measured iterations when Summary is absent.</summary>
    public static Dictionary<string, MetricStats> Metrics(PageResult page)
    {
        if (page.Summary is { Metrics.Count: > 0 } s) return s.Metrics;
        var measured = page.MeasuredIterations.Select(IterationMetrics).ToList();
        var result = new Dictionary<string, MetricStats>();
        if (measured.Count == 0) return result;
        foreach (var key in MetricOrder)
        {
            var values = measured.Select(m => m.GetValueOrDefault(key)).Where(v => v.HasValue).Select(v => v!.Value).ToList();
            if (values.Count > 0) result[key] = Stats.Compute(values);
        }
        return result;
    }

    public static double? Value(Dictionary<string, MetricStats> metrics, string metric, string statistic) =>
        metrics.TryGetValue(metric, out var s) && s.Count > 0 ? StatValue(s, statistic) : null;

    public static double? Median(Dictionary<string, MetricStats> metrics, string metric) => Value(metrics, metric, "median");

    public static double? Score(PageResult page) => page.Summary?.Score;

    public static string Grade(double? score) => score switch
    {
        null => "n/a",
        >= 90 => "A",
        >= 75 => "B",
        >= 60 => "C",
        >= 40 => "D",
        _ => "F",
    };

    public static VitalRating Rate(string metric, double? value)
    {
        if (value is not { } v || !VitalBands.TryGetValue(metric, out var b)) return VitalRating.Unknown;
        return v <= b.Good ? VitalRating.Good : v <= b.Poor ? VitalRating.NeedsImprovement : VitalRating.Poor;
    }

    /// <summary>Analysis rating when present, else computed from the vital bands.</summary>
    public static VitalRating Rating(RunResult run, PageResult page, string metric, double? value)
    {
        if (run.Analysis?.VitalRatings.TryGetValue(page.Name, out var byMetric) == true &&
            byMetric.TryGetValue(metric, out var r))
            return r;
        return Rate(metric, value);
    }

    public static string RatingClass(VitalRating r) => r switch
    {
        VitalRating.Good => "good",
        VitalRating.NeedsImprovement => "ni",
        VitalRating.Poor => "poor",
        _ => "unknown",
    };

    public static string RatingText(VitalRating r) => r switch
    {
        VitalRating.Good => "Good",
        VitalRating.NeedsImprovement => "Needs improvement",
        VitalRating.Poor => "Poor",
        _ => "n/a",
    };

    /// <summary>Average transfer bytes by resource type across measured iterations.</summary>
    public static Dictionary<string, double> AvgBytesByType(PageResult page)
    {
        var its = page.MeasuredIterations.ToList();
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (its.Count == 0) return result;
        foreach (var it in its)
            foreach (var (type, stat) in it.Resources.ByType)
                result[type] = result.GetValueOrDefault(type) + stat.TransferBytes;
        foreach (var k in result.Keys.ToList()) result[k] /= its.Count;
        return result;
    }

    /// <summary>The measured iteration whose load time is closest to the median (falls back to any successful one).</summary>
    public static PageIteration? RepresentativeIteration(PageResult page)
    {
        var its = page.MeasuredIterations.ToList();
        if (its.Count == 0) its = page.Iterations.Where(i => i.Success).ToList();
        if (its.Count == 0) return null;
        var median = Stats.PercentileSorted(its.Select(i => i.Timing.LoadEventMs).OrderBy(v => v).ToList(), 50);
        return its.OrderBy(i => Math.Abs(i.Timing.LoadEventMs - median)).First();
    }

    public static List<Finding> FindingsFor(RunResult run, PageResult page) =>
        run.Analysis?.Findings.Where(f => f.Target == page.Name || (page.Url.Length > 0 && f.Target == page.Url))
            .OrderByDescending(f => f.Severity).ToList() ?? [];

    public static int MeasuredCount(PageResult page) => page.Summary?.MeasuredIterations ?? page.MeasuredIterations.Count();

    public static IEnumerable<string> OrderedResourceTypes(IEnumerable<string> types)
    {
        var set = types.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return ResourceTypeOrder.Where(t => set.Contains(t, StringComparer.OrdinalIgnoreCase))
            .Concat(set.Where(t => !ResourceTypeOrder.Contains(t, StringComparer.OrdinalIgnoreCase)).OrderBy(t => t));
    }
}
