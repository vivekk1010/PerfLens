using System.Diagnostics;
using Microsoft.Playwright;
using PerfLens.Core.Abstractions;
using PerfLens.Core.Configuration;
using PerfLens.Core.Results;
using PerfLens.Core.Utilities;

namespace PerfLens.Browser.Internal;

internal sealed record CrawlOutcome(CrawlResult Result, List<MeasurementTarget> Pages);

/// <summary>Breadth-first, GET-only crawl that discovers pages and broken links.</summary>
internal static class SiteCrawler
{
    private sealed record QueueItem(string Url, string Canonical, int Depth, string? Referrer);

    public static async Task<CrawlOutcome> CrawlAsync(
        BrowserSession session, PerfLensConfig config, ISet<string> configuredCanonicalUrls, IProgressReporter progress, CancellationToken ct)
    {
        var crawl = config.Crawl;
        var baseUrl = config.Target.BaseUrl;
        var filter = new CrawlLinkFilter(crawl, baseUrl);
        var result = new CrawlResult();
        var pages = new List<MeasurementTarget>();
        var clock = Stopwatch.StartNew();

        var queue = new Queue<QueueItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var skipped = new HashSet<string>(StringComparer.Ordinal);

        var seeds = crawl.StartPaths.Count > 0 ? crawl.StartPaths : new List<string> { "/" };
        foreach (var seed in seeds)
        {
            var url = CrawlLinkFilter.NavigationUrl(UrlUtils.Resolve(baseUrl, seed));
            var canonical = filter.Canonicalize(url);
            if (seen.Add(canonical)) queue.Enqueue(new QueueItem(url, canonical, 0, null));
        }

        progress.Info($"Crawling from {string.Join(", ", seeds)} (max depth {crawl.MaxDepth}, max pages {crawl.MaxPages})");
        var waitUntil = config.Browser.WaitUntil switch
        {
            WaitUntil.Commit => WaitUntilState.Commit,
            WaitUntil.DomContentLoaded => WaitUntilState.DOMContentLoaded,
            _ => WaitUntilState.Load,
        };

        IBrowserContext? context = null;
        try
        {
            context = await session.NewContextAsync();
            var page = await context.NewPageAsync();

            while (queue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var item = queue.Dequeue();
                if (visited.Count >= crawl.MaxPages)
                {
                    skipped.Add(item.Canonical);
                    continue;
                }
                visited.Add(item.Canonical);

                var entry = new CrawlEntry
                {
                    Url = UrlUtils.RedactSecrets(item.Url),
                    Depth = item.Depth,
                    Referrer = item.Referrer is null ? null : UrlUtils.RedactSecrets(item.Referrer),
                };
                result.Visited.Add(entry);

                try
                {
                    var response = await page.GotoAsync(item.Url, new PageGotoOptions
                    {
                        WaitUntil = waitUntil,
                        Timeout = config.Browser.NavigationTimeoutMs,
                    });
                    entry.Status = response?.Status;
                    seen.Add(filter.Canonicalize(page.Url));
                    progress.Debug($"Crawl depth {item.Depth} [{entry.Status?.ToString() ?? "-"}] {entry.Url}");

                    if (entry.Status >= 400)
                    {
                        entry.Error = $"HTTP {entry.Status}";
                        result.BrokenLinks.Add(entry);
                        continue;
                    }

                    var contentType = response is null ? null : await SafeHeaderAsync(response, "content-type");
                    var isHtml = contentType is null || contentType.Contains("html", StringComparison.OrdinalIgnoreCase);
                    if (!isHtml) continue;

                    if (!configuredCanonicalUrls.Contains(item.Canonical))
                        pages.Add(new MeasurementTarget(PageName(item.Url, baseUrl), item.Url, PageSource.Crawled, item.Depth, null));

                    var links = await page.EvaluateAsync<string[]>(MetricsScript.ExtractLinks) ?? [];
                    foreach (var link in links)
                    {
                        var canonical = filter.Accept(link);
                        if (canonical is null || seen.Contains(canonical)) continue;
                        if (item.Depth + 1 > crawl.MaxDepth)
                        {
                            skipped.Add(canonical);
                            continue;
                        }
                        seen.Add(canonical);
                        queue.Enqueue(new QueueItem(CrawlLinkFilter.NavigationUrl(link), canonical, item.Depth + 1, item.Url));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ct.ThrowIfCancellationRequested();
                    entry.Error = ErrorText.Short(ex);
                    result.BrokenLinks.Add(entry);
                    progress.Debug($"Crawl failed for {entry.Url}: {entry.Error}");
                }
            }
        }
        finally
        {
            await BrowserSession.CloseQuietlyAsync(context);
        }

        skipped.ExceptWith(visited);
        result.SkippedLinks = skipped.Count;
        result.DurationMs = clock.Elapsed.TotalMilliseconds;
        progress.Info($"Crawl visited {result.Visited.Count} page(s), {result.BrokenLinks.Count} broken, {result.SkippedLinks} skipped by limits ({result.DurationMs:F0} ms).");
        return new CrawlOutcome(result, pages);
    }

    /// <summary>Path + query for same-origin pages ("/" for root); host is prefixed for other origins.</summary>
    internal static string PageName(string url, string baseUrl)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        var name = UrlUtils.RedactSecrets(uri.PathAndQuery);
        return UrlUtils.SameOrigin(url, baseUrl) ? name : uri.Authority + name;
    }

    private static async Task<string?> SafeHeaderAsync(IResponse response, string name)
    {
        try { return await response.HeaderValueAsync(name); }
        catch (PlaywrightException) { return null; }
    }
}
