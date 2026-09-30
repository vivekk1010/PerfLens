using PerfLens.Core.Results;

namespace PerfLens.Reporting;

/// <summary>Maps each page with a screenshot to an <c>img src</c>: a data: URI when small enough, else a relative link.</summary>
internal static class ScreenshotResolver
{
    private static readonly Dictionary<string, string> Mime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".webp"] = "image/webp", [".gif"] = "image/gif",
    };

    public static IReadOnlyDictionary<PageResult, string> Resolve(IEnumerable<PageResult> pages, string? outputDirectory)
    {
        var result = new Dictionary<PageResult, string>(ReferenceEqualityComparer.Instance);
        var files = new List<(PageResult Page, string Relative, string? FullPath, long Size)>();
        foreach (var p in pages)
        {
            if (string.IsNullOrWhiteSpace(p.ScreenshotPath)) continue;
            var rel = p.ScreenshotPath.Replace('\\', '/');
            string? full = null;
            long size = 0;
            try
            {
                var candidate = Path.IsPathRooted(p.ScreenshotPath) || outputDirectory == null
                    ? p.ScreenshotPath
                    : Path.Combine(outputDirectory, p.ScreenshotPath);
                var fi = new FileInfo(candidate);
                if (fi.Exists && Mime.ContainsKey(fi.Extension)) { full = fi.FullName; size = fi.Length; }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
            files.Add((p, rel, full, size));
        }

        bool embed = files.Sum(f => f.Size) <= HtmlReportWriter.MaxEmbeddedScreenshotBytes;
        foreach (var f in files)
        {
            if (embed && f.FullPath != null)
            {
                try
                {
                    var bytes = File.ReadAllBytes(f.FullPath);
                    result[f.Page] = $"data:{Mime[Path.GetExtension(f.FullPath)]};base64,{Convert.ToBase64String(bytes)}";
                    continue;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            if (!Path.IsPathRooted(f.Relative) && !f.Relative.Contains(':'))
                result[f.Page] = string.Join('/', f.Relative.Split('/').Select(Uri.EscapeDataString));
        }
        return result;
    }
}
