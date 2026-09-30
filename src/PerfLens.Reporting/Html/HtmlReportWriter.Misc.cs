using System.Text;
using PerfLens.Core.Results;
using PerfLens.Reporting.Internal;

namespace PerfLens.Reporting;

public sealed partial class HtmlReportWriter
{
    private const int MaxCrawlRows = 2000;

    private static void AppendCrawl(StringBuilder sb, RenderContext ctx)
    {
        var crawl = ctx.Run.Crawl!;
        SectionStart(sb, "crawl", "Crawl",
            $"{crawl.Visited.Count} pages visited, {crawl.BrokenLinks.Count} broken links, {crawl.SkippedLinks} links skipped, took {Fmt.Ms(crawl.DurationMs)}.");

        sb.Append("<div class=\"card\"><h3>Broken links (").Append(crawl.BrokenLinks.Count).Append(")</h3>");
        if (crawl.BrokenLinks.Count == 0) Empty(sb, "No broken links found. ✔");
        else CrawlTable(sb, crawl.BrokenLinks, broken: true);
        sb.Append("</div>");

        sb.Append("<details class=\"card sub\"").Append(crawl.Visited.Count <= 50 ? " open" : "").Append("><summary><h3>Visited pages (")
          .Append(crawl.Visited.Count).Append(")</h3></summary>");
        if (crawl.Visited.Count == 0) Empty(sb, "No pages visited.");
        else CrawlTable(sb, crawl.Visited, broken: false);
        sb.Append("</details>\n");
        SectionEnd(sb);
    }

    private static void CrawlTable(StringBuilder sb, List<CrawlEntry> entries, bool broken)
    {
        sb.Append("<div class=\"table-wrap\"><table class=\"data").Append(broken ? " broken" : "").Append("\"><thead><tr><th>URL</th><th class=\"n\">Depth</th><th>Status</th><th>Referrer</th></tr></thead><tbody>");
        foreach (var e in entries.Take(MaxCrawlRows))
        {
            bool bad = broken || e.Error != null || e.Status is >= 400;
            sb.Append("<tr").Append(bad ? " class=\"failed\"" : "").Append("><td class=\"url\">").Append(Link(e.Url, 100)).Append("</td><td class=\"n\">").Append(e.Depth)
              .Append("</td><td>").Append(e.Status is { } s ? $"<span class=\"chip small {(s >= 400 ? "bad" : "ok")}\">{s}</span>" : "")
              .Append(e.Error != null ? " <span class=\"bad-text\">" + H(e.Error) + "</span>" : e.Status == null ? "<span class=\"muted\">n/a</span>" : "")
              .Append("</td><td class=\"url\">").Append(e.Referrer != null ? Link(e.Referrer, 80) : "<span class=\"muted\">—</span>").Append("</td></tr>");
        }
        if (entries.Count > MaxCrawlRows)
            sb.Append("<tr><td colspan=\"4\" class=\"muted\">… ").Append(entries.Count - MaxCrawlRows).Append(" more (see results.json)</td></tr>");
        sb.Append("</tbody></table></div>");
    }

    private static void AppendWarnings(StringBuilder sb, RenderContext ctx)
    {
        SectionStart(sb, "warnings", "Warnings");
        if (ctx.Run.Warnings.Count == 0) Empty(sb, "No run warnings.");
        else
        {
            sb.Append("<ul class=\"warning-list\">");
            foreach (var w in ctx.Run.Warnings) sb.Append("<li>⚠️ ").Append(H(w)).Append("</li>");
            sb.Append("</ul>\n");
        }
        SectionEnd(sb);
    }

    private static readonly (string Term, string Text)[] Glossary =
    [
        ("TTFB — Time to First Byte", "Time from navigation start until the first byte of the HTML response arrives. Includes redirects, DNS, TCP/TLS setup and server think time. Good ≤ 800 ms, poor > 1.8 s."),
        ("DNS / Connect / TLS / Server", "Navigation Timing phases: DNS lookup, TCP connection, TLS handshake and server response (request sent until first byte). Zero usually means a reused connection."),
        ("Download", "Time to receive the HTML document body after the first byte."),
        ("FCP — First Contentful Paint", "When the browser first renders any text or image. Good ≤ 1.8 s, poor > 3 s."),
        ("LCP — Largest Contentful Paint", "When the largest image or text block in the viewport is rendered — the main perceived-load metric. Good ≤ 2.5 s, poor > 4 s. n/a when the browser reported no LCP entry."),
        ("CLS — Cumulative Layout Shift", "Unitless sum of unexpected layout shifts (content jumping around). Good ≤ 0.1, poor > 0.25."),
        ("TBT — Total Blocking Time", "Sum of the part of each long main-thread task (> 50 ms) beyond 50 ms, between FCP and the end of the measurement. A lab proxy for interactivity (INP). Good ≤ 200 ms, poor > 600 ms."),
        ("DOM interactive / DOMContentLoaded", "HTML parsed; DOMContentLoaded additionally waits for deferred scripts. DOM processing in the phases chart is DOMContentLoaded − TTFB − download."),
        ("Load", "The window load event: all subresources (images, styles, scripts) have loaded."),
        ("Page weight / requests", "Transferred bytes (compressed, over the network) and the number of requests made while loading the page. Cached resources transfer few or no bytes."),
        ("Warm-up iterations", "Initial loads that prime DNS, connections and caches. They are shown but excluded from statistics."),
        ("Percentiles (p50, p75, p95, p99)", "pN is the value below which N% of samples fall (linear interpolation between ranks). Median = p50. Core Web Vitals are assessed at p75. Few iterations make high percentiles noisy."),
        ("Std dev / CoV", "Standard deviation and coefficient of variation (std dev ÷ mean). A CoV above 25% means results vary a lot; consider more iterations."),
        ("Score & grade", "Each page gets a 0–100 score from its vitals and resource metrics; the overall score weights page scores and the API score. Grades: A ≥ 90, B ≥ 75, C ≥ 60, D ≥ 40, F below."),
        ("Thresholds", "Configured limits (e.g. LCP p75 ≤ 2.5 s, API p95 ≤ 800 ms). Any failure marks the run as FAIL."),
        ("API load test", "Requests sent directly to API endpoints under a closed (fixed virtual users) or open (fixed arrival rate) load profile. RPS = completed requests per second; error % = failed ÷ total."),
        ("Baseline regression", "Change of a metric versus a previous results.json. Regressed when it got worse by more than the configured tolerance."),
    ];

    private static void AppendGlossary(StringBuilder sb, RenderContext ctx)
    {
        SectionStart(sb, "glossary", "Glossary");
        sb.Append("<div class=\"card\"><dl class=\"glossary\">");
        foreach (var (term, text) in Glossary)
            sb.Append("<dt>").Append(H(term)).Append("</dt><dd>").Append(H(text)).Append("</dd>");
        sb.Append("</dl></div>\n");
        SectionEnd(sb);
    }
}
