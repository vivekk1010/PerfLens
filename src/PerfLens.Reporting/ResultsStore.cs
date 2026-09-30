using System.Text.Json;
using PerfLens.Core.Results;
using PerfLens.Core.Serialization;

namespace PerfLens.Reporting;

/// <summary>Loads a previously written <c>results.json</c> (baselines, re-rendering reports).</summary>
public static class ResultsStore
{
    /// <param name="path">A results.json file, or a run directory containing one.</param>
    public static async Task<RunResult> LoadAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = Directory.Exists(path) ? Path.Combine(path, JsonReportWriter.FileName) : path;
        if (!File.Exists(file))
            throw new FileNotFoundException($"Results file not found: '{Path.GetFullPath(file)}'. Point to a results.json written by a previous PerfLens run.", file);

        try
        {
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            var run = await JsonSerializer.DeserializeAsync<RunResult>(stream, JsonDefaults.Results, ct);
            if (run is null)
                throw new InvalidDataException($"Results file '{file}' is empty or contains 'null'.");
            if (run.SchemaVersion > 1)
                throw new InvalidDataException($"Results file '{file}' uses schema version {run.SchemaVersion}, which is newer than this PerfLens build supports (1).");
            return run;
        }
        catch (JsonException ex)
        {
            var where = ex.LineNumber is { } line ? $" (line {line + 1}, position {ex.BytePositionInLine + 1})" : "";
            throw new InvalidDataException($"Results file '{file}' is not a valid PerfLens results.json{where}: {ex.Message}", ex);
        }
    }
}
