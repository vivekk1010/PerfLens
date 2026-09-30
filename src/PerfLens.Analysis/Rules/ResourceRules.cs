using System.Text.RegularExpressions;
using PerfLens.Core.Results;

namespace PerfLens.Analysis.Rules;

/// <summary>LARGE_IMAGE: images above 300 KB (Low; Medium when any exceeds 1 MB or 3+ are large).</summary>
public sealed class LargeImageRule : PageRule
{
    public const long LimitBytes = 300 * 1024;

    protected override IEnumerable<Finding> Evaluate(AnalysisContext ctx, PageResult page)
    {
        var large = ctx.Resources(page)
            .Where(r => string.Equals(r.Type, "image", StringComparison.OrdinalIgnoreCase) && AnalysisContext.Size(r) > LimitBytes)
            .ToList();
        if (large.Count == 0) yield break;
        double totalKb = large.Sum(AnalysisContext.Size) / 1024.0;
        bool severe = large.Count >= 3 || AnalysisContext.Size(large[0]) > 1024 * 1024;
        yield return new Finding
        {
            RuleId = "LARGE_IMAGE", Severity = severe ? Severity.Medium : Severity.Low, Category = FindingCategory.Performance,
            Target = page.Name, Metric = MetricNames.TransferKb, Value = Math.Round(totalKb, 1),
            Title = $"{Formatting.Plural(large.Count, "large image")} on {page.Name} ({Formatting.Bytes(totalKb)})",
            Detail = $"Images over {Formatting.Bytes(LimitBytes / 1024.0)}: {ListResources(large)}.",
            Recommendation = "Resize images to their displayed size, use WebP/AVIF with srcset, compress (quality ~75-85) and lazy-load offscreen images.",
        };
    }
}

/// <summary>LARGE_SCRIPT: total script transfer above 500 KB or a single script above 250 KB.</summary>
public sealed class LargeScriptRule : PageRule
{
    protected override IEnumerable<Finding> Evaluate(AnalysisContext ctx, PageResult page)
    {
        var totals = PageSummarizer.Measured(page)
            .Select(i => i.Resources?.ByType is { } byType && byType.TryGetValue("script", out var s) ? s.TransferBytes / 1024.0 : 0)
            .OrderBy(v => v)
            .ToList();
        double totalKb = totals.Count == 0 ? 0 : totals[totals.Count / 2];
        var bigScripts = ctx.Resources(page)
            .Where(r => string.Equals(r.Type, "script", StringComparison.OrdinalIgnoreCase) && AnalysisContext.Size(r) > 250 * 1024)
            .ToList();
        if (totalKb <= 500 && bigScripts.Count == 0) yield break;

        string detail = $"JavaScript transferred per load: {Formatting.Bytes(totalKb)} (recommended < {Formatting.Bytes(500)}).";
        if (bigScripts.Count > 0) detail += $" Scripts over {Formatting.Bytes(250)}: {ListResources(bigScripts)}.";
        yield return new Finding
        {
            RuleId = "LARGE_SCRIPT", Severity = Severity.Medium, Category = FindingCategory.Performance,
            Target = page.Name, Metric = "script_kb", Value = Math.Round(Math.Max(totalKb, bigScripts.Sum(AnalysisContext.Size) / 1024.0), 1),
            Title = $"Heavy JavaScript on {page.Name}",
            Detail = detail,
            Recommendation = "Code-split by route, tree-shake and minify bundles, lazy-load non-critical features and audit large dependencies.",
        };
    }
}

/// <summary>UNCOMPRESSED_TEXT: text resources over 10 KB served without gzip/br/zstd/deflate.</summary>
public sealed class UncompressedTextRule : PageRule
{
    private static readonly string[] TextTypes = ["script", "stylesheet", "document"];
    private static readonly string[] ApiTypes = ["xhr", "fetch"];
    private static readonly string[] TextContentTypes = ["json", "html", "css", "javascript", "ecmascript", "xml", "text/"];
    private static readonly string[] Encodings = ["gzip", "br", "zstd", "deflate"];

    public static bool IsText(ResourceEntry r)
    {
        var type = r.Type ?? "";
        if (TextTypes.Contains(type, StringComparer.OrdinalIgnoreCase)) return true;
        return ApiTypes.Contains(type, StringComparer.OrdinalIgnoreCase)
               && r.ContentType is { } ct && TextContentTypes.Any(t => ct.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsCompressed(ResourceEntry r) =>
        r.ContentEncoding is { } enc && Encodings.Any(e => enc.Contains(e, StringComparison.OrdinalIgnoreCase));

    protected override IEnumerable<Finding> Evaluate(AnalysisContext ctx, PageResult page)
    {
        var offenders = ctx.Resources(page)
            .Where(r => !r.FromCache && IsText(r) && !IsCompressed(r) && AnalysisContext.Size(r) > 10 * 1024)
            .ToList();
        if (offenders.Count == 0) yield break;
        double totalKb = offenders.Sum(AnalysisContext.Size) / 1024.0;
        yield return new Finding
        {
            RuleId = "UNCOMPRESSED_TEXT", Severity = Severity.Medium, Category = FindingCategory.Network,
            Target = page.Name, Metric = MetricNames.TransferKb, Value = Math.Round(totalKb, 1),
            Title = $"{Formatting.Plural(offenders.Count, "text resource")} served uncompressed on {page.Name}",
            Detail = $"{Formatting.Bytes(totalKb)} of HTML/CSS/JS/JSON sent without Content-Encoding (compression typically saves 60-80%): {ListResources(offenders)}.",
            Recommendation = "Enable Brotli or gzip compression for text content types on the web server, CDN or reverse proxy.",
        };
    }
}

/// <summary>MISSING_CACHE_HEADERS: same-origin static assets without a useful Cache-Control header.</summary>
public sealed partial class MissingCacheHeadersRule : PageRule
{
    private static readonly string[] StaticTypes = ["script", "stylesheet", "image", "font"];

    [GeneratedRegex(@"(?i)no-store|max-age\s*=\s*0\b")]
    private static partial Regex NotCacheable();

    public static bool LacksCaching(string? cacheControl) =>
        string.IsNullOrWhiteSpace(cacheControl) || NotCacheable().IsMatch(cacheControl);

    protected override IEnumerable<Finding> Evaluate(AnalysisContext ctx, PageResult page)
    {
        var offenders = ctx.Resources(page)
            .Where(r => StaticTypes.Contains(r.Type ?? "", StringComparer.OrdinalIgnoreCase)
                        && ctx.IsSameOrigin(page, r.Url) && LacksCaching(r.CacheControl))
            .ToList();
        if (offenders.Count == 0) yield break;
        yield return new Finding
        {
            RuleId = "MISSING_CACHE_HEADERS", Severity = Severity.Low, Category = FindingCategory.BestPractice,
            Target = page.Name, Value = offenders.Count,
            Title = $"{Formatting.Plural(offenders.Count, "static asset")} on {page.Name} not cacheable",
            Detail = "Same-origin assets with missing Cache-Control, no-store or max-age=0: " +
                     string.Join(", ", offenders.Take(5).Select(r => $"{AnalysisContext.ShortUrl(r.Url)} ({(string.IsNullOrWhiteSpace(r.CacheControl) ? "none" : r.CacheControl)})")) + ".",
            Recommendation = "Serve fingerprinted static assets with Cache-Control: public, max-age=31536000, immutable; use revalidation (ETag) for non-fingerprinted files.",
        };
    }
}
