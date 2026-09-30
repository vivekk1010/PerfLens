using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Playwright;
using PerfLens.Cli.Infrastructure;
using PerfLens.Cli.Pipeline;
using PerfLens.Core.Configuration;
using PerfLens.Core.Utilities;
using PerfLens.Reporting;

namespace PerfLens.Cli.Commands;

internal static class UtilityCommands
{
    public static int Init(string[] args)
    {
        var a = ParsedArgs.Parse(args, new HashSet<string>());
        var path = a.Positionals.FirstOrDefault() ?? "perflens.yaml";
        bool force = a.Has("force");
        a.EnsureNoUnknown();

        if (File.Exists(path) && !force)
            throw new UsageException($"{path} already exists. Use --force to overwrite.");

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PerfLens.Cli.template.yaml");
        string template = stream is null ? FallbackTemplate : new StreamReader(stream).ReadToEnd();
        File.WriteAllText(path, template);
        Console.WriteLine($"Wrote {path}. Set target.baseUrl and pages, then run: perflens run -c {path}");
        return ExitCodes.Success;
    }

    public static int Validate(string[] args)
    {
        var a = ParsedArgs.Parse(args, new HashSet<string> { "config" }, new Dictionary<char, string> { ['c'] = "config" });
        var path = a.Get("config") ?? a.Positionals.FirstOrDefault() ?? throw new UsageException("perflens validate -c <config>");
        a.EnsureNoUnknown();

        var config = ConfigLoader.Load(path);
        Console.WriteLine($"Config '{path}' is valid: {config.Name}");
        foreach (var (k, v) in RunPipeline.DescribeSettings(config)) Console.WriteLine($"  {k,-20} {v}");
        foreach (var p in config.Pages) Console.WriteLine($"  page  {p.Name,-24} {UrlUtils.Resolve(config.Target.BaseUrl, p.Path)}");
        if (config.Api.Enabled)
            foreach (var e in config.Api.Endpoints) Console.WriteLine($"  api   {e.Name,-24} {e.Method.ToUpperInvariant()} {UrlUtils.Resolve(config.Target.BaseUrl, e.Url)}");
        return ExitCodes.Success;
    }

    public static async Task<int> ReportAsync(string[] args, CancellationToken ct)
    {
        var a = ParsedArgs.Parse(args, new HashSet<string> { "output", "formats" }, new Dictionary<char, string> { ['o'] = "output" });
        var path = a.Positionals.FirstOrDefault() ?? throw new UsageException("perflens report <results.json> [-o dir]");
        var output = a.Get("output") ?? Path.GetDirectoryName(Path.GetFullPath(path))!;
        var formats = RunCommands.ParseFormats(a.Get("formats")) ?? new[] { "html", "md", "csv" };
        bool open = a.Has("open");
        a.EnsureNoUnknown();

        var run = await ResultsStore.LoadAsync(path, ct);
        var files = await ReportService.CreateDefault().WriteAsync(run, output, formats, ct);
        foreach (var f in files) Console.WriteLine(f);
        if (open && files.FirstOrDefault(f => f.EndsWith(".html")) is { } html) Opener.TryOpen(html, new ConsoleProgressReporter(false));
        return ExitCodes.Success;
    }

    public static int InstallBrowsers(string[] args)
    {
        var a = ParsedArgs.Parse(args, new HashSet<string>());
        bool withDeps = a.Has("with-deps");
        a.EnsureNoUnknown();

        var engines = a.Positionals.Count == 0 ? new List<string> { "chromium" } : a.Positionals.Select(p => p.ToLowerInvariant()).ToList();
        if (engines.Contains("all")) engines = new() { "chromium", "firefox", "webkit" };
        var bad = engines.Where(e => e is not ("chromium" or "firefox" or "webkit")).ToList();
        if (bad.Count > 0) throw new UsageException($"Unknown browser(s): {string.Join(", ", bad)}. Use chromium, firefox, webkit or all.");

        PlatformUtils.ApplyPlaywrightHostOverride();
        var installArgs = new List<string> { "install" };
        if (withDeps) installArgs.Add("--with-deps");
        installArgs.AddRange(engines);
        Console.WriteLine($"Installing Playwright browsers: {string.Join(", ", engines)}{(withDeps ? " (with OS dependencies)" : "")}");
        int code = Microsoft.Playwright.Program.Main(installArgs.ToArray());
        if (code != 0) Console.Error.WriteLine("Browser installation failed. See the output above; on Linux try --with-deps (needs sudo).");
        return code == 0 ? ExitCodes.Success : ExitCodes.RuntimeError;
    }

    public static async Task<int> DoctorAsync()
    {
        PlatformUtils.ApplyPlaywrightHostOverride();
        int problems = 0;
        void Check(bool ok, string what, string? hint = null)
        {
            Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
            Console.Write(ok ? "  [ok]   " : "  [fail] ");
            Console.ResetColor();
            Console.WriteLine(what + (ok || hint is null ? "" : $"\n         {hint}"));
            if (!ok) problems++;
        }

        Console.WriteLine($"PerfLens {RunPipeline.ToolVersion} doctor");
        Check(true, $"Runtime: {RuntimeInformation.FrameworkDescription} on {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        var overrideValue = Environment.GetEnvironmentVariable("PLAYWRIGHT_HOST_PLATFORM_OVERRIDE");
        if (overrideValue is not null) Check(true, $"Playwright host override: {overrideValue}");

        try
        {
            using var pw = await Playwright.CreateAsync();
            await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true });
            var page = await browser.NewPageAsync();
            await page.SetContentAsync("<title>perflens</title><h1>ok</h1>");
            var fcp = await page.EvaluateAsync<double>("() => performance.now()");
            Check(fcp >= 0, $"Chromium {browser.Version} launches headless and runs scripts");
        }
        catch (Exception ex)
        {
            Check(false, "Chromium launch", $"{FirstLine(ex.Message)}\n         Run: perflens install-browsers chromium   (Linux libs: add --with-deps)");
        }

        var cwdWritable = true;
        try { var probe = Path.Combine(Path.GetTempPath(), $"perflens-{Guid.NewGuid():N}"); File.WriteAllText(probe, "x"); File.Delete(probe); }
        catch { cwdWritable = false; }
        Check(cwdWritable, "Temp directory writable");

        Console.WriteLine(problems == 0 ? "All checks passed." : $"{problems} problem(s) found.");
        return problems == 0 ? ExitCodes.Success : ExitCodes.RuntimeError;
    }

    private static string FirstLine(string s) => s.Split('\n')[0].Trim();

    private const string FallbackTemplate = """
        # PerfLens configuration. Full reference: docs/DETAILS.md
        name: My application
        target:
          baseUrl: https://your-app.example.com
        pages:
          - name: Home
            path: /
        crawl:
          enabled: false
        browser:
          iterations: 5
          warmupIterations: 1
        output:
          directory: perflens-results
        """;
}
