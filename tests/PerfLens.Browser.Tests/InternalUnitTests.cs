using PerfLens.Browser.Internal;
using PerfLens.Core.Configuration;

namespace PerfLens.Browser.Tests;

public sealed class FileNamesTests
{
    [Theory]
    [InlineData("/", "root")]
    [InlineData("/products/3", "products-3")]
    [InlineData("Home Page!", "home-page")]
    [InlineData("???", "page")]
    public void Slugify(string name, string expected) => Assert.Equal(expected, FileNames.Slugify(name));

    [Fact]
    public void UniqueSlugAppendsSuffix()
    {
        var used = new HashSet<string>();
        Assert.Equal("a", FileNames.UniqueSlug("/a", used));
        Assert.Equal("a-2", FileNames.UniqueSlug("A", used));
    }
}

public sealed class CrawlLinkFilterTests
{
    private static CrawlLinkFilter Filter(Action<CrawlConfig>? configure = null)
    {
        var config = new CrawlConfig();
        configure?.Invoke(config);
        return new CrawlLinkFilter(config, "http://site.test/");
    }

    [Theory]
    [InlineData("mailto:x@y.z")]
    [InlineData("javascript:void(0)")]
    [InlineData("http://site.test/files/a.PDF")]
    [InlineData("http://site.test/img/logo.png")]
    [InlineData("http://other.test/page")]
    [InlineData("http://site.test/account/logout")]
    [InlineData("not a url")]
    public void Rejects(string href) => Assert.Null(Filter().Accept(href));

    [Fact]
    public void CanonicalizesQueryAndFragment() =>
        Assert.Equal("http://site.test/products", Filter().Accept("http://SITE.test/products/?page=2#top"));

    [Fact]
    public void IncludeRequiresMatch()
    {
        var filter = Filter(c => c.Include = ["/docs/"]);
        Assert.Null(filter.Accept("http://site.test/blog"));
        Assert.NotNull(filter.Accept("http://site.test/docs/intro"));
    }

    [Fact]
    public void CrossOriginAllowedWhenNotSameOriginOnly() =>
        Assert.NotNull(Filter(c => c.SameOriginOnly = false).Accept("https://other.test/"));
}

public sealed class ThrottlingTests
{
    [Fact]
    public void Fast3GUsesBytesPerSecond()
    {
        var n = Throttling.GetNetworkConditions(new BrowserConfig { NetworkProfile = NetworkProfile.Fast3G })!.Value;
        Assert.Equal(562.5, n.LatencyMs);
        Assert.Equal(1474.56 * 1024 / 8, n.DownloadBytesPerSecond, 3);
        Assert.Equal(675 * 1024 / 8.0, n.UploadBytesPerSecond, 3);
    }

    [Fact]
    public void NoneIsNotRequested() => Assert.False(Throttling.IsRequested(new BrowserConfig()));

    [Fact]
    public void CpuOnlyIsRequested() => Assert.True(Throttling.IsRequested(new BrowserConfig { CpuThrottlingRate = 4 }));
}

public sealed class ResourceAggregatorTests
{
    private const string Base = "http://site.test/";

    [Fact]
    public void MergesTimingsWithRequestsAndFlagsCacheThirdPartyAndFailures()
    {
        var snapshot = new MetricsSnapshot
        {
            Observers = true,
            Navigation = new NavigationSnapshot { Name = Base, TransferSize = 1000, DecodedBodySize = 900, Duration = 50 },
            Resources =
            [
                new ResourceTiming { Name = "http://site.test/app.js", InitiatorType = "script", TransferSize = 0, DecodedBodySize = 500, Duration = 5 },
                new ResourceTiming { Name = "https://cdn.test/lib.js", InitiatorType = "script", TransferSize = 0, DecodedBodySize = 0, Duration = 40 },
                new ResourceTiming { Name = "http://site.test/font.woff2", InitiatorType = "css", TransferSize = 3000, DecodedBodySize = 3000, Duration = 9 },
            ],
        };
        var requests = new List<ObservedRequest>
        {
            new() { Sequence = 1, Url = Base, OriginalUrl = Base, ResourceType = "document", IsMainFrameDocument = true, Status = 200, Finished = true },
            new() { Sequence = 2, Url = "http://site.test/app.js", OriginalUrl = "http://site.test/app.js", ResourceType = "script", Status = 200, Finished = true },
            new() { Sequence = 3, Url = "https://cdn.test/lib.js", OriginalUrl = "https://cdn.test/lib.js", ResourceType = "script", Status = 200, ContentLength = 7000, Finished = true },
            new() { Sequence = 4, Url = "http://site.test/api/items/42?token=abc", OriginalUrl = "http://site.test/api/items/42?token=abc", ResourceType = "fetch", Status = 500, ContentLength = 20, ResponseEndMs = 12, Finished = true },
            new() { Sequence = 5, Url = "http://site.test/api/x", OriginalUrl = "http://site.test/api/x", ResourceType = "xhr", Failed = true, FailureText = "net::ERR_FAILED" },
        };

        var result = ResourceAggregator.Build(snapshot, requests, Base, topResources: 3, captureApiCalls: true);

        Assert.Equal(6, result.Stats.TotalRequests);
        Assert.Equal(1, result.Stats.CachedRequests);
        Assert.Equal(1, result.Stats.ThirdPartyRequests);
        Assert.Equal(2, result.Stats.FailedRequests);
        Assert.Equal(1000 + 7000 + 20 + 3000, result.Stats.TransferBytes);
        Assert.Equal(2, result.Stats.ByType["script"].Count);
        Assert.Equal(1, result.Stats.ByType["font"].Count);
        Assert.Equal(3, result.TopResources.Count);
        Assert.Equal("https://cdn.test/lib.js", result.TopResources[0].Url);
        Assert.True(result.TopResources[0].ThirdParty);

        Assert.Equal(2, result.ApiCalls.Count);
        var fetch = result.ApiCalls[0];
        Assert.Equal("http://site.test/api/items/{id}", fetch.NormalizedUrl);
        Assert.Contains("token=***", fetch.Url);
        Assert.True(fetch.Failed);
        Assert.Equal(12, fetch.DurationMs);
        Assert.Equal(20, fetch.Bytes);
        Assert.True(result.ApiCalls[1].Failed);
        Assert.Null(result.ApiCalls[1].Status);
    }

    [Fact]
    public void SkipsRedirectHopsAndMatchesTimingByOriginalUrl()
    {
        var snapshot = new MetricsSnapshot
        {
            Resources = [new ResourceTiming { Name = "http://site.test/old.css", InitiatorType = "link", TransferSize = 800, DecodedBodySize = 700 }],
        };
        var requests = new List<ObservedRequest>
        {
            new() { Sequence = 1, Url = "http://site.test/old.css", OriginalUrl = "http://site.test/old.css", ResourceType = "stylesheet", Status = 301, IsRedirectHop = true },
            new() { Sequence = 2, Url = "http://site.test/new.css", OriginalUrl = "http://site.test/old.css", ResourceType = "stylesheet", Status = 200 },
        };

        var result = ResourceAggregator.Build(snapshot, requests, Base, 10, true);

        Assert.Equal(1, result.Stats.TotalRequests);
        Assert.Equal(800, result.Stats.TransferBytes);
        Assert.Equal(200, result.TopResources.Single().Status);
    }
}

public sealed class ErrorTextTests
{
    [Fact]
    public void KeepsFirstLineAndMasksSecrets() =>
        Assert.Equal("fill *** failed", ErrorText.Short("fill hunter2 failed\nCall log:\n  - hunter2", ["hunter2"]));
}
