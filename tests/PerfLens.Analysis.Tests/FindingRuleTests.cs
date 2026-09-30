using PerfLens.Core.Results;

namespace PerfLens.Analysis.Tests;

public class FindingRuleTests
{
    private const string B = RunBuilder.Base;

    private static List<Finding> Findings(RunResult run) => TestConfig.Analyze(run).Findings;

    private static Finding Single(RunResult run, string ruleId) => Assert.Single(Findings(run), f => f.RuleId == ruleId);

    private static RunResult PageRun(Action<PageIteration> mutate, int iterations = 3) =>
        new RunBuilder().Page("/p", p => p.Iterations(iterations, mutate)).Build();

    private static ResourceEntry Res(string path, string type, long kb, string? enc = "br", string? cache = "max-age=31536000", string? ct = null, int status = 200) =>
        new() { Url = path.StartsWith("http") ? path : B + path, Type = type, TransferBytes = kb * 1024, DecodedBytes = kb * 1024, ContentEncoding = enc, CacheControl = cache, ContentType = ct, Status = status };

    [Fact]
    public void CleanRun_ProducesNoFindings()
    {
        var run = new RunBuilder()
            .Page("/a", p => p.Warmup().Iterations(5))
            .Page("/b", p => p.Iterations(5))
            .Endpoint("search")
            .Build();

        Assert.Empty(Findings(run));
    }

    [Fact]
    public void PageUnavailable_OnErrorOrAllFailed()
    {
        var run = new RunBuilder().Page("/err", p => p.Error("boom")).Page("/fail", p => p.Failed().Failed()).Build();

        var f = Findings(run).Where(x => x.RuleId == "PAGE_UNAVAILABLE").ToList();

        Assert.Equal(2, f.Count);
        Assert.All(f, x => Assert.Equal(Severity.Critical, x.Severity));
        Assert.Contains("boom", f.Single(x => x.Target == "/err").Detail);
    }

    [Fact]
    public void PageIterationFailures_WhenSomeFailed()
    {
        var run = new RunBuilder().Page("/p", p => p.Iterations(3).Failed("HTTP 502")).Build();

        var f = Single(run, "PAGE_ITERATION_FAILURES");

        Assert.Equal(Severity.High, f.Severity);
        Assert.Contains("1 of 4", f.Title);
        Assert.Contains("HTTP 502", f.Detail);
    }

    [Theory]
    [InlineData("LCP", 5000, "VITAL_POOR_LCP", Severity.High)]
    [InlineData("LCP", 3000, "VITAL_NI_LCP", Severity.Medium)]
    [InlineData("FCP", 3500, "VITAL_POOR_FCP", Severity.High)]
    [InlineData("FCP", 2000, "VITAL_NI_FCP", Severity.Medium)]
    [InlineData("CLS", 0.3, "VITAL_POOR_CLS", Severity.High)]
    [InlineData("CLS", 0.15, "VITAL_NI_CLS", Severity.Medium)]
    [InlineData("TBT", 700, "VITAL_POOR_TBT", Severity.High)]
    [InlineData("TBT", 300, "VITAL_NI_TBT", Severity.Medium)]
    [InlineData("TTFB", 2000, "VITAL_POOR_TTFB", Severity.High)]
    [InlineData("TTFB", 1000, "VITAL_NI_TTFB", Severity.Medium)]
    public void VitalRules(string vital, double value, string ruleId, Severity severity)
    {
        var run = PageRun(i =>
        {
            switch (vital)
            {
                case "LCP": i.Vitals.LcpMs = value; break;
                case "FCP": i.Vitals.FcpMs = value; i.Vitals.LcpMs = value; break;
                case "CLS": i.Vitals.Cls = value; break;
                case "TBT": i.Vitals.TbtMs = value; break;
                case "TTFB": i.Timing.TtfbMs = value; break;
            }
        });

        var f = Single(run, ruleId);

        Assert.Equal(severity, f.Severity);
        Assert.Equal(FindingCategory.Performance, f.Category);
        Assert.Equal(value, f.Value);
    }

    [Theory]
    [InlineData(700, Severity.Medium)]
    [InlineData(2000, Severity.High)]
    public void SlowServer(double serverMs, Severity severity)
    {
        var f = Single(PageRun(i => { i.Timing.ServerMs = serverMs; i.Timing.TtfbMs = serverMs + 50; }), "PAGE_SLOW_SERVER");
        Assert.Equal(severity, f.Severity);
        Assert.Contains("backend", f.Detail);
    }

    [Theory]
    [InlineData(2500, Severity.Medium)]
    [InlineData(6000, Severity.High)]
    public void HeavyPage_ListsLargestResources(long kb, Severity severity)
    {
        var f = Single(PageRun(i =>
        {
            i.Resources.TransferBytes = kb * 1024;
            i.TopResources.Add(Res("/hero.jpg", "image", 250));
        }), "PAGE_HEAVY");

        Assert.Equal(severity, f.Severity);
        Assert.Contains("/hero.jpg", f.Detail);
    }

    [Theory]
    [InlineData(100, Severity.Low)]
    [InlineData(200, Severity.Medium)]
    public void TooManyRequests(int requests, Severity severity) =>
        Assert.Equal(severity, Single(PageRun(i => i.Resources.TotalRequests = requests), "PAGE_TOO_MANY_REQUESTS").Severity);

    [Fact]
    public void LargeImage_ListsUpToFive()
    {
        var f = Single(PageRun(i =>
        {
            for (int n = 0; n < 7; n++) i.TopResources.Add(Res($"/img{n}.jpg", "image", 400 + n));
        }), "LARGE_IMAGE");

        Assert.Equal(Severity.Medium, f.Severity);
        Assert.Equal(5, f.Detail.Split(".jpg").Length - 1);
    }

    [Fact]
    public void LargeImage_SingleModerateImageIsLow() =>
        Assert.Equal(Severity.Low, Single(PageRun(i => i.TopResources.Add(Res("/big.png", "image", 350))), "LARGE_IMAGE").Severity);

    [Fact]
    public void LargeScript_TotalOrSingle()
    {
        var total = PageRun(i => i.Resources.ByType["script"] = new ResourceTypeStat { Count = 10, TransferBytes = 600 * 1024 });
        var single = PageRun(i => i.TopResources.Add(Res("/vendor.js", "script", 300)));

        Assert.Contains("600 KB", Single(total, "LARGE_SCRIPT").Detail);
        Assert.Contains("/vendor.js", Single(single, "LARGE_SCRIPT").Detail);
    }

    [Fact]
    public void UncompressedText_FiresForTextNotForImagesCacheOrSmall()
    {
        var f = Single(PageRun(i =>
        {
            i.TopResources.Add(Res("/raw.js", "script", 50, enc: null));
            i.TopResources.Add(Res("/data", "fetch", 40, enc: "", ct: "application/json; charset=utf-8"));
            i.TopResources.Add(Res("/tiny.css", "stylesheet", 5, enc: null));
            i.TopResources.Add(Res("/photo.jpg", "image", 200, enc: null));
            var cached = Res("/cached.js", "script", 50, enc: null);
            cached.FromCache = true;
            i.TopResources.Add(cached);
            i.TopResources.Add(Res("/gz.css", "stylesheet", 50, enc: "gzip"));
        }), "UNCOMPRESSED_TEXT");

        Assert.Equal(FindingCategory.Network, f.Category);
        Assert.Equal(Severity.Medium, f.Severity);
        Assert.Contains("/raw.js", f.Detail);
        Assert.Contains("/data", f.Detail);
        Assert.DoesNotContain("/tiny.css", f.Detail);
        Assert.DoesNotContain("/photo.jpg", f.Detail);
        Assert.DoesNotContain("/cached.js", f.Detail);
        Assert.DoesNotContain("/gz.css", f.Detail);
    }

    [Fact]
    public void MissingCacheHeaders_SameOriginStaticOnly()
    {
        var f = Single(PageRun(i =>
        {
            i.TopResources.Add(Res("/a.js", "script", 5, cache: null));
            i.TopResources.Add(Res("/b.css", "stylesheet", 5, cache: "no-store"));
            i.TopResources.Add(Res("/c.woff2", "font", 5, cache: "public, max-age=0"));
            i.TopResources.Add(Res("https://cdn.other.test/d.js", "script", 5, cache: null));
            i.TopResources.Add(Res("/api/x", "fetch", 5, cache: null, ct: "application/json"));
        }), "MISSING_CACHE_HEADERS");

        Assert.Equal(FindingCategory.BestPractice, f.Category);
        Assert.Equal(3, f.Value);
        Assert.DoesNotContain("d.js", f.Detail);
    }

    [Fact]
    public void ConsoleErrors_SamplesUpToThree()
    {
        var f = Single(PageRun(i =>
        {
            i.ConsoleErrors.AddRange(["e1", "e2", "e3", "e4"]);
            i.PageErrors.Add("TypeError: x is undefined");
        }), "CONSOLE_ERRORS");

        Assert.Equal(FindingCategory.Reliability, f.Category);
        Assert.Equal(5, f.Value);
        Assert.Equal(3, f.Detail.Split(" | ").Length);
        Assert.Contains("TypeError", f.Detail);
    }

    [Theory]
    [InlineData(2, Severity.Medium)]
    [InlineData(6, Severity.High)]
    public void FailedRequests(int failed, Severity severity)
    {
        var f = Single(PageRun(i =>
        {
            i.Resources.FailedRequests = failed;
            i.TopResources.Add(Res("/missing.png", "image", 0, status: 404));
        }), "FAILED_REQUESTS");

        Assert.Equal(severity, f.Severity);
        Assert.Contains("/missing.png (404)", f.Detail);
    }

    [Fact]
    public void HighVariance_NeedsThreeSamplesAndCovAbove03()
    {
        var noisy = new RunBuilder().Page("/p", p => p.Values((i, v) => i.Vitals.LcpMs = v, 500, 1500, 2400)).Build();
        var tooFew = new RunBuilder().Page("/p", p => p.Values((i, v) => i.Vitals.LcpMs = v, 500, 2400)).Build();

        Assert.Equal(FindingCategory.Stability, Single(noisy, "HIGH_VARIANCE").Category);
        Assert.DoesNotContain(Findings(tooFew), f => f.RuleId == "HIGH_VARIANCE");
    }

    [Fact]
    public void PageApiCalls_SlowFailingAndNPlusOne()
    {
        var run = PageRun(i =>
        {
            i.ApiCalls.Add(new ApiCall { Method = "GET", Url = B + "/api/report", NormalizedUrl = B + "/api/report", DurationMs = 1500 });
            i.ApiCalls.Add(new ApiCall { Method = "POST", Url = B + "/api/save", NormalizedUrl = B + "/api/save", DurationMs = 50, Failed = true, Status = 500 });
            for (int n = 0; n < 6; n++)
                i.ApiCalls.Add(new ApiCall { Method = "GET", Url = $"{B}/api/items/{n}", NormalizedUrl = B + "/api/items/{id}", DurationMs = 20 });
        });

        var f = Findings(run);

        Assert.Contains("/api/report", Assert.Single(f, x => x.RuleId == "SLOW_API_CALL").Title);
        Assert.Equal(Severity.High, Assert.Single(f, x => x.RuleId == "API_CALL_FAILURES").Severity);
        var n1 = Assert.Single(f, x => x.RuleId == "N_PLUS_ONE_API");
        Assert.Equal(6, n1.Value);
        Assert.Contains("/api/items/{id}", n1.Title);
    }

    [Fact]
    public void BrokenLink_IncludesReferrer()
    {
        var run = new RunBuilder().BrokenLink(B + "/gone", B + "/home").BrokenLink(B + "/err", B + "/about", 500).Build();

        var f = Findings(run).Where(x => x.RuleId == "BROKEN_LINK").ToList();

        Assert.Equal(2, f.Count);
        Assert.Contains("linked from /home", f.Single(x => x.Target.EndsWith("/gone")).Detail);
        Assert.All(f, x => Assert.Equal(Severity.High, x.Severity));
    }

    [Theory]
    [InlineData(2, Severity.High)]
    [InlineData(8, Severity.Critical)]
    public void ApiErrorRate(double pct, Severity severity) =>
        Assert.Equal(severity, Single(new RunBuilder().Endpoint("a", e => e.ErrorRatePct = pct).Build(), "API_ERROR_RATE").Severity);

    [Fact]
    public void ApiSlowP95_AgainstThreshold() =>
        Assert.Equal(Severity.High, Single(new RunBuilder().Endpoint("a", e => { e.Latency.P95 = 2000; e.Latency.P99 = 2100; }).Build(), "API_SLOW_P95").Severity);

    [Fact]
    public void ApiTailLatency()
    {
        Single(new RunBuilder().Endpoint("a", e => { e.Latency.Median = 50; e.Latency.P99 = 300; }).Build(), "API_TAIL_LATENCY");
        Assert.DoesNotContain(Findings(new RunBuilder().Endpoint("a", e => { e.Latency.Median = 20; e.Latency.P99 = 150; }).Build()), f => f.RuleId == "API_TAIL_LATENCY");
    }

    [Fact]
    public void ApiThroughputLow_WhenMinRpsNotMet()
    {
        var run = new RunBuilder().Endpoint("a", e => e.Rps = 10).Build();
        var cfg = TestConfig.Default();
        cfg.Thresholds.Api.MinRps = 20;

        Assert.Single(TestConfig.Analyze(run, cfg).Findings, f => f.RuleId == "API_THROUGHPUT_LOW");
        Assert.DoesNotContain(TestConfig.Analyze(run).Findings, f => f.RuleId == "API_THROUGHPUT_LOW");
    }

    [Fact]
    public void ApiUnreachable_SuppressesOtherApiRules()
    {
        var run = new RunBuilder()
            .Endpoint("err", e => e.Error = "connection refused")
            .Endpoint("zero", e => { e.SuccessfulRequests = 0; e.FailedRequests = 1000; e.ErrorRatePct = 100; e.StatusCodes["503"] = 1000; })
            .Build();

        var f = Findings(run);

        Assert.Equal(2, f.Count(x => x.RuleId == "API_UNREACHABLE"));
        Assert.All(f, x => Assert.Equal("API_UNREACHABLE", x.RuleId));
        Assert.Contains("HTTP 503: 1000", f.Single(x => x.Target == "zero").Detail);
    }

    [Fact]
    public void ApiTimeouts() =>
        Assert.Equal(Severity.High, Single(new RunBuilder().Endpoint("a", e => e.ErrorTypes["timeout"] = 50).Build(), "API_TIMEOUTS").Severity);

    [Fact]
    public void ApiDegradationUnderLoad()
    {
        static List<TimelinePoint> Timeline(params double[] p95) =>
            p95.Select((v, s) => new TimelinePoint { Second = s, Requests = 50, P95Ms = v }).ToList();

        var degraded = new RunBuilder().Endpoint("a", e => e.Timeline = Timeline(80, 80, 90, 120, 200, 250, 300, 350, 400)).Build();
        var steady = new RunBuilder().Endpoint("a", e => e.Timeline = Timeline(80, 85, 80, 90, 85, 80, 90, 85, 80)).Build();
        var tooShort = new RunBuilder().Endpoint("a", e => e.Timeline = Timeline(80, 400, 400)).Build();

        Assert.Equal(FindingCategory.Stability, Single(degraded, "API_DEGRADATION_UNDER_LOAD").Category);
        Assert.DoesNotContain(Findings(steady), f => f.RuleId == "API_DEGRADATION_UNDER_LOAD");
        Assert.DoesNotContain(Findings(tooShort), f => f.RuleId == "API_DEGRADATION_UNDER_LOAD");
    }

    [Fact]
    public void Findings_SortedBySeverityDescThenTarget()
    {
        var run = new RunBuilder()
            .Page("/b", p => p.Iterations(3, i => i.Vitals.LcpMs = 3000))
            .Page("/a", p => p.Iterations(3, i => i.Vitals.LcpMs = 3000))
            .Page("/down", p => p.Error("x"))
            .Build();

        var f = Findings(run);

        Assert.Equal("PAGE_UNAVAILABLE", f[0].RuleId);
        var ni = f.Where(x => x.RuleId == "VITAL_NI_LCP").Select(x => x.Target).ToList();
        Assert.Equal(["/a", "/b"], ni);
        for (int n = 1; n < f.Count; n++) Assert.True(f[n - 1].Severity >= f[n].Severity);
    }
}
