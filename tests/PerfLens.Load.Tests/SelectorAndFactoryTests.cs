using PerfLens.Core.Configuration;
using PerfLens.Core.Results;

namespace PerfLens.Load.Tests;

public sealed class DiscoveredEndpointSelectorTests
{
    private const string Base = "https://app.example.com";

    private static ApiCall Call(string url, string method = "GET", bool failed = false, int status = 200) => new()
    {
        Method = method, Url = url, NormalizedUrl = PerfLens.Core.Utilities.UrlUtils.NormalizeApiUrl(url),
        Status = status, Failed = failed,
    };

    private static PageResult Page(params ApiCall[] calls) => new()
    {
        Name = "p",
        Iterations =
        {
            new PageIteration { Success = true, ApiCalls = calls.ToList() },
            new PageIteration { IsWarmup = true, Success = true, ApiCalls = { Call(Base + "/api/warmup-only") } },
        },
    };

    [Fact]
    public void Filters_non_get_cross_origin_failed_and_dedupes_ids()
    {
        var config = new PerfLensConfig { Target = { BaseUrl = Base } };
        var pages = new[]
        {
            Page(
                Call(Base + "/api/products/1"),
                Call(Base + "/api/products/2"),
                Call(Base + "/api/products/3"),
                Call(Base + "/api/cart"),
                Call(Base + "/api/cart", method: "POST"),
                Call(Base + "/api/orders", method: "post"),
                Call("https://cdn.thirdparty.com/api/track"),
                Call("http://app.example.com/api/insecure"),
                Call(Base + "/api/broken", failed: true),
                Call(Base + "/api/notfound", status: 404)),
        };

        var selected = DiscoveredEndpointSelector.Select(config, pages);

        Assert.Equal(new[] { "GET /api/products/{id}", "GET /api/cart" }, selected.Select(e => e.Name));
        Assert.StartsWith(Base + "/api/products/", selected[0].Url);
        Assert.DoesNotContain("{id}", selected[0].Url);
        Assert.All(selected, e => Assert.Equal("GET", e.Method));
    }

    [Fact]
    public void Respects_max_and_skips_configured_endpoints()
    {
        var config = new PerfLensConfig { Target = { BaseUrl = Base } };
        config.Api.MaxDiscoveredEndpoints = 2;
        config.Api.Endpoints.Add(new ApiEndpointConfig { Url = "/api/a" });
        var pages = new[]
        {
            Page(
                Call(Base + "/api/a"), Call(Base + "/api/a"), Call(Base + "/api/a"),
                Call(Base + "/api/b"), Call(Base + "/api/b"),
                Call(Base + "/api/c"), Call(Base + "/api/c"),
                Call(Base + "/api/d")),
        };

        var selected = DiscoveredEndpointSelector.Select(config, pages);

        Assert.Equal(new[] { "GET /api/b", "GET /api/c" }, selected.Select(e => e.Name));
    }

    [Fact]
    public void Falls_back_to_page_summary_api_calls()
    {
        var config = new PerfLensConfig { Target = { BaseUrl = Base } };
        var page = new PageResult
        {
            Summary = new PageSummary
            {
                ApiCalls =
                {
                    new ApiCallSummary { Method = "GET", NormalizedUrl = Base + "/api/users/{id}", SampleUrl = Base + "/api/users/7", Calls = 3 },
                    new ApiCallSummary { Method = "GET", NormalizedUrl = Base + "/api/dead", SampleUrl = Base + "/api/dead", Calls = 2, Failures = 2 },
                },
            },
        };

        var selected = DiscoveredEndpointSelector.Select(config, new[] { page });

        var only = Assert.Single(selected);
        Assert.Equal("GET /api/users/{id}", only.Name);
        Assert.Equal(Base + "/api/users/7", only.Url);
    }
}

public sealed class LoadRequestFactoryTests
{
    [Fact]
    public void Merges_headers_with_precedence_and_cookies()
    {
        var config = new PerfLensConfig
        {
            Target = new TargetConfig
            {
                BaseUrl = "https://app.example.com/",
                Headers = { ["X-Env"] = "target", ["X-Shared"] = "target" },
                Cookies = { new CookieConfig { Name = "c1", Value = "v1" }, new CookieConfig { Name = "other", Value = "x", Domain = "elsewhere.com" } },
                IgnoreHttpsErrors = true,
            },
            Auth = new AuthConfig { Type = AuthType.Headers, Headers = { ["x-shared"] = "auth", ["Authorization"] = "Bearer t" } },
        };
        config.Api.Profile = new LoadProfile { Concurrency = 3 };
        var endpoint = new ApiEndpointConfig { Url = "/api/items?page=1", Headers = { ["AUTHORIZATION"] = "Bearer e" } };

        var req = LoadRequestFactory.Build(config, endpoint, EndpointSource.Discovered,
            new Dictionary<string, string> { ["sid"] = "abc", ["c1"] = "session" });

        Assert.Equal("https://app.example.com/api/items?page=1", req.AbsoluteUrl);
        Assert.Equal("target", req.Headers["x-env"]);
        Assert.Equal("auth", req.Headers["X-Shared"]);
        Assert.Equal("Bearer e", req.Headers["Authorization"]);
        Assert.Equal(3, req.Headers.Count);
        Assert.Equal("session", req.Cookies["c1"]);
        Assert.Equal("abc", req.Cookies["sid"]);
        Assert.False(req.Cookies.ContainsKey("other"));
        Assert.Same(config.Api.Profile, req.Profile);
        Assert.True(req.IgnoreHttpsErrors);
        Assert.Equal(EndpointSource.Discovered, req.Source);
    }

    [Fact]
    public void Ignores_auth_headers_and_session_cookies_when_not_enabled()
    {
        var config = new PerfLensConfig
        {
            Target = { BaseUrl = "https://app.example.com" },
            Auth = new AuthConfig { Type = AuthType.Login, Headers = { ["Authorization"] = "x" }, ShareCookiesWithApi = false },
        };
        var profile = new LoadProfile { Mode = LoadMode.Open };
        var endpoint = new ApiEndpointConfig { Url = "/api/x", Profile = profile };

        var req = LoadRequestFactory.Build(config, endpoint, EndpointSource.Configured, new Dictionary<string, string> { ["sid"] = "abc" });

        Assert.Empty(req.Headers);
        Assert.Empty(req.Cookies);
        Assert.Same(profile, req.Profile);
    }

    [Fact]
    public void Does_not_send_session_cookies_to_other_origins()
    {
        var config = new PerfLensConfig { Target = { BaseUrl = "https://app.example.com" } };
        var endpoint = new ApiEndpointConfig { Url = "https://api.other.com/v1/x" };

        var req = LoadRequestFactory.Build(config, endpoint, EndpointSource.Configured, new Dictionary<string, string> { ["sid"] = "abc" });

        Assert.Equal("https://api.other.com/v1/x", req.AbsoluteUrl);
        Assert.Empty(req.Cookies);
    }
}

public sealed class LatencyHistogramTests
{
    [Fact]
    public void Clips_outliers_into_last_bucket_and_preserves_counts()
    {
        var values = Enumerable.Range(1, 2000).Select(i => (double)(i % 100)).Append(100_000).ToList();

        var buckets = LatencyHistogram.Build(values, 20);

        Assert.Equal(20, buckets.Count);
        Assert.Equal(values.Count, buckets.Sum(b => b.Count));
        Assert.True(buckets[18].UpperMs < 200, $"range not clipped: {buckets[18].UpperMs}");
        Assert.Equal(100_000, buckets[^1].UpperMs);
    }

    [Fact]
    public void Handles_empty_and_constant_input()
    {
        Assert.Empty(LatencyHistogram.Build(Array.Empty<double>()));
        var buckets = LatencyHistogram.Build(new double[] { 5, 5, 5 });
        Assert.Equal(3, buckets.Sum(b => b.Count));
    }
}
