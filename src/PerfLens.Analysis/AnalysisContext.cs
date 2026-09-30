using PerfLens.Core.Configuration;
using PerfLens.Core.Results;
using PerfLens.Core.Utilities;

namespace PerfLens.Analysis;

/// <summary>Shared, read-mostly state passed to the analysis steps and finding rules.</summary>
public sealed class AnalysisContext
{
    private readonly Dictionary<PageResult, IReadOnlyList<ResourceEntry>> _resources = new(ReferenceEqualityComparer.Instance);

    public AnalysisContext(RunResult run, PerfLensConfig config, AnalysisResult result)
    {
        Run = run;
        Config = config;
        Result = result;
        Statistic = StatReader.Normalize(config.Thresholds?.PageStatistic);
        TolerancePct = Math.Max(0, config.Output?.RegressionTolerancePct ?? 10);
    }

    public RunResult Run { get; }
    public PerfLensConfig Config { get; }
    /// <summary>The result being built; earlier steps (ratings, regressions) are visible to later ones.</summary>
    public AnalysisResult Result { get; }
    /// <summary>Validated page statistic (lower-case), p75 when unset or invalid.</summary>
    public string Statistic { get; }
    public double TolerancePct { get; }

    public IReadOnlyList<PageResult> Pages => (Run.Pages ?? []).Where(p => p is not null).ToList();
    public IReadOnlyList<EndpointLoadResult> Endpoints => (Run.Api?.Endpoints ?? []).Where(e => e is not null).ToList();

    /// <summary>Page has an error or no measured iterations.</summary>
    public static bool IsUnavailable(PageResult page) =>
        page.Error is not null || (page.Summary?.MeasuredIterations ?? PageSummarizer.Measured(page).Count) == 0;

    /// <summary>Endpoint has an error or produced no successful request.</summary>
    public static bool IsUnreachable(EndpointLoadResult endpoint) =>
        endpoint.Error is not null || endpoint.SuccessfulRequests <= 0;

    /// <summary>Reads <paramref name="statistic"/> (default: the configured page statistic) of a page metric, null when absent.</summary>
    public double? Stat(PageResult page, string metric, string? statistic = null) =>
        page.Summary?.Metrics is { } m && m.TryGetValue(metric, out var s) && s.Count > 0
            ? StatReader.Get(s, statistic ?? Statistic)
            : null;

    public PageThresholds EffectivePageThresholds(PageResult page)
    {
        var global = Config.Thresholds?.Page ?? new PageThresholds();
        var local = (Config.Pages ?? []).FirstOrDefault(p => p is not null && string.Equals(p.Name, page.Name, StringComparison.OrdinalIgnoreCase))?.Thresholds;
        if (local is null) return global;
        return new PageThresholds
        {
            TtfbMs = local.TtfbMs ?? global.TtfbMs,
            FcpMs = local.FcpMs ?? global.FcpMs,
            LcpMs = local.LcpMs ?? global.LcpMs,
            Cls = local.Cls ?? global.Cls,
            TbtMs = local.TbtMs ?? global.TbtMs,
            LoadMs = local.LoadMs ?? global.LoadMs,
            PageWeightKb = local.PageWeightKb ?? global.PageWeightKb,
            RequestCount = local.RequestCount ?? global.RequestCount,
            MaxConsoleErrors = local.MaxConsoleErrors ?? global.MaxConsoleErrors,
        };
    }

    public ApiThresholds EffectiveApiThresholds(EndpointLoadResult endpoint)
    {
        var global = Config.Thresholds?.Api ?? new ApiThresholds();
        var local = (Config.Api?.Endpoints ?? []).FirstOrDefault(e => e is not null && string.Equals(e.Name, endpoint.Name, StringComparison.OrdinalIgnoreCase))?.Thresholds;
        if (local is null) return global;
        return new ApiThresholds
        {
            P50Ms = local.P50Ms ?? global.P50Ms,
            P95Ms = local.P95Ms ?? global.P95Ms,
            P99Ms = local.P99Ms ?? global.P99Ms,
            ErrorRatePct = local.ErrorRatePct ?? global.ErrorRatePct,
            MinRps = local.MinRps ?? global.MinRps,
        };
    }

    /// <summary>Distinct resources (by URL, largest observation kept) across the page's measured iterations, largest first.</summary>
    public IReadOnlyList<ResourceEntry> Resources(PageResult page)
    {
        if (_resources.TryGetValue(page, out var cached)) return cached;
        var list = PageSummarizer.Measured(page)
            .SelectMany(i => i.TopResources ?? [])
            .Where(r => r is not null && !string.IsNullOrEmpty(r.Url))
            .GroupBy(r => r.Url, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(Size).First())
            .OrderByDescending(Size)
            .ThenBy(r => r.Url, StringComparer.Ordinal)
            .ToList();
        _resources[page] = list;
        return list;
    }

    /// <summary>Transfer size in bytes, falling back to decoded size when the transfer size is unknown (e.g. cached).</summary>
    public static long Size(ResourceEntry r) => r.TransferBytes > 0 ? r.TransferBytes : r.DecodedBytes;

    public bool IsSameOrigin(PageResult page, string url) =>
        UrlUtils.SameOrigin(url, page.Url) || (!string.IsNullOrEmpty(Run.BaseUrl) && UrlUtils.SameOrigin(url, Run.BaseUrl));

    public static string ShortUrl(string url)
    {
        var redacted = UrlUtils.RedactSecrets(url);
        if (!Uri.TryCreate(redacted, UriKind.Absolute, out var u)) return redacted;
        var path = u.GetComponents(UriComponents.PathAndQuery, UriFormat.Unescaped);
        return path.Length > 90 ? path[..87] + "..." : path;
    }
}
