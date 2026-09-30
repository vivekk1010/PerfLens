using System.Text;
using PerfLens.Core.Abstractions;
using PerfLens.Core.Results;
using PerfLens.Reporting.Internal;

namespace PerfLens.Reporting;

/// <summary>Writes pages.csv, iterations.csv, api.csv, findings.csv and thresholds.csv.</summary>
public sealed class CsvReportWriter : IReportWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public string Format => "csv";

    public async Task<IReadOnlyList<string>> WriteAsync(RunResult run, string outputDirectory, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        Directory.CreateDirectory(outputDirectory);
        var files = new (string Name, string Content)[]
        {
            ("pages.csv", Pages(run)),
            ("iterations.csv", Iterations(run)),
            ("api.csv", Api(run)),
            ("findings.csv", Findings(run)),
            ("thresholds.csv", Thresholds(run)),
        };
        var written = new List<string>();
        foreach (var (name, content) in files)
        {
            var path = Path.Combine(outputDirectory, name);
            await File.WriteAllTextAsync(path, content, Utf8NoBom, ct);
            written.Add(path);
        }
        return written;
    }

    internal static string Pages(RunResult run)
    {
        var csv = new Csv();
        var header = new List<object?> { "page", "url", "source", "depth", "score", "grade", "measured_iterations", "failed_iterations", "error" };
        foreach (var m in ReportFacts.MetricOrder)
            header.AddRange([$"{m}_median", $"{m}_p75", $"{m}_p95"]);
        csv.Row(header);
        foreach (var p in run.Pages)
        {
            var metrics = ReportFacts.Metrics(p);
            var score = ReportFacts.Score(p);
            var row = new List<object?>
            {
                p.Name, p.Url, p.Source.ToString(), p.Depth, score, score is null ? null : ReportFacts.Grade(score),
                ReportFacts.MeasuredCount(p), p.Summary?.FailedIterations ?? p.Iterations.Count(i => !i.Success), p.Error,
            };
            foreach (var m in ReportFacts.MetricOrder)
            {
                if (metrics.TryGetValue(m, out var s) && s.Count > 0) row.AddRange([s.Median, s.P75, s.P95]);
                else row.AddRange([null, null, null]);
            }
            csv.Row(row);
        }
        return csv.ToString();
    }

    internal static string Iterations(RunResult run)
    {
        var csv = new Csv();
        csv.Row("page", "index", "warmup", "cold_cache", "success", "error", "http_status", "final_url", "wall_clock_ms",
            "redirect_ms", "dns_ms", "connect_ms", "tls_ms", "server_ms", "ttfb_ms", "download_ms", "dom_interactive_ms",
            "dom_content_loaded_ms", "load_ms", "document_transfer_bytes",
            "fcp_ms", "lcp_ms", "cls", "tbt_ms", "long_tasks", "lcp_element",
            "requests", "transfer_bytes", "decoded_bytes", "failed_requests", "third_party_requests", "cached_requests",
            "dom_nodes", "js_heap_used_bytes", "console_errors", "page_errors", "api_calls");
        foreach (var p in run.Pages)
            foreach (var i in p.Iterations)
            {
                var t = i.Timing; var v = i.Vitals; var r = i.Resources;
                csv.Row(p.Name, i.Index, i.IsWarmup, i.ColdCache, i.Success, i.Error, i.HttpStatus, i.FinalUrl, i.WallClockMs,
                    t.RedirectMs, t.DnsMs, t.ConnectMs, t.TlsMs, t.ServerMs, t.TtfbMs, t.DownloadMs, t.DomInteractiveMs,
                    t.DomContentLoadedMs, t.LoadEventMs, t.DocumentTransferBytes,
                    v.FcpMs, v.LcpMs, v.Cls, v.TbtMs, v.LongTaskCount, v.LcpElement,
                    r.TotalRequests, r.TransferBytes, r.DecodedBytes, r.FailedRequests, r.ThirdPartyRequests, r.CachedRequests,
                    i.DomNodes, i.JsHeapUsedBytes, i.ConsoleErrors.Count, i.PageErrors.Count, i.ApiCalls.Count);
            }
        return csv.ToString();
    }

    internal static string Api(RunResult run)
    {
        var csv = new Csv();
        csv.Row("name", "method", "url", "source", "profile", "total_requests", "successful_requests", "failed_requests",
            "error_rate_pct", "rps", "duration_s", "bytes_received", "latency_min_ms", "latency_mean_ms", "latency_p50_ms",
            "latency_p75_ms", "latency_p90_ms", "latency_p95_ms", "latency_p99_ms", "latency_max_ms", "latency_stddev_ms",
            "status_codes", "error_types", "error");
        foreach (var e in run.Api?.Endpoints ?? [])
        {
            var l = e.Latency;
            csv.Row(e.Name, e.Method, e.Url, e.Source.ToString(), e.ProfileDescription, e.TotalRequests, e.SuccessfulRequests,
                e.FailedRequests, e.ErrorRatePct, e.Rps, e.DurationSeconds, e.BytesReceived, l.Min, l.Mean, l.Median, l.P75,
                l.P90, l.P95, l.P99, l.Max, l.StdDev, Join(e.StatusCodes), Join(e.ErrorTypes), e.Error);
        }
        return csv.ToString();
    }

    internal static string Findings(RunResult run)
    {
        var csv = new Csv();
        csv.Row("severity", "category", "rule_id", "target", "title", "detail", "recommendation", "metric", "value");
        foreach (var f in (run.Analysis?.Findings ?? []).OrderByDescending(f => f.Severity))
            csv.Row(f.Severity.ToString(), f.Category.ToString(), f.RuleId, f.Target, f.Title, f.Detail, f.Recommendation, f.Metric, f.Value);
        return csv.ToString();
    }

    internal static string Thresholds(RunResult run)
    {
        var csv = new Csv();
        csv.Row("scope", "target", "metric", "statistic", "actual", "limit", "passed");
        foreach (var t in (run.Analysis?.Thresholds ?? []).OrderBy(t => t.Passed))
            csv.Row(t.Scope, t.Target, t.Metric, t.Statistic, t.Actual, t.Limit, t.Passed);
        return csv.ToString();
    }

    private static string Join(Dictionary<string, long> d) =>
        string.Join(";", d.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}:{kv.Value}"));
}
