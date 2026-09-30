using PerfLens.Analysis;
using PerfLens.Browser;
using PerfLens.Cli.Infrastructure;
using PerfLens.Cli.Pipeline;
using PerfLens.Core.Configuration;
using PerfLens.Core.Utilities;
using PerfLens.Load;
using PerfLens.Reporting;

namespace PerfLens.Cli.Commands;

internal static class RunCommands
{
    private static readonly Dictionary<char, string> Aliases = new() { ['c'] = "config", ['o'] = "output", ['v'] = "verbose" };

    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var a = ParsedArgs.Parse(args, new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "config", "url", "output", "baseline", "label", "iterations", "formats" }, Aliases);

        var path = a.Get("config") ?? a.Positionals.FirstOrDefault()
                   ?? throw new UsageException("perflens run needs a config file: perflens run -c perflens.yaml");
        var config = ConfigLoader.Load(path);

        if (a.Get("url") is { } url) config.Target.BaseUrl = url;
        if (a.GetInt("iterations", 1) is { } it) config.Browser.Iterations = it;
        if (a.Has("headed")) config.Browser.Headless = false;
        if (a.Has("no-api")) config.Api.Enabled = false;
        if (a.Has("no-crawl")) config.Crawl.Enabled = false;
        var formats = ParseFormats(a.Get("formats"));
        var options = new RunOptions
        {
            OutputDirectory = a.Get("output"),
            BaselinePath = a.Get("baseline"),
            Label = a.Get("label"),
            Formats = formats,
        };
        bool verbose = a.Has("verbose"), open = a.Has("open");
        a.EnsureNoUnknown();
        ConfigLoader.Validate(config);

        return await ExecuteAsync(config, options, verbose, open, ct);
    }

    public static async Task<int> QuickAsync(string[] args, CancellationToken ct)
    {
        var a = ParsedArgs.Parse(args, new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "pages", "max-pages", "iterations", "api", "output", "formats", "label" }, Aliases);

        var url = a.Positionals.FirstOrDefault()
                  ?? throw new UsageException("perflens quick needs a URL: perflens quick https://your-app.example.com");
        var config = new PerfLensConfig { Name = $"Quick check of {url}" };
        config.Target.BaseUrl = url;
        config.Browser.Iterations = a.GetInt("iterations", 1) ?? 3;
        config.Browser.Headless = !a.Has("headed");

        var pages = (a.Get("pages") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        config.Crawl.Enabled = a.Has("crawl");
        if (a.GetInt("max-pages", 1) is { } max) config.Crawl.MaxPages = max;
        if (pages.Length == 0 && !config.Crawl.Enabled)
        {
            // A bare URL means "measure that page"; keep its path relative to the origin.
            var uri = new Uri(url);
            config.Target.BaseUrl = uri.GetLeftPart(UriPartial.Authority);
            pages = new[] { uri.PathAndQuery };
        }
        config.Pages = pages.Select(p => new PageConfig { Name = p, Path = p }).ToList();

        var apis = a.GetAll("api");
        if (apis.Count > 0)
        {
            config.Api.Enabled = true;
            config.Api.Endpoints = apis.Select(p => new ApiEndpointConfig { Name = "GET " + p, Url = p }).ToList();
            config.Api.Profile = new LoadProfile { Mode = LoadMode.Closed, Concurrency = 5, DurationSeconds = 10, WarmupSeconds = 2 };
        }

        var options = new RunOptions
        {
            OutputDirectory = a.Get("output"),
            Label = a.Get("label"),
            Formats = ParseFormats(a.Get("formats")),
        };
        bool verbose = a.Has("verbose"), open = a.Has("open");
        a.EnsureNoUnknown();
        ConfigLoader.Validate(config);
        return await ExecuteAsync(config, options, verbose, open, ct);
    }

    private static async Task<int> ExecuteAsync(PerfLensConfig config, RunOptions options, bool verbose, bool open, CancellationToken ct)
    {
        PlatformUtils.ApplyPlaywrightHostOverride();
        var progress = new ConsoleProgressReporter(verbose);
        var pipeline = new RunPipeline(
            new PlaywrightPageBenchmarkRunner(),
            new HttpApiLoadRunner(),
            new AnalysisEngine(),
            ReportService.CreateDefault(),
            progress);

        var result = await pipeline.ExecuteAsync(config, options, ct);
        var analysis = result.Run.Analysis!;

        progress.Stage("Summary");
        foreach (var h in analysis.Highlights) progress.Info(h);
        int failedThresholds = analysis.Thresholds.Count(t => !t.Passed);
        progress.Info($"Thresholds: {analysis.Thresholds.Count - failedThresholds}/{analysis.Thresholds.Count} passed. Findings: {analysis.Findings.Count}.");
        foreach (var w in result.Run.Warnings) progress.Warn(w);

        if (result.HtmlReport is { } html)
        {
            progress.Success($"Report: {html}");
            if (open) Opener.TryOpen(html, progress);
        }

        if (!analysis.Passed)
        {
            if (config.Thresholds.FailOnThresholdBreach)
            {
                progress.Error($"Run FAILED: score {analysis.Score:F0} ({analysis.Grade}), {failedThresholds} threshold(s) failed.");
                return ExitCodes.ThresholdFailed;
            }
            progress.Warn($"Thresholds failed ({failedThresholds}) but thresholds.failOnThresholdBreach is false.");
        }
        else
        {
            progress.Success($"Run PASSED: score {analysis.Score:F0} ({analysis.Grade}).");
        }
        return ExitCodes.Success;
    }

    internal static IReadOnlyList<string>? ParseFormats(string? value) =>
        value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(f => f.ToLowerInvariant()).ToList();
}
