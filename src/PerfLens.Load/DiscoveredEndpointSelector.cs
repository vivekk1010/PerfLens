using PerfLens.Core.Configuration;
using PerfLens.Core.Results;
using PerfLens.Core.Utilities;

namespace PerfLens.Load;

public static class DiscoveredEndpointSelector
{
    /// <summary>
    /// Picks the most frequently observed successful same-origin GET API calls, one per normalized URL,
    /// skipping endpoints that are already configured. Never selects third-party hosts.
    /// </summary>
    public static List<ApiEndpointConfig> Select(PerfLensConfig config, IEnumerable<PageResult> pages)
    {
        ArgumentNullException.ThrowIfNull(config);
        var result = new List<ApiEndpointConfig>();
        int max = config.Api.MaxDiscoveredEndpoints;
        string baseUrl = config.Target.BaseUrl;
        if (max <= 0 || pages is null || !Uri.TryCreate(baseUrl, UriKind.Absolute, out _)) return result;

        var groups = new Dictionary<string, Group>(StringComparer.Ordinal);
        int order = 0;

        void Add(string method, string url, string? normalized, int count)
        {
            if (count <= 0 || string.IsNullOrWhiteSpace(url)) return;
            if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)) return;
            if (!UrlUtils.SameOrigin(url, baseUrl)) return;
            string key = string.IsNullOrWhiteSpace(normalized) ? UrlUtils.NormalizeApiUrl(url) : normalized;
            if (!groups.TryGetValue(key, out var g))
                groups[key] = g = new Group(key, url, order++);
            g.Count += count;
        }

        foreach (var page in pages)
        {
            if (page is null) continue;
            var calls = page.MeasuredIterations.SelectMany(i => i.ApiCalls ?? new List<ApiCall>()).ToList();
            if (calls.Count > 0)
            {
                foreach (var c in calls)
                {
                    if (c.Failed || c.Status is >= 400 or 0) continue;
                    Add(c.Method, c.Url, c.NormalizedUrl, 1);
                }
            }
            else if (page.Summary?.ApiCalls is { Count: > 0 } summaries)
            {
                foreach (var s in summaries)
                    Add(s.Method, s.SampleUrl, s.NormalizedUrl, s.Calls - s.Failures);
            }
        }

        var configured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in config.Api.Endpoints)
        {
            if (string.IsNullOrWhiteSpace(e.Url)) continue;
            try
            {
                string resolved = UrlUtils.Resolve(baseUrl, e.Url);
                configured.Add(resolved);
                configured.Add(UrlUtils.NormalizeApiUrl(resolved));
            }
            catch (UriFormatException)
            {
            }
        }

        foreach (var g in groups.Values.OrderByDescending(g => g.Count).ThenBy(g => g.Order))
        {
            if (result.Count >= max) break;
            if (configured.Contains(g.SampleUrl) || configured.Contains(g.NormalizedUrl) ||
                configured.Contains(UrlUtils.NormalizeApiUrl(g.SampleUrl)))
                continue;
            result.Add(new ApiEndpointConfig
            {
                Name = "GET " + PathOf(g.NormalizedUrl),
                Url = g.SampleUrl,
                Method = "GET",
            });
        }
        return result;
    }

    private static string PathOf(string normalizedUrl)
    {
        int scheme = normalizedUrl.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) return normalizedUrl;
        int slash = normalizedUrl.IndexOf('/', scheme + 3);
        return slash < 0 ? "/" : normalizedUrl[slash..];
    }

    private sealed class Group(string normalizedUrl, string sampleUrl, int order)
    {
        public string NormalizedUrl { get; } = normalizedUrl;
        public string SampleUrl { get; } = sampleUrl;
        public int Order { get; } = order;
        public int Count { get; set; }
    }
}
