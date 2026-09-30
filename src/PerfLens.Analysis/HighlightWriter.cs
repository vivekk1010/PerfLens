using PerfLens.Core.Results;

namespace PerfLens.Analysis;

/// <summary>Writes 1-6 short plain-English conclusions for the report header.</summary>
public static class HighlightWriter
{
    public const int MaxHighlights = 6;

    public static List<string> Write(AnalysisContext ctx)
    {
        var r = ctx.Result;
        var pages = ctx.Pages.Where(p => !AnalysisContext.IsUnavailable(p)).ToList();
        var endpoints = ctx.Endpoints;
        var lines = new List<string> { OverallLine(ctx, pages) };

        var slowest = pages
            .Select(p => (Page: p, Lcp: ctx.Stat(p, MetricNames.Lcp)))
            .Where(x => x.Lcp is not null)
            .OrderByDescending(x => x.Lcp).ThenBy(x => x.Page.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (slowest.Page is not null)
            lines.Add($"Slowest page: {slowest.Page.Name} (LCP {ctx.Statistic} {Formatting.Duration(slowest.Lcp!.Value)}).");
        else if (pages.Select(p => (Page: p, Load: ctx.Stat(p, MetricNames.Load))).Where(x => x.Load is not null)
                     .OrderByDescending(x => x.Load).ThenBy(x => x.Page.Name, StringComparer.Ordinal).FirstOrDefault() is { Page: not null } byLoad)
            lines.Add($"Slowest page: {byLoad.Page.Name} (load {ctx.Statistic} {Formatting.Duration(byLoad.Load!.Value)}).");

        var heaviest = pages
            .Select(p => (Page: p, Kb: ctx.Stat(p, MetricNames.TransferKb)))
            .Where(x => x.Kb is > 0)
            .OrderByDescending(x => x.Kb).ThenBy(x => x.Page.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (heaviest.Page is not null && pages.Count > 1)
            lines.Add($"Heaviest page: {heaviest.Page.Name} ({Formatting.Bytes(heaviest.Kb!.Value)}).");
        else if (heaviest.Page is not null)
            lines.Add($"Page weight: {heaviest.Page.Name} transfers {Formatting.Bytes(heaviest.Kb!.Value)}.");

        var reachable = endpoints.Where(e => !AnalysisContext.IsUnreachable(e)).ToList();
        var slowApi = reachable.OrderByDescending(e => e.Latency?.P95 ?? 0).ThenBy(e => e.Name, StringComparer.Ordinal).FirstOrDefault();
        if (slowApi is not null)
            lines.Add($"{(reachable.Count > 1 ? "Slowest API" : "API")}: {slowApi.Name} (p95 {Formatting.Duration(slowApi.Latency?.P95 ?? 0)} at {slowApi.Rps:0.#} rps, {Formatting.Percent(slowApi.ErrorRatePct)} errors).");
        int unreachable = endpoints.Count - reachable.Count;
        int unavailable = ctx.Pages.Count - pages.Count;
        if (unreachable + unavailable > 0)
        {
            var parts = new List<string>();
            if (unavailable > 0) parts.Add(Formatting.Plural(unavailable, "page"));
            if (unreachable > 0) parts.Add(Formatting.Plural(unreachable, "API endpoint"));
            lines.Add($"{string.Join(" and ", parts)} could not be measured.");
        }

        int broken = ctx.Run.Crawl?.BrokenLinks?.Count ?? 0;
        if (broken > 0) lines.Add($"{Formatting.Plural(broken, "broken link")} found.");

        if (r.BaselineRunId is not null || r.Regressions.Count > 0)
        {
            int regressed = r.Regressions.Count(x => x.Regressed), improved = r.Regressions.Count(x => x.Improved);
            lines.Add(regressed == 0
                ? $"No regressions vs baseline ({Formatting.Plural(improved, "metric")} improved)."
                : $"{Formatting.Plural(regressed, "regression")} vs baseline ({improved} improved).");
        }

        if (lines.Count > MaxHighlights)
        {
            // Keep the overall line and the regression summary; drop the lowest-priority middle lines.
            var tail = r.BaselineRunId is not null ? lines[^1] : null;
            lines = lines.Take(tail is null ? MaxHighlights : MaxHighlights - 1).ToList();
            if (tail is not null) lines.Add(tail);
        }
        return lines;
    }

    private static string OverallLine(AnalysisContext ctx, List<PageResult> pages)
    {
        var r = ctx.Result;
        string head = $"Overall score {Math.Round(r.Score):0} ({r.Grade})";
        var clauses = new List<string>();

        var rated = pages
            .Select(p => r.VitalRatings.TryGetValue(p.Name, out var v) ? v.GetValueOrDefault(MetricNames.Lcp, VitalRating.Unknown) : VitalRating.Unknown)
            .Where(x => x != VitalRating.Unknown)
            .ToList();
        if (rated.Count > 0)
            clauses.Add($"{rated.Count(x => x == VitalRating.Good)} of {Formatting.Plural(rated.Count, "page")} {(rated.Count == 1 ? "has" : "have")} good LCP");

        if (r.Thresholds.Count > 0)
        {
            int failed = r.Thresholds.Count(t => !t.Passed);
            clauses.Add(failed == 0
                ? $"all {Formatting.Plural(r.Thresholds.Count, "threshold")} passed"
                : $"{failed} of {Formatting.Plural(r.Thresholds.Count, "threshold")} failed");
        }

        if (ctx.Pages.Count == 0 && ctx.Endpoints.Count == 0) clauses.Add("no pages or APIs were measured");
        return clauses.Count == 0 ? head + "." : $"{head}: {string.Join("; ", clauses)}.";
    }
}
