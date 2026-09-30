using PerfLens.Core.Results;

namespace PerfLens.Analysis;

/// <summary>
/// Compares a run with a baseline run. A metric regresses when it is worse by more than the tolerance (percent)
/// AND the absolute change exceeds a per-unit guard (ms: 20, cls: 0.02, KB: 20, requests: 2, error rate: 0.5 pp, rps: 1).
/// It improves when better by more than the tolerance with the same absolute guard.
/// </summary>
public static class BaselineComparer
{
    private static readonly (string Metric, double MinAbsChange)[] PageMetrics =
    [
        (MetricNames.Ttfb, 20), (MetricNames.Fcp, 20), (MetricNames.Lcp, 20), (MetricNames.Tbt, 20),
        (MetricNames.Load, 20), (MetricNames.TransferKb, 20), (MetricNames.Requests, 2), (MetricNames.Cls, 0.02),
    ];

    public static List<RegressionEntry> Compare(AnalysisContext ctx, RunResult baseline)
    {
        var entries = new List<RegressionEntry>();
        var baselinePages = (baseline.Pages ?? []).Where(p => p is not null)
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var page in ctx.Pages)
        {
            if (!baselinePages.TryGetValue(page.Name, out var basePage)) continue;
            basePage.Summary ??= PageSummarizer.Summarize(basePage);
            foreach (var (metric, guard) in PageMetrics)
            {
                var current = ctx.Stat(page, metric);
                var previous = ctx.Stat(basePage, metric);
                if (current is null || previous is null) continue;
                Add(entries, ctx.TolerancePct, "page", page.Name, metric, previous.Value, current.Value, guard, lowerIsWorse: false);
            }
        }

        var baselineEndpoints = (baseline.Api?.Endpoints ?? []).Where(e => e is not null)
            .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var e in ctx.Endpoints)
        {
            if (!baselineEndpoints.TryGetValue(e.Name, out var b)) continue;
            if (AnalysisContext.IsUnreachable(e) || AnalysisContext.IsUnreachable(b)) continue;
            var cl = e.Latency ?? new MetricStats();
            var bl = b.Latency ?? new MetricStats();
            Add(entries, ctx.TolerancePct, "api", e.Name, "p50_ms", bl.Median, cl.Median, 20, false);
            Add(entries, ctx.TolerancePct, "api", e.Name, "p95_ms", bl.P95, cl.P95, 20, false);
            Add(entries, ctx.TolerancePct, "api", e.Name, "p99_ms", bl.P99, cl.P99, 20, false);
            Add(entries, ctx.TolerancePct, "api", e.Name, "error_rate_pct", b.ErrorRatePct, e.ErrorRatePct, 0.5, false);
            Add(entries, ctx.TolerancePct, "api", e.Name, "rps", b.Rps, e.Rps, 1, lowerIsWorse: true);
        }
        return entries;
    }

    private static void Add(List<RegressionEntry> entries, double tolerancePct, string scope, string target, string metric,
        double baseline, double current, double minAbsChange, bool lowerIsWorse)
    {
        if (baseline == 0 || double.IsNaN(baseline) || double.IsNaN(current)) return;
        double deltaPct = (current - baseline) / Math.Abs(baseline) * 100;
        double worsePct = lowerIsWorse ? -deltaPct : deltaPct;
        bool meaningful = Math.Abs(current - baseline) > minAbsChange;
        entries.Add(new RegressionEntry
        {
            Scope = scope, Target = target, Metric = metric,
            Baseline = baseline, Current = current, DeltaPct = Math.Round(deltaPct, 2),
            Regressed = meaningful && worsePct > tolerancePct,
            Improved = meaningful && -worsePct > tolerancePct,
        });
    }
}
