using PerfLens.Core.Utilities;

namespace PerfLens.Core.Tests;

public class UrlUtilsTests
{
    [Theory]
    [InlineData("https://app.test", "/products", "https://app.test/products")]
    [InlineData("https://app.test/", "products", "https://app.test/products")]
    [InlineData("https://app.test/app", "/products", "https://app.test/app/products")]
    [InlineData("https://app.test/app/", "products?x=1", "https://app.test/app/products?x=1")]
    [InlineData("https://app.test", "https://other.test/a", "https://other.test/a")]
    [InlineData("https://app.test", "/", "https://app.test/")]
    public void Resolve(string baseUrl, string path, string expected) => Assert.Equal(expected, UrlUtils.Resolve(baseUrl, path));

    [Theory]
    [InlineData("https://a.test/x", "https://A.TEST/y", true)]
    [InlineData("https://a.test/x", "https://a.test:443/y", true)]
    [InlineData("https://a.test/x", "http://a.test/x", false)]
    [InlineData("https://a.test/x", "https://a.test:8443/x", false)]
    [InlineData("https://a.test/x", "https://b.a.test/x", false)]
    [InlineData("/relative", "https://a.test/", false)]
    public void SameOrigin(string a, string b, bool expected) => Assert.Equal(expected, UrlUtils.SameOrigin(a, b));

    [Theory]
    [InlineData("https://a.test/api/products/123", "https://a.test/api/products/{id}")]
    [InlineData("https://a.test/api/u/0f8fad5b-d9cb-469f-a165-70867728950e/orders", "https://a.test/api/u/{id}/orders")]
    [InlineData("https://a.test/api/doc/507f1f77bcf86cd799439011", "https://a.test/api/doc/{id}")]
    [InlineData("https://a.test/api/search?q=shoes&page=2", "https://a.test/api/search")]
    [InlineData("https://a.test:8080/api/v2/items", "https://a.test:8080/api/v2/items")]
    [InlineData("https://a.test:443/api/x", "https://a.test/api/x")]
    [InlineData("not a url", "not a url")]
    public void NormalizeApiUrl(string url, string expected) => Assert.Equal(expected, UrlUtils.NormalizeApiUrl(url));

    [Fact]
    public void NormalizeApiUrl_RelativePathReturnedUnchanged() =>
        Assert.Equal("/api/products/1", UrlUtils.NormalizeApiUrl("/api/products/1"));

    [Theory]
    [InlineData("https://App.Test/a/b/", true, true, "https://app.test/a/b")]
    [InlineData("https://app.test/a?x=1#frag", true, true, "https://app.test/a")]
    [InlineData("https://app.test/a?x=1#frag", false, true, "https://app.test/a?x=1")]
    [InlineData("https://app.test/a#frag", true, false, "https://app.test/a#frag")]
    [InlineData("https://app.test/", true, true, "https://app.test/")]
    [InlineData("not a url", true, true, "not a url")]
    public void Canonicalize(string url, bool ignoreQuery, bool ignoreFragment, string expected) =>
        Assert.Equal(expected, UrlUtils.Canonicalize(url, ignoreQuery, ignoreFragment));

    [Theory]
    [InlineData("https://a.test/x?token=abc123&page=2", "https://a.test/x?token=***&page=2")]
    [InlineData("https://a.test/x?access_token=abc", "https://a.test/x?access_token=***")]
    [InlineData("https://a.test/x?api_key=k&api-key=k2&apikey=k3", "https://a.test/x?api_key=***&api-key=***&apikey=***")]
    [InlineData("https://a.test/x?Password=hunter2#top", "https://a.test/x?Password=***#top")]
    [InlineData("https://a.test/x?sig=s&signature=t&secret=u&auth=v&pwd=w&key=z", "https://a.test/x?sig=***&signature=***&secret=***&auth=***&pwd=***&key=***")]
    [InlineData("https://a.test/x?monkey=1&author=me&page=2", "https://a.test/x?monkey=1&author=me&page=2")]
    public void RedactSecrets(string url, string expected) => Assert.Equal(expected, UrlUtils.RedactSecrets(url));
}
