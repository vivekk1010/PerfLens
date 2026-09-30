using PerfLens.Core.Abstractions;
using PerfLens.Core.Configuration;
using PerfLens.Core.Results;
using PerfLens.Core.Utilities;

namespace PerfLens.Load;

public static class LoadRequestFactory
{
    /// <summary>
    /// Resolves the endpoint URL and merges headers (Target.UserAgent, Target.Headers, Auth.Headers when Auth.Type is Headers,
    /// endpoint headers; later wins, case-insensitive) and cookies (Target.Cookies, then session cookies when
    /// Auth.ShareCookiesWithApi and the endpoint is same-origin with BaseUrl).
    /// </summary>
    public static ApiLoadRequest Build(
        PerfLensConfig config,
        ApiEndpointConfig endpoint,
        EndpointSource source,
        IReadOnlyDictionary<string, string>? sessionCookies)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(endpoint);

        var target = config.Target;
        string absoluteUrl = UrlUtils.Resolve(target.BaseUrl, endpoint.Url);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(target.UserAgent)) headers["User-Agent"] = target.UserAgent!;
        Merge(headers, target.Headers);
        if (config.Auth.Type == AuthType.Headers) Merge(headers, config.Auth.Headers);
        Merge(headers, endpoint.Headers);

        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
        Uri.TryCreate(absoluteUrl, UriKind.Absolute, out var endpointUri);
        foreach (var c in target.Cookies)
        {
            if (string.IsNullOrEmpty(c.Name)) continue;
            if (!CookieMatches(c, endpointUri, target.BaseUrl)) continue;
            cookies[c.Name] = c.Value;
        }
        if (config.Auth.ShareCookiesWithApi && sessionCookies is not null && UrlUtils.SameOrigin(absoluteUrl, target.BaseUrl))
        {
            foreach (var (name, value) in sessionCookies)
                if (!string.IsNullOrEmpty(name)) cookies[name] = value;
        }

        return new ApiLoadRequest
        {
            Endpoint = endpoint,
            Source = source,
            AbsoluteUrl = absoluteUrl,
            Profile = endpoint.Profile ?? config.Api.Profile,
            Headers = headers,
            Cookies = cookies,
            IgnoreHttpsErrors = target.IgnoreHttpsErrors,
        };
    }

    private static void Merge(Dictionary<string, string> into, Dictionary<string, string>? from)
    {
        if (from is null) return;
        foreach (var (k, v) in from)
            if (!string.IsNullOrWhiteSpace(k)) into[k.Trim()] = v;
    }

    private static bool CookieMatches(CookieConfig cookie, Uri? endpointUri, string baseUrl)
    {
        if (endpointUri is null) return true;
        string? domain = cookie.Domain;
        if (string.IsNullOrWhiteSpace(domain))
            domain = Uri.TryCreate(baseUrl, UriKind.Absolute, out var b) ? b.Host : null;
        if (string.IsNullOrWhiteSpace(domain)) return true;

        domain = domain.Trim().TrimStart('.');
        string host = endpointUri.Host;
        if (!host.Equals(domain, StringComparison.OrdinalIgnoreCase) &&
            !host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
            return false;

        string path = string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path;
        return endpointUri.AbsolutePath.StartsWith(path, StringComparison.Ordinal);
    }
}
