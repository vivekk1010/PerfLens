using System.Text;
using PerfLens.Core.Abstractions;
using PerfLens.Core.Results;
using PerfLens.Reporting.Internal;

namespace PerfLens.Reporting;

/// <summary>Writes a concise <c>summary.md</c> suitable for PR comments and CI job summaries.</summary>
public sealed class MarkdownReportWriter : IReportWriter
{
    public const string FileName = "summary.md";
    private const int MaxFindings = 15;

    public string Format => "md";

    public async Task<IReadOnlyList<string>> WriteAsync(RunResult run, string outputDirectory, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, FileName);
        await File.WriteAllTextAsync(path, Render(run), new UTF8Encoding(false), ct);
        return [path];
    }

    internal static string Render(RunResult run)
    {
        var sb = new StringBuilder();
        var a = run.Analysis;
        var stat = ReportFacts.Statistic(run);

        sb.Append("# PerfLens report: ").AppendLine(Esc(string.IsNullOrWhiteSpace(run.Name) ? run.BaseUrl : run.Name)).AppendLine();
        sb.Append("- **Base URL:** ").AppendLine(Esc(run.BaseUrl));
        if (!string.IsNullOrWhiteSpace(run.Label)) sb.Append("- **Label:** ").AppendLine(Esc(run.Label));
        sb.Append("- **Started:** ").Append(Fmt.Date(run.StartedAt)).Append(" (duration ").Append(Fmt.Duration(run.FinishedAt - run.StartedAt)).AppendLine(")");
        if (a != null)
        {
            sb.Append("- **Score:** ").Append(Fmt.Score(a.Score)).Append("/100 (grade ").Append(Esc(a.Grade)).AppendLine(")");
            sb.Append("- **Result:** ").AppendLine(a.Passed ? "✅ PASS" : "❌ FAIL");
            var tp = a.Thresholds.Count(t => t.Passed);
            sb.Append("- **Thresholds:** ").Append(tp).Append('/').Append(a.Thresholds.Count).AppendLine(" passed");
        }
        else sb.AppendLine("- **Score:** n/a (analysis not available)");
        sb.AppendLine();

        if (a?.Highlights.Count > 0)
        {
            sb.AppendLine("## Highlights").AppendLine();
            foreach (var h in a.Highlights) sb.Append("- ").AppendLine(Esc(h));
            sb.AppendLine();
        }

        if (run.Settings.Count > 0)
        {
            sb.AppendLine("## Settings").AppendLine();
            sb.AppendLine("| Setting | Value |").AppendLine("|---|---|");
            foreach (var (k, v) in run.Settings) sb.Append("| ").Append(Esc(k)).Append(" | ").Append(Esc(v)).AppendLine(" |");
            sb.AppendLine();
        }

        sb.Append("## Pages (").Append(stat).AppendLine(")").AppendLine();
        if (run.Pages.Count == 0) sb.AppendLine("_No pages measured._");
        else
        {
            sb.AppendLine("| Page | Iterations | TTFB | FCP | LCP | CLS | TBT | Load | Weight | Requests | Score |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var p in run.Pages)
            {
                var m = ReportFacts.Metrics(p);
                string V(string metric) => Fmt.Metric(metric, ReportFacts.Value(m, metric, stat));
                var name = Esc(p.Name) + (p.Error != null ? " ⚠️" : "");
                sb.Append("| ").Append(name)
                  .Append(" | ").Append(ReportFacts.MeasuredCount(p))
                  .Append(" | ").Append(V(MetricNames.Ttfb))
                  .Append(" | ").Append(V(MetricNames.Fcp))
                  .Append(" | ").Append(V(MetricNames.Lcp))
                  .Append(" | ").Append(V(MetricNames.Cls))
                  .Append(" | ").Append(V(MetricNames.Tbt))
                  .Append(" | ").Append(V(MetricNames.Load))
                  .Append(" | ").Append(V(MetricNames.TransferKb))
                  .Append(" | ").Append(Fmt.Num(ReportFacts.Value(m, MetricNames.Requests, stat), "0"))
                  .Append(" | ").Append(Fmt.Score(ReportFacts.Score(p)))
                  .AppendLine(" |");
            }
            var errored = run.Pages.Where(p => p.Error != null).ToList();
            if (errored.Count > 0)
            {
                sb.AppendLine();
                foreach (var p in errored) sb.Append("- ⚠️ **").Append(Esc(p.Name)).Append("**: ").AppendLine(Esc(p.Error));
            }
        }
        sb.AppendLine();

        if (run.Api?.Endpoints.Count > 0)
        {
            sb.AppendLine("## API load tests").AppendLine();
            sb.AppendLine("| Endpoint | Method | Profile | Requests | RPS | Errors | p50 | p95 | p99 | Max |");
            sb.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var e in run.Api.Endpoints)
            {
                sb.Append("| ").Append(Esc(e.Name)).Append(e.Error != null ? " ⚠️" : "")
                  .Append(" | ").Append(Esc(e.Method))
                  .Append(" | ").Append(Esc(e.ProfileDescription))
                  .Append(" | ").Append(Fmt.Int(e.TotalRequests))
                  .Append(" | ").Append(Fmt.Num(e.Rps, "0.0"))
                  .Append(" | ").Append(Fmt.Pct(e.ErrorRatePct))
                  .Append(" | ").Append(Fmt.Ms(e.Latency.Median))
                  .Append(" | ").Append(Fmt.Ms(e.Latency.P95))
                  .Append(" | ").Append(Fmt.Ms(e.Latency.P99))
                  .Append(" | ").Append(Fmt.Ms(e.Latency.Max))
                  .AppendLine(" |");
            }
            foreach (var e in run.Api.Endpoints.Where(e => e.Error != null))
                sb.AppendLine().Append("- ⚠️ **").Append(Esc(e.Name)).Append("**: ").AppendLine(Esc(e.Error));
            sb.AppendLine();
        }

        var failed = a?.Thresholds.Where(t => !t.Passed).ToList() ?? [];
        if (failed.Count > 0)
        {
            sb.AppendLine("## Threshold failures").AppendLine();
            sb.AppendLine("| Scope | Target | Metric | Statistic | Actual | Limit |").AppendLine("|---|---|---|---|---:|---:|");
            foreach (var t in failed)
                sb.Append("| ").Append(Esc(t.Scope)).Append(" | ").Append(Esc(t.Target)).Append(" | ").Append(Esc(ReportFacts.Label(t.Metric)))
                  .Append(" | ").Append(Esc(t.Statistic)).Append(" | ").Append(Fmt.Metric(t.Metric, t.Actual))
                  .Append(" | ").Append(Fmt.Metric(t.Metric, t.Limit)).AppendLine(" |");
            sb.AppendLine();
        }

        if (a?.Findings.Count > 0)
        {
            sb.Append("## Top findings (").Append(a.Findings.Count).AppendLine(" total)").AppendLine();
            sb.AppendLine("| Severity | Category | Target | Finding | Recommendation |").AppendLine("|---|---|---|---|---|");
            foreach (var f in a.Findings.OrderByDescending(f => f.Severity).Take(MaxFindings))
                sb.Append("| ").Append(f.Severity).Append(" | ").Append(f.Category).Append(" | ").Append(Esc(f.Target))
                  .Append(" | **").Append(Esc(f.Title)).Append("** ").Append(Esc(f.Detail))
                  .Append(" | ").Append(Esc(f.Recommendation)).AppendLine(" |");
            sb.AppendLine();
        }

        if (a?.Regressions.Count > 0)
        {
            sb.Append("## Baseline comparison");
            if (a.BaselineRunId != null) sb.Append(" (baseline ").Append(Esc(a.BaselineRunId)).Append(')');
            sb.AppendLine().AppendLine();
            sb.AppendLine("| Scope | Target | Metric | Baseline | Current | Delta | |").AppendLine("|---|---|---|---:|---:|---:|---|");
            foreach (var r in a.Regressions.OrderByDescending(r => r.Regressed).ThenByDescending(r => Math.Abs(r.DeltaPct)))
                sb.Append("| ").Append(Esc(r.Scope)).Append(" | ").Append(Esc(r.Target)).Append(" | ").Append(Esc(ReportFacts.Label(r.Metric)))
                  .Append(" | ").Append(Fmt.Metric(r.Metric, r.Baseline)).Append(" | ").Append(Fmt.Metric(r.Metric, r.Current))
                  .Append(" | ").Append(Fmt.SignedPct(r.DeltaPct))
                  .Append(" | ").Append(r.Regressed ? "🔴 regressed" : r.Improved ? "🟢 improved" : "").AppendLine(" |");
            sb.AppendLine();
        }

        if (run.Crawl?.BrokenLinks.Count > 0)
        {
            sb.Append("## Broken links (").Append(run.Crawl.BrokenLinks.Count).AppendLine(")").AppendLine();
            sb.AppendLine("| URL | Status | Referrer |").AppendLine("|---|---:|---|");
            foreach (var b in run.Crawl.BrokenLinks)
                sb.Append("| ").Append(Esc(b.Url)).Append(" | ").Append(b.Status?.ToString() ?? Esc(b.Error ?? "n/a"))
                  .Append(" | ").Append(Esc(b.Referrer)).AppendLine(" |");
            sb.AppendLine();
        }

        if (run.Warnings.Count > 0)
        {
            sb.AppendLine("## Warnings").AppendLine();
            foreach (var w in run.Warnings) sb.Append("- ").AppendLine(Esc(w));
            sb.AppendLine();
        }

        sb.Append("_Generated by PerfLens ").Append(Esc(string.IsNullOrEmpty(run.Environment.ToolVersion) ? ToolInfo.Version : run.Environment.ToolVersion))
          .Append(" at ").Append(Fmt.Date(DateTimeOffset.Now)).AppendLine("._");
        return sb.ToString();
    }

    /// <summary>Escapes untrusted text for a single-line markdown table cell (no raw HTML, no pipes).</summary>
    internal static string Esc(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '|': sb.Append("\\|"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '&': sb.Append("&amp;"); break;
                case '\r': break;
                case '\n': sb.Append(' '); break;
                case '`': sb.Append("\\`"); break;
                case '*': sb.Append("\\*"); break;
                case '_': sb.Append("\\_"); break;
                case '[': sb.Append("\\["); break;
                case ']': sb.Append("\\]"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
