using System.Text.RegularExpressions;

namespace PerfLens.Core.Utilities;

public static partial class UrlUtils
{
    /// <summary>Resolves a path or absolute URL against the base URL.</summary>
    public static string Resolve(string baseUrl, string pathOrUrl)
    {
        if (TryHttpUri(pathOrUrl, out var abs))
            return abs.ToString();
        var b = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
        return new Uri(b, pathOrUrl.TrimStart('/')).ToString();
    }

    /// <summary>
    /// Absolute http(s) URL only. On Unix, Uri.TryCreate treats "/path" as an absolute file:// URI,
    /// so the scheme must be checked explicitly.
    /// </summary>
    public static bool TryHttpUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
        {
            uri = u;
            return true;
        }
        uri = null!;
        return false;
    }

    public static bool SameOrigin(string a, string b) =>
        TryHttpUri(a, out var ua) && TryHttpUri(b, out var ub) &&
        string.Equals(ua.Scheme, ub.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(ua.Host, ub.Host, StringComparison.OrdinalIgnoreCase) && ua.Port == ub.Port;

    [GeneratedRegex(@"^(\d+|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|[0-9a-fA-F]{24,})$")]
    private static partial Regex IdSegment();

    /// <summary>Replaces numeric, GUID and long hex path segments with {id} and drops the query string.</summary>
    public static string NormalizeApiUrl(string url)
    {
        if (!TryHttpUri(url, out var u)) return url;
        var segments = u.AbsolutePath.Split('/').Select(s => IdSegment().IsMatch(s) ? "{id}" : s);
        return $"{u.Scheme}://{u.Authority}{string.Join('/', segments)}";
    }

    /// <summary>Canonical form for crawl de-duplication.</summary>
    public static string Canonicalize(string url, bool ignoreQuery, bool ignoreFragment)
    {
        if (!TryHttpUri(url, out var u)) return url;
        var builder = new UriBuilder(u) { Host = u.Host.ToLowerInvariant() };
        if (ignoreFragment) builder.Fragment = "";
        if (ignoreQuery) builder.Query = "";
        var s = builder.Uri.ToString();
        if (s.EndsWith('/') && builder.Path.Length > 1) s = s.TrimEnd('/');
        return s;
    }

    /// <summary>Removes query-string values that look like secrets before URLs are written to reports.</summary>
    public static string RedactSecrets(string url) =>
        SecretQuery().Replace(url, m => m.Groups[1].Value + "=***");

    [GeneratedRegex(@"(?i)\b((?:access_)?token|api[_-]?key|key|secret|password|pwd|sig|signature|auth)=[^&#]*")]
    private static partial Regex SecretQuery();
}
