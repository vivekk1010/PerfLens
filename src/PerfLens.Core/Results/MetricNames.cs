namespace PerfLens.Core.Results;

/// <summary>Keys used in <see cref="PageSummary.Metrics"/>. Units are ms unless the name says otherwise.</summary>
public static class MetricNames
{
    public const string Ttfb = "ttfb_ms";
    public const string Dns = "dns_ms";
    public const string Connect = "connect_ms";
    public const string Tls = "tls_ms";
    public const string Server = "server_ms";
    public const string Download = "download_ms";
    public const string DomInteractive = "dom_interactive_ms";
    public const string DomContentLoaded = "dom_content_loaded_ms";
    public const string Load = "load_ms";
    public const string Fcp = "fcp_ms";
    public const string Lcp = "lcp_ms";
    public const string Cls = "cls";
    public const string Tbt = "tbt_ms";
    public const string LongTasks = "long_tasks";
    public const string WallClock = "wall_clock_ms";
    public const string Requests = "requests";
    public const string TransferKb = "transfer_kb";
    public const string DecodedKb = "decoded_kb";
    public const string FailedRequests = "failed_requests";
    public const string DomNodes = "dom_nodes";
    public const string JsHeapMb = "js_heap_mb";
    public const string ConsoleErrors = "console_errors";

    /// <summary>Core Web Vitals / Lighthouse rating bands: value &lt;= Good is good, value &gt; Poor is poor.</summary>
    public static readonly IReadOnlyDictionary<string, (double Good, double Poor)> VitalBands =
        new Dictionary<string, (double, double)>
        {
            [Fcp] = (1800, 3000),
            [Lcp] = (2500, 4000),
            [Cls] = (0.1, 0.25),
            [Tbt] = (200, 600),
            [Ttfb] = (800, 1800),
        };

    /// <summary>Display labels and units for reports.</summary>
    public static readonly IReadOnlyDictionary<string, (string Label, string Unit)> Display =
        new Dictionary<string, (string, string)>
        {
            [Ttfb] = ("Time to First Byte", "ms"),
            [Dns] = ("DNS lookup", "ms"),
            [Connect] = ("TCP connect", "ms"),
            [Tls] = ("TLS handshake", "ms"),
            [Server] = ("Server response", "ms"),
            [Download] = ("Document download", "ms"),
            [DomInteractive] = ("DOM interactive", "ms"),
            [DomContentLoaded] = ("DOMContentLoaded", "ms"),
            [Load] = ("Load event", "ms"),
            [Fcp] = ("First Contentful Paint", "ms"),
            [Lcp] = ("Largest Contentful Paint", "ms"),
            [Cls] = ("Cumulative Layout Shift", ""),
            [Tbt] = ("Total Blocking Time", "ms"),
            [LongTasks] = ("Long tasks", "count"),
            [WallClock] = ("Navigation wall clock", "ms"),
            [Requests] = ("Requests", "count"),
            [TransferKb] = ("Transferred", "KB"),
            [DecodedKb] = ("Decoded size", "KB"),
            [FailedRequests] = ("Failed requests", "count"),
            [DomNodes] = ("DOM nodes", "count"),
            [JsHeapMb] = ("JS heap used", "MB"),
            [ConsoleErrors] = ("Console errors", "count"),
        };
}
