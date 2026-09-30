using System.Text;
using PerfLens.Core.Results;
using PerfLens.Reporting.Internal;

namespace PerfLens.Reporting;

public sealed partial class HtmlReportWriter
{
    private static void AppendApi(StringBuilder sb, RenderContext ctx)
    {
        var endpoints = ctx.Run.Api!.Endpoints;
        SectionStart(sb, "api", "API load tests", "HTTP load generated directly against API endpoints (outside the browser). Latency covers successful and failed requests.");

        sb.Append("<div class=\"card\"><div class=\"table-wrap\"><table class=\"data\"><thead><tr><th>Endpoint</th><th>Method</th><th>Profile</th>")
          .Append("<th class=\"n\">Requests</th><th class=\"n\">RPS</th><th class=\"n\">Errors</th><th class=\"n\">p50</th><th class=\"n\">p90</th>")
          .Append("<th class=\"n\">p95</th><th class=\"n\">p99</th><th class=\"n\">Max</th><th>Status codes</th></tr></thead><tbody>");
        for (int i = 0; i < endpoints.Count; i++)
        {
            var e = endpoints[i];
            var l = e.Latency;
            string L(double v) => l.Count > 0 ? Fmt.Ms(v) : Fmt.NA;
            sb.Append("<tr").Append(e.Error != null ? " class=\"failed\"" : "").Append("><td><a href=\"#api-").Append(i).Append("\">").Append(H(e.Name)).Append("</a>")
              .Append(e.Source == EndpointSource.Discovered ? " <span class=\"chip small\">discovered</span>" : "").Append("</td><td><span class=\"method\">")
              .Append(H(e.Method)).Append("</span></td><td>").Append(HOrNa(e.ProfileDescription)).Append("</td><td class=\"n\">").Append(Fmt.Int(e.TotalRequests))
              .Append("</td><td class=\"n\">").Append(Fmt.Num(e.Rps, "0.0")).Append("</td><td class=\"n").Append(e.ErrorRatePct > 0 ? " bad-text" : "").Append("\">")
              .Append(Fmt.Pct(e.ErrorRatePct)).Append("</td><td class=\"n\">").Append(L(l.Median)).Append("</td><td class=\"n\">").Append(L(l.P90))
              .Append("</td><td class=\"n\">").Append(L(l.P95)).Append("</td><td class=\"n\">").Append(L(l.P99)).Append("</td><td class=\"n\">")
              .Append(L(l.Max)).Append("</td><td>").Append(StatusChips(e.StatusCodes)).Append("</td></tr>");
        }
        sb.Append("</tbody></table></div></div>\n");

        for (int i = 0; i < endpoints.Count; i++)
        {
            var e = endpoints[i];
            sb.Append("<div class=\"card endpoint\" id=\"api-").Append(i).Append("\"><h3><span class=\"method\">").Append(H(e.Method)).Append("</span> ")
              .Append(H(e.Name)).Append("</h3><p class=\"muted small url\">").Append(H(e.Url)).Append(" · ").Append(H(e.ProfileDescription))
              .Append(" · ").Append(Fmt.Num(e.DurationSeconds, "0.#")).Append(" s · ").Append(Fmt.Bytes(e.BytesReceived)).Append(" received</p>");
            if (e.Error != null) sb.Append("<div class=\"alert bad\"><strong>Load test error:</strong> ").Append(H(e.Error)).Append("</div>");

            if (e.TotalRequests > 0 || e.Latency.Count > 0)
            {
                sb.Append("<div class=\"chart-grid\">");
                if (e.Latency.Count > 0)
                    ChartBox(sb, "api-lat-" + i, "Latency percentiles", "Min, p50, p75, p90, p95, p99 and max response time.", 260);
                if (e.StatusCodes.Count > 0 || e.ErrorTypes.Count > 0)
                    ChartBox(sb, "api-status-" + i, "Status codes", "Share of responses per HTTP status (or error type when no response arrived).", 260);
                if (e.Timeline.Count > 0)
                    ChartBox(sb, "api-time-" + i, "Timeline", "Bars: requests per second (errors in red). Lines: p50 and p95 latency per second (right axis).", 300, "wide");
                if (e.Histogram.Count > 0)
                    ChartBox(sb, "api-hist-" + i, "Latency histogram", "How many requests fell into each latency bucket.", 280, "wide");
                sb.Append("</div>");
            }
            else if (e.Error == null) Empty(sb, "No requests were recorded for this endpoint.");

            if (e.ErrorTypes.Count > 0)
            {
                sb.Append("<h4>Error types</h4><table class=\"data compact\"><thead><tr><th>Type</th><th class=\"n\">Count</th><th class=\"n\">Share</th></tr></thead><tbody>");
                foreach (var (type, count) in e.ErrorTypes.OrderByDescending(kv => kv.Value))
                    sb.Append("<tr><td>").Append(H(type)).Append("</td><td class=\"n\">").Append(Fmt.Int(count)).Append("</td><td class=\"n\">")
                      .Append(Fmt.Pct(e.TotalRequests > 0 ? 100.0 * count / e.TotalRequests : null)).Append("</td></tr>");
                sb.Append("</tbody></table>");
            }
            sb.Append("</div>\n");
        }
        SectionEnd(sb);
    }

    private static string StatusChips(Dictionary<string, long> codes)
    {
        if (codes.Count == 0) return "<span class=\"muted\">n/a</span>";
        var sb = new StringBuilder();
        foreach (var (code, count) in codes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var tone = code.Length > 0 && code[0] is '2' or '3' ? "ok" : "bad";
            sb.Append("<span class=\"chip small ").Append(tone).Append("\">").Append(H(code)).Append(": ").Append(Fmt.Int(count)).Append("</span> ");
        }
        return sb.ToString();
    }
}
