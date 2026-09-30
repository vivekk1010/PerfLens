using PerfLens.Core.Configuration;
using PerfLens.Core.Results;

namespace PerfLens.Analysis.Tests;

public class ThresholdTests
{
    private static ThresholdResult Find(AnalysisResult r, string target, string metric, string? statistic = null) =>
        Assert.Single(r.Thresholds, t => t.Target == target && t.Metric == metric && (statistic is null || t.Statistic == statistic));

    [Fact]
    public void GlobalPageThresholds_UsePageStatistic()
    {
        var run = new RunBuilder().Page("/p", p => p.Values((i, v) => i.Vitals.LcpMs = v, 1000, 2000, 3000, 4000, 5000)).Build();

        var r = TestConfig.Analyze(run);
        var lcp = Find(r, "/p", MetricNames.Lcp);

        Assert.Equal("p75", lcp.Statistic);
        Assert.Equal(4000, lcp.Actual);
        Assert.Equal(2500, lcp.Limit);
        Assert.False(lcp.Passed);
        Assert.True(Find(r, "/p", MetricNames.Ttfb).Passed);
        Assert.Equal("page", lcp.Scope);
    }

    [Fact]
    public void PerPageThresholds_OverrideGlobalFieldByField()
    {
        var run = new RunBuilder()
            .Page("/slow", p => p.Iterations(3, i => { i.Vitals.LcpMs = 3500; i.Timing.TtfbMs = 900; }))
            .Page("/other", p => p.Iterations(3, i => { i.Vitals.LcpMs = 3500; i.Timing.TtfbMs = 900; }))
            .Build();
        var cfg = TestConfig.Default();
        cfg.Pages.Add(new PageConfig
        {
            Name = "/SLOW", Path = "/slow",
            Thresholds = new PageThresholds { LcpMs = 4000, TtfbMs = null, FcpMs = null, Cls = null, TbtMs = null, LoadMs = null, PageWeightKb = null, RequestCount = null, MaxConsoleErrors = null },
        });

        var r = TestConfig.Analyze(run, cfg);

        Assert.True(Find(r, "/slow", MetricNames.Lcp).Passed);
        Assert.Equal(4000, Find(r, "/slow", MetricNames.Lcp).Limit);
        Assert.Equal(800, Find(r, "/slow", MetricNames.Ttfb).Limit);
        Assert.False(Find(r, "/slow", MetricNames.Ttfb).Passed);
        Assert.False(Find(r, "/other", MetricNames.Lcp).Passed);
    }

    [Fact]
    public void NullThresholdsAndAbsentMetricsAreSkipped()
    {
        var run = new RunBuilder().Page("/p", p => p.Iterations(2, i => i.Vitals = new WebVitals())).Build();
        var cfg = TestConfig.Default();
        cfg.Thresholds.Page.LoadMs = null;

        var r = TestConfig.Analyze(run, cfg);

        Assert.DoesNotContain(r.Thresholds, t => t.Metric == MetricNames.Load);
        Assert.DoesNotContain(r.Thresholds, t => t.Metric == MetricNames.Lcp);
        Assert.Contains(r.Thresholds, t => t.Metric == MetricNames.Ttfb);
    }

    [Fact]
    public void ConsoleErrorsUseMax()
    {
        var run = new RunBuilder().Page("/p", p => p.Iterations(3).Iteration(i => i.ConsoleErrors.Add("boom"))).Build();

        var t = Find(TestConfig.Analyze(run), "/p", MetricNames.ConsoleErrors);

        Assert.Equal("max", t.Statistic);
        Assert.Equal(1, t.Actual);
        Assert.Equal(0, t.Limit);
        Assert.False(t.Passed);
    }

    [Fact]
    public void UnavailablePages_ProduceFailedAvailabilityThreshold()
    {
        var run = new RunBuilder()
            .Page("/err", p => p.Error("DNS failure"))
            .Page("/allfailed", p => p.Failed().Failed())
            .Page("/ok", p => p.Iterations(2))
            .Build();

        var r = TestConfig.Analyze(run);

        foreach (var page in new[] { "/err", "/allfailed" })
        {
            var a = Find(r, page, ThresholdEvaluator.Availability);
            Assert.False(a.Passed);
            Assert.Equal(0, a.Actual);
            Assert.Equal(1, a.Limit);
        }
        Assert.DoesNotContain(r.Thresholds, t => t.Target == "/ok" && t.Metric == ThresholdEvaluator.Availability);
        Assert.False(r.Passed);
    }

    [Fact]
    public void ApiThresholds_EndpointOverridesGlobal_AndMinRpsPassesWhenAtLeastLimit()
    {
        var run = new RunBuilder()
            .Endpoint("search", e => { e.Latency.P95 = 900; e.ErrorRatePct = 2; e.Rps = 30; })
            .Endpoint("list", e => { e.Latency.P95 = 900; e.Rps = 30; })
            .Build();
        var cfg = TestConfig.Default();
        cfg.Thresholds.Api.MinRps = 30;
        cfg.Api.Endpoints.Add(new ApiEndpointConfig { Name = "search", Url = "/api/search", Thresholds = new ApiThresholds { P95Ms = 1000, P99Ms = null, ErrorRatePct = null } });

        var r = TestConfig.Analyze(run, cfg);

        Assert.True(Find(r, "search", "latency_ms", "p95").Passed);
        Assert.False(Find(r, "list", "latency_ms", "p95").Passed);
        Assert.False(Find(r, "search", "error_rate_pct").Passed);
        Assert.Equal("rate", Find(r, "search", "error_rate_pct").Statistic);
        Assert.True(Find(r, "list", "rps").Passed);
        Assert.DoesNotContain(r.Thresholds, t => t.Metric == "latency_ms" && t.Statistic == "p50");
        Assert.All(r.Thresholds, t => Assert.Equal("api", t.Scope));
    }

    [Fact]
    public void ApiThresholds_MinRpsFailsBelowLimit()
    {
        var run = new RunBuilder().Endpoint("a", e => e.Rps = 9.5).Build();
        var cfg = TestConfig.Default();
        cfg.Thresholds.Api.MinRps = 10;

        Assert.False(Find(TestConfig.Analyze(run, cfg), "a", "rps").Passed);
    }

    [Fact]
    public void UnreachableEndpoint_FailsAvailability()
    {
        var run = new RunBuilder().Endpoint("down", e => { e.Error = "refused"; e.SuccessfulRequests = 0; }).Build();

        var r = TestConfig.Analyze(run);

        Assert.False(Assert.Single(r.Thresholds).Passed);
        Assert.Equal(ThresholdEvaluator.Availability, r.Thresholds[0].Metric);
    }
}
