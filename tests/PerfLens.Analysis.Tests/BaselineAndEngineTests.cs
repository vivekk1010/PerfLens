using PerfLens.Core.Results;

namespace PerfLens.Analysis.Tests;

public class BaselineTests
{
    private static RunResult PageRun(string id, double lcp, double cls = 0.01, int requests = 20) =>
        new RunBuilder().Id(id).Page("/p", p => p.Iterations(3, i => { i.Vitals.LcpMs = lcp; i.Vitals.Cls = cls; i.Resources.TotalRequests = requests; })).Build();

    private static RegressionEntry Entry(AnalysisResult r, string metric) => Assert.Single(r.Regressions, x => x.Metric == metric);

    [Fact]
    public void Regression_WhenWorseThanToleranceAndMeaningful()
    {
        var r = TestConfig.Analyze(PageRun("cur", 1300), baseline: PageRun("base", 1000));

        Assert.Equal("base", r.BaselineRunId);
        var lcp = Entry(r, MetricNames.Lcp);
        Assert.Equal(30, lcp.DeltaPct, 1);
        Assert.True(lcp.Regressed);
        Assert.False(lcp.Improved);
        var finding = Assert.Single(r.Findings, f => f.RuleId == "REGRESSION");
        Assert.Equal(Severity.High, finding.Severity);
        Assert.Equal(FindingCategory.Regression, finding.Category);
    }

    [Fact]
    public void Regression_WithinToleranceIsNeither_AndBelow2xToleranceIsMedium()
    {
        var within = TestConfig.Analyze(PageRun("cur", 1080), baseline: PageRun("base", 1000));
        Assert.False(Entry(within, MetricNames.Lcp).Regressed);
        Assert.False(Entry(within, MetricNames.Lcp).Improved);

        var medium = TestConfig.Analyze(PageRun("cur", 1150), baseline: PageRun("base", 1000));
        Assert.Equal(Severity.Medium, Assert.Single(medium.Findings, f => f.RuleId == "REGRESSION").Severity);
    }

    [Fact]
    public void Regression_AbsoluteChangeGuard()
    {
        var r = TestConfig.Analyze(PageRun("cur", 60, cls: 0.03, requests: 4), baseline: PageRun("base", 40, cls: 0.015, requests: 2));

        Assert.Equal(50, Entry(r, MetricNames.Lcp).DeltaPct, 1);
        Assert.False(Entry(r, MetricNames.Lcp).Regressed);
        Assert.False(Entry(r, MetricNames.Cls).Regressed);
        Assert.False(Entry(r, MetricNames.Requests).Regressed);
        Assert.DoesNotContain(r.Findings, f => f.RuleId == "REGRESSION");
    }

    [Fact]
    public void Improvement_WhenBetterThanTolerance()
    {
        var r = TestConfig.Analyze(PageRun("cur", 700), baseline: PageRun("base", 1000));

        Assert.True(Entry(r, MetricNames.Lcp).Improved);
        Assert.False(Entry(r, MetricNames.Lcp).Regressed);
    }

    [Fact]
    public void ToleranceIsConfigurable()
    {
        var cfg = TestConfig.Default();
        cfg.Output.RegressionTolerancePct = 50;

        var r = TestConfig.Analyze(PageRun("cur", 1300), cfg, PageRun("base", 1000));

        Assert.False(Entry(r, MetricNames.Lcp).Regressed);
    }

    [Fact]
    public void BaselineWithoutSummaries_IsSummarized_AndZeroBaselineSkipped()
    {
        var baseline = PageRun("base", 1000);
        Assert.Null(baseline.Pages[0].Summary);
        foreach (var i in baseline.Pages[0].Iterations) i.Vitals.TbtMs = 0;

        var r = TestConfig.Analyze(PageRun("cur", 1500), baseline: baseline);

        Assert.True(Entry(r, MetricNames.Lcp).Regressed);
        Assert.DoesNotContain(r.Regressions, x => x.Metric == MetricNames.Tbt);
    }

    [Fact]
    public void Api_RpsLowerIsWorse_LatencyHigherIsWorse()
    {
        var baseline = new RunBuilder().Id("base").Endpoint("search", e => e.Rps = 100).Build();
        var slower = new RunBuilder().Endpoint("search", e => { e.Rps = 70; e.Latency.P95 = 200; }).Build();
        var faster = new RunBuilder().Endpoint("search", e => e.Rps = 130).Build();

        var r1 = TestConfig.Analyze(slower, baseline: baseline);
        Assert.True(Entry(r1, "rps").Regressed);
        Assert.Equal(-30, Entry(r1, "rps").DeltaPct, 1);
        Assert.True(Entry(r1, "p95_ms").Regressed);

        var r2 = TestConfig.Analyze(faster, baseline: baseline);
        Assert.True(Entry(r2, "rps").Improved);
        Assert.False(Entry(r2, "rps").Regressed);
    }

    [Fact]
    public void UnmatchedPagesAndEndpointsAreIgnored()
    {
        var baseline = new RunBuilder().Id("b").Page("/old", p => p.Iterations(2)).Endpoint("old").Build();
        var r = TestConfig.Analyze(new RunBuilder().Page("/new", p => p.Iterations(2)).Endpoint("new").Build(), baseline: baseline);

        Assert.Empty(r.Regressions);
        Assert.Equal("b", r.BaselineRunId);
    }
}

public class EngineTests
{
    [Fact]
    public void EmptyRun_DoesNotThrow_AndHasHighlights()
    {
        var run = new RunResult();

        var r = new AnalysisEngine().Analyze(run, new(), null);

        Assert.Same(r, run.Analysis);
        Assert.Equal(0, r.Score);
        Assert.Equal("F", r.Grade);
        Assert.True(r.Passed);
        Assert.Empty(r.Findings);
        Assert.NotEmpty(r.Highlights);
    }

    [Fact]
    public void PartialData_DoesNotThrow()
    {
        var run = new RunBuilder()
            .Page("/nulls", p => p.Iteration(i => { i.Vitals = null!; i.Resources = null!; i.TopResources = null!; i.ApiCalls = null!; i.ConsoleErrors = null!; }))
            .Page("/none")
            .Endpoint("e", e => { e.Latency = null!; e.Timeline = null!; e.ErrorTypes = null!; e.StatusCodes = null!; })
            .Build();
        run.Crawl = new CrawlResult { BrokenLinks = null! };

        var r = TestConfig.Analyze(run, baseline: new RunResult { Pages = null!, Api = new ApiLoadResult() });

        Assert.DoesNotContain(r.Findings, f => f.RuleId == "ANALYSIS_RULE_ERROR");
        Assert.NotEmpty(r.Highlights);
    }

    [Fact]
    public void InvalidPageStatistic_FallsBackToP75()
    {
        var cfg = TestConfig.Default();
        cfg.Thresholds.PageStatistic = "bogus";
        var run = new RunBuilder().Page("/p", p => p.Iterations(2)).Build();

        var r = TestConfig.Analyze(run, cfg);

        Assert.All(r.Thresholds.Where(t => t.Metric != MetricNames.ConsoleErrors), t => Assert.Equal("p75", t.Statistic));
    }

    [Fact]
    public void Passed_RequiresThresholdsAndMinScore()
    {
        var run = new RunBuilder().Page("/p", p => p.Iterations(3)).Build();
        var cfg = TestConfig.Default();

        var r = TestConfig.Analyze(run, cfg);
        Assert.True(r.Passed);

        cfg.Thresholds.MinScore = r.Score + 1;
        Assert.False(TestConfig.Analyze(run, cfg).Passed);

        cfg.Thresholds.MinScore = r.Score;
        Assert.True(TestConfig.Analyze(run, cfg).Passed);

        cfg.Thresholds.MinScore = null;
        cfg.Thresholds.Page.LcpMs = 100;
        cfg.Thresholds.FailOnThresholdBreach = false;
        Assert.False(TestConfig.Analyze(run, cfg).Passed);
    }

    [Fact]
    public void Highlights_DescribeRun()
    {
        var run = new RunBuilder()
            .Page("/fast", p => p.Iterations(3))
            .Page("/reports", p => p.Iterations(3, i => { i.Vitals.LcpMs = 3900; i.Resources.TransferBytes = 4300 * 1024; }))
            .Endpoint("search", e => { e.Latency.P95 = 612; e.Rps = 48; e.ErrorRatePct = 0.4; })
            .BrokenLink(RunBuilder.Base + "/x", RunBuilder.Base + "/fast")
            .BrokenLink(RunBuilder.Base + "/y", RunBuilder.Base + "/fast")
            .Build();

        var h = TestConfig.Analyze(run, baseline: new RunBuilder().Id("b").Build()).Highlights;

        Assert.InRange(h.Count, 3, 6);
        Assert.StartsWith("Overall score", h[0]);
        Assert.Contains("1 of 2 pages have good LCP", h[0]);
        Assert.Contains("Slowest page: /reports (LCP p75 3.9 s).", h);
        Assert.Contains("Heaviest page: /reports (4.2 MB).", h);
        Assert.Contains("API: search (p95 612 ms at 48 rps, 0.4% errors).", h);
        Assert.Contains("2 broken links found.", h);
        Assert.Contains(h, x => x.Contains("regressions vs baseline", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Deterministic()
    {
        RunResult Make() => new RunBuilder()
            .Page("/a", p => p.Values((i, v) => i.Vitals.LcpMs = v, 3000, 5000, 4000))
            .Page("/b", p => p.Error("x"))
            .Endpoint("e", e => e.ErrorRatePct = 3)
            .Build();

        var a = System.Text.Json.JsonSerializer.Serialize(TestConfig.Analyze(Make()));
        var b = System.Text.Json.JsonSerializer.Serialize(TestConfig.Analyze(Make()));
        Assert.Equal(a, b);
    }
}

public class FormattingTests
{
    [Theory]
    [InlineData(612, "612 ms")]
    [InlineData(999.4, "999 ms")]
    [InlineData(999.6, "1.0 s")]
    [InlineData(3900, "3.9 s")]
    public void Duration(double ms, string expected) => Assert.Equal(expected, Formatting.Duration(ms));

    [Theory]
    [InlineData(850, "850 KB")]
    [InlineData(4300, "4.2 MB")]
    public void Bytes(double kb, string expected) => Assert.Equal(expected, Formatting.Bytes(kb));
}
