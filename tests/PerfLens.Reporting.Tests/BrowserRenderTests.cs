using Microsoft.Playwright;
using Xunit.Abstractions;

namespace PerfLens.Reporting.Tests;

/// <summary>Loads the generated report in headless Chromium and checks that the JS and Chart.js run cleanly.</summary>
public class BrowserRenderTests(ITestOutputHelper output)
{
    public const string SampleDir = "/tmp/perflens-sample-report";

    [Fact]
    public async Task Rich_report_runs_without_js_errors_and_draws_charts()
    {
        var dir = Fixtures.OutputDirWithScreenshot();
        await ReportService.CreateDefault().WriteAsync(Fixtures.Rich(), dir, ["html"], CancellationToken.None);
        CopySample(dir);

        var result = await LoadAsync(Path.Combine(dir, "report.html"), async page =>
        {
            int topLevel = await page.EvaluateAsync<int>("() => Object.keys(Chart.instances).length");
            await page.ClickAsync("#page-0 > summary");
            await page.WaitForTimeoutAsync(300);
            int afterOpen = await page.EvaluateAsync<int>("() => Object.keys(Chart.instances).length");
            await page.ClickAsync("[data-th-filter=failed]");
            int visibleThresholds = await page.EvaluateAsync<int>("() => [...document.querySelectorAll('#threshold-table tbody tr')].filter(r => !r.hidden).length");
            await page.ClickAsync(".sev-toggle[data-sev=critical]");
            int visibleFindings = await page.EvaluateAsync<int>("() => [...document.querySelectorAll('#finding-table tbody tr')].filter(r => !r.hidden).length");
            await page.ClickAsync("[data-sort-pages=name]");
            bool drawn = await page.EvaluateAsync<bool>(
                "() => { const c = document.getElementById('chart-vitals'); const d = c.getContext('2d').getImageData(0, 0, c.width, c.height).data; for (let i = 3; i < d.length; i += 4) if (d[i] !== 0) return true; return false; }");
            if (Directory.Exists(SampleDir))
                await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(SampleDir, "report.png"), FullPage = true });
            return (topLevel, afterOpen, visibleThresholds, visibleFindings, drawn);
        });
        if (result is null) return;
        var (top, afterOpen, th, fi, drawn) = result.Value;

        // gauge, vitals, phases, weight, requests, cls, tbt, scores, regressions + API charts (4 + status doughnut of the failing endpoint).
        Assert.Equal(14, top);
        Assert.True(afterOpen > top, "opening a page card should draw its charts");
        Assert.Equal(2, th);
        Assert.Equal(4, fi);
        Assert.True(drawn, "vitals canvas has no pixels");
    }

    [Fact]
    public async Task Minimal_report_runs_without_js_errors()
    {
        var dir = Fixtures.TempDir();
        await ReportService.CreateDefault().WriteAsync(Fixtures.Minimal(), dir, ["html"], CancellationToken.None);
        var result = await LoadAsync(Path.Combine(dir, "report.html"),
            page => page.EvaluateAsync<int>("() => Object.keys(Chart.instances).length"));
        if (result is null) return;
        Assert.True(result.Value > 0);
    }

    /// <summary>Returns null (test is skipped) when no Playwright Chromium is installed.</summary>
    private async Task<T?> LoadAsync<T>(string file, Func<IPage, Task<T>> act) where T : struct
    {
        if (!OperatingSystem.IsWindows() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PLAYWRIGHT_HOST_PLATFORM_OVERRIDE")))
            Environment.SetEnvironmentVariable("PLAYWRIGHT_HOST_PLATFORM_OVERRIDE", "ubuntu22.04-x64");

        IPlaywright playwright;
        IBrowser browser;
        try
        {
            playwright = await Playwright.CreateAsync();
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        }
        catch (PlaywrightException ex)
        {
            output.WriteLine("Skipping browser test, Chromium unavailable: " + ex.Message);
            return null;
        }

        using (playwright)
        await using (browser)
        {
            var page = await browser.NewPageAsync(new BrowserNewPageOptions { ViewportSize = new ViewportSize { Width = 1366, Height = 900 } });
            var errors = new List<string>();
            var requests = new List<string>();
            page.PageError += (_, e) => errors.Add("pageerror: " + e);
            page.Console += (_, m) => { if (m.Type is "error" or "warning") errors.Add($"console.{m.Type}: {m.Text}"); };
            page.Request += (_, r) => { if (!r.Url.StartsWith("file:") && !r.Url.StartsWith("data:")) requests.Add(r.Url); };

            await page.GotoAsync(new Uri(file).AbsoluteUri, new PageGotoOptions { WaitUntil = WaitUntilState.Load });
            await page.WaitForTimeoutAsync(500);
            Assert.True(await page.EvaluateAsync<bool>("() => typeof Chart === 'function' && !!window.PerfLensReport"), "Chart.js / report JS not initialised");
            var value = await act(page);

            foreach (var e in errors) output.WriteLine(e);
            Assert.Empty(errors);
            Assert.Empty(requests);
            return value;
        }
    }

    private static void CopySample(string dir)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(SampleDir, "screenshots"));
            File.Copy(Path.Combine(dir, "report.html"), Path.Combine(SampleDir, "report.html"), true);
            File.Copy(Path.Combine(dir, "screenshots", "home.png"), Path.Combine(SampleDir, "screenshots", "home.png"), true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
