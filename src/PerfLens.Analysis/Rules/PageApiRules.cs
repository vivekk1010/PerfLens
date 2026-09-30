using PerfLens.Core.Results;

namespace PerfLens.Analysis.Rules;

/// <summary>SLOW_API_CALL (p95 &gt; 1000 ms) and API_CALL_FAILURES (any failure) for XHR/fetch calls made by a page.</summary>
public sealed class PageApiCallRule : PageRule
{
    public const double SlowP95Ms = 1000;

    protected override IEnumerable<Finding> Evaluate(AnalysisContext ctx, PageResult page)
    {
        foreach (var call in page.Summary!.ApiCalls)
        {
            string name = $"{call.Method} {AnalysisContext.ShortUrl(call.NormalizedUrl)}";
            if (call.Failures > 0)
                yield return new Finding
                {
                    RuleId = "API_CALL_FAILURES", Severity = Severity.High, Category = FindingCategory.Reliability,
                    Target = page.Name, Metric = "failures", Value = call.Failures,
                    Title = $"{name} failed {call.Failures} of {call.Calls} times on {page.Name}",
                    Detail = $"{call.Failures} of {call.Calls} calls to {name} failed (example URL: {call.SampleUrl}).",
                    Recommendation = "Check server logs for this endpoint and make sure the page handles API errors gracefully.",
                };
            if (call.Duration.P95 > SlowP95Ms)
                yield return new Finding
                {
                    RuleId = "SLOW_API_CALL", Severity = Severity.Medium, Category = FindingCategory.Performance,
                    Target = page.Name, Metric = "p95_ms", Value = call.Duration.P95,
                    Title = $"Slow API call {name} on {page.Name} (p95 {Formatting.Duration(call.Duration.P95)})",
                    Detail = $"{call.Calls} calls, median {Formatting.Duration(call.Duration.Median)}, p95 {Formatting.Duration(call.Duration.P95)}, avg {Formatting.Bytes(call.AvgBytes / 1024)}.",
                    Recommendation = "Optimize the endpoint (indexes, caching, pagination), or load its data lazily so it does not block rendering.",
                };
        }
    }
}

/// <summary>N_PLUS_ONE_API: the same normalized API URL is called more than 5 times per page load on average.</summary>
public sealed class NPlusOneApiRule : PageRule
{
    public const double MaxCallsPerLoad = 5;

    protected override IEnumerable<Finding> Evaluate(AnalysisContext ctx, PageResult page)
    {
        int loads = page.Summary!.MeasuredIterations;
        if (loads == 0) yield break;
        foreach (var call in page.Summary.ApiCalls)
        {
            double perLoad = (double)call.Calls / loads;
            if (perLoad <= MaxCallsPerLoad) continue;
            string name = $"{call.Method} {AnalysisContext.ShortUrl(call.NormalizedUrl)}";
            yield return new Finding
            {
                RuleId = "N_PLUS_ONE_API", Severity = Severity.Low, Category = FindingCategory.Performance,
                Target = page.Name, Metric = "calls_per_load", Value = Math.Round(perLoad, 1),
                Title = $"{name} called {perLoad:0.#} times per load on {page.Name}",
                Detail = $"{call.Calls} calls over {Formatting.Plural(loads, "page load")} suggests an N+1 pattern (one request per item).",
                Recommendation = "Batch these requests into a single list/bulk endpoint or include the data in the parent response.",
            };
        }
    }
}

/// <summary>BROKEN_LINK: one finding per broken link found by the crawler.</summary>
public sealed class BrokenLinkRule : IFindingRule
{
    public IEnumerable<Finding> Evaluate(AnalysisContext ctx) =>
        (ctx.Run.Crawl?.BrokenLinks ?? []).Where(b => b is not null).Select(b =>
        {
            string url = AnalysisContext.ShortUrl(b.Url);
            string status = b.Status is > 0 ? $"HTTP {b.Status}" : b.Error ?? "unreachable";
            string from = string.IsNullOrEmpty(b.Referrer) ? "" : $" linked from {AnalysisContext.ShortUrl(b.Referrer)}";
            return new Finding
            {
                RuleId = "BROKEN_LINK", Severity = Severity.High, Category = FindingCategory.Reliability,
                Target = Core.Utilities.UrlUtils.RedactSecrets(b.Url), Metric = "status", Value = b.Status,
                Title = $"Broken link {url} ({status})",
                Detail = $"{url} returned {status}{from}.",
                Recommendation = "Fix or remove the link, or add a redirect to the new location.",
            };
        });
}
