using Microsoft.Playwright;
using PerfLens.Core.Configuration;

namespace PerfLens.Browser.Internal;

internal readonly record struct NetworkConditions(double LatencyMs, double DownloadKbps, double UploadKbps)
{
    public double DownloadBytesPerSecond => DownloadKbps * 1024 / 8;
    public double UploadBytesPerSecond => UploadKbps * 1024 / 8;
}

/// <summary>Network and CPU throttling through the Chrome DevTools Protocol (Chromium only).</summary>
internal static class Throttling
{
    public static NetworkConditions? GetNetworkConditions(BrowserConfig b) => b.NetworkProfile switch
    {
        NetworkProfile.Fast3G => new NetworkConditions(562.5, 1474.56, 675),
        NetworkProfile.Slow4G => new NetworkConditions(150, 1638.4, 750),
        NetworkProfile.Regular4G => new NetworkConditions(20, 4096, 3072),
        NetworkProfile.Custom when b.CustomNetwork is { } c => new NetworkConditions(c.LatencyMs, c.DownloadKbps, c.UploadKbps),
        _ => null,
    };

    public static bool IsRequested(BrowserConfig b) => GetNetworkConditions(b) is not null || b.CpuThrottlingRate > 1;

    public static string Describe(BrowserConfig b)
    {
        var parts = new List<string>();
        if (GetNetworkConditions(b) is { } n)
            parts.Add($"{b.NetworkProfile} ({n.LatencyMs} ms RTT, {n.DownloadKbps} kbps down, {n.UploadKbps} kbps up)");
        if (b.CpuThrottlingRate > 1) parts.Add($"CPU {b.CpuThrottlingRate}x slowdown");
        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    /// <summary>Applies throttling to <paramref name="page"/>. Returns the CDP session to detach afterwards, or null when nothing was applied.</summary>
    public static async Task<ICDPSession?> ApplyAsync(IBrowserContext context, IPage page, BrowserConfig b)
    {
        if (b.Engine != BrowserEngine.Chromium || !IsRequested(b)) return null;
        var cdp = await context.NewCDPSessionAsync(page);
        try
        {
            if (GetNetworkConditions(b) is { } n)
            {
                await cdp.SendAsync("Network.enable");
                await EmulateNetworkAsync(cdp, n);
            }
            if (b.CpuThrottlingRate > 1)
                await cdp.SendAsync("Emulation.setCPUThrottlingRate", new Dictionary<string, object> { ["rate"] = b.CpuThrottlingRate });
            return cdp;
        }
        catch
        {
            await DetachQuietlyAsync(cdp);
            throw;
        }
    }

    /// <summary>
    /// Chrome deprecated Network.emulateNetworkConditions in favour of emulateNetworkConditionsByRule
    /// (an empty urlPattern matches every request); older builds only know the legacy command.
    /// </summary>
    private static async Task EmulateNetworkAsync(ICDPSession cdp, NetworkConditions n)
    {
        try
        {
            await cdp.SendAsync("Network.emulateNetworkConditionsByRule", new Dictionary<string, object>
            {
                ["matchedNetworkConditions"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["urlPattern"] = "",
                        ["latency"] = n.LatencyMs,
                        ["downloadThroughput"] = n.DownloadBytesPerSecond,
                        ["uploadThroughput"] = n.UploadBytesPerSecond,
                    },
                },
            });
        }
        catch (PlaywrightException)
        {
            await cdp.SendAsync("Network.emulateNetworkConditions", new Dictionary<string, object>
            {
                ["offline"] = false,
                ["latency"] = n.LatencyMs,
                ["downloadThroughput"] = n.DownloadBytesPerSecond,
                ["uploadThroughput"] = n.UploadBytesPerSecond,
            });
        }
    }

    public static async Task DetachQuietlyAsync(ICDPSession? cdp)
    {
        if (cdp is null) return;
        try { await cdp.DetachAsync(); }
        catch (PlaywrightException) { }
    }
}
