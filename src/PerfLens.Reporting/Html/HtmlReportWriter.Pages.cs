using System.Text;
using PerfLens.Core.Results;
using PerfLens.Reporting.Internal;

namespace PerfLens.Reporting;

public sealed partial class HtmlReportWriter
{
    private const int MaxTopResources = 25;
    private const int MaxErrorsListed = 50;

    private static void AppendPages(StringBuilder sb, RenderContext ctx)
    {
        SectionStart(sb, "pages", "Pages", "One card per page, worst score first. Click a card to expand it.");
        if (ctx.Pages.Count == 0)
        {
            Empty(sb, "No pages were measured.");
            SectionEnd(sb);
            return;
        }

        sb.Append("<div class=\"toolbar\"><span class=\"muted\">Sort:</span>")
          .Append("<button type=\"button\" class=\"btn active\" data-sort-pages=\"score\">Worst score first</button>")
          .Append("<button type=\"button\" class=\"btn\" data-sort-pages=\"name\">Name</button>")
          .Append("<span class=\"spacer\"></span>")
          .Append("<button type=\"button\" class=\"btn\" data-expand=\"open\">Expand all</button>")
          .Append("<button type=\"button\" class=\"btn\" data-expand=\"close\">Collapse all</button></div>\n");

        sb.Append("<div id=\"page-cards\">\n");
        foreach (var p in ctx.Pages.OrderBy(p => p.Score ?? -1).ThenBy(p => p.Page.Name, StringComparer.OrdinalIgnoreCase))
            AppendPageCard(sb, ctx, p);
        sb.Append("</div>\n");
        SectionEnd(sb);
    }

    private static void AppendPageCard(StringBuilder sb, RenderContext ctx, PageView pv)
    {
        var page = pv.Page;
        var score = pv.Score;
        sb.Append("<details class=\"page-card\" id=\"").Append(pv.Id).Append("\" data-idx=\"").Append(pv.Index)
          .Append("\" data-score=\"").Append(Fmt.Num(score ?? -1, "0.###")).Append("\" data-name=\"").Append(H(page.Name.ToLowerInvariant())).Append("\">\n");

        // Summary line
        sb.Append("<summary><span class=\"grade big ").Append(GradeClass(pv.Grade)).Append("\" title=\"Score ").Append(Fmt.Score(score)).Append("\">")
          .Append(Fmt.Score(score)).Append("</span><span class=\"pc-title\"><strong>").Append(H(page.Name)).Append("</strong><span class=\"pc-url\">")
          .Append(H(page.Url)).Append("</span></span><span class=\"pc-chips\">");
        if (page.Error != null) sb.Append("<span class=\"chip bad\">Error</span>");
        foreach (var m in new[] { MetricNames.Lcp, MetricNames.Cls, MetricNames.Tbt })
        {
            var v = pv.Value(m, ctx.Statistic);
            if (v == null) continue;
            var r = ReportFacts.Rating(ctx.Run, page, m, v);
            sb.Append("<span class=\"chip rate ").Append(ReportFacts.RatingClass(r)).Append("\">").Append(m == MetricNames.Cls ? "CLS" : m == MetricNames.Lcp ? "LCP" : "TBT")
              .Append(' ').Append(Fmt.Metric(m, v)).Append("</span>");
        }
        sb.Append("</span></summary>\n<div class=\"pc-body\">\n");

        // Facts row
        sb.Append("<div class=\"pc-facts\"><dl class=\"meta\">");
        Meta(sb, "URL", Link(page.Url));
        Meta(sb, "Source", page.Source == PageSource.Crawled ? $"Crawled (depth {page.Depth})" : "Configured");
        Meta(sb, "Score", $"{Fmt.Score(score)} <span class=\"grade {GradeClass(pv.Grade)}\">{H(pv.Grade)}</span>");
        Meta(sb, "Iterations", $"{ReportFacts.MeasuredCount(page)} measured · {page.Iterations.Count(i => i.IsWarmup)} warm-up · " +
                               $"{page.Summary?.FailedIterations ?? page.Iterations.Count(i => !i.Success)} failed");
        var rep = ReportFacts.RepresentativeIteration(page);
        if (rep?.HttpStatus is { } status) Meta(sb, "HTTP status", status.ToString());
        if (!string.IsNullOrEmpty(rep?.Vitals.LcpElement)) Meta(sb, "LCP element", "<code>" + H(rep.Vitals.LcpElement) + "</code>");
        sb.Append("</dl></div>\n");

        if (page.Error != null)
            sb.Append("<div class=\"alert bad\"><strong>Page could not be measured:</strong> ").Append(H(page.Error)).Append("</div>\n");

        if (ctx.Screenshots.TryGetValue(page, out var src))
            sb.Append("<figure class=\"screenshot\"><a href=\"").Append(H(src)).Append("\" target=\"_blank\" rel=\"noopener\"><img loading=\"lazy\" src=\"")
              .Append(H(src)).Append("\" alt=\"Screenshot of ").Append(H(page.Name)).Append("\"></a><figcaption>Screenshot</figcaption></figure>\n");

        if (pv.Metrics.Count > 0)
        {
            AppendMetricsTable(sb, ctx, pv);
            sb.Append("<div class=\"chart-grid\">");
            ChartBox(sb, "pc-iter-" + pv.Index, "Timings across iterations",
                "TTFB, FCP, LCP, DOMContentLoaded and load per iteration. Warm-up iterations are drawn as grey hollow points.", 300, "wide");
            ChartBox(sb, "pc-types-" + pv.Index, "Transfer by resource type", "Average transferred bytes per type.", 300);
            sb.Append("</div>\n");
        }
        else if (page.Error == null)
            Empty(sb, "No measured iterations for this page.");

        AppendIterationsTable(sb, page);
        if (rep != null) AppendResourcesTable(sb, rep);
        AppendApiCallsTable(sb, page);
        AppendErrorsList(sb, page);

        var findings = ReportFacts.FindingsFor(ctx.Run, page);
        if (findings.Count > 0)
        {
            sb.Append("<h4>Findings for this page (").Append(findings.Count).Append(")</h4><ul class=\"finding-list\">");
            foreach (var f in findings)
                sb.Append("<li><span class=\"sev ").Append(SeverityClass(f.Severity)).Append("\">").Append(f.Severity).Append("</span> <strong>")
                  .Append(H(f.Title)).Append("</strong> — ").Append(H(f.Detail))
                  .Append(string.IsNullOrWhiteSpace(f.Recommendation) ? "" : "<div class=\"rec\">💡 " + H(f.Recommendation) + "</div>").Append("</li>");
            sb.Append("</ul>\n");
        }
        sb.Append("</div>\n</details>\n");
    }

    private static void AppendMetricsTable(StringBuilder sb, RenderContext ctx, PageView pv)
    {
        sb.Append("<h4>Metrics</h4><div class=\"table-wrap\"><table class=\"data num\"><thead><tr><th>Metric</th><th>Min</th><th>Median</th><th>p75</th><th>p95</th><th>Max</th><th>Std dev</th><th>CoV</th><th>n</th></tr></thead><tbody>");
        var keys = ReportFacts.MetricOrder.Where(pv.Metrics.ContainsKey).Concat(pv.Metrics.Keys.Where(k => !ReportFacts.MetricOrder.Contains(k)));
        foreach (var key in keys)
        {
            var s = pv.Metrics[key];
            if (s.Count == 0) continue;
            var unit = ReportFacts.Unit(key);
            bool vital = ReportFacts.VitalBands.ContainsKey(key);
            string Cell(double v)
            {
                var cls = vital ? " class=\"rate-cell " + ReportFacts.RatingClass(ReportFacts.Rate(key, v)) + "\"" : "";
                return "<td" + cls + ">" + Fmt.ByUnit(v, unit) + "</td>";
            }
            sb.Append("<tr><th>").Append(H(ReportFacts.Label(key))).Append(unit is "" or "count" ? "" : " <span class=\"unit\">(" + unit + ")</span>").Append("</th>")
              .Append(Cell(s.Min)).Append(Cell(s.Median)).Append(Cell(s.P75)).Append(Cell(s.P95)).Append(Cell(s.Max))
              .Append("<td>").Append(Fmt.ByUnit(s.StdDev, unit)).Append("</td><td").Append(s.CoV > 0.25 ? " class=\"warn-text\" title=\"High variance\"" : "")
              .Append('>').Append(Fmt.Pct(s.CoV * 100)).Append("</td><td>").Append(s.Count).Append("</td></tr>");
        }
        sb.Append("</tbody></table></div>\n");
    }

    private static void AppendIterationsTable(StringBuilder sb, PageResult page)
    {
        if (page.Iterations.Count == 0) return;
        sb.Append("<details class=\"sub\"><summary>Iterations (").Append(page.Iterations.Count).Append(")</summary><div class=\"table-wrap\"><table class=\"data num\"><thead><tr>")
          .Append("<th>#</th><th>Kind</th><th>Status</th><th>TTFB</th><th>FCP</th><th>LCP</th><th>CLS</th><th>TBT</th><th>DCL</th><th>Load</th><th>Requests</th><th>Transfer</th><th>Error</th></tr></thead><tbody>");
        foreach (var i in page.Iterations.OrderBy(i => i.Index))
        {
            sb.Append("<tr").Append(i.IsWarmup ? " class=\"warmup\"" : !i.Success ? " class=\"failed\"" : "").Append("><td>").Append(i.Index).Append("</td><td>")
              .Append(i.IsWarmup ? "warm-up" : "measured").Append(i.ColdCache ? " · cold" : "").Append("</td><td>")
              .Append(i.HttpStatus?.ToString() ?? "n/a").Append("</td><td>").Append(Fmt.Ms(i.Timing.TtfbMs)).Append("</td><td>").Append(Fmt.Ms(i.Vitals.FcpMs))
              .Append("</td><td>").Append(Fmt.Ms(i.Vitals.LcpMs)).Append("</td><td>").Append(Fmt.Cls(i.Vitals.Cls)).Append("</td><td>").Append(Fmt.Ms(i.Vitals.TbtMs))
              .Append("</td><td>").Append(Fmt.Ms(i.Timing.DomContentLoadedMs)).Append("</td><td>").Append(Fmt.Ms(i.Timing.LoadEventMs))
              .Append("</td><td>").Append(i.Resources.TotalRequests).Append("</td><td>").Append(Fmt.Bytes(i.Resources.TransferBytes))
              .Append("</td><td class=\"text\">").Append(H(i.Error)).Append("</td></tr>");
        }
        sb.Append("</tbody></table></div></details>\n");
    }

    private static void AppendResourcesTable(StringBuilder sb, PageIteration rep)
    {
        if (rep.TopResources.Count == 0) return;
        var top = rep.TopResources.OrderByDescending(r => r.TransferBytes).ThenByDescending(r => r.DurationMs).Take(MaxTopResources).ToList();
        sb.Append("<h4>Top resources <span class=\"muted small\">(iteration ").Append(rep.Index).Append(", largest ").Append(top.Count).Append(")</span></h4>")
          .Append("<div class=\"table-wrap\"><table class=\"data\"><thead><tr><th>URL</th><th>Type</th><th>Status</th><th class=\"n\">Size</th><th class=\"n\">Duration</th><th>Cached</th><th>Encoding</th><th>Cache-Control</th></tr></thead><tbody>");
        foreach (var r in top)
        {
            bool bad = r.Status is >= 400 || r.Status == 0;
            sb.Append("<tr").Append(bad ? " class=\"failed\"" : "").Append("><td class=\"url\">").Append(Link(r.Url, 70))
              .Append(r.ThirdParty ? " <span class=\"chip small\">3rd party</span>" : "").Append("</td><td>").Append(H(r.Type)).Append("</td><td>")
              .Append(r.Status?.ToString() ?? "n/a").Append("</td><td class=\"n\">").Append(Fmt.Kb(r.TransferBytes / 1024.0)).Append("</td><td class=\"n\">")
              .Append(Fmt.Ms(r.DurationMs)).Append("</td><td>").Append(r.FromCache ? "✔" : "").Append("</td><td>").Append(HOrNa(r.ContentEncoding))
              .Append("</td><td class=\"text\">").Append(HOrNa(r.CacheControl)).Append("</td></tr>");
        }
        sb.Append("</tbody></table></div>\n");
    }

    private static void AppendApiCallsTable(StringBuilder sb, PageResult page)
    {
        var calls = page.Summary?.ApiCalls is { Count: > 0 } summary
            ? summary
            : page.MeasuredIterations.SelectMany(i => i.ApiCalls)
                .GroupBy(c => (c.Method.ToUpperInvariant(), string.IsNullOrEmpty(c.NormalizedUrl) ? c.Url : c.NormalizedUrl))
                .Select(g => new ApiCallSummary
                {
                    Method = g.Key.Item1, NormalizedUrl = g.Key.Item2, SampleUrl = g.First().Url, Calls = g.Count(),
                    Failures = g.Count(c => c.Failed), Duration = Core.Statistics.Stats.Compute(g.Select(c => c.DurationMs)),
                    AvgBytes = g.Average(c => (double)c.Bytes),
                }).ToList();
        if (calls.Count == 0) return;
        sb.Append("<h4>XHR / fetch API calls</h4><div class=\"table-wrap\"><table class=\"data\"><thead><tr><th>Method</th><th>URL</th><th class=\"n\">Calls</th><th class=\"n\">Failures</th><th class=\"n\">p50</th><th class=\"n\">p95</th><th class=\"n\">Max</th><th class=\"n\">Avg size</th></tr></thead><tbody>");
        foreach (var c in calls.OrderByDescending(c => c.Duration.P95))
        {
            sb.Append("<tr").Append(c.Failures > 0 ? " class=\"failed\"" : "").Append("><td><span class=\"method\">").Append(H(c.Method)).Append("</span></td><td class=\"url\" title=\"")
              .Append(H(c.SampleUrl)).Append("\">").Append(H(Truncate(c.NormalizedUrl, 90))).Append("</td><td class=\"n\">").Append(c.Calls).Append("</td><td class=\"n\">")
              .Append(c.Failures).Append("</td><td class=\"n\">").Append(Fmt.Ms(c.Duration.Median)).Append("</td><td class=\"n\">").Append(Fmt.Ms(c.Duration.P95))
              .Append("</td><td class=\"n\">").Append(Fmt.Ms(c.Duration.Max)).Append("</td><td class=\"n\">").Append(Fmt.Bytes(c.AvgBytes)).Append("</td></tr>");
        }
        sb.Append("</tbody></table></div>\n");
    }

    private static void AppendErrorsList(StringBuilder sb, PageResult page)
    {
        var errors = page.Iterations.SelectMany(i => i.PageErrors.Select(e => ("Page error", e)))
            .Concat(page.Iterations.SelectMany(i => i.ConsoleErrors.Select(e => ("Console", e))))
            .GroupBy(x => x).Select(g => (Kind: g.Key.Item1, Message: g.Key.Item2, Count: g.Count())).OrderByDescending(x => x.Count).ToList();
        if (errors.Count == 0) return;
        sb.Append("<h4>Console &amp; page errors (").Append(errors.Count).Append(" distinct)</h4><ul class=\"error-list\">");
        foreach (var e in errors.Take(MaxErrorsListed))
            sb.Append("<li><span class=\"chip ").Append(e.Kind == "Page error" ? "bad" : "warn").Append("\">").Append(e.Kind).Append("</span> <code>")
              .Append(H(Truncate(e.Message, 600))).Append("</code>").Append(e.Count > 1 ? $" <span class=\"muted\">×{e.Count}</span>" : "").Append("</li>");
        if (errors.Count > MaxErrorsListed) sb.Append("<li class=\"muted\">… ").Append(errors.Count - MaxErrorsListed).Append(" more</li>");
        sb.Append("</ul>\n");
    }
}
