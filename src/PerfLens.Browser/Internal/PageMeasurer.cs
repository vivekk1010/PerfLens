using System.Diagnostics;
using System.Globalization;
using Microsoft.Playwright;
using PerfLens.Core.Abstractions;
using PerfLens.Core.Configuration;
using PerfLens.Core.Results;
using PerfLens.Core.Utilities;

namespace PerfLens.Browser.Internal;

/// <summary>Loads one page repeatedly and records per-iteration metrics.</summary>
internal sealed class PageMeasurer(BrowserSession session, PerfLensConfig config, IProgressReporter progress, string outputDirectory)
{
    private static readonly TimeSpan MaxInFlightWait = TimeSpan.FromSeconds(2);

    private BrowserConfig Browser => config.Browser;

    public async Task<PageResult> MeasureAsync(MeasurementTarget target, int position, int total, string screenshotSlug, CancellationToken ct)
    {
        var result = new PageResult
        {
            Name = target.Name,
            Url = UrlUtils.RedactSecrets(target.Url),
            Source = target.Source,
            Depth = target.Depth,
        };
        var warmups = Math.Max(0, Browser.WarmupIterations);
        var measured = Math.Max(1, target.Config?.Iterations ?? Browser.Iterations);
        var prefix = $"[{position}/{total}] {target.Name}";
        byte[]? screenshot = null;
        IBrowserContext? sharedContext = null;

        try
        {
            for (var i = 0; i < warmups + measured; i++)
            {
                ct.ThrowIfCancellationRequested();
                var isWarmup = i < warmups;
                var iteration = new PageIteration { Index = i + 1, IsWarmup = isWarmup };
                var label = isWarmup ? $"warmup {i + 1}/{warmups}" : $"iteration {i - warmups + 1}/{measured}";
                IBrowserContext? context = null;
                try
                {
                    if (Browser.CacheMode == CacheMode.Warm)
                    {
                        iteration.ColdCache = sharedContext is null;
                        sharedContext ??= await NewMeasurementContextAsync();
                        context = sharedContext;
                    }
                    else
                    {
                        iteration.ColdCache = true;
                        context = await NewMeasurementContextAsync();
                    }

                    var shot = await RunIterationAsync(context, target, iteration, captureScreenshot: !isWarmup && Browser.CaptureScreenshots, ct);
                    if (shot is not null) screenshot = shot;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ct.ThrowIfCancellationRequested();
                    iteration.Success = false;
                    iteration.Error ??= ErrorText.Short(ex);
                }
                finally
                {
                    if (Browser.CacheMode == CacheMode.Cold) await BrowserSession.CloseQuietlyAsync(context);
                }

                result.Iterations.Add(iteration);
                Report(prefix, label, iteration);
            }
        }
        finally
        {
            await BrowserSession.CloseQuietlyAsync(sharedContext);
        }

        if (!result.Iterations.Any(i => !i.IsWarmup && i.Success))
        {
            var firstError = result.Iterations.FirstOrDefault(i => !i.IsWarmup)?.Error ?? "unknown error";
            result.Error = $"All {measured} measured iteration(s) failed: {firstError}";
        }

        if (screenshot is not null)
            result.ScreenshotPath = await SaveScreenshotAsync(screenshot, screenshotSlug, ct);

        return result;
    }

    private async Task<IBrowserContext> NewMeasurementContextAsync()
    {
        var context = await session.NewContextAsync();
        try
        {
            await context.AddInitScriptAsync(MetricsScript.Init);
            return context;
        }
        catch
        {
            await BrowserSession.CloseQuietlyAsync(context);
            throw;
        }
    }

    /// <returns>Viewport screenshot PNG when requested and the iteration succeeded.</returns>
    private async Task<byte[]?> RunIterationAsync(IBrowserContext context, MeasurementTarget target, PageIteration iteration, bool captureScreenshot, CancellationToken ct)
    {
        IPage? page = null;
        ICDPSession? cdp = null;
        PageEventCollector? collector = null;
        try
        {
            page = await context.NewPageAsync();
            collector = new PageEventCollector(page);
            cdp = await Throttling.ApplyAsync(context, page, Browser);

            var clock = Stopwatch.StartNew();
            IResponse? response;
            try
            {
                response = await page.GotoAsync(target.Url, new PageGotoOptions
                {
                    WaitUntil = MapWaitUntil(target.Config?.WaitUntil ?? Browser.WaitUntil),
                    Timeout = Browser.NavigationTimeoutMs,
                });
                iteration.HttpStatus = response?.Status;
                if (!string.IsNullOrWhiteSpace(target.Config?.WaitForSelector))
                {
                    await page.WaitForSelectorAsync(target.Config.WaitForSelector, new PageWaitForSelectorOptions
                    {
                        State = WaitForSelectorState.Visible,
                        Timeout = Browser.NavigationTimeoutMs,
                    });
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ct.ThrowIfCancellationRequested();
                iteration.WallClockMs = clock.Elapsed.TotalMilliseconds;
                iteration.FinalUrl = SafeUrl(page);
                iteration.Error = ErrorText.Short(ex);
                CopyMessages(collector, iteration);
                return null;
            }
            iteration.WallClockMs = clock.Elapsed.TotalMilliseconds;
            iteration.FinalUrl = SafeUrl(page);

            if (Browser.SettleMs > 0) await Task.Delay(Browser.SettleMs, ct);
            await collector.WaitForInFlightAsync(MaxInFlightWait, ct);

            var snapshot = await CollectMetricsAsync(page, ct);
            ApplySnapshot(iteration, snapshot);
            var aggregation = ResourceAggregator.Build(snapshot, collector.Snapshot(), config.Target.BaseUrl, Browser.TopResources, Browser.CaptureApiCalls);
            iteration.Resources = aggregation.Stats;
            iteration.TopResources = aggregation.TopResources;
            iteration.ApiCalls = aggregation.ApiCalls;
            CopyMessages(collector, iteration);

            iteration.Success = iteration.HttpStatus is null or < 400;
            if (!iteration.Success) iteration.Error = $"HTTP {iteration.HttpStatus}";

            if (captureScreenshot && iteration.Success)
            {
                try
                {
                    return await page.ScreenshotAsync(new PageScreenshotOptions { Type = ScreenshotType.Png, FullPage = false });
                }
                catch (PlaywrightException ex)
                {
                    progress.Debug($"Screenshot failed for {target.Name}: {ErrorText.Short(ex)}");
                }
            }
            return null;
        }
        finally
        {
            collector?.Dispose();
            await Throttling.DetachQuietlyAsync(cdp);
            await BrowserSession.CloseQuietlyAsync(page);
        }
    }

    private static async Task<MetricsSnapshot> CollectMetricsAsync(IPage page, CancellationToken ct)
    {
        // A late client-side navigation can destroy the execution context mid-evaluate; retry once.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return MetricsSnapshot.Parse(await page.EvaluateAsync<string>(MetricsScript.Collect));
            }
            catch (PlaywrightException) when (attempt < 2)
            {
                await Task.Delay(250, ct);
            }
            catch (PlaywrightException ex)
            {
                throw new InvalidOperationException($"Metrics collection failed: {ErrorText.Short(ex)}", ex);
            }
        }
    }

    private static void ApplySnapshot(PageIteration iteration, MetricsSnapshot s)
    {
        if (s.Navigation is { } n)
        {
            iteration.Timing = new NavigationTiming
            {
                RedirectMs = n.Redirect,
                DnsMs = n.Dns,
                ConnectMs = n.Connect,
                TlsMs = n.Tls,
                ServerMs = n.Server,
                TtfbMs = n.Ttfb,
                DownloadMs = n.Download,
                DomInteractiveMs = n.DomInteractive,
                DomContentLoadedMs = n.DomContentLoaded,
                LoadEventMs = n.Load,
                DocumentTransferBytes = (long)n.TransferSize,
            };
        }
        iteration.Vitals = new WebVitals
        {
            FcpMs = s.Fcp,
            LcpMs = s.Lcp,
            Cls = s.Observers ? s.Cls : null,
            TbtMs = s.Observers ? s.Tbt : null,
            LongTaskCount = s.LongTaskCount,
            LcpElement = s.LcpElement,
        };
        iteration.DomNodes = s.DomNodes;
        iteration.JsHeapUsedBytes = s.JsHeapUsed is { } heap ? (long)heap : null;
    }

    private static void CopyMessages(PageEventCollector collector, PageIteration iteration)
    {
        iteration.ConsoleErrors = collector.ConsoleErrors.ToList();
        iteration.PageErrors = collector.PageErrors.ToList();
    }

    private async Task<string?> SaveScreenshotAsync(byte[] png, string slug, CancellationToken ct)
    {
        try
        {
            var directory = Path.Combine(outputDirectory, "screenshots");
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, slug + ".png"), png, ct);
            return $"screenshots/{slug}.png";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            progress.Warn($"Could not save screenshot '{slug}.png': {ErrorText.Short(ex)}");
            return null;
        }
    }

    private void Report(string prefix, string label, PageIteration iteration)
    {
        if (!iteration.Success)
        {
            progress.Warn($"{prefix}  {label}  failed: {iteration.Error}");
            return;
        }
        var lcp = iteration.Vitals.LcpMs is { } v ? $"LCP {v.ToString("F0", CultureInfo.InvariantCulture)} ms" : "LCP n/a";
        progress.Info($"{prefix}  {label}  {lcp}");
        progress.Debug(string.Create(CultureInfo.InvariantCulture,
            $"{prefix}  {label}: status {iteration.HttpStatus?.ToString() ?? "-"}, TTFB {iteration.Timing.TtfbMs:F0} ms, " +
            $"FCP {iteration.Vitals.FcpMs:F0} ms, CLS {iteration.Vitals.Cls:F3}, TBT {iteration.Vitals.TbtMs:F0} ms, " +
            $"{iteration.Resources.TotalRequests} requests, {iteration.Resources.TransferBytes / 1024.0:F1} KB, wall {iteration.WallClockMs:F0} ms"));
    }

    private static string? SafeUrl(IPage page)
    {
        try { return UrlUtils.RedactSecrets(page.Url); }
        catch (PlaywrightException) { return null; }
    }

    internal static WaitUntilState MapWaitUntil(WaitUntil value) => value switch
    {
        WaitUntil.DomContentLoaded => WaitUntilState.DOMContentLoaded,
        WaitUntil.NetworkIdle => WaitUntilState.NetworkIdle,
        WaitUntil.Commit => WaitUntilState.Commit,
        _ => WaitUntilState.Load,
    };
}
