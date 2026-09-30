using PerfLens.Core.Results;
using PerfLens.Core.Utilities;

namespace PerfLens.Browser.Internal;

internal sealed record ResourceAggregation(ResourceStats Stats, List<ResourceEntry> TopResources, List<ApiCall> ApiCalls);

/// <summary>
/// Merges Playwright network events with the page's Resource Timing entries. Playwright sees every request
/// (including failures and iframe sub-resources); Resource Timing supplies sizes, cache hits and precise timings.
/// </summary>
internal static class ResourceAggregator
{
    private sealed record Item(ResourceEntry Entry, bool Failed);

    public static ResourceAggregation Build(
        MetricsSnapshot snapshot,
        IReadOnlyList<ObservedRequest> requests,
        string baseUrl,
        int topResources,
        bool captureApiCalls)
    {
        var timings = snapshot.Resources
            .Where(t => IsHttp(t.Name))
            .GroupBy(t => t.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => new Queue<ResourceTiming>(g.OrderBy(t => t.StartTime)), StringComparer.Ordinal);

        var mainDocument = requests.LastOrDefault(r => r.IsMainFrameDocument && !r.IsRedirectHop);
        var documentStart = requests.FirstOrDefault(r => r.IsMainFrameDocument)?.StartEpochMs;
        var items = new List<Item>();
        var apiCalls = new List<ApiCall>();

        foreach (var request in requests)
        {
            if (request.IsRedirectHop || !IsHttp(request.Url)) continue;

            ResourceTiming? timing = null;
            if (ReferenceEquals(request, mainDocument) && snapshot.Navigation is { } nav)
                timing = FromNavigation(nav, request.Url);
            else
                timing = Dequeue(timings, request.OriginalUrl) ?? Dequeue(timings, request.Url);

            var item = FromRequest(request, timing, baseUrl, documentStart);
            items.Add(item);

            if (captureApiCalls && ResourceClassifier.IsApi(request.ResourceType))
                apiCalls.Add(ToApiCall(request, timing));
        }

        foreach (var timing in timings.Values.SelectMany(q => q))
            items.Add(FromTimingOnly(timing, baseUrl));

        var stats = new ResourceStats
        {
            TotalRequests = items.Count,
            TransferBytes = items.Sum(i => i.Entry.TransferBytes),
            DecodedBytes = items.Sum(i => i.Entry.DecodedBytes),
            FailedRequests = items.Count(i => i.Failed),
            ThirdPartyRequests = items.Count(i => i.Entry.ThirdParty),
            CachedRequests = items.Count(i => i.Entry.FromCache),
            ByType = items
                .GroupBy(i => i.Entry.Type)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => new ResourceTypeStat { Count = g.Count(), TransferBytes = g.Sum(i => i.Entry.TransferBytes) }),
        };

        var top = items
            .Select(i => i.Entry)
            .OrderByDescending(e => e.TransferBytes)
            .ThenByDescending(e => e.DurationMs)
            .Take(Math.Max(0, topResources))
            .ToList();

        return new ResourceAggregation(stats, top, apiCalls);
    }

    private static Item FromRequest(ObservedRequest r, ResourceTiming? t, string baseUrl, double? documentStart)
    {
        var status = r.Status ?? (t?.ResponseStatus is > 0 ? t.ResponseStatus : null);
        if (r.Failed && r.Status is null) status = null;

        var fromCache = t is not null && t.TransferSize == 0 && t.DecodedBodySize > 0;
        var transfer = (long)(t?.TransferSize ?? 0);
        var decoded = (long)(t?.DecodedBodySize ?? 0);
        if (!fromCache && transfer == 0 && r.ContentLength is { } length)
        {
            // Cross-origin resources without Timing-Allow-Origin report zero sizes.
            transfer = length;
            if (decoded == 0 && IsIdentityEncoding(r.ContentEncoding)) decoded = length;
        }

        double start = t?.StartTime ?? 0;
        if (t is null && documentStart is { } docStart && r.StartEpochMs is { } reqStart)
            start = Math.Max(0, reqStart - docStart);

        var entry = new ResourceEntry
        {
            Url = UrlUtils.RedactSecrets(r.Url),
            Type = ResourceClassifier.FromPlaywright(r.ResourceType, r.Url),
            Status = status,
            TransferBytes = transfer,
            DecodedBytes = decoded,
            DurationMs = t?.Duration ?? r.ResponseEndMs ?? 0,
            StartMs = start,
            FromCache = fromCache,
            ThirdParty = !UrlUtils.SameOrigin(r.Url, baseUrl),
            ContentEncoding = r.ContentEncoding,
            CacheControl = r.CacheControl,
            ContentType = r.ContentType,
        };
        return new Item(entry, r.Failed || status >= 400);
    }

    private static Item FromTimingOnly(ResourceTiming t, string baseUrl)
    {
        var entry = new ResourceEntry
        {
            Url = UrlUtils.RedactSecrets(t.Name),
            Type = ResourceClassifier.FromInitiator(t.InitiatorType, t.Name),
            Status = t.ResponseStatus is > 0 ? t.ResponseStatus : null,
            TransferBytes = (long)t.TransferSize,
            DecodedBytes = (long)t.DecodedBodySize,
            DurationMs = t.Duration,
            StartMs = t.StartTime,
            FromCache = t.TransferSize == 0 && t.DecodedBodySize > 0,
            ThirdParty = !UrlUtils.SameOrigin(t.Name, baseUrl),
        };
        return new Item(entry, entry.Status >= 400);
    }

    private static ApiCall ToApiCall(ObservedRequest r, ResourceTiming? t)
    {
        long bytes = t is { EncodedBodySize: > 0 } ? (long)t.EncodedBodySize : r.ContentLength ?? 0;
        return new ApiCall
        {
            Method = r.Method,
            Url = UrlUtils.RedactSecrets(r.Url),
            NormalizedUrl = UrlUtils.NormalizeApiUrl(r.Url),
            Status = r.Status,
            DurationMs = r.ResponseEndMs ?? t?.Duration ?? 0,
            Bytes = bytes,
            Failed = r.Failed || r.Status >= 400,
        };
    }

    private static ResourceTiming FromNavigation(NavigationSnapshot nav, string url) => new()
    {
        Name = nav.Name ?? url,
        InitiatorType = "navigation",
        StartTime = 0,
        Duration = nav.Duration,
        TransferSize = nav.TransferSize,
        EncodedBodySize = nav.EncodedBodySize,
        DecodedBodySize = nav.DecodedBodySize,
        ResponseStatus = nav.ResponseStatus,
    };

    private static ResourceTiming? Dequeue(Dictionary<string, Queue<ResourceTiming>> timings, string url)
    {
        if (!timings.TryGetValue(url, out var queue) || queue.Count == 0) return null;
        var timing = queue.Dequeue();
        if (queue.Count == 0) timings.Remove(url);
        return timing;
    }

    private static bool IsIdentityEncoding(string? encoding) =>
        string.IsNullOrEmpty(encoding) || encoding.Equals("identity", StringComparison.OrdinalIgnoreCase);

    private static bool IsHttp(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
