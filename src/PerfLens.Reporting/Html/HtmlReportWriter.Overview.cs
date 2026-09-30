using System.Text;
using PerfLens.Core.Results;
using PerfLens.Reporting.Internal;

namespace PerfLens.Reporting;

public sealed partial class HtmlReportWriter
{
    private static void AppendOverview(StringBuilder sb, RenderContext ctx)
    {
        var stat = H(ctx.StatLabel);
        SectionStart(sb, "overview", "Overview",
            $"Page charts use the <strong>{stat}</strong> of measured iterations (warm-up and failed iterations excluded) unless noted otherwise.");

        var measured = ctx.Pages.Where(p => p.Metrics.Count > 0).ToList();
        if (measured.Count == 0)
        {
            Empty(sb, "No page produced measured iterations, so there is nothing to chart.");
            SectionEnd(sb);
            return;
        }

        int barHeight = Math.Clamp(80 + measured.Count * 34, 240, 1400);
        sb.Append("<div class=\"chart-grid\">\n");
        ChartBox(sb, "chart-vitals", "Loading milestones per page",
            $"TTFB, FCP and LCP ({stat}). The short green and red marks on each bar are the Good and Poor limits for that metric; lower is better.",
            360, "wide");
        ChartBox(sb, "chart-phases", "Navigation phases",
            "Where the time goes, using medians: DNS, connect, TLS and server time lead to the first byte, then the document download, " +
            "DOM processing (DOMContentLoaded − TTFB − download) and the remainder until the load event.", barHeight, "wide");
        ChartBox(sb, "chart-weight", "Page weight by resource type",
            "Average transferred bytes per resource type across measured iterations. Compressed, cached responses count as their transfer size.", 340);
        ChartBox(sb, "chart-requests", "Requests per page", $"Number of network requests ({stat}).", 340);
        ChartBox(sb, "chart-cls", "Cumulative Layout Shift", $"Visual stability ({stat}); Good ≤ 0.1, Poor > 0.25.", 300);
        ChartBox(sb, "chart-tbt", "Total Blocking Time", $"Main-thread blocking after FCP ({stat}); Good ≤ 200 ms, Poor > 600 ms.", 300);
        ChartBox(sb, "chart-scores", "Score per page",
            "0–100 page score computed by the analysis, coloured by grade (A ≥ 90, B ≥ 75, C ≥ 60, D ≥ 40, F below).", 300, "wide");
        sb.Append("</div>\n");

        AppendVitalsMatrix(sb, ctx);
        SectionEnd(sb);
    }

    private static void AppendVitalsMatrix(StringBuilder sb, RenderContext ctx)
    {
        sb.Append("<div class=\"card\"><h3>Web Vitals rating matrix</h3><p class=\"muted small\">")
          .Append(H(ctx.StatLabel)).Append(" per page, rated against Core Web Vitals bands. ")
          .Append("<span class=\"rate good\">Good</span> <span class=\"rate ni\">Needs improvement</span> <span class=\"rate poor\">Poor</span></p>")
          .Append("<div class=\"table-wrap\"><table class=\"data matrix\"><thead><tr><th>Page</th>");
        foreach (var m in ReportFacts.VitalMetrics)
            sb.Append("<th>").Append(H(ReportFacts.Label(m))).Append("</th>");
        sb.Append("<th>Score</th></tr></thead><tbody>");
        foreach (var p in ctx.Pages.OrderBy(p => p.Score ?? -1))
        {
            sb.Append("<tr><td><a href=\"#").Append(p.Id).Append("\">").Append(H(p.Page.Name)).Append("</a></td>");
            foreach (var m in ReportFacts.VitalMetrics)
            {
                var v = p.Value(m, ctx.Statistic);
                var r = ReportFacts.Rating(ctx.Run, p.Page, m, v);
                sb.Append("<td class=\"rate-cell ").Append(ReportFacts.RatingClass(r)).Append("\" title=\"").Append(ReportFacts.RatingText(r)).Append("\">")
                  .Append(Fmt.Metric(m, v)).Append("</td>");
            }
            sb.Append("<td><span class=\"grade ").Append(GradeClass(p.Grade)).Append("\">").Append(Fmt.Score(p.Score)).Append("</span></td></tr>");
        }
        sb.Append("</tbody></table></div></div>\n");
    }
}
