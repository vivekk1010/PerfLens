using PerfLens.Core.Results;

namespace PerfLens.Analysis.Rules;

/// <summary>PAGE_UNAVAILABLE: the page errored or no iteration succeeded.</summary>
public sealed class PageUnavailableRule : IFindingRule
{
    public IEnumerable<Finding> Evaluate(AnalysisContext ctx)
    {
        foreach (var page in ctx.Pages.Where(AnalysisContext.IsUnavailable))
        {
            var reason = page.Error
                ?? (page.Iterations ?? []).FirstOrDefault(i => i is not null && !i.IsWarmup && i.Error is not null)?.Error
                ?? "no measured iteration succeeded";
            int attempts = (page.Iterations ?? []).Count(i => i is not null && !i.IsWarmup);
            yield return new Finding
            {
                RuleId = "PAGE_UNAVAILABLE", Severity = Severity.Critical, Category = FindingCategory.Reliability,
                Target = page.Name,
                Title = $"Page {page.Name} could not be measured",
                Detail = $"{Formatting.Plural(attempts, "measured attempt")} and none succeeded. Reason: {reason}",
                Recommendation = "Open the URL manually, check authentication, the ready selector and navigation timeout, and fix server errors before benchmarking.",
            };
        }
    }
}

/// <summary>PAGE_ITERATION_FAILURES: some (not all) measured iterations failed.</summary>
public sealed class PageIterationFailuresRule : PageRule
{
    protected override IEnumerable<Finding> Evaluate(AnalysisContext ctx, PageResult page)
    {
        var s = page.Summary!;
        if (s.FailedIterations == 0) yield break;
        int total = s.FailedIterations + s.MeasuredIterations;
        var sample = (page.Iterations ?? []).FirstOrDefault(i => i is not null && !i.IsWarmup && !i.Success)?.Error ?? "unknown error";
        yield return new Finding
        {
            RuleId = "PAGE_ITERATION_FAILURES", Severity = Severity.High, Category = FindingCategory.Reliability,
            Target = page.Name, Metric = "failed_iterations", Value = s.FailedIterations,
            Title = $"{s.FailedIterations} of {total} iterations failed on {page.Name}",
            Detail = $"Statistics use only the {s.MeasuredIterations} successful iterations. First error: {sample}",
            Recommendation = "Investigate intermittent failures (timeouts, 5xx responses, flaky selectors); intermittent failures often point to capacity problems.",
        };
    }
}

/// <summary>VITAL_POOR_* / VITAL_NI_*: Core Web Vital rated Poor (High) or Needs Improvement (Medium).</summary>
public sealed class VitalRatingRule : PageRule
{
    private static readonly Dictionary<string, string> Advice = new()
    {
        [MetricNames.Lcp] = "Speed up the largest element: preload the hero image/font, serve responsive compressed images, reduce render-blocking CSS/JS and server response time.",
        [MetricNames.Fcp] = "Reduce render-blocking resources: inline critical CSS, defer non-critical JS, and cut server response time and redirects.",
        [MetricNames.Cls] = "Reserve space for images, ads and embeds (width/height or aspect-ratio), avoid inserting content above existing content, and use font-display: optional/swap with size-adjust.",
        [MetricNames.Tbt] = "Break up long JavaScript tasks, code-split and defer non-critical scripts, and remove unused third-party code.",
        [MetricNames.Ttfb] = "Reduce server processing (caching, query optimization), use a CDN close to users and avoid redirect chains.",
    };

    protected override IEnumerable<Finding> Evaluate(AnalysisContext ctx, PageResult page)
    {
        if (!ctx.Result.VitalRatings.TryGetValue(page.Name, out var ratings)) yield break;
        foreach (var band in VitalRater.Bands)
        {
            if (!ratings.TryGetValue(band.Metric, out var rating) || ctx.Stat(page, band.Metric) is not { } value) continue;
            if (rating is not (VitalRating.Poor or VitalRating.NeedsImprovement)) continue;
            bool poor = rating == VitalRating.Poor;
            string v = Formatting.Metric(band.Metric, value);
            string good = Formatting.Metric(band.Metric, band.Good);
            string bad = Formatting.Metric(band.Metric, band.Poor);
            yield return new Finding
            {
                RuleId = $"VITAL_{(poor ? "POOR" : "NI")}_{band.ShortName}",
                Severity = poor ? Severity.High : Severity.Medium, Category = FindingCategory.Performance,
                Target = page.Name, Metric = band.Metric, Value = value,
                Title = $"{band.ShortName} is {(poor ? "poor" : "needs improvement")} on {page.Name} ({v})",
                Detail = $"{band.ShortName} {Label(ctx)} is {v}; good is <= {good}, poor is > {bad}.",
                Recommendation = Advice[band.Metric],
            };
        }
    }
}

/// <summary>PAGE_SLOW_SERVER: server response (request start to first byte) is slow (Medium &gt; 600 ms, High &gt; 1500 ms).</summary>
public sealed class SlowServerRule : PageRule
{
    protected override IEnumerable<Finding> Evaluate(AnalysisContext ctx, PageResult page)
    {
        if (ctx.Stat(page, MetricNames.Server) is not { } server || server <= 600) yield break;
        var ttfb = ctx.Stat(page, MetricNames.Ttfb);
        string share = ttfb is > 0 ? $" of a {Formatting.Duration(ttfb.Value)} TTFB ({Math.Min(100, server / ttfb.Value * 100):0}% backend)" : "";
        yield return new Finding
        {
            RuleId = "PAGE_SLOW_SERVER", Severity = server > 1500 ? Severity.High : Severity.Medium, Category = FindingCategory.Performance,
            Target = page.Name, Metric = MetricNames.Server, Value = server,
            Title = $"Slow server response on {page.Name} ({Formatting.Duration(server)})",
            Detail = $"Server time {Label(ctx)} is {Formatting.Duration(server)}{share}; the backend, not the network, dominates time to first byte.",
            Recommendation = "Profile the request on the server: add caching, optimize database queries, avoid synchronous downstream calls, and consider streaming the HTML head early.",
        };
    }
}

/// <summary>PAGE_HEAVY: transferred bytes above 2000 KB (Medium) or 5000 KB (High).</summary>
public sealed class HeavyPageRule : PageRule
{
    protected override IEnumerable<Finding> Evaluate(AnalysisContext ctx, PageResult page)
    {
        if (ctx.Stat(page, MetricNames.TransferKb) is not { } kb || kb <= 2000) yield break;
        var largest = ctx.Resources(page).Take(5).ToList();
        string top = largest.Count > 0 ? " Largest resources: " + ListResources(largest) + "." : "";
        yield return new Finding
        {
            RuleId = "PAGE_HEAVY", Severity = kb > 5000 ? Severity.High : Severity.Medium, Category = FindingCategory.Performance,
            Target = page.Name, Metric = MetricNames.TransferKb, Value = kb,
            Title = $"{page.Name} transfers {Formatting.Bytes(kb)}",
            Detail = $"Page weight {Label(ctx)} is {Formatting.Bytes(kb)} (recommended < {Formatting.Bytes(2000)}).{top}",
            Recommendation = "Compress and resize images (WebP/AVIF), lazy-load below-the-fold media, code-split JavaScript and remove unused libraries.",
        };
    }
}

/// <summary>PAGE_TOO_MANY_REQUESTS: more than 80 (Low) or 150 (Medium) requests.</summary>
public sealed class TooManyRequestsRule : PageRule
{
    protected override IEnumerable<Finding> Evaluate(AnalysisContext ctx, PageResult page)
    {
        if (ctx.Stat(page, MetricNames.Requests) is not { } n || n <= 80) yield break;
        yield return new Finding
        {
            RuleId = "PAGE_TOO_MANY_REQUESTS", Severity = n > 150 ? Severity.Medium : Severity.Low, Category = FindingCategory.Network,
            Target = page.Name, Metric = MetricNames.Requests, Value = n,
            Title = $"{page.Name} makes {n:0} requests",
            Detail = $"Request count {Label(ctx)} is {n:0} (recommended <= 80).",
            Recommendation = "Bundle small scripts and styles, use image sprites or inline SVG icons, defer third-party tags and lazy-load offscreen content.",
        };
    }
}

/// <summary>CONSOLE_ERRORS: console or uncaught page errors during load.</summary>
public sealed class ConsoleErrorsRule : PageRule
{
    protected override IEnumerable<Finding> Evaluate(AnalysisContext ctx, PageResult page)
    {
        if (ctx.Stat(page, MetricNames.ConsoleErrors, "max") is not { } max || max <= 0) yield break;
        var messages = PageSummarizer.Measured(page)
            .SelectMany(i => (i.PageErrors ?? []).Concat(i.ConsoleErrors ?? []))
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .Select(m => m.Length > 160 ? m[..157] + "..." : m)
            .ToList();
        string sample = messages.Count > 0 ? " Examples: " + string.Join(" | ", messages) : "";
        yield return new Finding
        {
            RuleId = "CONSOLE_ERRORS", Severity = Severity.Medium, Category = FindingCategory.Reliability,
            Target = page.Name, Metric = MetricNames.ConsoleErrors, Value = max,
            Title = $"{page.Name} logs JavaScript errors (up to {max:0} per load)",
            Detail = $"Up to {Formatting.Plural((int)max, "console/page error")} per page load.{sample}",
            Recommendation = "Fix the reported errors; uncaught exceptions can break functionality and slow down hydration.",
        };
    }
}

/// <summary>FAILED_REQUESTS: sub-resource requests that failed (Medium; High when &gt;= 5 per load).</summary>
public sealed class FailedRequestsRule : PageRule
{
    protected override IEnumerable<Finding> Evaluate(AnalysisContext ctx, PageResult page)
    {
        if (ctx.Stat(page, MetricNames.FailedRequests, "max") is not { } max || max <= 0) yield break;
        var failed = ctx.Resources(page).Where(r => r.Status is 0 or >= 400)
            .Take(5)
            .Select(r => $"{AnalysisContext.ShortUrl(r.Url)} ({(r.Status is > 0 ? r.Status.ToString() : "network error")})")
            .ToList();
        string list = failed.Count > 0 ? " Failed: " + string.Join(", ", failed) + "." : "";
        yield return new Finding
        {
            RuleId = "FAILED_REQUESTS", Severity = max >= 5 ? Severity.High : Severity.Medium, Category = FindingCategory.Reliability,
            Target = page.Name, Metric = MetricNames.FailedRequests, Value = max,
            Title = $"{page.Name} has failed requests (up to {max:0} per load)",
            Detail = $"Up to {Formatting.Plural((int)max, "sub-resource request")} failed with 4xx/5xx or network errors per load.{list}",
            Recommendation = "Remove references to missing assets, fix failing endpoints and check CORS/CSP rules; failed requests waste connections and may break features.",
        };
    }
}

/// <summary>HIGH_VARIANCE: coefficient of variation above 0.3 for LCP, load or TTFB with at least 3 samples.</summary>
public sealed class HighVarianceRule : PageRule
{
    private static readonly string[] Metrics = [MetricNames.Lcp, MetricNames.Load, MetricNames.Ttfb];

    protected override IEnumerable<Finding> Evaluate(AnalysisContext ctx, PageResult page)
    {
        var noisy = Metrics
            .Select(m => (Metric: m, Stats: page.Summary!.Metrics.GetValueOrDefault(m)))
            .Where(x => x.Stats is { Count: >= 3, CoV: > 0.3 })
            .ToList();
        if (noisy.Count == 0) yield break;
        var worst = noisy.MaxBy(x => x.Stats!.CoV);
        yield return new Finding
        {
            RuleId = "HIGH_VARIANCE", Severity = Severity.Low, Category = FindingCategory.Stability,
            Target = page.Name, Metric = worst.Metric, Value = Math.Round(worst.Stats!.CoV, 3),
            Title = $"Measurements on {page.Name} are noisy",
            Detail = string.Join("; ", noisy.Select(x =>
                $"{MetricNames.Display[x.Metric].Label} CoV {x.Stats!.CoV:0.00} (min {Formatting.Duration(x.Stats.Min)}, max {Formatting.Duration(x.Stats.Max)}, n={x.Stats.Count})")) + ". Results may not be reliable.",
            Recommendation = "Increase iterations, run from a quieter machine/network, and check for server-side variability (cold caches, GC pauses, autoscaling).",
        };
    }
}
