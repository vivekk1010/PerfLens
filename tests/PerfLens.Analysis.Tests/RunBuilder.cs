using PerfLens.Core.Configuration;
using PerfLens.Core.Results;

namespace PerfLens.Analysis.Tests;

/// <summary>Fluent helpers for synthetic runs. Default iterations are "clean": fast, small, compressed, cached.</summary>
internal sealed class RunBuilder
{
    public const string Base = "https://app.test";
    private readonly RunResult _run = new() { RunId = "run-1", Name = "test", BaseUrl = Base };

    public RunBuilder Page(string name, Action<PageBuilder>? configure = null)
    {
        var pb = new PageBuilder(name);
        configure?.Invoke(pb);
        _run.Pages.Add(pb.Build());
        return this;
    }

    public RunBuilder Endpoint(string name, Action<EndpointLoadResult>? configure = null)
    {
        _run.Api ??= new ApiLoadResult();
        var e = CleanEndpoint(name);
        configure?.Invoke(e);
        _run.Api.Endpoints.Add(e);
        return this;
    }

    public RunBuilder BrokenLink(string url, string referrer, int status = 404)
    {
        _run.Crawl ??= new CrawlResult();
        _run.Crawl.BrokenLinks.Add(new CrawlEntry { Url = url, Referrer = referrer, Status = status });
        return this;
    }

    public RunBuilder Id(string id) { _run.RunId = id; return this; }

    public RunResult Build() => _run;

    public static EndpointLoadResult CleanEndpoint(string name) => new()
    {
        Name = name, Url = Base + "/api/" + name, TotalRequests = 1000, SuccessfulRequests = 1000, Rps = 50, DurationSeconds = 20,
        Latency = new MetricStats { Count = 1000, Min = 10, Median = 50, P75 = 60, P90 = 70, P95 = 80, P99 = 100, Max = 150, Mean = 55 },
    };

    public static PageIteration CleanIteration(int index = 0) => new()
    {
        Index = index, Success = true, WallClockMs = 900,
        Timing = new NavigationTiming { TtfbMs = 200, ServerMs = 100, DnsMs = 5, ConnectMs = 10, TlsMs = 15, DownloadMs = 20, DomInteractiveMs = 500, DomContentLoadedMs = 550, LoadEventMs = 800 },
        Vitals = new WebVitals { FcpMs = 600, LcpMs = 900, Cls = 0.01, TbtMs = 20 },
        Resources = new ResourceStats { TotalRequests = 20, TransferBytes = 300 * 1024, DecodedBytes = 900 * 1024 },
        TopResources =
        {
            new ResourceEntry { Url = Base + "/app.js", Type = "script", Status = 200, TransferBytes = 80 * 1024, DecodedBytes = 250 * 1024, ContentEncoding = "br", CacheControl = "public, max-age=31536000" },
            new ResourceEntry { Url = Base + "/logo.png", Type = "image", Status = 200, TransferBytes = 30 * 1024, DecodedBytes = 30 * 1024, CacheControl = "max-age=86400" },
        },
        DomNodes = 800,
        JsHeapUsedBytes = 10 * 1024 * 1024,
    };
}

internal sealed class PageBuilder(string name)
{
    private readonly PageResult _page = new() { Name = name, Url = RunBuilder.Base + name };

    /// <summary>Adds a warm-up iteration (clean values modified by <paramref name="mutate"/>).</summary>
    public PageBuilder Warmup(Action<PageIteration>? mutate = null) => Add(i => { i.IsWarmup = true; mutate?.Invoke(i); });

    public PageBuilder Iteration(Action<PageIteration>? mutate = null) => Add(mutate);

    public PageBuilder Iterations(int count, Action<PageIteration>? mutate = null)
    {
        for (int n = 0; n < count; n++) Add(mutate);
        return this;
    }

    /// <summary>One measured iteration per value, applying <paramref name="set"/> with each value.</summary>
    public PageBuilder Values(Action<PageIteration, double> set, params double[] values)
    {
        foreach (var v in values) Add(i => set(i, v));
        return this;
    }

    public PageBuilder Failed(string error = "Timeout 30000ms exceeded") => Add(i => { i.Success = false; i.Error = error; });

    public PageBuilder Error(string error) { _page.Error = error; return this; }

    private PageBuilder Add(Action<PageIteration>? mutate)
    {
        var it = RunBuilder.CleanIteration(_page.Iterations.Count);
        mutate?.Invoke(it);
        _page.Iterations.Add(it);
        return this;
    }

    public PageResult Build() => _page;
}

internal static class TestConfig
{
    public static PerfLensConfig Default() => new() { Target = { BaseUrl = RunBuilder.Base } };

    public static AnalysisResult Analyze(RunResult run, PerfLensConfig? config = null, RunResult? baseline = null) =>
        new AnalysisEngine().Analyze(run, config ?? Default(), baseline);
}
