using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using PerfLens.Core.Abstractions;
using PerfLens.Core.Configuration;
using PerfLens.Core.Utilities;

namespace PerfLens.Browser.Internal;

/// <summary>Performs a scripted UI login once and captures the resulting storage state.</summary>
internal static class LoginAuthenticator
{
    /// <returns>Storage state JSON on success; null when login failed (a warning was recorded).</returns>
    public static async Task<string?> LoginAsync(
        BrowserSession session, PerfLensConfig config, IProgressReporter progress, ICollection<string> warnings, CancellationToken ct)
    {
        var login = config.Auth.Login;
        if (login is null || login.Steps.Count == 0)
        {
            Warn("Login auth configured without steps; continuing unauthenticated.", progress, warnings);
            return null;
        }

        var secrets = login.Steps
            .Where(s => s.Action is LoginAction.Fill or LoginAction.Press && !string.IsNullOrEmpty(s.Value))
            .Select(s => s.Value!)
            .ToList();
        var timeoutMs = Math.Max(1000, login.TimeoutMs);
        var clock = Stopwatch.StartNew();
        float Remaining() => (float)Math.Max(1, timeoutMs - clock.Elapsed.TotalMilliseconds);

        var url = UrlUtils.Resolve(config.Target.BaseUrl, login.Url);
        progress.Info($"Logging in at {UrlUtils.RedactSecrets(url)}");
        string stage = "open login page";
        IBrowserContext? context = null;
        try
        {
            context = await session.NewContextAsync(authenticated: false);
            context.SetDefaultTimeout(timeoutMs);
            context.SetDefaultNavigationTimeout(timeoutMs);
            var page = await context.NewPageAsync();
            await page.GotoAsync(url, new PageGotoOptions { Timeout = Remaining() });

            for (var i = 0; i < login.Steps.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var step = login.Steps[i];
                stage = $"step {i + 1} ({step.Action}{(step.Selector is null ? "" : " " + step.Selector)})";
                progress.Debug($"Login {stage}");
                await RunStepAsync(page, step, Remaining, ct);
            }

            stage = "verify success";
            if (!string.IsNullOrWhiteSpace(login.SuccessUrlPattern))
            {
                var pattern = new Regex(login.SuccessUrlPattern);
                await page.WaitForURLAsync(pattern, new PageWaitForURLOptions { Timeout = Remaining(), WaitUntil = WaitUntilState.Commit });
            }
            if (!string.IsNullOrWhiteSpace(login.SuccessSelector))
            {
                await page.Locator(login.SuccessSelector).First.WaitForAsync(
                    new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = Remaining() });
            }
            if (string.IsNullOrWhiteSpace(login.SuccessUrlPattern) && string.IsNullOrWhiteSpace(login.SuccessSelector))
            {
                progress.Debug("No login success condition configured; assuming success after steps.");
                await page.WaitForLoadStateAsync(LoadState.Load, new PageWaitForLoadStateOptions { Timeout = Remaining() });
            }

            var state = await context.StorageStateAsync();
            progress.Info($"Login succeeded ({clock.ElapsedMilliseconds} ms).");
            return state;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            Warn($"Login failed at {stage}: {ErrorText.Short(ex, secrets)}. Continuing unauthenticated.", progress, warnings);
            return null;
        }
        finally
        {
            await BrowserSession.CloseQuietlyAsync(context);
        }
    }

    private static async Task RunStepAsync(IPage page, LoginStep step, Func<float> remaining, CancellationToken ct)
    {
        switch (step.Action)
        {
            case LoginAction.Fill:
                await Locate(page, step).FillAsync(step.Value ?? "", new LocatorFillOptions { Timeout = remaining() });
                break;
            case LoginAction.Click:
                await Locate(page, step).ClickAsync(new LocatorClickOptions { Timeout = remaining() });
                break;
            case LoginAction.Press:
                if (string.IsNullOrWhiteSpace(step.Value)) throw new ArgumentException("Press step requires a key in 'value'.");
                if (string.IsNullOrWhiteSpace(step.Selector)) await page.Keyboard.PressAsync(step.Value);
                else await Locate(page, step).PressAsync(step.Value, new LocatorPressOptions { Timeout = remaining() });
                break;
            case LoginAction.WaitForSelector:
                await Locate(page, step).WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = remaining() });
                break;
            case LoginAction.WaitForUrl:
                if (string.IsNullOrWhiteSpace(step.Value)) throw new ArgumentException("WaitForUrl step requires a URL regex in 'value'.");
                await page.WaitForURLAsync(new Regex(step.Value), new PageWaitForURLOptions { Timeout = remaining(), WaitUntil = WaitUntilState.Commit });
                break;
            case LoginAction.Wait:
                var ms = int.TryParse(step.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 1000;
                await Task.Delay(Math.Clamp(ms, 0, (int)remaining()), ct);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(step), step.Action, "Unknown login action.");
        }
    }

    private static ILocator Locate(IPage page, LoginStep step)
    {
        if (string.IsNullOrWhiteSpace(step.Selector))
            throw new ArgumentException($"{step.Action} step requires a selector.");
        return page.Locator(step.Selector).First;
    }

    private static void Warn(string message, IProgressReporter progress, ICollection<string> warnings)
    {
        warnings.Add(message);
        progress.Warn(message);
    }
}
