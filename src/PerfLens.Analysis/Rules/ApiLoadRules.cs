using PerfLens.Core.Results;

namespace PerfLens.Analysis.Rules;

/// <summary>
/// Load-test rules per endpoint: API_UNREACHABLE, API_ERROR_RATE, API_SLOW_P95, API_TAIL_LATENCY,
/// API_THROUGHPUT_LOW and API_TIMEOUTS.
/// </summary>
public sealed class ApiLoadRule : IFindingRule
{
    public IEnumerable<Finding> Evaluate(AnalysisContext ctx) => ctx.Endpoints.SelectMany(e => Evaluate(ctx, e));

    private static IEnumerable<Finding> Evaluate(AnalysisContext ctx, EndpointLoadResult e)
    {
        if (AnalysisContext.IsUnreachable(e))
        {
            yield return new Finding
            {
                RuleId = "API_UNREACHABLE", Severity = Severity.Critical, Category = FindingCategory.Reliability,
                Target = e.Name, Metric = "successful_requests", Value = e.SuccessfulRequests,
                Title = $"API {e.Name} is unreachable",
                Detail = e.Error is not null
                    ? $"Load test failed: {e.Error}"
                    : $"0 of {e.TotalRequests} requests succeeded.{TopErrors(e)}",
                Recommendation = "Verify the URL, method, authentication headers/cookies and expected status codes, and that the service is up.",
            };
            yield break;
        }

        var t = ctx.EffectiveApiThresholds(e);
        var lat = e.Latency ?? new MetricStats();
        string profile = string.IsNullOrEmpty(e.ProfileDescription) ? "" : $" ({e.ProfileDescription})";

        double errorLimit = t.ErrorRatePct ?? 1;
        if (e.ErrorRatePct > errorLimit)
            yield return new Finding
            {
                RuleId = "API_ERROR_RATE", Severity = e.ErrorRatePct > 5 ? Severity.Critical : Severity.High, Category = FindingCategory.Reliability,
                Target = e.Name, Metric = "error_rate_pct", Value = e.ErrorRatePct,
                Title = $"API {e.Name} error rate {Formatting.Percent(e.ErrorRatePct)}",
                Detail = $"{e.FailedRequests} of {e.TotalRequests} requests failed under load{profile} (limit {Formatting.Percent(errorLimit)}).{TopErrors(e)}",
                Recommendation = "Check server logs for errors under concurrency (pool exhaustion, timeouts, rate limiting) and add capacity or back-pressure.",
            };

        double p95Limit = t.P95Ms ?? 1000;
        if (lat.P95 > p95Limit)
            yield return new Finding
            {
                RuleId = "API_SLOW_P95", Severity = lat.P95 > 2 * p95Limit ? Severity.High : Severity.Medium, Category = FindingCategory.Performance,
                Target = e.Name, Metric = "p95_ms", Value = lat.P95,
                Title = $"API {e.Name} p95 is {Formatting.Duration(lat.P95)}",
                Detail = $"p50 {Formatting.Duration(lat.Median)}, p95 {Formatting.Duration(lat.P95)}, p99 {Formatting.Duration(lat.P99)} at {e.Rps:0.#} rps{profile}; limit p95 {Formatting.Duration(p95Limit)}.",
                Recommendation = "Profile the endpoint under load: optimize queries, add caching, and check thread/connection pool sizing.",
            };

        if (lat.Median > 0 && lat.P99 > 4 * lat.Median && lat.P99 > 200)
            yield return new Finding
            {
                RuleId = "API_TAIL_LATENCY", Severity = Severity.Medium, Category = FindingCategory.Stability,
                Target = e.Name, Metric = "p99_ms", Value = lat.P99,
                Title = $"API {e.Name} has a long latency tail (p99 {lat.P99 / lat.Median:0.#}x p50)",
                Detail = $"p50 {Formatting.Duration(lat.Median)} but p99 {Formatting.Duration(lat.P99)} (max {Formatting.Duration(lat.Max)}).",
                Recommendation = "Look for lock contention, GC pauses, cold caches, slow downstream calls or queueing; consider timeouts and hedged requests.",
            };

        if (t.MinRps is { } minRps && e.Rps < minRps)
            yield return new Finding
            {
                RuleId = "API_THROUGHPUT_LOW", Severity = Severity.Medium, Category = FindingCategory.Performance,
                Target = e.Name, Metric = "rps", Value = e.Rps,
                Title = $"API {e.Name} throughput {e.Rps:0.#} rps is below {minRps:0.#} rps",
                Detail = $"Achieved {e.Rps:0.#} rps{profile} with p95 {Formatting.Duration(lat.P95)}; required at least {minRps:0.#} rps.",
                Recommendation = "Increase concurrency/instances, remove serialization points, and verify the load generator was not the bottleneck.",
            };

        long timeouts = (e.ErrorTypes ?? []).Where(kv => kv.Key.Contains("timeout", StringComparison.OrdinalIgnoreCase)).Sum(kv => kv.Value);
        if (timeouts > 0)
            yield return new Finding
            {
                RuleId = "API_TIMEOUTS", Severity = e.TotalRequests > 0 && timeouts * 100.0 / e.TotalRequests > 1 ? Severity.High : Severity.Medium,
                Category = FindingCategory.Reliability,
                Target = e.Name, Metric = "timeouts", Value = timeouts,
                Title = $"API {e.Name} had {Formatting.Plural((int)Math.Min(int.MaxValue, timeouts), "timeout")}",
                Detail = $"{timeouts} of {e.TotalRequests} requests timed out{profile}.",
                Recommendation = "Find what blocks these requests (slow queries, exhausted pools, downstream latency); make sure client timeouts match SLAs.",
            };
    }

    private static string TopErrors(EndpointLoadResult e)
    {
        var parts = (e.ErrorTypes ?? []).Select(kv => (kv.Key, kv.Value))
            .Concat((e.StatusCodes ?? []).Where(kv => int.TryParse(kv.Key, out var c) && c >= 400).Select(kv => ($"HTTP {kv.Key}", kv.Value)))
            .OrderByDescending(x => x.Value).ThenBy(x => x.Item1, StringComparer.Ordinal)
            .Take(3)
            .Select(x => $"{x.Item1}: {x.Value}")
            .ToList();
        return parts.Count == 0 ? "" : " Top errors: " + string.Join(", ", parts) + ".";
    }
}

/// <summary>
/// API_DEGRADATION_UNDER_LOAD: request-weighted p95 in the last third of the timeline is more than 2x the first third
/// (needs at least 6 timeline seconds, 10 requests per third, and a 50 ms absolute increase).
/// </summary>
public sealed class ApiDegradationRule : IFindingRule
{
    public IEnumerable<Finding> Evaluate(AnalysisContext ctx)
    {
        foreach (var e in ctx.Endpoints.Where(e => !AnalysisContext.IsUnreachable(e)))
        {
            var points = (e.Timeline ?? []).Where(p => p is not null && p.Requests > 0).OrderBy(p => p.Second).ToList();
            if (points.Count < 6) continue;
            int third = points.Count / 3;
            var first = points.Take(third).ToList();
            var last = points.Skip(points.Count - third).ToList();
            if (first.Sum(p => p.Requests) < 10 || last.Sum(p => p.Requests) < 10) continue;
            double firstP95 = Weighted(first), lastP95 = Weighted(last);
            if (firstP95 <= 0 || lastP95 <= 2 * firstP95 || lastP95 - firstP95 < 50) continue;
            yield return new Finding
            {
                RuleId = "API_DEGRADATION_UNDER_LOAD", Severity = Severity.Medium, Category = FindingCategory.Stability,
                Target = e.Name, Metric = "p95_ms", Value = Math.Round(lastP95, 1),
                Title = $"API {e.Name} slows down under sustained load",
                Detail = $"p95 grew from {Formatting.Duration(firstP95)} in the first third of the test to {Formatting.Duration(lastP95)} in the last third ({lastP95 / firstP95:0.#}x).",
                Recommendation = "Look for resource leaks, queue build-up, connection pool exhaustion, GC pressure or throttling; run a longer soak test to confirm.",
            };
        }
    }

    private static double Weighted(List<TimelinePoint> points) =>
        points.Sum(p => p.P95Ms * p.Requests) / Math.Max(1, points.Sum(p => p.Requests));
}

/// <summary>REGRESSION: a metric regressed against the baseline (High at &gt;= 2x tolerance, else Medium).</summary>
public sealed class RegressionRule : IFindingRule
{
    public IEnumerable<Finding> Evaluate(AnalysisContext ctx) =>
        ctx.Result.Regressions.Where(r => r.Regressed).Select(r =>
        {
            string label = MetricNames.Display.TryGetValue(r.Metric, out var d) ? d.Label : r.Metric;
            return new Finding
            {
                RuleId = "REGRESSION",
                Severity = Math.Abs(r.DeltaPct) >= 2 * ctx.TolerancePct ? Severity.High : Severity.Medium,
                Category = FindingCategory.Regression,
                Target = r.Target, Metric = r.Metric, Value = r.Current,
                Title = $"{label} regressed on {r.Target} ({(r.DeltaPct >= 0 ? "+" : "")}{r.DeltaPct:0.#}%)",
                Detail = $"{label} went from {Formatting.Metric(r.Metric, r.Baseline)} to {Formatting.Metric(r.Metric, r.Current)} vs baseline {ctx.Result.BaselineRunId} (tolerance {ctx.TolerancePct:0.#}%).",
                Recommendation = "Review changes deployed since the baseline run (code, dependencies, infrastructure, data volume) that affect this metric.",
            };
        });
}
