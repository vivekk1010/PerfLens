using PerfLens.Browser.Internal;
using PerfLens.Core.Abstractions;
using PerfLens.Core.Configuration;
using PerfLens.Core.Results;
using PerfLens.Core.Utilities;

namespace PerfLens.Browser;

/// <summary>
/// Measures pages in a real browser via Playwright: authenticates, optionally crawls, then loads every page
/// sequentially for the configured warm-up and measured iterations.
/// </summary>
public sealed class PlaywrightPageBenchmarkRunner : IPageBenchmarkRunner
{
    public async Task<BrowserRunOutput> RunAsync(PerfLensConfig config, string outputDirectory, IProgressReporter progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(outputDirectory);
        ArgumentNullException.ThrowIfNull(progress);
        ct.ThrowIfCancellationRequested();

        var output = new BrowserRunOutput();
        var targets = ConfiguredTargets(config);

        BrowserSession session;
        try
        {
            session = await BrowserSession.LaunchAsync(config, progress, output.Warnings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = $"Could not launch {config.Browser.Engine} browser: {ErrorText.Short(ex)}";
            progress.Error(message);
            output.Warnings.Add(message);
            output.Pages.AddRange(targets.Select(t => new PageResult
            {
                Name = t.Name,
                Url = UrlUtils.RedactSecrets(t.Url),
                Source = t.Source,
                Depth = t.Depth,
                Error = message,
            }));
            return output;
        }

        await using (session)
        {
            using var abortOnCancel = ct.Register(session.Abort);
            output.BrowserName = session.BrowserName;
            output.BrowserVersion = session.BrowserVersion;
            progress.Info($"Browser: {session.BrowserName} {session.BrowserVersion} ({(config.Browser.Headless ? "headless" : "headed")})");

            if (Throttling.IsRequested(config.Browser))
            {
                if (config.Browser.Engine == BrowserEngine.Chromium)
                    progress.Info($"Throttling: {Throttling.Describe(config.Browser)}");
                else
                    Warn(output, progress, $"Network/CPU throttling is only supported on Chromium; ignored for {config.Browser.Engine}.");
            }

            await AuthenticateAsync(session, config, output, progress, ct);
            await CollectSessionCookiesAsync(session, output, progress, ct);

            if (config.Crawl.Enabled)
            {
                var configuredCanonical = targets
                    .Select(t => UrlUtils.Canonicalize(t.Url, config.Crawl.IgnoreQueryString, config.Crawl.IgnoreFragments))
                    .ToHashSet(StringComparer.Ordinal);
                try
                {
                    var crawl = await SiteCrawler.CrawlAsync(session, config, configuredCanonical, progress, ct);
                    output.Crawl = crawl.Result;
                    var names = targets.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var page in crawl.Pages)
                        targets.Add(page with { Name = UniqueName(page.Name, names) });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ct.ThrowIfCancellationRequested();
                    Warn(output, progress, $"Crawl failed: {ErrorText.Short(ex)}");
                }
            }

            if (targets.Count == 0) Warn(output, progress, "No pages to measure.");

            var measurer = new PageMeasurer(session, config, progress, outputDirectory);
            var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < targets.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var target = targets[i];
                var slug = FileNames.UniqueSlug(target.Name, slugs);
                try
                {
                    output.Pages.Add(await measurer.MeasureAsync(target, i + 1, targets.Count, slug, ct));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ct.ThrowIfCancellationRequested();
                    var error = ErrorText.Short(ex);
                    progress.Warn($"[{i + 1}/{targets.Count}] {target.Name}  could not be measured: {error}");
                    output.Pages.Add(new PageResult
                    {
                        Name = target.Name,
                        Url = UrlUtils.RedactSecrets(target.Url),
                        Source = target.Source,
                        Depth = target.Depth,
                        Error = error,
                    });
                }
            }
        }

        return output;
    }

    private static List<MeasurementTarget> ConfiguredTargets(PerfLensConfig config) =>
        config.Pages
            .Select(p => new MeasurementTarget(
                string.IsNullOrWhiteSpace(p.Name) ? p.Path : p.Name,
                UrlUtils.Resolve(config.Target.BaseUrl, p.Path),
                PageSource.Configured,
                0,
                p))
            .ToList();

    private static async Task AuthenticateAsync(BrowserSession session, PerfLensConfig config, BrowserRunOutput output, IProgressReporter progress, CancellationToken ct)
    {
        switch (config.Auth.Type)
        {
            case AuthType.Login:
                session.StorageState = await LoginAuthenticator.LoginAsync(session, config, progress, output.Warnings, ct);
                break;
            case AuthType.StorageState:
                var path = config.Auth.StorageStatePath;
                try
                {
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                        Warn(output, progress, $"Storage state file not found: '{path}'. Continuing unauthenticated.");
                    else
                    {
                        session.StorageState = await File.ReadAllTextAsync(path, ct);
                        progress.Info($"Using storage state from {path}");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Warn(output, progress, $"Could not read storage state '{path}': {ErrorText.Short(ex)}. Continuing unauthenticated.");
                }
                break;
            case AuthType.Headers:
                progress.Debug($"Sending auth headers: {string.Join(", ", config.Auth.Headers.Keys)}");
                break;
        }
    }

    private static async Task CollectSessionCookiesAsync(BrowserSession session, BrowserRunOutput output, IProgressReporter progress, CancellationToken ct)
    {
        try
        {
            output.SessionCookies = await session.GetSessionCookiesAsync();
            if (output.SessionCookies.Count > 0)
                progress.Debug($"Session cookies: {string.Join(", ", output.SessionCookies.Keys)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            Warn(output, progress, $"Could not read session cookies: {ErrorText.Short(ex)}");
        }
    }

    private static string UniqueName(string name, HashSet<string> used)
    {
        var candidate = name;
        for (var n = 2; !used.Add(candidate); n++) candidate = $"{name} ({n})";
        return candidate;
    }

    private static void Warn(BrowserRunOutput output, IProgressReporter progress, string message)
    {
        output.Warnings.Add(message);
        progress.Warn(message);
    }
}
