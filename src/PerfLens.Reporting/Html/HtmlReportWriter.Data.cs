using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using PerfLens.Core.Results;
using PerfLens.Reporting.Internal;

namespace PerfLens.Reporting;

public sealed partial class HtmlReportWriter
{
    private static readonly JsonSerializerOptions DataJsonOptions = new()
    {
        // The default encoder escapes <, >, &, ' and non-ASCII, so the blob cannot close its <script> element.
        Encoder = JavaScriptEncoder.Default,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    /// <summary>JSON consumed by report.js to draw the charts. Only what the charts need; tables are server-rendered.</summary>
    internal static string BuildDataJson(RenderContext ctx)
    {
        var run = ctx.Run;
        var stat = ctx.Statistic;
        var data = new
        {
            statistic = ctx.StatLabel,
            score = ctx.Analysis is { } a ? N(a.Score) : null,
            grade = ctx.Analysis?.Grade,
            bands = ReportFacts.VitalBands.ToDictionary(kv => kv.Key, kv => new[] { kv.Value.Good, kv.Value.Poor }),
            pages = ctx.Pages.Select(p => PageData(ctx, p, stat)).ToList(),
            api = (run.Api?.Endpoints ?? []).Select((e, i) => ApiData(e, i)).ToList(),
            regressions = (ctx.Analysis?.Regressions ?? [])
                .OrderByDescending(r => r.Regressed).ThenByDescending(r => Math.Abs(r.DeltaPct))
                .Select(r => new
                {
                    label = $"{r.Target} · {ReportFacts.Label(r.Metric)}",
                    delta = N(r.DeltaPct),
                    state = r.Regressed ? "regressed" : r.Improved ? "improved" : "same",
                }).ToList(),
        };
        var json = JsonSerializer.Serialize(data, DataJsonOptions);
        return json.Replace("<", "\\u003c").Replace(">", "\\u003e");
    }

    private static object PageData(RenderContext ctx, PageView p, string stat)
    {
        var m = p.Metrics;
        double? Med(string k) => ReportFacts.Median(m, k);
        double? S(string k) => N(p.Value(k, stat));

        object? phases = null;
        if (m.Count > 0)
        {
            double ttfb = Med(MetricNames.Ttfb) ?? 0, download = Med(MetricNames.Download) ?? 0;
            double dcl = Med(MetricNames.DomContentLoaded) ?? 0, load = Med(MetricNames.Load) ?? 0;
            double dns = Med(MetricNames.Dns) ?? 0, connect = Med(MetricNames.Connect) ?? 0, tls = Med(MetricNames.Tls) ?? 0, server = Med(MetricNames.Server) ?? 0;
            phases = new
            {
                dns = N(dns), connect = N(connect), tls = N(tls), server = N(server),
                other = N(Math.Max(0, ttfb - dns - connect - tls - server)),
                download = N(download),
                dom = N(Math.Max(0, dcl - ttfb - download)),
                loadRest = N(Math.Max(0, load - dcl)),
            };
        }

        var byType = ReportFacts.AvgBytesByType(p.Page);
        return new
        {
            id = p.Id,
            idx = p.Index,
            name = p.Page.Name,
            score = N(p.Score),
            grade = p.Grade,
            measured = m.Count > 0,
            ttfb = S(MetricNames.Ttfb), fcp = S(MetricNames.Fcp), lcp = S(MetricNames.Lcp),
            cls = S(MetricNames.Cls), tbt = S(MetricNames.Tbt), requests = S(MetricNames.Requests),
            phases,
            byType = ReportFacts.OrderedResourceTypes(byType.Keys).ToDictionary(t => t, t => N(byType[t])),
            iterations = p.Page.Iterations.OrderBy(i => i.Index).Select(i => new
            {
                index = i.Index,
                warmup = i.IsWarmup,
                ok = i.Success,
                ttfb = i.Success ? N(i.Timing.TtfbMs) : null,
                fcp = i.Success ? N(i.Vitals.FcpMs) : null,
                lcp = i.Success ? N(i.Vitals.LcpMs) : null,
                dcl = i.Success ? N(i.Timing.DomContentLoadedMs) : null,
                load = i.Success ? N(i.Timing.LoadEventMs) : null,
            }).ToList(),
        };
    }

    private static object ApiData(EndpointLoadResult e, int i)
    {
        var l = e.Latency;
        var status = e.StatusCodes.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value);
        long statusTotal = status.Values.Sum();
        if (e.ErrorTypes.Count > 0 && statusTotal < e.TotalRequests)
            foreach (var (type, count) in e.ErrorTypes.Where(kv => !status.ContainsKey(kv.Key)))
                status[type] = count;
        return new
        {
            id = "api-" + i,
            name = e.Name,
            hasLatency = l.Count > 0,
            latency = new[] { N(l.Min), N(l.Median), N(l.P75), N(l.P90), N(l.P95), N(l.P99), N(l.Max) },
            timeline = e.Timeline.OrderBy(t => t.Second).Select(t => new
            {
                s = t.Second, req = t.Requests, err = t.Errors, p50 = N(t.P50Ms), p95 = N(t.P95Ms),
            }).ToList(),
            histogram = e.Histogram.Select(h => new { lo = N(h.LowerMs), hi = N(h.UpperMs), n = h.Count }).ToList(),
            status,
        };
    }

    /// <summary>JSON-safe number: NaN/Infinity become null, values rounded to keep the blob small.</summary>
    private static double? N(double? v) =>
        v is { } x && !double.IsNaN(x) && !double.IsInfinity(x) ? Math.Round(x, Math.Abs(x) < 10 ? 4 : 1) : null;
}
