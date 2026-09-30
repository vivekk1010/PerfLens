using System.Text;
using PerfLens.Core.Results;
using PerfLens.Reporting.Internal;

namespace PerfLens.Reporting;

public sealed partial class HtmlReportWriter
{
    private static void AppendHeader(StringBuilder sb, RenderContext ctx)
    {
        var run = ctx.Run;
        var a = ctx.Analysis;
        var env = run.Environment;
        var title = string.IsNullOrWhiteSpace(run.Name) ? run.BaseUrl : run.Name;

        sb.Append("<section id=\"summary\" class=\"section hero\">\n<div class=\"hero-main\">\n<div class=\"hero-text\">")
          .Append("<p class=\"eyebrow\">Performance report</p><h1>").Append(H(title)).Append("</h1>\n<dl class=\"meta\">");
        Meta(sb, "Base URL", Link(run.BaseUrl));
        if (!string.IsNullOrWhiteSpace(run.Label)) Meta(sb, "Label", "<span class=\"chip\">" + H(run.Label) + "</span>");
        Meta(sb, "Started", H(Fmt.Date(run.StartedAt)));
        Meta(sb, "Duration", H(Fmt.Duration(run.FinishedAt - run.StartedAt)));
        Meta(sb, "PerfLens", H(ctx.ToolVersion));
        var browser = string.Join(" ", new[] { env.BrowserName, env.BrowserVersion }.Where(s => !string.IsNullOrWhiteSpace(s)));
        Meta(sb, "Browser", HOrNa(browser));
        Meta(sb, "Run id", "<code>" + HOrNa(run.RunId) + "</code>");
        sb.Append("</dl>\n</div>\n");

        sb.Append("<div class=\"gauge-card\">");
        if (a != null)
        {
            var grade = string.IsNullOrWhiteSpace(a.Grade) ? ReportFacts.Grade(a.Score) : a.Grade;
            sb.Append("<div class=\"gauge\"><canvas id=\"chart-score-gauge\" aria-label=\"Overall score gauge\" role=\"img\"></canvas>")
              .Append("<div class=\"gauge-label\"><span class=\"gauge-score\">").Append(Fmt.Score(a.Score)).Append("</span>")
              .Append("<span class=\"gauge-of\">/ 100</span></div></div>")
              .Append("<div class=\"gauge-foot\"><span class=\"grade ").Append(GradeClass(grade)).Append("\">Grade ").Append(H(grade)).Append("</span>")
              .Append(a.Passed ? "<span class=\"badge pass\">PASS</span>" : "<span class=\"badge fail\">FAIL</span>").Append("</div>");
        }
        else
        {
            sb.Append("<div class=\"gauge gauge-empty\"><div class=\"gauge-label\"><span class=\"gauge-score\">n/a</span></div></div>")
              .Append("<div class=\"gauge-foot\"><span class=\"badge neutral\">Not analyzed</span></div>");
        }
        sb.Append("</div>\n</div>\n");

        AppendCountCards(sb, ctx);

        if (a?.Highlights.Count > 0)
        {
            sb.Append("<div class=\"card highlights\"><h3>Highlights</h3><ul>");
            foreach (var h in a.Highlights) sb.Append("<li>").Append(H(h)).Append("</li>");
            sb.Append("</ul></div>\n");
        }
        sb.Append("</section>\n");
    }

    private static void Meta(StringBuilder sb, string term, string htmlValue) =>
        sb.Append("<div><dt>").Append(term).Append("</dt><dd>").Append(htmlValue).Append("</dd></div>");

    private static void AppendCountCards(StringBuilder sb, RenderContext ctx)
    {
        var run = ctx.Run;
        var a = ctx.Analysis;
        int measuredPages = ctx.Pages.Count(p => ReportFacts.MeasuredCount(p.Page) > 0);
        int iterations = run.Pages.Sum(p => p.Iterations.Count);
        int measuredIterations = run.Pages.Sum(p => p.MeasuredIterations.Count());
        long requests = run.Pages.Sum(p => p.MeasuredIterations.Sum(i => (long)i.Resources.TotalRequests));

        sb.Append("<div class=\"stat-grid\">");
        Stat(sb, "Pages measured", $"{measuredPages}<small>/{run.Pages.Count}</small>", run.Pages.Count(p => p.Error != null) is var e and > 0 ? $"{e} failed" : null, measuredPages < run.Pages.Count ? "warn" : null);
        Stat(sb, "Iterations", measuredIterations.ToString(), $"{iterations - measuredIterations} warm-up/failed");
        Stat(sb, "Requests", Fmt.Int(requests), "across measured iterations");
        if (a != null)
        {
            int tp = a.Thresholds.Count(t => t.Passed);
            Stat(sb, "Thresholds passed", $"{tp}<small>/{a.Thresholds.Count}</small>", null, tp < a.Thresholds.Count ? "bad" : "ok");
            sb.Append("<div class=\"stat\"><div class=\"stat-label\">Findings</div><div class=\"stat-value\">").Append(a.Findings.Count)
              .Append("</div><div class=\"sev-row\">");
            foreach (var sev in new[] { Severity.Critical, Severity.High, Severity.Medium, Severity.Low, Severity.Info })
            {
                int n = a.Findings.Count(f => f.Severity == sev);
                sb.Append("<span class=\"sev ").Append(SeverityClass(sev)).Append(n == 0 ? " zero" : "").Append("\" title=\"").Append(sev)
                  .Append("\">").Append(sev.ToString()[0]).Append(' ').Append(n).Append("</span>");
            }
            sb.Append("</div></div>");
        }
        if (run.Crawl != null)
            Stat(sb, "Broken links", run.Crawl.BrokenLinks.Count.ToString(), $"{run.Crawl.Visited.Count} visited", run.Crawl.BrokenLinks.Count > 0 ? "bad" : "ok");
        if (run.Api != null)
            Stat(sb, "API endpoints", run.Api.Endpoints.Count.ToString(), $"{Fmt.Int(run.Api.Endpoints.Sum(x => x.TotalRequests))} requests",
                run.Api.Endpoints.Any(x => x.Error != null || x.ErrorRatePct > 0) ? "warn" : null);
        if (run.Warnings.Count > 0) Stat(sb, "Warnings", run.Warnings.Count.ToString(), null, "warn");
        sb.Append("</div>\n");
    }

    private static void Stat(StringBuilder sb, string label, string valueHtml, string? sub, string? tone = null)
    {
        sb.Append("<div class=\"stat").Append(tone != null ? " " + tone : "").Append("\"><div class=\"stat-label\">").Append(label)
          .Append("</div><div class=\"stat-value\">").Append(valueHtml).Append("</div>");
        if (sub != null) sb.Append("<div class=\"stat-sub\">").Append(H(sub)).Append("</div>");
        sb.Append("</div>");
    }

    private static void AppendSettings(StringBuilder sb, RenderContext ctx)
    {
        var run = ctx.Run;
        var env = run.Environment;
        SectionStart(sb, "settings", "Settings &amp; environment");
        sb.Append("<div class=\"grid-2\">");

        sb.Append("<div class=\"card\"><h3>Run settings</h3>");
        if (run.Settings.Count == 0) Empty(sb, "No settings recorded.");
        else
        {
            sb.Append("<table class=\"kv\"><tbody>");
            foreach (var (k, v) in run.Settings)
                sb.Append("<tr><th>").Append(H(k)).Append("</th><td>").Append(H(v)).Append("</td></tr>");
            sb.Append("</tbody></table>");
        }
        sb.Append("</div>");

        sb.Append("<div class=\"card\"><h3>Environment</h3><table class=\"kv\"><tbody>");
        void Row(string k, string? v) => sb.Append("<tr><th>").Append(k).Append("</th><td>").Append(HOrNa(v)).Append("</td></tr>");
        Row("PerfLens version", ctx.ToolVersion);
        Row("Machine", env.MachineName);
        Row("Operating system", env.OperatingSystem);
        Row(".NET", env.DotnetVersion);
        Row("Processors", env.ProcessorCount > 0 ? env.ProcessorCount.ToString() : null);
        Row("Browser", env.BrowserName);
        Row("Browser version", env.BrowserVersion);
        Row("Started", Fmt.Date(run.StartedAt));
        Row("Finished", Fmt.Date(run.FinishedAt));
        Row("Page statistic", ctx.StatLabel);
        if (ctx.Analysis?.BaselineRunId != null) Row("Baseline run", ctx.Analysis.BaselineRunId);
        sb.Append("</tbody></table></div>");

        sb.Append("</div>\n");
        SectionEnd(sb);
    }
}
