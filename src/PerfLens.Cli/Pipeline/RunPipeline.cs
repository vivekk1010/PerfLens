using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using PerfLens.Cli.Infrastructure;
using PerfLens.Core.Abstractions;
using PerfLens.Core.Configuration;
using PerfLens.Core.Results;
using PerfLens.Load;
using PerfLens.Reporting;

namespace PerfLens.Cli.Pipeline;

public sealed class RunOptions
{
    public string? OutputDirectory { get; init; }
    public string? BaselinePath { get; init; }
    public string? Label { get; init; }
    public IReadOnlyList<string>? Formats { get; init; }
}

public sealed class PipelineResult
{
    public required RunResult Run { get; init; }
    public required string OutputDirectory { get; init; }
    public required IReadOnlyList<string> Files { get; init; }
    public string? HtmlReport => Files.FirstOrDefault(f => f.EndsWith(".html", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Orchestrates one run: browser pages, API load, analysis, reports.</summary>
public sealed class RunPipeline(
    IPageBenchmarkRunner browser,
    IApiLoadRunner load,
    IAnalysisEngine analysis,
    ReportService reports,
    ConsoleProgressReporter progress)
{
    public async Task<PipelineResult> ExecuteAsync(PerfLensConfig config, RunOptions options, CancellationToken ct)
    {
        var started = DateTimeOffset.Now;
        var outputDir = ResolveOutputDirectory(config, options, started);
        Directory.CreateDirectory(outputDir);

        var run = new RunResult
        {
            RunId = $"{started:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}",
            Name = config.Name,
            Label = options.Label ?? config.Output.RunLabel,
            BaseUrl = config.Target.BaseUrl,
            StartedAt = started,
            Environment = DescribeEnvironment(),
            Settings = DescribeSettings(config),
        };

        progress.Stage($"PerfLens run '{config.Name}' against {config.Target.BaseUrl}");
        progress.Info($"Output: {outputDir}");

        BrowserRunOutput? browserOutput = null;
        if (config.Pages.Count > 0 || config.Crawl.Enabled)
        {
            progress.Stage("Measuring pages in the browser");
            var sw = Stopwatch.StartNew();
            browserOutput = await browser.RunAsync(config, outputDir, progress, ct);
            run.Pages = browserOutput.Pages;
            run.Crawl = browserOutput.Crawl;
            run.Environment.BrowserName = browserOutput.BrowserName;
            run.Environment.BrowserVersion = browserOutput.BrowserVersion;
            run.Warnings.AddRange(browserOutput.Warnings);
            progress.Info($"Measured {run.Pages.Count} page(s) in {sw.Elapsed.TotalSeconds:F1} s.");
        }

        if (config.Api.Enabled)
        {
            var requests = BuildApiRequests(config, browserOutput, run.Pages);
            if (requests.Count == 0)
            {
                run.Warnings.Add("API load testing was enabled but no endpoints were configured or discovered.");
                progress.Warn(run.Warnings[^1]);
            }
            else
            {
                progress.Stage($"Load testing {requests.Count} API endpoint(s)");
                run.Api = new ApiLoadResult();
                foreach (var request in requests)
                {
                    ct.ThrowIfCancellationRequested();
                    run.Api.Endpoints.Add(await load.RunAsync(request, progress, ct));
                }
            }
        }

        RunResult? baseline = await LoadBaselineAsync(options.BaselinePath ?? config.Output.BaselinePath, run, ct);

        progress.Stage("Analyzing results");
        run.FinishedAt = DateTimeOffset.Now;
        run.Analysis = analysis.Analyze(run, config, baseline);

        progress.Stage("Writing reports");
        var formats = options.Formats is { Count: > 0 } ? options.Formats : config.Output.Formats;
        var files = await reports.WriteAsync(run, outputDir, formats, ct);
        foreach (var f in files) progress.Info(Path.GetRelativePath(Directory.GetCurrentDirectory(), f));

        return new PipelineResult { Run = run, OutputDirectory = outputDir, Files = files };
    }

    private List<ApiLoadRequest> BuildApiRequests(PerfLensConfig config, BrowserRunOutput? browserOutput, List<PageResult> pages)
    {
        var cookies = browserOutput?.SessionCookies;
        var requests = config.Api.Endpoints
            .Select(e => LoadRequestFactory.Build(config, e, EndpointSource.Configured, cookies))
            .ToList();

        if (config.Api.UseDiscoveredEndpoints)
        {
            var discovered = DiscoveredEndpointSelector.Select(config, pages);
            if (discovered.Count > 0)
                progress.Info($"Discovered {discovered.Count} GET API endpoint(s) from page traffic: {string.Join(", ", discovered.Select(d => d.Name))}");
            requests.AddRange(discovered.Select(e => LoadRequestFactory.Build(config, e, EndpointSource.Discovered, cookies)));
        }
        return requests;
    }

    private async Task<RunResult?> LoadBaselineAsync(string? path, RunResult run, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var baseline = await ResultsStore.LoadAsync(path, ct);
            progress.Info($"Comparing against baseline {baseline.RunId} ({baseline.StartedAt:yyyy-MM-dd HH:mm}).");
            return baseline;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.Warnings.Add($"Baseline '{path}' could not be loaded: {ex.Message}");
            progress.Warn(run.Warnings[^1]);
            return null;
        }
    }

    private static string ResolveOutputDirectory(PerfLensConfig config, RunOptions options, DateTimeOffset started)
    {
        var root = options.OutputDirectory ?? config.Output.Directory;
        var dir = config.Output.TimestampedSubfolder && options.OutputDirectory is null
            ? Path.Combine(root, started.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture))
            : root;
        return Path.GetFullPath(dir);
    }

    public static string ToolVersion =>
        typeof(RunPipeline).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(RunPipeline).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    private static RunEnvironment DescribeEnvironment() => new()
    {
        ToolVersion = ToolVersion,
        MachineName = Environment.MachineName,
        OperatingSystem = RuntimeInformation.OSDescription,
        DotnetVersion = RuntimeInformation.FrameworkDescription,
        ProcessorCount = Environment.ProcessorCount,
    };

    internal static Dictionary<string, string> DescribeSettings(PerfLensConfig c)
    {
        var b = c.Browser;
        var s = new Dictionary<string, string>
        {
            ["Base URL"] = c.Target.BaseUrl,
            ["Configured pages"] = c.Pages.Count.ToString(CultureInfo.InvariantCulture),
            ["Crawl"] = c.Crawl.Enabled ? $"on (depth {c.Crawl.MaxDepth}, max {c.Crawl.MaxPages} pages)" : "off",
            ["Authentication"] = c.Auth.Type.ToString(),
            ["Browser"] = $"{b.Engine}{(b.Headless ? " (headless)" : " (headed)")}",
            ["Iterations"] = $"{b.Iterations} measured + {b.WarmupIterations} warm-up per page",
            ["Cache mode"] = b.CacheMode == CacheMode.Cold ? "Cold (fresh context per iteration)" : "Warm (context reused per page)",
            ["Viewport / device"] = b.Device ?? $"{b.ViewportWidth}x{b.ViewportHeight}",
            ["Network throttling"] = b.NetworkProfile.ToString(),
            ["CPU throttling"] = b.CpuThrottlingRate <= 1 ? "none" : $"{b.CpuThrottlingRate.ToString(CultureInfo.InvariantCulture)}x",
            ["Ready condition"] = $"{b.WaitUntil} + {b.SettleMs} ms settle",
            ["Threshold statistic"] = c.Thresholds.PageStatistic,
        };
        if (c.Api.Enabled)
        {
            var p = c.Api.Profile;
            s["API load profile"] = p.Mode == LoadMode.Closed
                ? $"Closed, {p.Concurrency} VUs, {p.DurationSeconds}s (+{p.WarmupSeconds}s warm-up)"
                : $"Open, {p.RatePerSecond.ToString(CultureInfo.InvariantCulture)} rps, {p.DurationSeconds}s (+{p.WarmupSeconds}s warm-up)";
            s["API endpoints"] = $"{c.Api.Endpoints.Count} configured{(c.Api.UseDiscoveredEndpoints ? $" + up to {c.Api.MaxDiscoveredEndpoints} discovered" : "")}";
        }
        return s;
    }
}
