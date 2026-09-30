using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PerfLens.Core.Abstractions;
using PerfLens.Core.Configuration;
using PerfLens.Core.Results;

namespace PerfLens.Load.Tests;

[Collection(ServerCollection.Name)]
public sealed class HttpApiLoadRunnerTests(TestServerFixture server)
{
    private static LoadProfile Closed(int vus, int seconds, int warmup = 0, int timeoutMs = 5000, int? max = null) => new()
    {
        Mode = LoadMode.Closed, Concurrency = vus, DurationSeconds = seconds, WarmupSeconds = warmup,
        TimeoutMs = timeoutMs, MaxRequests = max,
    };

    private static LoadProfile Open(double rps, int seconds, int warmup = 0) => new()
    {
        Mode = LoadMode.Open, RatePerSecond = rps, DurationSeconds = seconds, WarmupSeconds = warmup, TimeoutMs = 5000,
    };

    private ApiLoadRequest Request(string path, LoadProfile profile, string method = "GET", string? body = null,
        List<int>? expected = null) => new()
    {
        Endpoint = new ApiEndpointConfig
        {
            Name = $"{method} {path}", Url = path, Method = method, Body = body, ExpectedStatus = expected ?? new(),
        },
        AbsoluteUrl = server.BaseUrl + path,
        Profile = profile,
    };

    private static void AssertOrderedStats(MetricStats s)
    {
        Assert.True(s.Min <= s.Median, $"min {s.Min} > p50 {s.Median}");
        Assert.True(s.Median <= s.P95);
        Assert.True(s.P95 <= s.P99);
        Assert.True(s.P99 <= s.Max);
    }

    [Fact]
    public async Task Closed_mode_counts_requests_and_orders_latency_stats()
    {
        var progress = new RecordingProgress();
        var r = await new HttpApiLoadRunner().RunAsync(Request("/fast", Closed(4, 1, warmup: 1)), progress, CancellationToken.None);

        Assert.Null(r.Error);
        Assert.True(r.TotalRequests > 10, $"total {r.TotalRequests}");
        Assert.Equal(r.TotalRequests, r.SuccessfulRequests);
        Assert.Equal(0, r.FailedRequests);
        Assert.Equal(0, r.ErrorRatePct);
        Assert.True(r.Rps > 0);
        Assert.True(r.BytesReceived > 0);
        Assert.Equal(r.TotalRequests, r.StatusCodes["200"]);
        Assert.Equal(r.TotalRequests, r.Latency.Count);
        AssertOrderedStats(r.Latency);
        Assert.Equal(r.TotalRequests, r.Histogram.Sum(b => b.Count));
        Assert.Equal(20, r.Histogram.Count);
        Assert.Equal("closed, 4 VUs, 1s (+1s warm-up)", r.ProfileDescription);
        Assert.Contains(progress.Messages, m => m.StartsWith("INFO GET /fast") && m.Contains("rps") && m.Contains("% errors"));
    }

    [Fact]
    public async Task Open_mode_holds_arrival_rate_and_measures_from_intended_start()
    {
        var r = await new HttpApiLoadRunner().RunAsync(Request("/slow", Open(50, 2)), new RecordingProgress(), CancellationToken.None);

        Assert.Null(r.Error);
        Assert.InRange(r.TotalRequests, 90, 110);
        Assert.True(r.Latency.Min >= 100, $"min latency {r.Latency.Min}");
        Assert.Equal("open, 50 rps, 2s", r.ProfileDescription);
        Assert.Equal(2, r.Timeline.Count);
        Assert.Equal(new[] { 0, 1 }, r.Timeline.Select(t => t.Second));
        Assert.Equal(r.TotalRequests, r.Timeline.Sum(t => t.Requests));
        Assert.Equal(r.TotalRequests, r.Histogram.Sum(b => b.Count));
        Assert.All(r.Timeline.Where(t => t.Requests > 0), t => Assert.True(t.P50Ms <= t.P95Ms && t.P95Ms <= t.MaxMs));
    }

    [Fact]
    public async Task Flaky_endpoint_reports_error_rate_and_status_codes()
    {
        var r = await new HttpApiLoadRunner().RunAsync(Request("/flaky", Closed(2, 1)), new RecordingProgress(), CancellationToken.None);

        Assert.True(r.TotalRequests > 20);
        Assert.InRange(r.ErrorRatePct, 20, 30);
        Assert.True(r.StatusCodes.ContainsKey("500"));
        Assert.True(r.StatusCodes.ContainsKey("200"));
        Assert.Equal(r.FailedRequests, r.ErrorTypes[HttpApiLoadRunner.ErrorUnexpectedStatus]);
        Assert.Null(r.Error);
    }

    [Fact]
    public async Task Post_body_is_sent_and_response_fully_read()
    {
        const string body = "{\"hello\":\"world\"}";
        var r = await new HttpApiLoadRunner().RunAsync(
            Request("/echo", Closed(1, 2, max: 5), method: "POST", body: body), new RecordingProgress(), CancellationToken.None);

        Assert.Equal(5, r.TotalRequests);
        Assert.Equal(5, r.SuccessfulRequests);
        Assert.Equal(5L * body.Length, r.BytesReceived);
        Assert.Equal("POST", r.Method);
    }

    [Fact]
    public async Task Factory_merged_headers_and_cookies_reach_auth_endpoint()
    {
        var config = new PerfLensConfig
        {
            Target = new TargetConfig { BaseUrl = server.BaseUrl, Headers = { ["x-api-key"] = "wrong" } },
            Auth = new AuthConfig { Type = AuthType.Headers, Headers = { ["X-Api-Key"] = "k1" }, ShareCookiesWithApi = true },
        };
        config.Api.Profile = Closed(1, 1, max: 10);
        var endpoint = new ApiEndpointConfig { Name = "auth", Url = "/auth" };
        var session = new Dictionary<string, string> { ["sid"] = "abc" };

        var ok = await new HttpApiLoadRunner().RunAsync(
            LoadRequestFactory.Build(config, endpoint, EndpointSource.Configured, session), new RecordingProgress(), CancellationToken.None);
        Assert.Equal(10, ok.TotalRequests);
        Assert.Equal(10, ok.StatusCodes["200"]);
        Assert.Equal(0, ok.FailedRequests);

        var denied = await new HttpApiLoadRunner().RunAsync(
            LoadRequestFactory.Build(config, endpoint, EndpointSource.Configured, null), new RecordingProgress(), CancellationToken.None);
        Assert.Equal(10, denied.FailedRequests);
        Assert.Equal(10, denied.StatusCodes["401"]);
        Assert.Equal(10, denied.ErrorTypes[HttpApiLoadRunner.ErrorUnexpectedStatus]);
        Assert.Equal(100, denied.ErrorRatePct);
        Assert.Null(denied.Error);
    }

    [Fact]
    public async Task Expected_status_makes_401_a_success()
    {
        var r = await new HttpApiLoadRunner().RunAsync(
            Request("/auth", Closed(1, 1, max: 5), expected: new() { 401 }), new RecordingProgress(), CancellationToken.None);

        Assert.Equal(5, r.SuccessfulRequests);
        Assert.Equal(0, r.FailedRequests);
        Assert.Empty(r.ErrorTypes);
    }

    [Fact]
    public async Task Slow_response_beyond_timeout_is_classified_as_timeout()
    {
        var r = await new HttpApiLoadRunner().RunAsync(
            Request("/timeout", Closed(2, 1, timeoutMs: 200)), new RecordingProgress(), CancellationToken.None);

        Assert.True(r.TotalRequests >= 4, $"total {r.TotalRequests}");
        Assert.Equal(r.TotalRequests, r.ErrorTypes[HttpApiLoadRunner.ErrorTimeout]);
        Assert.Equal(100, r.ErrorRatePct);
        Assert.InRange(r.Latency.Median, 190, 1000);
    }

    [Fact]
    public async Task Unreachable_port_returns_result_with_error_instead_of_throwing()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var request = new ApiLoadRequest
        {
            Endpoint = new ApiEndpointConfig { Name = "down", Url = "/x" },
            AbsoluteUrl = $"http://127.0.0.1:{port}/x?api_key=secret",
            Profile = Closed(2, 1, max: 200),
        };
        var r = await new HttpApiLoadRunner().RunAsync(request, new RecordingProgress(), CancellationToken.None);

        Assert.NotNull(r.Error);
        Assert.True(r.ErrorTypes[HttpApiLoadRunner.ErrorConnection] > 0);
        Assert.Equal(0, r.SuccessfulRequests);
        Assert.Empty(r.StatusCodes);
        Assert.DoesNotContain("secret", r.Url);
    }

    [Fact]
    public async Task Invalid_url_returns_result_with_error()
    {
        var request = new ApiLoadRequest { Endpoint = new ApiEndpointConfig { Url = "nope" }, AbsoluteUrl = "not a url", Profile = Closed(1, 1) };
        var r = await new HttpApiLoadRunner().RunAsync(request, new RecordingProgress(), CancellationToken.None);
        Assert.NotNull(r.Error);
        Assert.Equal(0, r.TotalRequests);
    }

    [Fact]
    public async Task Timeline_has_one_point_per_measured_second()
    {
        var r = await new HttpApiLoadRunner().RunAsync(Request("/fast", Closed(2, 2)), new RecordingProgress(), CancellationToken.None);

        Assert.Equal(2, r.DurationSeconds, 3);
        Assert.Equal(2, r.Timeline.Count);
        Assert.All(r.Timeline, t => Assert.True(t.Requests > 0));
        Assert.Equal(r.TotalRequests, r.Timeline.Sum(t => t.Requests));
        Assert.Equal(r.TotalRequests, r.Histogram.Sum(b => b.Count));
    }

    [Theory]
    [InlineData(LoadMode.Closed)]
    [InlineData(LoadMode.Open)]
    public async Task Cancellation_stops_promptly(LoadMode mode)
    {
        var profile = mode == LoadMode.Open ? Open(100, 30) : Closed(4, 30);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var sw = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new HttpApiLoadRunner().RunAsync(Request("/slow", profile), new RecordingProgress(), cts.Token));

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed}");
    }
}
