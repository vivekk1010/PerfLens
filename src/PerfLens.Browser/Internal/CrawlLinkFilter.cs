using System.Text.RegularExpressions;
using PerfLens.Core.Configuration;
using PerfLens.Core.Utilities;

namespace PerfLens.Browser.Internal;

/// <summary>Decides which discovered links the crawler may follow, and canonicalizes them.</summary>
internal sealed class CrawlLinkFilter
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly HashSet<string> DownloadExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".zip", ".gz", ".tgz", ".tar", ".rar", ".7z", ".bz2", ".xz",
        ".exe", ".msi", ".dmg", ".pkg", ".deb", ".rpm", ".iso", ".apk", ".bin",
        ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".avif", ".ico", ".bmp", ".tif", ".tiff",
        ".mp3", ".mp4", ".avi", ".mov", ".wmv", ".webm", ".ogg", ".wav", ".flac", ".m4a",
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".csv", ".rtf",
        ".json", ".js", ".css", ".woff", ".woff2", ".ttf", ".otf", ".eot",
    };

    private readonly CrawlConfig _config;
    private readonly string _baseUrl;
    private readonly List<Regex> _include;
    private readonly List<Regex> _exclude;

    public CrawlLinkFilter(CrawlConfig config, string baseUrl)
    {
        _config = config;
        _baseUrl = baseUrl;
        _include = config.Include.Select(Compile).ToList();
        _exclude = config.Exclude.Select(Compile).ToList();
    }

    public string Canonicalize(string url) => UrlUtils.Canonicalize(url, _config.IgnoreQueryString, _config.IgnoreFragments);

    /// <summary>Returns the canonical URL when the link may be crawled, otherwise null.</summary>
    public string? Accept(string? href)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        if (!Uri.TryCreate(href.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        if (DownloadExtensions.Contains(Path.GetExtension(uri.AbsolutePath))) return null;
        var url = uri.ToString();
        if (_config.SameOriginOnly && !UrlUtils.SameOrigin(url, _baseUrl)) return null;
        if (_exclude.Any(rx => SafeMatch(rx, url))) return null;
        if (_include.Count > 0 && !_include.Any(rx => SafeMatch(rx, url))) return null;
        return Canonicalize(url);
    }

    /// <summary>URL to navigate to: the link without its fragment.</summary>
    public static string NavigationUrl(string href)
    {
        var hash = href.IndexOf('#');
        return hash >= 0 ? href[..hash] : href;
    }

    private static Regex Compile(string pattern) => new(pattern, RegexOptions.CultureInvariant, RegexTimeout);

    private static bool SafeMatch(Regex rx, string input)
    {
        try { return rx.IsMatch(input); }
        catch (RegexMatchTimeoutException) { return false; }
    }
}
