using System.Text.Json.Serialization;

namespace PerfLens.Core.Results;

/// <summary>Everything one run produced. Serialized as results.json and consumed by the report writers.</summary>
public sealed class RunResult
{
    public int SchemaVersion { get; set; } = 1;
    public string RunId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Label { get; set; }
    public string BaseUrl { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public RunEnvironment Environment { get; set; } = new();
    /// <summary>Human-readable key settings (iterations, cache mode, throttling, load profile).</summary>
    public Dictionary<string, string> Settings { get; set; } = new();
    public List<PageResult> Pages { get; set; } = new();
    public CrawlResult? Crawl { get; set; }
    public ApiLoadResult? Api { get; set; }
    public AnalysisResult? Analysis { get; set; }
    /// <summary>Run-level problems that did not abort the run (e.g. login failed).</summary>
    public List<string> Warnings { get; set; } = new();
}

public sealed class RunEnvironment
{
    public string ToolVersion { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string OperatingSystem { get; set; } = "";
    public string DotnetVersion { get; set; } = "";
    public int ProcessorCount { get; set; }
    public string? BrowserName { get; set; }
    public string? BrowserVersion { get; set; }
}

public enum PageSource { Configured, Crawled }

public sealed class PageResult
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public PageSource Source { get; set; }
    public int Depth { get; set; }
    public List<PageIteration> Iterations { get; set; } = new();
    /// <summary>Aggregated stats over measured (non-warm-up, successful) iterations. Filled by the analysis engine.</summary>
    public PageSummary? Summary { get; set; }
    /// <summary>Screenshot path relative to the output directory.</summary>
    public string? ScreenshotPath { get; set; }
    /// <summary>Set when the page could not be measured at all.</summary>
    public string? Error { get; set; }

    [JsonIgnore]
    public IEnumerable<PageIteration> MeasuredIterations => Iterations.Where(i => !i.IsWarmup && i.Success);
}

public sealed class PageIteration
{
    public int Index { get; set; }
    public bool IsWarmup { get; set; }
    public bool ColdCache { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public int? HttpStatus { get; set; }
    public string? FinalUrl { get; set; }
    /// <summary>Wall-clock time of the navigation call until the ready condition was met.</summary>
    public double WallClockMs { get; set; }
    public NavigationTiming Timing { get; set; } = new();
    public WebVitals Vitals { get; set; } = new();
    public ResourceStats Resources { get; set; } = new();
    public List<ResourceEntry> TopResources { get; set; } = new();
    public List<ApiCall> ApiCalls { get; set; } = new();
    public List<string> ConsoleErrors { get; set; } = new();
    public List<string> PageErrors { get; set; } = new();
    public int? DomNodes { get; set; }
    public long? JsHeapUsedBytes { get; set; }
}

/// <summary>W3C Navigation Timing Level 2 phases, all in milliseconds relative to navigation start.</summary>
public sealed class NavigationTiming
{
    public double RedirectMs { get; set; }
    public double DnsMs { get; set; }
    public double ConnectMs { get; set; }
    public double TlsMs { get; set; }
    /// <summary>Request start to first response byte.</summary>
    public double ServerMs { get; set; }
    /// <summary>Time to first byte from navigation start.</summary>
    public double TtfbMs { get; set; }
    /// <summary>Response body download time.</summary>
    public double DownloadMs { get; set; }
    public double DomInteractiveMs { get; set; }
    public double DomContentLoadedMs { get; set; }
    public double LoadEventMs { get; set; }
    public long DocumentTransferBytes { get; set; }
}

public sealed class WebVitals
{
    public double? FcpMs { get; set; }
    public double? LcpMs { get; set; }
    public double? Cls { get; set; }
    /// <summary>Total Blocking Time: sum of (long task duration - 50ms) between FCP and settle end.</summary>
    public double? TbtMs { get; set; }
    public int LongTaskCount { get; set; }
    public string? LcpElement { get; set; }
}

public sealed class ResourceStats
{
    public int TotalRequests { get; set; }
    public long TransferBytes { get; set; }
    public long DecodedBytes { get; set; }
    public int FailedRequests { get; set; }
    public int ThirdPartyRequests { get; set; }
    public int CachedRequests { get; set; }
    /// <summary>Keyed by resource type: document, script, stylesheet, image, font, xhr, fetch, media, other.</summary>
    public Dictionary<string, ResourceTypeStat> ByType { get; set; } = new();
}

public sealed class ResourceTypeStat
{
    public int Count { get; set; }
    public long TransferBytes { get; set; }
}

public sealed class ResourceEntry
{
    public string Url { get; set; } = "";
    public string Type { get; set; } = "";
    public int? Status { get; set; }
    public long TransferBytes { get; set; }
    public long DecodedBytes { get; set; }
    public double DurationMs { get; set; }
    public double StartMs { get; set; }
    public bool FromCache { get; set; }
    public bool ThirdParty { get; set; }
    public string? ContentEncoding { get; set; }
    public string? CacheControl { get; set; }
    public string? ContentType { get; set; }
}

/// <summary>An XHR/fetch call observed while the page loaded.</summary>
public sealed class ApiCall
{
    public string Method { get; set; } = "GET";
    public string Url { get; set; } = "";
    /// <summary>URL with ids replaced (e.g. /api/products/{id}) used to group calls.</summary>
    public string NormalizedUrl { get; set; } = "";
    public int? Status { get; set; }
    public double DurationMs { get; set; }
    public long Bytes { get; set; }
    public bool Failed { get; set; }
}

public sealed class PageSummary
{
    public int MeasuredIterations { get; set; }
    public int FailedIterations { get; set; }
    /// <summary>Keyed by <see cref="MetricNames"/>.</summary>
    public Dictionary<string, MetricStats> Metrics { get; set; } = new();
    /// <summary>Aggregated XHR/fetch calls by method + normalized URL.</summary>
    public List<ApiCallSummary> ApiCalls { get; set; } = new();
    public double Score { get; set; }
}

public sealed class ApiCallSummary
{
    public string Method { get; set; } = "";
    public string NormalizedUrl { get; set; } = "";
    public string SampleUrl { get; set; } = "";
    public int Calls { get; set; }
    public int Failures { get; set; }
    public MetricStats Duration { get; set; } = new();
    public double AvgBytes { get; set; }
}

public sealed class CrawlResult
{
    public List<CrawlEntry> Visited { get; set; } = new();
    public List<CrawlEntry> BrokenLinks { get; set; } = new();
    public int SkippedLinks { get; set; }
    public double DurationMs { get; set; }
}

public sealed class CrawlEntry
{
    public string Url { get; set; } = "";
    public int Depth { get; set; }
    public string? Referrer { get; set; }
    public int? Status { get; set; }
    public string? Error { get; set; }
}

public enum EndpointSource { Configured, Discovered }

public sealed class ApiLoadResult
{
    public List<EndpointLoadResult> Endpoints { get; set; } = new();
}

public sealed class EndpointLoadResult
{
    public string Name { get; set; } = "";
    public string Method { get; set; } = "GET";
    public string Url { get; set; } = "";
    public EndpointSource Source { get; set; }
    /// <summary>E.g. "closed, 10 VUs, 20s" or "open, 50 rps, 20s".</summary>
    public string ProfileDescription { get; set; } = "";
    public long TotalRequests { get; set; }
    public long SuccessfulRequests { get; set; }
    public long FailedRequests { get; set; }
    public double ErrorRatePct { get; set; }
    public double Rps { get; set; }
    public double DurationSeconds { get; set; }
    public long BytesReceived { get; set; }
    /// <summary>Latency over all measured requests (successful and failed).</summary>
    public MetricStats Latency { get; set; } = new();
    public List<HistogramBucket> Histogram { get; set; } = new();
    public List<TimelinePoint> Timeline { get; set; } = new();
    public Dictionary<string, long> StatusCodes { get; set; } = new();
    /// <summary>Non-HTTP failures: timeout, connection_refused, unexpected_status, etc.</summary>
    public Dictionary<string, long> ErrorTypes { get; set; } = new();
    public string? Error { get; set; }
}

public sealed class HistogramBucket
{
    public double LowerMs { get; set; }
    public double UpperMs { get; set; }
    public long Count { get; set; }
}

public sealed class TimelinePoint
{
    /// <summary>Seconds since the measured phase started.</summary>
    public int Second { get; set; }
    public long Requests { get; set; }
    public long Errors { get; set; }
    public double P50Ms { get; set; }
    public double P95Ms { get; set; }
    public double MaxMs { get; set; }
}

public sealed class MetricStats
{
    public int Count { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
    public double Mean { get; set; }
    public double Median { get; set; }
    public double P75 { get; set; }
    public double P90 { get; set; }
    public double P95 { get; set; }
    public double P99 { get; set; }
    public double StdDev { get; set; }
    /// <summary>Coefficient of variation (StdDev / Mean), 0 when Mean is 0.</summary>
    public double CoV { get; set; }
}
