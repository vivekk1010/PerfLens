using System.Collections.Concurrent;
using PerfLens.Core.Abstractions;
using PerfLens.Core.Configuration;
using PerfLens.Core.Results;

namespace PerfLens.Browser.Tests;

internal sealed class RecordingProgress : IProgressReporter
{
    public ConcurrentQueue<string> Messages { get; } = new();
    public void Info(string message) => Messages.Enqueue("INFO " + message);
    public void Warn(string message) => Messages.Enqueue("WARN " + message);
    public void Error(string message) => Messages.Enqueue("ERROR " + message);
    public void Debug(string message) => Messages.Enqueue("DEBUG " + message);
}

[Collection(BrowserCollection.Name)]
public sealed class PlaywrightPageBenchmarkRunnerTests(TestSite site) : IDisposable
{
    private readonly string _outputDirectory = Directory.CreateTempSubdirectory("perflens-browser-tests-").FullName;
    private readonly RecordingProgress _progress = new();

    public void Dispose()
    {
        try { Directory.Delete(_outputDirectory, recursive: true); }
        catch (IOException) { }
    }

    private PerfLensConfig Config(params PageConfig[] pages) => new()
    {
        Target = new TargetConfig { BaseUrl = site.BaseUrl },
        Pages = pages.ToList(),
        Browser = new BrowserConfig
        {
            Iterations = 1,
            WarmupIterations = 0,
            SettleMs = 300,
            NavigationTimeoutMs = 10000,
            CaptureScreenshots = false,
        },
    };

    private static PageConfig Page(string path) => new() { Name = path, Path = path };

    private Task<BrowserRunOutput> RunAsync(PerfLensConfig config) =>
        new PlaywrightPageBenchmarkRunner().RunAsync(config, _outputDirectory, _progress, CancellationToken.None);

    [Fact]
    public async Task MeasuresNavigationTimingResourcesAndApiCalls()
    {
        var output = await RunAsync(Config(Page("/a")));

        Assert.Equal("chromium", output.BrowserName);
        Assert.False(string.IsNullOrEmpty(output.BrowserVersion));
        var page = Assert.Single(output.Pages);
        Assert.Null(page.Error);
        Assert.Null(page.Summary);
        var it = Assert.Single(page.Iterations);
        Assert.True(it.Success, it.Error);
        Assert.Equal(200, it.HttpStatus);
        Assert.True(it.WallClockMs > 0);
        Assert.True(it.Timing.TtfbMs > 0);
        Assert.True(it.Timing.DomContentLoadedMs > 0);
        Assert.True(it.Timing.LoadEventMs > 0);
        Assert.True(it.Vitals.FcpMs > 0);
        Assert.True(it.Vitals.LcpMs > 0);
        Assert.NotNull(it.Vitals.Cls);
        Assert.True(it.DomNodes > 0);

        Assert.True(it.Resources.TotalRequests >= 4, $"requests: {it.Resources.TotalRequests}");
        Assert.True(it.Resources.TransferBytes > 0);
        Assert.Equal(0, it.Resources.FailedRequests);
        Assert.Equal(0, it.Resources.ThirdPartyRequests);
        foreach (var type in new[] { "document", "script", "stylesheet", "image", "fetch" })
            Assert.True(it.Resources.ByType.ContainsKey(type), $"missing type {type}: {string.Join(",", it.Resources.ByType.Keys)}");
        Assert.NotEmpty(it.TopResources);
        Assert.Contains(it.TopResources, r => r.Url.EndsWith("/static/site.css") && r.CacheControl == "public, max-age=3600" && r.Status == 200);

        var api = Assert.Single(it.ApiCalls);
        Assert.EndsWith("/api/data", api.Url);
        Assert.EndsWith("/api/data", api.NormalizedUrl);
        Assert.Equal(200, api.Status);
        Assert.Equal("GET", api.Method);
        Assert.False(api.Failed);
        Assert.True(api.DurationMs > 0);
        Assert.True(api.Bytes > 0);
    }

    [Fact]
    public async Task CapturesLongTasksAndConsoleErrors()
    {
        var config = Config(Page("/b"));
        config.Browser.SettleMs = 800;
        var output = await RunAsync(config);

        var it = Assert.Single(Assert.Single(output.Pages).Iterations);
        Assert.True(it.Success, it.Error);
        Assert.True(it.Vitals.LongTaskCount >= 1, "expected a long task");
        Assert.True(it.Vitals.TbtMs > 50, $"TBT {it.Vitals.TbtMs}");
        Assert.Contains(it.ConsoleErrors, e => e.Contains("boom from page b"));
    }

    [Fact]
    public async Task CrawlDiscoversPagesAndBrokenLinks()
    {
        var config = Config();
        config.Crawl = new CrawlConfig { Enabled = true, MaxDepth = 2, MaxPages = 20 };

        var output = await RunAsync(config);

        var crawl = Assert.IsType<CrawlResult>(output.Crawl);
        var visited = crawl.Visited.Select(v => new Uri(v.Url).AbsolutePath).ToList();
        Assert.Contains("/", visited);
        Assert.Contains("/a", visited);
        Assert.Contains("/b", visited);
        Assert.DoesNotContain("/logout", visited);
        Assert.DoesNotContain(crawl.Visited, v => v.Url.Contains("external.example.invalid") || v.Url.Contains(".pdf"));
        Assert.Equal(visited.Count, visited.Distinct().Count());

        var broken = Assert.Single(crawl.BrokenLinks);
        Assert.EndsWith("/missing", broken.Url);
        Assert.Equal(404, broken.Status);
        Assert.Equal(site.BaseUrl + "/", broken.Referrer);
        Assert.Equal(1, broken.Depth);
        Assert.True(crawl.DurationMs > 0);

        var names = output.Pages.Select(p => p.Name).ToList();
        Assert.Contains("/", names);
        Assert.Contains("/a", names);
        Assert.Contains("/b", names);
        Assert.DoesNotContain("/missing", names);
        Assert.All(output.Pages, p => Assert.Equal(PageSource.Crawled, p.Source));
        Assert.Equal(1, output.Pages.Single(p => p.Name == "/a").Depth);
    }

    [Fact]
    public async Task CrawlRespectsMaxPagesAndSkipsConfiguredPages()
    {
        var config = Config(Page("/a"));
        config.Browser.Iterations = 1;
        config.Crawl = new CrawlConfig { Enabled = true, MaxDepth = 1, MaxPages = 2 };

        var output = await RunAsync(config);

        var crawl = output.Crawl!;
        Assert.Equal(2, crawl.Visited.Count);
        Assert.True(crawl.SkippedLinks >= 1);
        Assert.Single(output.Pages, p => p.Url.EndsWith("/a"));
        Assert.Equal(PageSource.Configured, output.Pages[0].Source);
    }

    [Fact]
    public async Task LoginReachesSecurePageAndExposesSessionCookies()
    {
        var config = Config(Page("/secure"));
        config.Auth = new AuthConfig
        {
            Type = AuthType.Login,
            Login = new LoginConfig
            {
                Url = "/login",
                Steps =
                [
                    new LoginStep { Action = LoginAction.Fill, Selector = "#username", Value = TestSite.Username },
                    new LoginStep { Action = LoginAction.Fill, Selector = "#password", Value = TestSite.Password },
                    new LoginStep { Action = LoginAction.Click, Selector = "#submit" },
                ],
                SuccessUrlPattern = "/secure$",
                SuccessSelector = "#welcome",
                TimeoutMs = 10000,
            },
        };

        var output = await RunAsync(config);

        Assert.Empty(output.Warnings);
        Assert.Equal("token-123", output.SessionCookies[TestSite.SessionCookie]);
        var it = Assert.Single(Assert.Single(output.Pages).Iterations);
        Assert.True(it.Success, it.Error);
        Assert.EndsWith("/secure", it.FinalUrl);
        Assert.DoesNotContain(_progress.Messages, m => m.Contains(TestSite.Password));
    }

    [Fact]
    public async Task FailedLoginWarnsWithoutLeakingSecretsAndContinues()
    {
        var config = Config(Page("/secure"));
        config.Auth = new AuthConfig
        {
            Type = AuthType.Login,
            Login = new LoginConfig
            {
                Steps =
                [
                    new LoginStep { Action = LoginAction.Fill, Selector = "#username", Value = TestSite.Username },
                    new LoginStep { Action = LoginAction.Fill, Selector = "#password", Value = "wrong-password-xyz" },
                    new LoginStep { Action = LoginAction.Click, Selector = "#submit" },
                ],
                SuccessSelector = "#welcome",
                TimeoutMs = 2000,
            },
        };

        var output = await RunAsync(config);

        Assert.Contains(output.Warnings, w => w.Contains("Login failed"));
        Assert.DoesNotContain(output.Warnings, w => w.Contains("wrong-password-xyz"));
        Assert.DoesNotContain(_progress.Messages, m => m.Contains("wrong-password-xyz"));
        Assert.Empty(output.SessionCookies);
        var it = Assert.Single(Assert.Single(output.Pages).Iterations);
        Assert.EndsWith("/login", it.FinalUrl);
    }

    [Fact]
    public async Task ColdCacheUsesFreshContextEveryIteration()
    {
        var config = Config(Page("/a"));
        config.Browser.WarmupIterations = 1;
        config.Browser.Iterations = 2;
        config.Browser.SettleMs = 100;
        config.Browser.CacheMode = CacheMode.Cold;

        var output = await RunAsync(config);

        var iterations = Assert.Single(output.Pages).Iterations;
        Assert.Equal(3, iterations.Count);
        Assert.Equal([true, false, false], iterations.Select(i => i.IsWarmup));
        Assert.All(iterations, i => Assert.True(i.ColdCache));
        Assert.All(iterations, i => Assert.True(i.Success, i.Error));
        Assert.Equal([1, 2, 3], iterations.Select(i => i.Index));
    }

    [Fact]
    public async Task WarmCacheReusesContextAndServesFromCache()
    {
        var config = Config(Page("/a"));
        config.Browser.WarmupIterations = 1;
        config.Browser.Iterations = 2;
        config.Browser.SettleMs = 100;
        config.Browser.CacheMode = CacheMode.Warm;

        var output = await RunAsync(config);

        var iterations = Assert.Single(output.Pages).Iterations;
        Assert.Equal([true, false, false], iterations.Select(i => i.ColdCache));
        Assert.All(iterations, i => Assert.True(i.Success, i.Error));
        Assert.Equal(0, iterations[0].Resources.CachedRequests);
        Assert.True(iterations[2].Resources.CachedRequests >= 1, "expected cached static assets on a warm load");
    }

    [Fact]
    public async Task UnreachablePageRecordsErrorWithoutThrowing()
    {
        var config = Config(new PageConfig { Name = "down", Path = "http://127.0.0.1:1/" }, Page("/a"));
        config.Browser.Iterations = 2;

        var output = await RunAsync(config);

        Assert.Equal(2, output.Pages.Count);
        var down = output.Pages[0];
        Assert.Equal(2, down.Iterations.Count);
        Assert.All(down.Iterations, i =>
        {
            Assert.False(i.Success);
            Assert.False(string.IsNullOrEmpty(i.Error));
        });
        Assert.NotNull(down.Error);
        Assert.Null(output.Pages[1].Error);
        Assert.Contains(_progress.Messages, m => m.StartsWith("WARN [1/2] down"));
    }

    [Fact]
    public async Task NotFoundPageIsUnsuccessfulWithStatus()
    {
        var output = await RunAsync(Config(Page("/missing")));

        var it = Assert.Single(Assert.Single(output.Pages).Iterations);
        Assert.False(it.Success);
        Assert.Equal(404, it.HttpStatus);
        Assert.Equal("HTTP 404", it.Error);
    }

    [Fact]
    public async Task WritesScreenshotForMeasuredPage()
    {
        var config = Config(new PageConfig { Name = "Page A", Path = "/a" });
        config.Browser.CaptureScreenshots = true;

        var output = await RunAsync(config);

        var page = Assert.Single(output.Pages);
        Assert.Equal("screenshots/page-a.png", page.ScreenshotPath);
        var file = Path.Combine(_outputDirectory, "screenshots", "page-a.png");
        Assert.True(File.Exists(file));
        var header = File.ReadAllBytes(file).Take(4).ToArray();
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, header);
    }

    [Fact]
    public async Task AppliesChromiumThrottling()
    {
        var config = Config(Page("/a"));
        config.Browser.NetworkProfile = NetworkProfile.Custom;
        config.Browser.CustomNetwork = new CustomNetwork { LatencyMs = 300, DownloadKbps = 5000, UploadKbps = 5000 };
        config.Browser.CpuThrottlingRate = 2;

        var output = await RunAsync(config);

        var it = Assert.Single(Assert.Single(output.Pages).Iterations);
        Assert.True(it.Success, it.Error);
        // Chromium applies emulated latency to sub-resources and the document body rather than the navigation TTFB.
        Assert.True(it.Timing.LoadEventMs >= 500, $"load {it.Timing.LoadEventMs} should include emulated latency");
        Assert.True(Assert.Single(it.ApiCalls).DurationMs >= 250, "API call should include emulated latency");
    }

    [Fact]
    public async Task UnknownDeviceWarnsAndFallsBack()
    {
        var config = Config(Page("/a"));
        config.Browser.Device = "No Such Phone 9000";

        var output = await RunAsync(config);

        Assert.Contains(output.Warnings, w => w.Contains("Unknown device"));
        Assert.True(Assert.Single(Assert.Single(output.Pages).Iterations).Success);
    }

    [Fact]
    public async Task CancellationStopsTheRun()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PlaywrightPageBenchmarkRunner().RunAsync(Config(Page("/a")), _outputDirectory, _progress, cts.Token));
    }

    [Fact]
    public async Task CancellationMidRunStopsPromptly()
    {
        var config = Config(Page("/a"), Page("/b"));
        config.Browser.Iterations = 50;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PlaywrightPageBenchmarkRunner().RunAsync(config, _outputDirectory, _progress, cts.Token));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
    }
}
