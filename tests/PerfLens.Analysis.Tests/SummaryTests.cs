using PerfLens.Core.Results;

namespace PerfLens.Analysis.Tests;

public class SummaryTests
{
    [Fact]
    public void Summary_ExcludesWarmupAndFailedIterations_AndComputesPercentiles()
    {
        var run = new RunBuilder().Page("/home", p => p
            .Warmup(i => i.Vitals.LcpMs = 99999)
            .Failed()
            .Values((i, v) => i.Vitals.LcpMs = v, 1000, 2000, 3000, 4000, 5000)).Build();

        TestConfig.Analyze(run);
        var s = run.Pages[0].Summary!;

        Assert.Equal(5, s.MeasuredIterations);
        Assert.Equal(1, s.FailedIterations);
        var lcp = s.Metrics[MetricNames.Lcp];
        Assert.Equal(5, lcp.Count);
        Assert.Equal(1000, lcp.Min);
        Assert.Equal(5000, lcp.Max);
        Assert.Equal(3000, lcp.Median);
        Assert.Equal(4000, lcp.P75);
    }

    [Fact]
    public void Summary_SkipsNullVitals_AndOmitsMetricsWithoutValues()
    {
        var run = new RunBuilder().Page("/a", p => p
            .Iteration(i => { i.Vitals = new WebVitals { LcpMs = 1200 }; i.DomNodes = null; i.JsHeapUsedBytes = null; })
            .Iteration(i => { i.Vitals = new WebVitals(); i.DomNodes = null; i.JsHeapUsedBytes = null; })).Build();

        TestConfig.Analyze(run);
        var m = run.Pages[0].Summary!.Metrics;

        Assert.Equal(1, m[MetricNames.Lcp].Count);
        Assert.False(m.ContainsKey(MetricNames.Fcp));
        Assert.False(m.ContainsKey(MetricNames.Cls));
        Assert.False(m.ContainsKey(MetricNames.DomNodes));
        Assert.False(m.ContainsKey(MetricNames.JsHeapMb));
        Assert.Equal(2, m[MetricNames.Ttfb].Count);
    }

    [Fact]
    public void Summary_ConvertsUnits_AndCountsConsoleAndPageErrors()
    {
        var run = new RunBuilder().Page("/a", p => p.Iteration(i =>
        {
            i.Resources.TransferBytes = 2048;
            i.Resources.DecodedBytes = 4096;
            i.JsHeapUsedBytes = 3 * 1024 * 1024;
            i.ConsoleErrors.Add("x");
            i.PageErrors.AddRange(["y", "z"]);
        })).Build();

        TestConfig.Analyze(run);
        var m = run.Pages[0].Summary!.Metrics;

        Assert.Equal(2, m[MetricNames.TransferKb].Median);
        Assert.Equal(4, m[MetricNames.DecodedKb].Median);
        Assert.Equal(3, m[MetricNames.JsHeapMb].Median);
        Assert.Equal(3, m[MetricNames.ConsoleErrors].Max);
        Assert.Equal(20, m[MetricNames.Requests].Median);
    }

    [Fact]
    public void Summary_GroupsApiCallsByMethodAndNormalizedUrl_OrderedByCallsThenP95()
    {
        static ApiCall Call(string method, string norm, double ms, bool failed = false) =>
            new() { Method = method, Url = norm.Replace("{id}", "42") + "?token=secret", NormalizedUrl = norm, DurationMs = ms, Bytes = 1000, Failed = failed };

        var run = new RunBuilder().Page("/a", p => p
            .Warmup(i => i.ApiCalls.Add(Call("GET", "https://app.test/api/warm", 1)))
            .Iterations(2, i =>
            {
                i.ApiCalls.Add(Call("GET", "https://app.test/api/items/{id}", 100));
                i.ApiCalls.Add(Call("get", "https://app.test/api/items/{id}", 300, failed: true));
                i.ApiCalls.Add(Call("POST", "https://app.test/api/slow", 900));
                i.ApiCalls.Add(Call("GET", "https://app.test/api/fast", 10));
            })).Build();

        TestConfig.Analyze(run);
        var calls = run.Pages[0].Summary!.ApiCalls;

        Assert.Equal(3, calls.Count);
        Assert.Equal("https://app.test/api/items/{id}", calls[0].NormalizedUrl);
        Assert.Equal("GET", calls[0].Method);
        Assert.Equal(4, calls[0].Calls);
        Assert.Equal(2, calls[0].Failures);
        Assert.Equal(1000, calls[0].AvgBytes);
        Assert.Contains("token=***", calls[0].SampleUrl);
        Assert.Equal("https://app.test/api/slow", calls[1].NormalizedUrl);
        Assert.Equal("https://app.test/api/fast", calls[2].NormalizedUrl);
        Assert.DoesNotContain(calls, c => c.NormalizedUrl.EndsWith("warm"));
    }

    [Fact]
    public void Summary_ZeroMeasuredIterations_HasNoMetricsAndScoreZero()
    {
        var run = new RunBuilder().Page("/down", p => p.Failed().Failed()).Build();

        TestConfig.Analyze(run);
        var s = run.Pages[0].Summary!;

        Assert.Equal(0, s.MeasuredIterations);
        Assert.Equal(2, s.FailedIterations);
        Assert.Empty(s.Metrics);
        Assert.Equal(0, s.Score);
    }
}
