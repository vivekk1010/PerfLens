using System.Text.Json;
using PerfLens.Cli;
using PerfLens.Core.Results;
using PerfLens.Core.Serialization;

namespace PerfLens.E2E.Tests;

/// <summary>Runs the real CLI pipeline (Playwright Chromium + HTTP load + analysis + reports) against DemoShop.</summary>
[Collection(DemoShopCollection.Name)]
public sealed class PipelineEndToEndTests(DemoShopFixture shop) : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("perflens-e2e-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { }
    }

    private string WriteConfig(string yaml)
    {
        var path = Path.Combine(_work, $"config-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, yaml);
        return path;
    }

    private string FullConfig() => WriteConfig($$"""
        name: DemoShop E2E
        target:
          baseUrl: {{shop.BaseUrl}}
        auth:
          type: Login
          login:
            url: /login
            steps:
              - { action: Fill, selector: "#username", value: demo }
              - { action: Fill, selector: "#password", value: "${DEMOSHOP_PASSWORD:-perflens-demo}" }
              - { action: Click, selector: "#login-btn" }
              - { action: WaitForUrl, value: ".*/dashboard.*" }
            successSelector: "#dashboard-title"
        pages:
          - { name: Home, path: / }
          - { name: Products, path: /products }
          - { name: Slow, path: /slow }
          - { name: Heavy, path: /heavy }
          - { name: Jank, path: /jank }
          - { name: Errors, path: /errors }
          - { name: Dashboard, path: /dashboard, waitForSelector: "#dashboard-title" }
        crawl:
          enabled: true
          maxDepth: 1
          maxPages: 25
        browser:
          iterations: 2
          warmupIterations: 0
          settleMs: 500
        api:
          enabled: true
          useDiscoveredEndpoints: true
          maxDiscoveredEndpoints: 2
          profile: { mode: Closed, concurrency: 4, durationSeconds: 2, warmupSeconds: 0 }
          endpoints:
            - { name: Products API, url: /api/products }
            - { name: Orders API, url: /api/orders }
            - { name: Flaky API, url: /api/flaky, profile: { mode: Open, ratePerSecond: 40, durationSeconds: 2, warmupSeconds: 0 } }
        output:
          formats: [html, json, md, csv]
        """);

    [Fact]
    public async Task Full_run_measures_pages_crawls_load_tests_and_writes_reports()
    {
        var output = Path.Combine(_work, "full");
        int exit = await CliApp.DispatchAsync(new[] { "run", "-c", FullConfig(), "-o", output }, CancellationToken.None);

        // DemoShop has deliberate problems, so thresholds are expected to fail (exit 1), never crash (3).
        Assert.True(exit is ExitCodes.Success or ExitCodes.ThresholdFailed, $"exit code {exit}");

        foreach (var file in new[] { "report.html", "results.json", "summary.md", "pages.csv", "iterations.csv", "api.csv", "findings.csv", "thresholds.csv" })
            Assert.True(File.Exists(Path.Combine(output, file)), $"{file} missing");

        var run = JsonSerializer.Deserialize<RunResult>(await File.ReadAllTextAsync(Path.Combine(output, "results.json")), JsonDefaults.Results)!;
        Assert.DoesNotContain(run.Warnings, w => w.Contains("login", StringComparison.OrdinalIgnoreCase));

        var byName = run.Pages.ToDictionary(p => p.Name);
        foreach (var name in new[] { "Home", "Products", "Slow", "Heavy", "Jank", "Errors", "Dashboard" })
        {
            Assert.True(byName.ContainsKey(name), $"page {name} missing");
            Assert.NotNull(byName[name].Summary);
            Assert.Equal(2, byName[name].Summary!.MeasuredIterations);
            Assert.True(byName[name].Summary!.Metrics[MetricNames.Ttfb].Median > 0, $"{name} TTFB");
        }

        // Authenticated page really rendered the dashboard (not the login redirect).
        Assert.Contains(byName["Dashboard"].Iterations, i => i.FinalUrl?.Contains("/dashboard") == true);
        // Slow page: server time dominated by the ~700 ms delay.
        Assert.True(byName["Slow"].Summary!.Metrics[MetricNames.Ttfb].Median >= 600);
        // Products page fetches its data from the API.
        Assert.Contains(byName["Products"].Summary!.ApiCalls, c => c.NormalizedUrl.EndsWith("/api/products"));
        // Crawl discovered extra pages and the broken link.
        Assert.NotNull(run.Crawl);
        Assert.Contains(run.Crawl!.BrokenLinks, b => b.Url.Contains("/missing-page"));
        Assert.Contains(run.Pages, p => p.Source == PageSource.Crawled);

        // API load: configured endpoints, the session cookie reached /api/orders, flaky errors detected.
        Assert.NotNull(run.Api);
        var api = run.Api!.Endpoints.ToDictionary(e => e.Name);
        Assert.True(api["Products API"].SuccessfulRequests > 0);
        Assert.Equal(0, api["Orders API"].FailedRequests);
        Assert.True(api["Flaky API"].FailedRequests > 0);
        Assert.Contains(run.Api.Endpoints, e => e.Source == EndpointSource.Discovered);

        // Analysis caught the deliberate issues.
        var rules = run.Analysis!.Findings.Select(f => f.RuleId).ToHashSet();
        Assert.Contains("BROKEN_LINK", rules);
        Assert.Contains("CONSOLE_ERRORS", rules);
        Assert.Contains("API_ERROR_RATE", rules);
        Assert.Contains(run.Analysis.Findings, f => f.Target == "Slow" && f.RuleId is "PAGE_SLOW_SERVER" or "VITAL_POOR_TTFB" or "VITAL_NI_TTFB");
        Assert.Contains(run.Analysis.Findings, f => f.Target == "Heavy");
        Assert.InRange(run.Analysis.Score, 0, 100);
        Assert.NotEmpty(run.Analysis.Highlights);

        var html = await File.ReadAllTextAsync(Path.Combine(output, "report.html"));
        Assert.Contains("perflens-data", html);
        Assert.Contains("Dashboard", html);
        Assert.DoesNotContain("<script src=\"http", html);
        Assert.True(Directory.GetFiles(Path.Combine(output, "screenshots"), "*.png").Length >= 7);

        // Baseline comparison: second run against the first results.json.
        var second = Path.Combine(_work, "second");
        int exit2 = await CliApp.DispatchAsync(new[]
        {
            "run", "-c", FullConfig(), "-o", second, "--baseline", Path.Combine(output, "results.json"),
            "--no-crawl", "--no-api", "--label", "rerun",
        }, CancellationToken.None);
        Assert.True(exit2 is ExitCodes.Success or ExitCodes.ThresholdFailed, $"exit code {exit2}");
        var run2 = JsonSerializer.Deserialize<RunResult>(await File.ReadAllTextAsync(Path.Combine(second, "results.json")), JsonDefaults.Results)!;
        Assert.Equal(run.RunId, run2.Analysis!.BaselineRunId);
        Assert.NotEmpty(run2.Analysis.Regressions);
        Assert.Equal("rerun", run2.Label);
        Assert.Null(run2.Api);

        // Re-render from saved results.
        var rerender = Path.Combine(_work, "rerender");
        Assert.Equal(ExitCodes.Success, await CliApp.DispatchAsync(new[] { "report", Path.Combine(second, "results.json"), "-o", rerender }, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(rerender, "report.html")));
    }

    [Fact]
    public async Task Quick_command_measures_a_single_url()
    {
        var output = Path.Combine(_work, "quick");
        int exit = await CliApp.DispatchAsync(new[] { "quick", shop.BaseUrl + "/about", "--iterations", "1", "-o", output, "--api", "/api/products" }, CancellationToken.None);
        Assert.True(exit is ExitCodes.Success or ExitCodes.ThresholdFailed, $"exit code {exit}");
        var run = JsonSerializer.Deserialize<RunResult>(await File.ReadAllTextAsync(Path.Combine(output, "results.json")), JsonDefaults.Results)!;
        var page = Assert.Single(run.Pages);
        Assert.Equal("/about", page.Name);
        Assert.True(page.Iterations.Single(i => !i.IsWarmup).Success);
        Assert.Single(run.Api!.Endpoints);
    }

    [Fact]
    public async Task Shipped_quick_config_validates_and_runs()
    {
        var config = Path.Combine(shop.RepoRoot, "configs", "demoshop-quick.json");
        Assert.Equal(ExitCodes.Success, await CliApp.DispatchAsync(new[] { "validate", "-c", config }, CancellationToken.None));
        var output = Path.Combine(_work, "shipped");
        int exit = await CliApp.DispatchAsync(new[] { "run", "-c", config, "--url", shop.BaseUrl, "-o", output }, CancellationToken.None);
        Assert.True(exit is ExitCodes.Success or ExitCodes.ThresholdFailed, $"exit code {exit}");
        Assert.True(File.Exists(Path.Combine(output, "report.html")));
    }
}
