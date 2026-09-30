using System.Text.Json;
using System.Text.RegularExpressions;
using PerfLens.Core.Results;

namespace PerfLens.Reporting.Tests;

public class WriterTests
{
    private static readonly string[] AllFormats = ["html", "json", "md", "csv"];

    [Fact]
    public async Task ReportService_writes_all_formats()
    {
        var dir = Fixtures.OutputDirWithScreenshot();
        var files = await ReportService.CreateDefault().WriteAsync(Fixtures.Rich(), dir, AllFormats, CancellationToken.None);

        string[] expected = ["report.html", "results.json", "summary.md", "pages.csv", "iterations.csv", "api.csv", "findings.csv", "thresholds.csv"];
        foreach (var name in expected)
        {
            Assert.Contains(files, f => Path.GetFileName(f) == name);
            Assert.True(new FileInfo(Path.Combine(dir, name)).Length > 0, name);
        }
    }

    [Fact]
    public async Task ReportService_formats_are_case_insensitive_and_unknown_throws()
    {
        var dir = Path.Combine(Fixtures.TempDir(), "nested", "out");
        var files = await ReportService.CreateDefault().WriteAsync(Fixtures.Minimal(), dir, ["JSON", "Md"], CancellationToken.None);
        Assert.Equal(2, files.Count);
        Assert.True(Directory.Exists(dir));

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            ReportService.CreateDefault().WriteAsync(Fixtures.Minimal(), Fixtures.TempDir(), ["html", "pdf"], CancellationToken.None));
        Assert.Contains("pdf", ex.Message);
    }

    [Fact]
    public async Task Minimal_run_renders_in_every_format()
    {
        var dir = Fixtures.TempDir();
        var files = await ReportService.CreateDefault().WriteAsync(Fixtures.Minimal(), dir, AllFormats, CancellationToken.None);
        Assert.Equal(8, files.Count);
        var html = await File.ReadAllTextAsync(Path.Combine(dir, "report.html"));
        Assert.Contains("Not analyzed", html);
        Assert.DoesNotContain("id=\"api\"", html);
        Assert.DoesNotContain("id=\"crawl\"", html);
        Assert.DoesNotContain("id=\"baseline\"", html);
        ParseDataBlob(html);
    }

    [Fact]
    public void Empty_run_renders()
    {
        var html = HtmlReportWriter.Render(new RunResult());
        Assert.Contains("No pages were measured", html);
        Assert.NotEmpty(MarkdownReportWriter.Render(new RunResult()));
        Assert.NotEmpty(CsvReportWriter.Pages(new RunResult()));
    }

    [Fact]
    public void Html_contains_all_sections_and_inline_chartjs()
    {
        var html = HtmlReportWriter.Render(Fixtures.Rich(), Fixtures.OutputDirWithScreenshot());

        foreach (var id in new[] { "summary", "settings", "overview", "pages", "api", "thresholds", "findings", "baseline", "crawl", "warnings", "glossary" })
            Assert.Contains($"<section id=\"{id}\"", html);
        foreach (var canvas in new[] { "chart-score-gauge", "chart-vitals", "chart-phases", "chart-weight", "chart-requests", "chart-cls", "chart-tbt", "chart-scores", "chart-regressions", "pc-iter-0", "pc-types-0", "api-lat-0", "api-time-0", "api-hist-0", "api-status-0" })
            Assert.Contains($"id=\"{canvas}\"", html);

        Assert.Contains("Chart.js v4.4.7", html);
        Assert.DoesNotMatch(new Regex("<script[^>]+src=", RegexOptions.IgnoreCase), html);
        Assert.DoesNotMatch(new Regex("<link[^>]+href=", RegexOptions.IgnoreCase), html);
        Assert.DoesNotMatch(new Regex(@"url\(\s*['""]?https?:", RegexOptions.IgnoreCase), html);
        Assert.Contains("PASS", html);
        Assert.Contains("FAIL", html);
        Assert.Contains("Grade C", html);
    }

    [Fact]
    public void Html_encodes_untrusted_strings()
    {
        var html = HtmlReportWriter.Render(Fixtures.Rich());
        Assert.DoesNotContain("<script>alert(1)", html);
        Assert.DoesNotContain("<img src=x onerror", html);
        Assert.DoesNotContain("<b>boom</b>", html);
        Assert.DoesNotContain("href=\"javascript:", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);

        // Exactly our three script elements: data blob, Chart.js, report JS.
        Assert.Equal(3, Regex.Matches(html, "<script", RegexOptions.IgnoreCase).Count);
        Assert.Equal(3, Regex.Matches(html, "</script", RegexOptions.IgnoreCase).Count);
    }

    [Fact]
    public void Html_data_blob_parses_and_contains_pages()
    {
        var html = HtmlReportWriter.Render(Fixtures.Rich());
        var doc = ParseDataBlob(html);
        var pages = doc.RootElement.GetProperty("pages");
        Assert.Equal(3, pages.GetArrayLength());
        Assert.Contains(pages.EnumerateArray(), p => p.GetProperty("name").GetString() == Fixtures.EvilName);
        var evil = pages.EnumerateArray().First(p => p.GetProperty("name").GetString() == Fixtures.EvilName);
        Assert.Equal(JsonValueKind.Null, evil.GetProperty("lcp").ValueKind);
        Assert.Equal(2, doc.RootElement.GetProperty("api").GetArrayLength());
        Assert.Equal("p75", doc.RootElement.GetProperty("statistic").GetString());
    }

    [Fact]
    public void Html_embeds_small_screenshots_as_data_uri()
    {
        var html = HtmlReportWriter.Render(Fixtures.Rich(), Fixtures.OutputDirWithScreenshot());
        Assert.Contains("src=\"data:image/png;base64," + Convert.ToBase64String(Fixtures.Png1x1) + "\"", html);
    }

    [Fact]
    public void Html_links_screenshot_when_file_missing()
    {
        var html = HtmlReportWriter.Render(Fixtures.Rich(), Fixtures.TempDir());
        Assert.Contains("src=\"screenshots/home.png\"", html);
    }

    [Fact]
    public void Html_formats_numbers_invariantly()
    {
        var prev = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var html = HtmlReportWriter.Render(Fixtures.Rich());
            Assert.Contains("1.35 s", html);  // Home LCP p75
            Assert.Matches(@"\b\d+ ms\b", html);
        }
        finally { Thread.CurrentThread.CurrentCulture = prev; }
    }

    [Fact]
    public async Task Json_round_trips_through_ResultsStore()
    {
        var dir = Fixtures.TempDir();
        var run = Fixtures.Rich();
        var files = await new JsonReportWriter().WriteAsync(run, dir, CancellationToken.None);
        var loaded = await ResultsStore.LoadAsync(files[0], CancellationToken.None);

        Assert.Equal(run.RunId, loaded.RunId);
        Assert.Equal(run.Pages.Count, loaded.Pages.Count);
        Assert.Equal(Fixtures.EvilName, loaded.Pages[1].Name);
        Assert.Null(loaded.Pages[1].Iterations[1].Vitals.LcpMs);
        Assert.Equal(run.Analysis!.Findings.Count, loaded.Analysis!.Findings.Count);
        Assert.Equal(Severity.High, loaded.Analysis.Findings[0].Severity);
        Assert.Equal(run.Pages[0].Summary!.Metrics[MetricNames.Lcp].P75, loaded.Pages[0].Summary!.Metrics[MetricNames.Lcp].P75);
        Assert.Equal(run.Api!.Endpoints[0].Timeline.Count, loaded.Api!.Endpoints[0].Timeline.Count);
        Assert.Contains("\"severity\": \"high\"", await File.ReadAllTextAsync(files[0]));

        // A directory works too.
        Assert.Equal(run.RunId, (await ResultsStore.LoadAsync(dir, CancellationToken.None)).RunId);
    }

    [Fact]
    public async Task ResultsStore_reports_missing_and_invalid_files_clearly()
    {
        var dir = Fixtures.TempDir();
        var missing = await Assert.ThrowsAsync<FileNotFoundException>(() => ResultsStore.LoadAsync(Path.Combine(dir, "nope.json"), CancellationToken.None));
        Assert.Contains("nope.json", missing.Message);

        var bad = Path.Combine(dir, "bad.json");
        await File.WriteAllTextAsync(bad, "{ \"runId\": ");
        var invalid = await Assert.ThrowsAsync<InvalidDataException>(() => ResultsStore.LoadAsync(bad, CancellationToken.None));
        Assert.Contains("not a valid PerfLens results.json", invalid.Message);

        var nul = Path.Combine(dir, "null.json");
        await File.WriteAllTextAsync(nul, "null");
        await Assert.ThrowsAsync<InvalidDataException>(() => ResultsStore.LoadAsync(nul, CancellationToken.None));
    }

    [Fact]
    public void Csv_quotes_commas_quotes_and_newlines()
    {
        var findings = CsvReportWriter.Findings(Fixtures.Rich());
        Assert.Contains("\"Search, \"\"quoted\"\"\nnext line\"", findings);
        var rows = ParseCsv(findings);
        Assert.Equal(6, rows.Count);
        Assert.All(rows, r => Assert.Equal(9, r.Count));
        Assert.Equal(Fixtures.CsvTricky, rows.First(r => r[2] == "PAGE_SLOW_TTFB")[5]);
        Assert.Equal("Critical", rows[1][0]);
    }

    [Fact]
    public void Csv_files_have_consistent_columns()
    {
        var run = Fixtures.Rich();
        foreach (var (content, expectedRows) in new[]
                 {
                     (CsvReportWriter.Pages(run), 4), (CsvReportWriter.Iterations(run), 1 + 4 + 4), (CsvReportWriter.Api(run), 3),
                     (CsvReportWriter.Thresholds(run), 5), (CsvReportWriter.Findings(run), 6),
                 })
        {
            var rows = ParseCsv(content);
            Assert.Equal(expectedRows, rows.Count);
            Assert.All(rows, r => Assert.Equal(rows[0].Count, r.Count));
        }
        var pages = ParseCsv(CsvReportWriter.Pages(run));
        int col = pages[0].IndexOf("lcp_ms_median");
        Assert.Equal("1350", pages[1][col]);
        Assert.Equal("", pages[2][col]);  // null LCP
        Assert.Equal("false", ParseCsv(CsvReportWriter.Thresholds(run))[1][6]);  // failed first
    }

    [Fact]
    public void Markdown_contains_page_table_and_sections()
    {
        var md = MarkdownReportWriter.Render(Fixtures.Rich());
        Assert.Contains("| Page | Iterations | TTFB | FCP | LCP | CLS | TBT | Load | Weight | Requests | Score |", md);
        Assert.Contains("| Home | 3 |", md);
        Assert.Contains("❌ FAIL", md);
        Assert.Contains("## API load tests", md);
        Assert.Contains("## Threshold failures", md);
        Assert.Contains("## Top findings", md);
        Assert.Contains("## Baseline comparison", md);
        Assert.Contains("+22.7%", md);
        Assert.DoesNotContain("<script>", md);
        Assert.Contains("Search, \"quoted\" next line |", md);  // newline in a cell was flattened
        Assert.Contains("Checkout &lt;script&gt;alert(1)&lt;/script&gt;", md);
    }

    [Fact]
    public void Inline_report_js_is_valid_javascript()
    {
        var node = NodeLocator.Find();
        if (node == null) return;  // Node.js not installed; the Playwright test covers runtime errors.

        var html = HtmlReportWriter.Render(Fixtures.Rich());
        var scripts = Regex.Matches(html, "<script(?<attrs>[^>]*)>(?<body>.*?)</script>", RegexOptions.Singleline)
            .Where(m => !m.Groups["attrs"].Value.Contains("application/json")).ToList();
        Assert.Equal(2, scripts.Count);
        foreach (var (m, i) in scripts.Select((m, i) => (m, i)))
        {
            var file = Path.Combine(Fixtures.TempDir(), $"script{i}.js");
            File.WriteAllText(file, m.Groups["body"].Value);
            var psi = new System.Diagnostics.ProcessStartInfo(node, ["--check", file]) { RedirectStandardError = true, RedirectStandardOutput = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(30_000);
            Assert.True(p.ExitCode == 0, $"node --check failed for script {i}: {err}");
        }
    }

    internal static JsonDocument ParseDataBlob(string html)
    {
        var m = Regex.Match(html, "<script type=\"application/json\" id=\"perflens-data\">(?<json>.*?)</script>", RegexOptions.Singleline);
        Assert.True(m.Success, "data blob missing");
        Assert.DoesNotContain("<", m.Groups["json"].Value);
        return JsonDocument.Parse(m.Groups["json"].Value);
    }

    /// <summary>RFC 4180 reader used to validate the writer.</summary>
    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') { row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = []; i++; }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }
}

internal static class NodeLocator
{
    public static string? Find()
    {
        const string cursorNode = "/home/vkaushik/.local/share/cursor-agent/versions/2026.09.28-64d2043/node";
        if (File.Exists(cursorNode)) return cursorNode;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(dir, OperatingSystem.IsWindows() ? "node.exe" : "node");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
