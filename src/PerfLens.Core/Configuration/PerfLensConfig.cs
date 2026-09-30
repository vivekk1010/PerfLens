using System.Text.Json.Serialization;

namespace PerfLens.Core.Configuration;

/// <summary>Root configuration for a PerfLens benchmark run. Loaded from YAML or JSON.</summary>
public sealed class PerfLensConfig
{
    public string Name { get; set; } = "PerfLens run";
    public string? Description { get; set; }
    public TargetConfig Target { get; set; } = new();
    public AuthConfig Auth { get; set; } = new();
    public List<PageConfig> Pages { get; set; } = new();
    public CrawlConfig Crawl { get; set; } = new();
    public BrowserConfig Browser { get; set; } = new();
    public ApiLoadConfig Api { get; set; } = new();
    public ThresholdsConfig Thresholds { get; set; } = new();
    public OutputConfig Output { get; set; } = new();
}

public sealed class TargetConfig
{
    /// <summary>Base URL of the application under test, e.g. https://staging.example.com.</summary>
    public string BaseUrl { get; set; } = "";
    /// <summary>Extra HTTP headers sent with every browser and API request.</summary>
    public Dictionary<string, string> Headers { get; set; } = new();
    public List<CookieConfig> Cookies { get; set; } = new();
    public bool IgnoreHttpsErrors { get; set; }
    public string? UserAgent { get; set; }
}

public sealed class CookieConfig
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    /// <summary>Defaults to the host of Target.BaseUrl.</summary>
    public string? Domain { get; set; }
    public string Path { get; set; } = "/";
}

public enum AuthType { None, Headers, Login, StorageState }

public sealed class AuthConfig
{
    public AuthType Type { get; set; } = AuthType.None;
    /// <summary>For Type=Headers, e.g. Authorization: Bearer ${ENV:TOKEN}.</summary>
    public Dictionary<string, string> Headers { get; set; } = new();
    /// <summary>For Type=Login: a scripted UI login performed once before measuring.</summary>
    public LoginConfig? Login { get; set; }
    /// <summary>For Type=StorageState: path to a Playwright storage state JSON file.</summary>
    public string? StorageStatePath { get; set; }
    /// <summary>Reuse the authenticated browser cookies for API load tests.</summary>
    public bool ShareCookiesWithApi { get; set; } = true;
}

public sealed class LoginConfig
{
    /// <summary>Login page URL, relative to BaseUrl or absolute.</summary>
    public string Url { get; set; } = "/login";
    public List<LoginStep> Steps { get; set; } = new();
    /// <summary>Regex the URL must match after login for it to count as successful.</summary>
    public string? SuccessUrlPattern { get; set; }
    /// <summary>CSS selector that must be visible after login.</summary>
    public string? SuccessSelector { get; set; }
    public int TimeoutMs { get; set; } = 30000;
}

public enum LoginAction { Fill, Click, Press, WaitForSelector, WaitForUrl, Wait }

public sealed class LoginStep
{
    public LoginAction Action { get; set; }
    public string? Selector { get; set; }
    /// <summary>Value for Fill/Press, URL regex for WaitForUrl, milliseconds for Wait. Supports ${ENV:NAME}.</summary>
    public string? Value { get; set; }
}

public enum WaitUntil { Load, DomContentLoaded, NetworkIdle, Commit }

public sealed class PageConfig
{
    public string Name { get; set; } = "";
    /// <summary>Path relative to BaseUrl (e.g. /products) or an absolute URL.</summary>
    public string Path { get; set; } = "/";
    /// <summary>Optional selector that must appear before the page counts as ready.</summary>
    public string? WaitForSelector { get; set; }
    public WaitUntil? WaitUntil { get; set; }
    /// <summary>Overrides Browser.Iterations for this page.</summary>
    public int? Iterations { get; set; }
    /// <summary>Per-page thresholds; override the global page thresholds.</summary>
    public PageThresholds? Thresholds { get; set; }
}

public sealed class CrawlConfig
{
    public bool Enabled { get; set; }
    /// <summary>Crawl seeds relative to BaseUrl. Defaults to "/".</summary>
    public List<string> StartPaths { get; set; } = new() { "/" };
    public int MaxDepth { get; set; } = 2;
    public int MaxPages { get; set; } = 20;
    /// <summary>Only URLs matching at least one regex are kept (empty = all).</summary>
    public List<string> Include { get; set; } = new();
    /// <summary>URLs matching any regex are skipped (e.g. logout links).</summary>
    public List<string> Exclude { get; set; } = new() { "(?i)logout", "(?i)signout", "(?i)delete" };
    public bool SameOriginOnly { get; set; } = true;
    public bool IgnoreQueryString { get; set; } = true;
    public bool IgnoreFragments { get; set; } = true;
}

public enum BrowserEngine { Chromium, Firefox, Webkit }
public enum CacheMode { Cold, Warm }
public enum NetworkProfile { None, Fast3G, Slow4G, Regular4G, Custom }

public sealed class BrowserConfig
{
    public BrowserEngine Engine { get; set; } = BrowserEngine.Chromium;
    public bool Headless { get; set; } = true;
    /// <summary>Measured iterations per page.</summary>
    public int Iterations { get; set; } = 5;
    /// <summary>Unmeasured warm-up loads per page (recorded with IsWarmup=true, excluded from stats).</summary>
    public int WarmupIterations { get; set; } = 1;
    /// <summary>Cold = fresh browser context per iteration (empty cache). Warm = one context per page.</summary>
    public CacheMode CacheMode { get; set; } = CacheMode.Cold;
    public int ViewportWidth { get; set; } = 1366;
    public int ViewportHeight { get; set; } = 768;
    /// <summary>Optional Playwright device name, e.g. "iPhone 13" or "Pixel 7". Overrides viewport.</summary>
    public string? Device { get; set; }
    public NetworkProfile NetworkProfile { get; set; } = NetworkProfile.None;
    public CustomNetwork? CustomNetwork { get; set; }
    /// <summary>CPU slowdown factor (1 = none, 4 = mid-tier mobile). Chromium only.</summary>
    public double CpuThrottlingRate { get; set; } = 1;
    public int NavigationTimeoutMs { get; set; } = 30000;
    /// <summary>Time to wait after the ready condition so LCP/CLS/long tasks settle.</summary>
    public int SettleMs { get; set; } = 1500;
    public WaitUntil WaitUntil { get; set; } = WaitUntil.Load;
    public bool CaptureScreenshots { get; set; } = true;
    public bool CaptureApiCalls { get; set; } = true;
    /// <summary>Number of largest/slowest resources kept per iteration.</summary>
    public int TopResources { get; set; } = 15;
}

public sealed class CustomNetwork
{
    public double LatencyMs { get; set; } = 150;
    public double DownloadKbps { get; set; } = 1600;
    public double UploadKbps { get; set; } = 750;
}

public enum LoadMode
{
    /// <summary>Fixed number of concurrent virtual users, each sending back-to-back requests.</summary>
    Closed,
    /// <summary>Fixed arrival rate (requests/second) regardless of response time. Avoids coordinated omission.</summary>
    Open
}

public sealed class ApiLoadConfig
{
    public bool Enabled { get; set; }
    public List<ApiEndpointConfig> Endpoints { get; set; } = new();
    /// <summary>Also load-test GET API calls discovered while benchmarking pages.</summary>
    public bool UseDiscoveredEndpoints { get; set; }
    public int MaxDiscoveredEndpoints { get; set; } = 10;
    public LoadProfile Profile { get; set; } = new();
}

public sealed class ApiEndpointConfig
{
    public string Name { get; set; } = "";
    /// <summary>Relative to BaseUrl or absolute.</summary>
    public string Url { get; set; } = "";
    public string Method { get; set; } = "GET";
    public Dictionary<string, string> Headers { get; set; } = new();
    public string? Body { get; set; }
    public string ContentType { get; set; } = "application/json";
    /// <summary>Status codes counted as success. Empty = any 2xx/3xx.</summary>
    public List<int> ExpectedStatus { get; set; } = new();
    /// <summary>Per-endpoint load profile override.</summary>
    public LoadProfile? Profile { get; set; }
    public ApiThresholds? Thresholds { get; set; }
}

public sealed class LoadProfile
{
    public LoadMode Mode { get; set; } = LoadMode.Closed;
    public int Concurrency { get; set; } = 10;
    public double RatePerSecond { get; set; } = 20;
    public int DurationSeconds { get; set; } = 20;
    public int WarmupSeconds { get; set; } = 3;
    public int TimeoutMs { get; set; } = 10000;
    /// <summary>Optional hard cap on measured requests.</summary>
    public int? MaxRequests { get; set; }
}

public sealed class ThresholdsConfig
{
    /// <summary>Statistic used for page thresholds: p50, p75, p90, p95, mean, max.</summary>
    public string PageStatistic { get; set; } = "p75";
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public PageThresholds Page { get; set; } = PageThresholds.Defaults();
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public ApiThresholds Api { get; set; } = ApiThresholds.Defaults();
    /// <summary>Fail the run (non-zero exit code) when any threshold fails.</summary>
    public bool FailOnThresholdBreach { get; set; } = true;
    /// <summary>Minimum overall score (0-100). Null = not enforced.</summary>
    public double? MinScore { get; set; }
}

/// <summary>
/// Page limits. Unset (null) fields inherit from the global thresholds.page block, whose defaults
/// come from <see cref="Defaults"/>. Set a field to null in the global block to disable it.
/// </summary>
public sealed class PageThresholds
{
    public double? TtfbMs { get; set; }
    public double? FcpMs { get; set; }
    public double? LcpMs { get; set; }
    public double? Cls { get; set; }
    public double? TbtMs { get; set; }
    public double? LoadMs { get; set; }
    public double? PageWeightKb { get; set; }
    public double? RequestCount { get; set; }
    public int? MaxConsoleErrors { get; set; }

    public static PageThresholds Defaults() => new()
    {
        TtfbMs = 800, FcpMs = 1800, LcpMs = 2500, Cls = 0.1, TbtMs = 200,
        LoadMs = 4000, PageWeightKb = 3000, RequestCount = 120, MaxConsoleErrors = 0,
    };
}

/// <summary>API limits. Unset fields inherit from thresholds.api, whose defaults come from <see cref="Defaults"/>.</summary>
public sealed class ApiThresholds
{
    public double? P50Ms { get; set; }
    public double? P95Ms { get; set; }
    public double? P99Ms { get; set; }
    public double? ErrorRatePct { get; set; }
    public double? MinRps { get; set; }

    public static ApiThresholds Defaults() => new() { P95Ms = 800, P99Ms = 1500, ErrorRatePct = 1 };
}

public sealed class OutputConfig
{
    public string Directory { get; set; } = "perflens-results";
    /// <summary>html, json, md, csv.</summary>
    public List<string> Formats { get; set; } = new() { "html", "json", "md", "csv" };
    /// <summary>Each run writes to Directory/&lt;timestamp&gt; when true.</summary>
    public bool TimestampedSubfolder { get; set; } = true;
    /// <summary>Previous results.json to compare against for regressions.</summary>
    public string? BaselinePath { get; set; }
    public double RegressionTolerancePct { get; set; } = 10;
    public string? RunLabel { get; set; }
}
