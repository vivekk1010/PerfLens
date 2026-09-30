using PerfLens.Core.Results;
using PerfLens.Core.Statistics;

namespace PerfLens.Reporting.Tests;

internal static class Fixtures
{
    public const string EvilName = "Checkout <script>alert(1)</script>";
    public const string EvilConsole = "Uncaught Error: bad </script><img src=x onerror=alert(2)>";
    public const string CsvTricky = "Search, \"quoted\"\nnext line";

    public static RunResult Minimal() => new()
    {
        RunId = "min-1",
        Name = "Minimal",
        BaseUrl = "https://example.test",
        StartedAt = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero),
        FinishedAt = new DateTimeOffset(2026, 10, 1, 10, 0, 5, TimeSpan.Zero),
        Pages =
        {
            new PageResult { Name = "Home", Url = "https://example.test/", Iterations = { Iteration(0, false, 300, 900, null) } },
            new PageResult { Name = "Empty", Url = "https://example.test/empty" },
        },
    };

    public static RunResult Rich()
    {
        var home = Page("Home", "https://shop.test/", PageSource.Configured, 0,
            [Iteration(0, true, 900, 1900, 2600), Iteration(1, false, 210, 800, 1300), Iteration(2, false, 230, 850, 1350), Iteration(3, false, 250, 820, 1400)]);
        home.ScreenshotPath = "screenshots/home.png";

        var evil = Page(EvilName, "https://shop.test/checkout?q=<b>", PageSource.Crawled, 1,
            [Iteration(0, true, 1200, 2500, null), Iteration(1, false, 1500, 3200, null), Iteration(2, false, 1700, 3500, null)]);
        evil.Iterations[1].ConsoleErrors.Add(EvilConsole);
        evil.Iterations[2].ConsoleErrors.Add(EvilConsole);
        evil.Iterations[2].PageErrors.Add("TypeError: x is undefined");
        evil.Iterations.Add(new PageIteration { Index = 3, Success = false, Error = "Timeout 30000ms exceeded" });

        var broken = new PageResult
        {
            Name = "Broken page", Url = "https://shop.test/broken", Source = PageSource.Configured,
            Error = "net::ERR_CONNECTION_REFUSED <at> navigation",
        };

        var api = new ApiLoadResult
        {
            Endpoints =
            {
                Endpoint("Products API", "GET", "https://shop.test/api/products", ok: true),
                new EndpointLoadResult
                {
                    Name = "Orders API", Method = "POST", Url = "https://shop.test/api/orders", Source = EndpointSource.Discovered,
                    ProfileDescription = "closed, 5 VUs, 10s", Error = "Connection refused <b>boom</b>",
                    TotalRequests = 12, FailedRequests = 12, ErrorRatePct = 100,
                    ErrorTypes = { ["connection_refused"] = 12 },
                },
            },
        };

        var run = new RunResult
        {
            RunId = "run-2026-10-01",
            Name = "Shop benchmark",
            Label = "release-1.2",
            BaseUrl = "https://shop.test",
            StartedAt = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero),
            FinishedAt = new DateTimeOffset(2026, 10, 1, 10, 3, 12, TimeSpan.Zero),
            Environment = new RunEnvironment
            {
                ToolVersion = "1.0.0", MachineName = "ci-box", OperatingSystem = "Ubuntu 20.04", DotnetVersion = "10.0.0",
                ProcessorCount = 8, BrowserName = "chromium", BrowserVersion = "140.0",
            },
            Settings = { ["Iterations"] = "3 (+1 warm-up)", ["Cache mode"] = "cold", ["Throttling"] = "none", ["Page statistic"] = "p75" },
            Pages = { home, evil, broken },
            Api = api,
            Crawl = new CrawlResult
            {
                Visited =
                {
                    new CrawlEntry { Url = "https://shop.test/", Depth = 0, Status = 200 },
                    new CrawlEntry { Url = "https://shop.test/checkout", Depth = 1, Status = 200, Referrer = "https://shop.test/" },
                },
                BrokenLinks = { new CrawlEntry { Url = "https://shop.test/missing", Depth = 1, Status = 404, Referrer = "https://shop.test/" } },
                SkippedLinks = 3, DurationMs = 4200,
            },
            Warnings = { "Login step 2 took longer than expected <warn>" },
        };

        run.Analysis = new AnalysisResult
        {
            Score = 67.4, Grade = "C", Passed = false, BaselineRunId = "run-2026-09-30",
            Highlights = { "Checkout is the slowest page (LCP n/a, TTFB 1.6 s)", "1 broken link found" },
            Thresholds =
            {
                new ThresholdResult { Scope = "page", Target = "Home", Metric = MetricNames.Ttfb, Statistic = "p75", Actual = 240, Limit = 800, Passed = true },
                new ThresholdResult { Scope = "page", Target = EvilName, Metric = MetricNames.Ttfb, Statistic = "p75", Actual = 1650, Limit = 800, Passed = false },
                new ThresholdResult { Scope = "api", Target = "Products API", Metric = "p95_ms", Statistic = "p95", Actual = 180, Limit = 800, Passed = true },
                new ThresholdResult { Scope = "api", Target = "Orders API", Metric = "error_rate_pct", Statistic = "value", Actual = 100, Limit = 1, Passed = false },
            },
            Findings =
            {
                new Finding { RuleId = "PAGE_SLOW_TTFB", Severity = Severity.High, Category = FindingCategory.Performance, Target = EvilName,
                    Title = "Slow server response", Detail = CsvTricky, Recommendation = "Cache the page, profile the backend.", Metric = MetricNames.Ttfb, Value = 1650 },
                new Finding { RuleId = "PAGE_CONSOLE_ERRORS", Severity = Severity.Medium, Category = FindingCategory.Reliability, Target = EvilName,
                    Title = "Console errors", Detail = EvilConsole, Recommendation = "Fix the script errors." },
                new Finding { RuleId = "API_ERRORS", Severity = Severity.Critical, Category = FindingCategory.Reliability, Target = "Orders API",
                    Title = "Endpoint failing", Detail = "100% errors", Recommendation = "Check the service." },
                new Finding { RuleId = "BROKEN_LINK", Severity = Severity.Low, Category = FindingCategory.BestPractice, Target = "https://shop.test/missing",
                    Title = "Broken link", Detail = "404", Recommendation = "Remove or fix the link." },
                new Finding { RuleId = "INFO", Severity = Severity.Info, Category = FindingCategory.Network, Target = "Home",
                    Title = "HTTP/2 in use", Detail = "Good", Recommendation = "" },
            },
            VitalRatings = { ["Home"] = new() { [MetricNames.Lcp] = VitalRating.Good, [MetricNames.Ttfb] = VitalRating.Good } },
            Regressions =
            {
                new RegressionEntry { Scope = "page", Target = "Home", Metric = MetricNames.Lcp, Baseline = 1100, Current = 1350, DeltaPct = 22.7, Regressed = true },
                new RegressionEntry { Scope = "page", Target = "Home", Metric = MetricNames.Ttfb, Baseline = 300, Current = 240, DeltaPct = -20, Improved = true },
                new RegressionEntry { Scope = "api", Target = "Products API", Metric = "p95_ms", Baseline = 175, Current = 180, DeltaPct = 2.9 },
            },
        };

        // Summaries like the analysis engine would produce (the broken page has none).
        foreach (var p in new[] { home, evil }) p.Summary = Summarize(p);
        home.Summary!.Score = 92;
        evil.Summary!.Score = 38;
        return run;
    }

    public static PageIteration Iteration(int index, bool warmup, double ttfb, double fcp, double? lcp)
    {
        var it = new PageIteration
        {
            Index = index, IsWarmup = warmup, ColdCache = index == 0, Success = true, HttpStatus = 200, FinalUrl = "https://shop.test/",
            WallClockMs = fcp + 900,
            Timing = new NavigationTiming
            {
                DnsMs = 12, ConnectMs = 20, TlsMs = 25, ServerMs = ttfb - 70, TtfbMs = ttfb, DownloadMs = 30,
                DomInteractiveMs = fcp - 50, DomContentLoadedMs = fcp + 100, LoadEventMs = fcp + 700, DocumentTransferBytes = 25_000,
            },
            Vitals = new WebVitals { FcpMs = fcp, LcpMs = lcp, Cls = 0.034 + index * 0.05, TbtMs = 120 + index * 90, LongTaskCount = 2, LcpElement = lcp is null ? null : "img.hero<x>" },
            Resources = new ResourceStats
            {
                TotalRequests = 40 + index, TransferBytes = 900_000, DecodedBytes = 2_400_000, FailedRequests = index % 2, ThirdPartyRequests = 5, CachedRequests = 3,
                ByType =
                {
                    ["document"] = new ResourceTypeStat { Count = 1, TransferBytes = 25_000 },
                    ["script"] = new ResourceTypeStat { Count = 20, TransferBytes = 500_000 },
                    ["image"] = new ResourceTypeStat { Count = 12, TransferBytes = 300_000 },
                    ["font"] = new ResourceTypeStat { Count = 2, TransferBytes = 75_000 },
                },
            },
            TopResources =
            {
                new ResourceEntry { Url = "https://cdn.test/app.js?v=1&x=" + new string('a', 120), Type = "script", Status = 200, TransferBytes = 320_000,
                    DecodedBytes = 1_000_000, DurationMs = 210, ContentEncoding = "br", CacheControl = "max-age=31536000, immutable", ThirdParty = true },
                new ResourceEntry { Url = "javascript:alert(3)", Type = "other", Status = 404, TransferBytes = 100, DurationMs = 5 },
            },
            ApiCalls =
            {
                new ApiCall { Method = "GET", Url = "https://shop.test/api/cart/42", NormalizedUrl = "https://shop.test/api/cart/{id}", Status = 200, DurationMs = 80 + index * 10, Bytes = 1200 },
                new ApiCall { Method = "POST", Url = "https://shop.test/api/track", NormalizedUrl = "https://shop.test/api/track", Status = 500, DurationMs = 40, Failed = true },
            },
            DomNodes = 1500, JsHeapUsedBytes = 24L * 1024 * 1024,
        };
        return it;
    }

    private static PageResult Page(string name, string url, PageSource source, int depth, PageIteration[] its)
    {
        var p = new PageResult { Name = name, Url = url, Source = source, Depth = depth };
        p.Iterations.AddRange(its);
        return p;
    }

    private static PageSummary Summarize(PageResult p)
    {
        var measured = p.MeasuredIterations.ToList();
        var s = new PageSummary { MeasuredIterations = measured.Count, FailedIterations = p.Iterations.Count(i => !i.Success) };
        void Add(string key, Func<PageIteration, double?> f)
        {
            var values = measured.Select(f).Where(v => v.HasValue).Select(v => v!.Value).ToList();
            if (values.Count > 0) s.Metrics[key] = Stats.Compute(values);
        }
        Add(MetricNames.Ttfb, i => i.Timing.TtfbMs);
        Add(MetricNames.Dns, i => i.Timing.DnsMs);
        Add(MetricNames.Connect, i => i.Timing.ConnectMs);
        Add(MetricNames.Tls, i => i.Timing.TlsMs);
        Add(MetricNames.Server, i => i.Timing.ServerMs);
        Add(MetricNames.Download, i => i.Timing.DownloadMs);
        Add(MetricNames.DomContentLoaded, i => i.Timing.DomContentLoadedMs);
        Add(MetricNames.Load, i => i.Timing.LoadEventMs);
        Add(MetricNames.Fcp, i => i.Vitals.FcpMs);
        Add(MetricNames.Lcp, i => i.Vitals.LcpMs);
        Add(MetricNames.Cls, i => i.Vitals.Cls);
        Add(MetricNames.Tbt, i => i.Vitals.TbtMs);
        Add(MetricNames.Requests, i => i.Resources.TotalRequests);
        Add(MetricNames.TransferKb, i => i.Resources.TransferBytes / 1024.0);
        Add(MetricNames.ConsoleErrors, i => i.ConsoleErrors.Count);
        s.ApiCalls.Add(new ApiCallSummary
        {
            Method = "GET", NormalizedUrl = "https://shop.test/api/cart/{id}", SampleUrl = "https://shop.test/api/cart/42", Calls = measured.Count,
            Duration = Stats.Compute(measured.SelectMany(i => i.ApiCalls).Where(c => c.Method == "GET").Select(c => c.DurationMs)), AvgBytes = 1200,
        });
        return s;
    }

    private static EndpointLoadResult Endpoint(string name, string method, string url, bool ok)
    {
        var rnd = new Random(7);
        var samples = Enumerable.Range(0, 500).Select(_ => 40 + rnd.NextDouble() * 160).ToList();
        return new EndpointLoadResult
        {
            Name = name, Method = method, Url = url, Source = EndpointSource.Configured, ProfileDescription = "closed, 10 VUs, 20s",
            TotalRequests = 500, SuccessfulRequests = 495, FailedRequests = 5, ErrorRatePct = 1.0, Rps = 25, DurationSeconds = 20, BytesReceived = 1_500_000,
            Latency = Stats.Compute(samples),
            Histogram = Stats.Histogram(samples, 15),
            Timeline = Enumerable.Range(0, 20).Select(s => new TimelinePoint { Second = s, Requests = 25, Errors = s % 7 == 0 ? 1 : 0, P50Ms = 100 + s, P95Ms = 180 + s * 2, MaxMs = 220 }).ToList(),
            StatusCodes = { ["200"] = 495, ["503"] = 5 },
            ErrorTypes = { ["unexpected_status"] = 5 },
        };
    }

    /// <summary>Smallest valid PNG (1x1 transparent).</summary>
    public static readonly byte[] Png1x1 = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    public static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "perflens-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A temp output dir with the Home screenshot on disk.</summary>
    public static string OutputDirWithScreenshot()
    {
        var dir = TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "screenshots"));
        File.WriteAllBytes(Path.Combine(dir, "screenshots", "home.png"), Png1x1);
        return dir;
    }
}
