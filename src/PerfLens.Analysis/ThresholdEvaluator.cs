using PerfLens.Core.Results;

namespace PerfLens.Analysis;

/// <summary>Evaluates page and API thresholds (per-page / per-endpoint values override global ones field by field).</summary>
public static class ThresholdEvaluator
{
    public const string Availability = "availability";

    public static List<ThresholdResult> Evaluate(AnalysisContext ctx)
    {
        var results = new List<ThresholdResult>();
        foreach (var page in ctx.Pages) EvaluatePage(ctx, page, results);
        foreach (var endpoint in ctx.Endpoints) EvaluateEndpoint(ctx, endpoint, results);
        return results;
    }

    private static void EvaluatePage(AnalysisContext ctx, PageResult page, List<ThresholdResult> results)
    {
        if (AnalysisContext.IsUnavailable(page))
            results.Add(new ThresholdResult { Scope = "page", Target = page.Name, Metric = Availability, Statistic = "rate", Actual = 0, Limit = 1, Passed = false });

        var t = ctx.EffectivePageThresholds(page);
        (string Metric, double? Limit, string Statistic)[] checks =
        [
            (MetricNames.Ttfb, t.TtfbMs, ctx.Statistic),
            (MetricNames.Fcp, t.FcpMs, ctx.Statistic),
            (MetricNames.Lcp, t.LcpMs, ctx.Statistic),
            (MetricNames.Cls, t.Cls, ctx.Statistic),
            (MetricNames.Tbt, t.TbtMs, ctx.Statistic),
            (MetricNames.Load, t.LoadMs, ctx.Statistic),
            (MetricNames.TransferKb, t.PageWeightKb, ctx.Statistic),
            (MetricNames.Requests, t.RequestCount, ctx.Statistic),
            (MetricNames.ConsoleErrors, t.MaxConsoleErrors, "max"),
        ];
        foreach (var (metric, limit, statistic) in checks)
        {
            if (limit is null || ctx.Stat(page, metric, statistic) is not { } actual) continue;
            results.Add(new ThresholdResult
            {
                Scope = "page", Target = page.Name, Metric = metric, Statistic = statistic,
                Actual = actual, Limit = limit.Value, Passed = actual <= limit.Value,
            });
        }
    }

    private static void EvaluateEndpoint(AnalysisContext ctx, EndpointLoadResult e, List<ThresholdResult> results)
    {
        if (AnalysisContext.IsUnreachable(e))
        {
            results.Add(new ThresholdResult { Scope = "api", Target = e.Name, Metric = Availability, Statistic = "rate", Actual = 0, Limit = 1, Passed = false });
            return;
        }

        var t = ctx.EffectiveApiThresholds(e);
        var latency = e.Latency ?? new MetricStats();
        void Max(string metric, string statistic, double actual, double? limit)
        {
            if (limit is { } l)
                results.Add(new ThresholdResult { Scope = "api", Target = e.Name, Metric = metric, Statistic = statistic, Actual = actual, Limit = l, Passed = actual <= l });
        }

        Max("latency_ms", "p50", latency.Median, t.P50Ms);
        Max("latency_ms", "p95", latency.P95, t.P95Ms);
        Max("latency_ms", "p99", latency.P99, t.P99Ms);
        Max("error_rate_pct", "rate", e.ErrorRatePct, t.ErrorRatePct);
        if (t.MinRps is { } minRps)
            results.Add(new ThresholdResult { Scope = "api", Target = e.Name, Metric = "rps", Statistic = "min", Actual = e.Rps, Limit = minRps, Passed = e.Rps >= minRps });
    }
}
