using PerfLens.Core.Results;
using PerfLens.Core.Statistics;
using PerfLens.Core.Utilities;

namespace PerfLens.Analysis;

/// <summary>Aggregates a page's measured (non-warm-up, successful) iterations into a <see cref="PageSummary"/>.</summary>
public static class PageSummarizer
{
    private static readonly (string Metric, Func<PageIteration, double?> Select)[] Extractors =
    [
        (MetricNames.Ttfb, i => i.Timing?.TtfbMs),
        (MetricNames.Dns, i => i.Timing?.DnsMs),
        (MetricNames.Connect, i => i.Timing?.ConnectMs),
        (MetricNames.Tls, i => i.Timing?.TlsMs),
        (MetricNames.Server, i => i.Timing?.ServerMs),
        (MetricNames.Download, i => i.Timing?.DownloadMs),
        (MetricNames.DomInteractive, i => i.Timing?.DomInteractiveMs),
        (MetricNames.DomContentLoaded, i => i.Timing?.DomContentLoadedMs),
        (MetricNames.Load, i => i.Timing?.LoadEventMs),
        (MetricNames.Fcp, i => i.Vitals?.FcpMs),
        (MetricNames.Lcp, i => i.Vitals?.LcpMs),
        (MetricNames.Cls, i => i.Vitals?.Cls),
        (MetricNames.Tbt, i => i.Vitals?.TbtMs),
        (MetricNames.LongTasks, i => i.Vitals?.LongTaskCount),
        (MetricNames.WallClock, i => i.WallClockMs),
        (MetricNames.Requests, i => i.Resources?.TotalRequests),
        (MetricNames.TransferKb, i => i.Resources is null ? null : i.Resources.TransferBytes / 1024.0),
        (MetricNames.DecodedKb, i => i.Resources is null ? null : i.Resources.DecodedBytes / 1024.0),
        (MetricNames.FailedRequests, i => i.Resources?.FailedRequests),
        (MetricNames.DomNodes, i => i.DomNodes),
        (MetricNames.JsHeapMb, i => i.JsHeapUsedBytes is { } b ? b / (1024.0 * 1024.0) : null),
        (MetricNames.ConsoleErrors, i => (i.ConsoleErrors?.Count ?? 0) + (i.PageErrors?.Count ?? 0)),
    ];

    /// <summary>Non-warm-up iterations that succeeded. Null-safe replacement for <see cref="PageResult.MeasuredIterations"/>.</summary>
    public static List<PageIteration> Measured(PageResult page) =>
        (page.Iterations ?? []).Where(i => i is not null && !i.IsWarmup && i.Success).ToList();

    public static PageSummary Summarize(PageResult page)
    {
        var measured = Measured(page);
        var summary = new PageSummary
        {
            MeasuredIterations = measured.Count,
            FailedIterations = (page.Iterations ?? []).Count(i => i is not null && !i.IsWarmup && !i.Success),
        };

        foreach (var (metric, select) in Extractors)
        {
            var values = measured.Select(select).Where(v => v.HasValue).Select(v => v!.Value).ToList();
            if (values.Count == 0) continue;
            var stats = Stats.Compute(values);
            if (stats.Count > 0) summary.Metrics[metric] = stats;
        }

        summary.ApiCalls = SummarizeApiCalls(measured);
        return summary;
    }

    public static List<ApiCallSummary> SummarizeApiCalls(IEnumerable<PageIteration> measured) =>
        measured
            .SelectMany(i => i.ApiCalls ?? [])
            .Where(c => c is not null)
            .GroupBy(c => (Method: (c.Method ?? "GET").ToUpperInvariant(), Url: string.IsNullOrEmpty(c.NormalizedUrl) ? c.Url ?? "" : c.NormalizedUrl))
            .Select(g => new ApiCallSummary
            {
                Method = g.Key.Method,
                NormalizedUrl = g.Key.Url,
                SampleUrl = UrlUtils.RedactSecrets(g.First().Url ?? ""),
                Calls = g.Count(),
                Failures = g.Count(c => c.Failed || c.Status >= 400),
                Duration = Stats.Compute(g.Select(c => c.DurationMs)),
                AvgBytes = g.Average(c => (double)c.Bytes),
            })
            .OrderByDescending(s => s.Calls)
            .ThenByDescending(s => s.Duration.P95)
            .ThenBy(s => s.Method, StringComparer.Ordinal)
            .ThenBy(s => s.NormalizedUrl, StringComparer.Ordinal)
            .ToList();
}
