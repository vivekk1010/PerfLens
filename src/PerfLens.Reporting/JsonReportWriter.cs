using System.Text.Json;
using PerfLens.Core.Abstractions;
using PerfLens.Core.Results;
using PerfLens.Core.Serialization;

namespace PerfLens.Reporting;

/// <summary>Writes the complete run as <c>results.json</c> (the machine-readable source of truth and baseline format).</summary>
public sealed class JsonReportWriter : IReportWriter
{
    public const string FileName = "results.json";

    public string Format => "json";

    public async Task<IReadOnlyList<string>> WriteAsync(RunResult run, string outputDirectory, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, FileName);
        await using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            await JsonSerializer.SerializeAsync(stream, run, JsonDefaults.Results, ct);
        return [path];
    }
}
