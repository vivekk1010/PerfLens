using Microsoft.Playwright;
using PerfLens.Core.Abstractions;
using PerfLens.Core.Configuration;
using PerfLens.Core.Utilities;

namespace PerfLens.Browser.Internal;

/// <summary>Owns the Playwright driver and browser, and creates consistently configured contexts.</summary>
internal sealed class BrowserSession : IAsyncDisposable
{
    private readonly IPlaywright _playwright;
    private readonly IBrowser _browser;
    private readonly PerfLensConfig _config;
    private readonly BrowserNewContextOptions _baseOptions;
    private readonly List<Cookie> _cookies;
    private int _disposed;

    private BrowserSession(IPlaywright playwright, IBrowser browser, PerfLensConfig config, BrowserNewContextOptions baseOptions, string browserName)
    {
        _playwright = playwright;
        _browser = browser;
        _config = config;
        _baseOptions = baseOptions;
        _cookies = BuildCookies(config);
        BrowserName = browserName;
    }

    public string BrowserName { get; }
    public string BrowserVersion => _browser.Version;
    public BrowserEngine Engine => _config.Browser.Engine;

    /// <summary>Playwright storage state (JSON) applied to every authenticated context; null = unauthenticated.</summary>
    public string? StorageState { get; set; }

    public static async Task<BrowserSession> LaunchAsync(PerfLensConfig config, IProgressReporter progress, ICollection<string> warnings)
    {
        PlatformUtils.ApplyPlaywrightHostOverride();
        var playwright = await Playwright.CreateAsync();
        try
        {
            var type = config.Browser.Engine switch
            {
                BrowserEngine.Firefox => playwright.Firefox,
                BrowserEngine.Webkit => playwright.Webkit,
                _ => playwright.Chromium,
            };
            var browser = await type.LaunchAsync(new BrowserTypeLaunchOptions { Headless = config.Browser.Headless });
            var options = BuildContextOptions(config, playwright, progress, warnings);
            return new BrowserSession(playwright, browser, config, options, type.Name);
        }
        catch
        {
            playwright.Dispose();
            throw;
        }
    }

    public async Task<IBrowserContext> NewContextAsync(bool authenticated = true)
    {
        var options = new BrowserNewContextOptions(_baseOptions);
        if (authenticated && StorageState is not null) options.StorageState = StorageState;
        var context = await _browser.NewContextAsync(options);
        try
        {
            context.SetDefaultNavigationTimeout(_config.Browser.NavigationTimeoutMs);
            context.SetDefaultTimeout(_config.Browser.NavigationTimeoutMs);
            if (_cookies.Count > 0) await context.AddCookiesAsync(_cookies);
            return context;
        }
        catch
        {
            await CloseQuietlyAsync(context);
            throw;
        }
    }

    /// <summary>name → value of the session cookies that apply to the BaseUrl host.</summary>
    public async Task<Dictionary<string, string>> GetSessionCookiesAsync()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Uri.TryCreate(_config.Target.BaseUrl, UriKind.Absolute, out var baseUri)) return result;
        var context = await NewContextAsync();
        try
        {
            foreach (var cookie in await context.CookiesAsync())
            {
                if (DomainMatches(cookie.Domain, baseUri.Host)) result[cookie.Name] = cookie.Value;
            }
        }
        finally
        {
            await CloseQuietlyAsync(context);
        }
        return result;
    }

    internal static bool DomainMatches(string? cookieDomain, string host)
    {
        if (string.IsNullOrEmpty(cookieDomain)) return false;
        var domain = cookieDomain.TrimStart('.');
        return host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Closes the browser so in-flight Playwright calls fail fast (used on cancellation).</summary>
    public void Abort() => _ = CloseBrowserQuietlyAsync();

    public static async Task CloseQuietlyAsync(IBrowserContext? context)
    {
        if (context is null) return;
        try { await context.CloseAsync(); }
        catch (PlaywrightException) { }
    }

    public static async Task CloseQuietlyAsync(IPage? page)
    {
        if (page is null) return;
        try { await page.CloseAsync(); }
        catch (PlaywrightException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        await CloseBrowserQuietlyAsync();
        _playwright.Dispose();
    }

    private async Task CloseBrowserQuietlyAsync()
    {
        try { await _browser.CloseAsync(); }
        catch (Exception ex) when (ex is PlaywrightException or ObjectDisposedException or InvalidOperationException) { }
    }

    internal static Dictionary<string, string> BuildHeaders(PerfLensConfig config)
    {
        var headers = new Dictionary<string, string>(config.Target.Headers, StringComparer.OrdinalIgnoreCase);
        if (config.Auth.Type == AuthType.Headers)
        {
            foreach (var (name, value) in config.Auth.Headers) headers[name] = value;
        }
        return headers;
    }

    private static BrowserNewContextOptions BuildContextOptions(PerfLensConfig config, IPlaywright playwright, IProgressReporter progress, ICollection<string> warnings)
    {
        var b = config.Browser;
        var options = new BrowserNewContextOptions();

        if (!string.IsNullOrWhiteSpace(b.Device))
        {
            var match = playwright.Devices.Keys.FirstOrDefault(k => k.Equals(b.Device, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                var device = playwright.Devices[match];
                options.UserAgent = device.UserAgent;
                options.ViewportSize = device.ViewportSize;
                options.ScreenSize = device.ScreenSize;
                options.DeviceScaleFactor = device.DeviceScaleFactor;
                options.IsMobile = b.Engine == BrowserEngine.Firefox ? null : device.IsMobile;
                options.HasTouch = device.HasTouch;
                progress.Debug($"Emulating device '{match}'.");
            }
            else
            {
                var message = $"Unknown device '{b.Device}'; using viewport {b.ViewportWidth}x{b.ViewportHeight}.";
                warnings.Add(message);
                progress.Warn(message);
            }
        }

        options.ViewportSize ??= new ViewportSize { Width = b.ViewportWidth, Height = b.ViewportHeight };
        options.IgnoreHTTPSErrors = config.Target.IgnoreHttpsErrors;
        if (!string.IsNullOrWhiteSpace(config.Target.UserAgent)) options.UserAgent = config.Target.UserAgent;

        var headers = BuildHeaders(config);
        if (headers.Count > 0) options.ExtraHTTPHeaders = headers;
        return options;
    }

    private static List<Cookie> BuildCookies(PerfLensConfig config)
    {
        var host = Uri.TryCreate(config.Target.BaseUrl, UriKind.Absolute, out var u) ? u.Host : "localhost";
        return config.Target.Cookies
            .Where(c => !string.IsNullOrEmpty(c.Name))
            .Select(c => new Cookie
            {
                Name = c.Name,
                Value = c.Value,
                Domain = string.IsNullOrWhiteSpace(c.Domain) ? host : c.Domain,
                Path = string.IsNullOrWhiteSpace(c.Path) ? "/" : c.Path,
            })
            .ToList();
    }
}
