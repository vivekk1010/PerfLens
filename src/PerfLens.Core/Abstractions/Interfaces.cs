using PerfLens.Core.Configuration;
using PerfLens.Core.Results;

namespace PerfLens.Core.Abstractions;

public interface IProgressReporter
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
    /// <summary>Verbose details, shown only with --verbose.</summary>
    void Debug(string message);
}

public sealed class BrowserRunOutput
{
    public List<PageResult> Pages { get; set; } = new();
    public CrawlResult? Crawl { get; set; }
    public string? BrowserName { get; set; }
    public string? BrowserVersion { get; set; }
    /// <summary>Cookies of the authenticated session (name=value), shared with the API load tester when enabled.</summary>
    public Dictionary<string, string> SessionCookies { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

/// <summary>Discovers pages (configured + crawl), handles auth and measures each page in a real browser.</summary>
public interface IPageBenchmarkRunner
{
    /// <param name="outputDirectory">Run output directory; screenshots go to outputDirectory/screenshots.</param>
    Task<BrowserRunOutput> RunAsync(PerfLensConfig config, string outputDirectory, IProgressReporter progress, CancellationToken ct);
}

public sealed class ApiLoadRequest
{
    public ApiEndpointConfig Endpoint { get; set; } = new();
    public EndpointSource Source { get; set; }
    /// <summary>Absolute URL (already resolved against BaseUrl).</summary>
    public string AbsoluteUrl { get; set; } = "";
    public LoadProfile Profile { get; set; } = new();
    /// <summary>Headers merged from Target.Headers, Auth.Headers and the endpoint.</summary>
    public Dictionary<string, string> Headers { get; set; } = new();
    public Dictionary<string, string> Cookies { get; set; } = new();
    public bool IgnoreHttpsErrors { get; set; }
}

/// <summary>Drives HTTP load against API endpoints and measures latency/throughput/errors.</summary>
public interface IApiLoadRunner
{
    Task<EndpointLoadResult> RunAsync(ApiLoadRequest request, IProgressReporter progress, CancellationToken ct);
}

/// <summary>Fills page summaries, evaluates thresholds, produces findings, score and baseline regressions.</summary>
public interface IAnalysisEngine
{
    AnalysisResult Analyze(RunResult run, PerfLensConfig config, RunResult? baseline);
}

public interface IReportWriter
{
    /// <summary>Format key: html, json, md, csv.</summary>
    string Format { get; }
    /// <returns>Paths of files written.</returns>
    Task<IReadOnlyList<string>> WriteAsync(RunResult run, string outputDirectory, CancellationToken ct);
}
