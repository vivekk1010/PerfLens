using System.Text;
using PerfLens.Core.Results;
using PerfLens.Reporting.Internal;

namespace PerfLens.Reporting;

public sealed partial class HtmlReportWriter
{
    private static void AppendThresholds(StringBuilder sb, RenderContext ctx)
    {
        SectionStart(sb, "thresholds", "Thresholds", "Configured limits evaluated against this run. Failed checks are listed first.");
        var thresholds = ctx.Analysis?.Thresholds;
        if (thresholds is null) { Empty(sb, "Analysis was not run, so no thresholds were evaluated."); SectionEnd(sb); return; }
        if (thresholds.Count == 0) { Empty(sb, "No thresholds configured."); SectionEnd(sb); return; }

        int failed = thresholds.Count(t => !t.Passed);
        sb.Append("<div class=\"toolbar\" data-filter-group=\"thresholds\">")
          .Append("<button type=\"button\" class=\"btn active\" data-th-filter=\"all\">All (").Append(thresholds.Count).Append(")</button>")
          .Append("<button type=\"button\" class=\"btn\" data-th-filter=\"failed\">Failed (").Append(failed).Append(")</button></div>");
        sb.Append("<div class=\"card\"><div class=\"table-wrap\"><table class=\"data\" id=\"threshold-table\"><thead><tr><th>Result</th><th>Scope</th><th>Target</th>")
          .Append("<th>Metric</th><th>Statistic</th><th class=\"n\">Actual</th><th class=\"n\">Limit</th></tr></thead><tbody>");
        foreach (var t in thresholds.OrderBy(t => t.Passed).ThenBy(t => t.Scope).ThenBy(t => t.Target))
        {
            sb.Append("<tr data-passed=\"").Append(t.Passed ? "true" : "false").Append("\"").Append(t.Passed ? "" : " class=\"failed\"").Append("><td>")
              .Append(t.Passed ? "<span class=\"badge pass sm\">PASS</span>" : "<span class=\"badge fail sm\">FAIL</span>").Append("</td><td>")
              .Append(H(t.Scope)).Append("</td><td>").Append(H(t.Target)).Append("</td><td>").Append(H(ReportFacts.Label(t.Metric)))
              .Append(ReportFacts.Label(t.Metric) == t.Metric ? "" : " <span class=\"muted small\">" + H(t.Metric) + "</span>").Append("</td><td>").Append(H(t.Statistic)).Append("</td><td class=\"n\">")
              .Append(Fmt.Metric(t.Metric, t.Actual)).Append("</td><td class=\"n\">").Append(Fmt.Metric(t.Metric, t.Limit)).Append("</td></tr>");
        }
        sb.Append("</tbody></table></div></div>\n");
        SectionEnd(sb);
    }

    private static void AppendFindings(StringBuilder sb, RenderContext ctx)
    {
        SectionStart(sb, "findings", "Findings", "Issues detected by the analysis rules, with recommendations. Click a column header to sort.");
        var findings = ctx.Analysis?.Findings;
        if (findings is null) { Empty(sb, "Analysis was not run, so there are no findings."); SectionEnd(sb); return; }
        if (findings.Count == 0) { Empty(sb, "No findings — nothing to fix. 🎉"); SectionEnd(sb); return; }

        sb.Append("<div class=\"toolbar findings-toolbar\">");
        foreach (var sev in new[] { Severity.Critical, Severity.High, Severity.Medium, Severity.Low, Severity.Info })
        {
            int n = findings.Count(f => f.Severity == sev);
            sb.Append("<button type=\"button\" class=\"sev-toggle sev ").Append(SeverityClass(sev)).Append(n == 0 ? " zero" : "").Append("\" aria-pressed=\"true\" data-sev=\"")
              .Append(sev.ToString().ToLowerInvariant()).Append("\">").Append(sev).Append(" <b>").Append(n).Append("</b></button>");
        }
        sb.Append("<select id=\"finding-category\" aria-label=\"Category\"><option value=\"\">All categories</option>");
        foreach (var cat in findings.Select(f => f.Category).Distinct().OrderBy(c => c.ToString()))
            sb.Append("<option value=\"").Append(cat.ToString().ToLowerInvariant()).Append("\">").Append(cat).Append("</option>");
        sb.Append("</select><input type=\"search\" id=\"finding-search\" placeholder=\"Search findings…\" aria-label=\"Search findings\">")
          .Append("<span class=\"muted small\" id=\"finding-count\"></span></div>");

        sb.Append("<div class=\"card\"><div class=\"table-wrap\"><table class=\"data sortable\" id=\"finding-table\"><thead><tr>")
          .Append("<th data-sort=\"num\">Severity</th><th data-sort=\"text\">Category</th><th data-sort=\"text\">Target</th><th data-sort=\"text\">Title</th>")
          .Append("<th>Detail</th><th>Recommendation</th></tr></thead><tbody>");
        foreach (var f in findings.OrderByDescending(f => f.Severity).ThenBy(f => f.Category).ThenBy(f => f.Target))
        {
            sb.Append("<tr data-sev=\"").Append(f.Severity.ToString().ToLowerInvariant()).Append("\" data-cat=\"").Append(f.Category.ToString().ToLowerInvariant())
              .Append("\"><td data-v=\"").Append((int)f.Severity).Append("\"><span class=\"sev ").Append(SeverityClass(f.Severity)).Append("\">").Append(f.Severity)
              .Append("</span></td><td>").Append(f.Category).Append("</td><td class=\"text\">").Append(H(f.Target)).Append("</td><td class=\"text\"><strong>")
              .Append(H(f.Title)).Append("</strong>").Append(string.IsNullOrEmpty(f.RuleId) ? "" : "<div class=\"muted small\">" + H(f.RuleId) + "</div>")
              .Append("</td><td class=\"text\">").Append(H(f.Detail)).Append("</td><td class=\"text\">").Append(H(f.Recommendation)).Append("</td></tr>");
        }
        sb.Append("</tbody></table></div></div>\n");
        SectionEnd(sb);
    }

    private static void AppendBaseline(StringBuilder sb, RenderContext ctx)
    {
        var a = ctx.Analysis!;
        SectionStart(sb, "baseline", "Baseline comparison",
            "Change versus the baseline run" + (a.BaselineRunId != null ? " <code>" + H(a.BaselineRunId) + "</code>" : "") +
            ". Positive delta means the metric grew (slower / heavier for timing and size metrics).");
        var rows = a.Regressions.OrderByDescending(r => r.Regressed).ThenByDescending(r => Math.Abs(r.DeltaPct)).ToList();
        sb.Append("<div class=\"chart-grid\">");
        ChartBox(sb, "chart-regressions", "Delta vs baseline", "Red bars regressed beyond tolerance, green improved, grey within tolerance.",
            Math.Clamp(80 + rows.Count * 26, 220, 1200), "wide");
        sb.Append("</div><div class=\"card\"><div class=\"table-wrap\"><table class=\"data\"><thead><tr><th>Scope</th><th>Target</th><th>Metric</th>")
          .Append("<th class=\"n\">Baseline</th><th class=\"n\">Current</th><th class=\"n\">Delta</th><th>Status</th></tr></thead><tbody>");
        foreach (var r in rows)
        {
            var tone = r.Regressed ? "bad-text" : r.Improved ? "good-text" : "";
            sb.Append("<tr><td>").Append(H(r.Scope)).Append("</td><td>").Append(H(r.Target)).Append("</td><td>").Append(H(ReportFacts.Label(r.Metric)))
              .Append("</td><td class=\"n\">").Append(Fmt.Metric(r.Metric, r.Baseline)).Append("</td><td class=\"n\">").Append(Fmt.Metric(r.Metric, r.Current))
              .Append("</td><td class=\"n ").Append(tone).Append("\"><strong>").Append(Fmt.SignedPct(r.DeltaPct)).Append("</strong></td><td>")
              .Append(r.Regressed ? "<span class=\"chip bad\">Regressed</span>" : r.Improved ? "<span class=\"chip ok\">Improved</span>" : "<span class=\"chip\">Unchanged</span>")
              .Append("</td></tr>");
        }
        sb.Append("</tbody></table></div></div>\n");
        SectionEnd(sb);
    }
}
