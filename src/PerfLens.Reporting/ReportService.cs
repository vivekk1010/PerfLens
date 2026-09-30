using System.Reflection;
using PerfLens.Core.Abstractions;
using PerfLens.Core.Results;

namespace PerfLens.Reporting;

/// <summary>Dispatches a run to the report writers for the requested formats.</summary>
public sealed class ReportService
{
    private readonly Dictionary<string, IReportWriter> _writers;

    public ReportService(IEnumerable<IReportWriter> writers)
    {
        ArgumentNullException.ThrowIfNull(writers);
        _writers = new Dictionary<string, IReportWriter>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in writers) _writers[w.Format] = w;
    }

    public static ReportService CreateDefault() =>
        new([new HtmlReportWriter(), new JsonReportWriter(), new MarkdownReportWriter(), new CsvReportWriter()]);

    public IReadOnlyCollection<string> Formats => _writers.Keys;

    /// <returns>Paths of all files written.</returns>
    /// <exception cref="ArgumentException">A requested format has no writer.</exception>
    public async Task<IReadOnlyList<string>> WriteAsync(RunResult run, string outputDirectory, IEnumerable<string> formats, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(formats);

        var selected = new List<IReportWriter>();
        foreach (var raw in formats)
        {
            var f = raw?.Trim().TrimStart('.') ?? "";
            if (f.Equals("markdown", StringComparison.OrdinalIgnoreCase)) f = "md";
            if (!_writers.TryGetValue(f, out var writer))
                throw new ArgumentException($"Unknown report format '{raw}'. Supported formats: {string.Join(", ", _writers.Keys)}.", nameof(formats));
            if (!selected.Contains(writer)) selected.Add(writer);
        }

        Directory.CreateDirectory(outputDirectory);
        var written = new List<string>();
        foreach (var writer in selected)
        {
            ct.ThrowIfCancellationRequested();
            written.AddRange(await writer.WriteAsync(run, outputDirectory, ct));
        }
        return written;
    }
}

internal static class ToolInfo
{
    public static string Version { get; } =
        typeof(ToolInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(ToolInfo).Assembly.GetName().Version?.ToString() ?? "1.0.0";
}
