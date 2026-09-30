using System.Globalization;

namespace PerfLens.Analysis;

/// <summary>Human-readable formatting of durations, sizes and rates (invariant culture).</summary>
public static class Formatting
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Formats milliseconds as <c>612 ms</c> below one second, otherwise <c>3.9 s</c>.</summary>
    public static string Duration(double ms)
    {
        if (double.IsNaN(ms) || double.IsInfinity(ms)) return "n/a";
        var rounded = Math.Round(ms);
        return Math.Abs(rounded) < 1000
            ? rounded.ToString("0", Inv) + " ms"
            : (ms / 1000).ToString("0.0", Inv) + " s";
    }

    /// <summary>Formats kilobytes as <c>850 KB</c> below one megabyte, otherwise <c>4.2 MB</c>.</summary>
    public static string Bytes(double kb)
    {
        if (double.IsNaN(kb) || double.IsInfinity(kb)) return "n/a";
        var rounded = Math.Round(kb);
        return Math.Abs(rounded) < 1024
            ? rounded.ToString("0", Inv) + " KB"
            : (kb / 1024).ToString("0.0", Inv) + " MB";
    }

    /// <summary>Formats a percentage value (already 0-100) with up to one decimal, e.g. <c>0.4%</c>.</summary>
    public static string Percent(double pct) => pct.ToString("0.#", Inv) + "%";

    /// <summary>Formats a number with up to <paramref name="decimals"/> decimals.</summary>
    public static string Number(double value, int decimals = 1) =>
        value.ToString(decimals <= 0 ? "0" : "0." + new string('#', decimals), Inv);

    /// <summary>Formats a metric value using the unit implied by its <see cref="Core.Results.MetricNames"/> key.</summary>
    public static string Metric(string metric, double value) => metric switch
    {
        _ when metric.EndsWith("_ms", StringComparison.Ordinal) => Duration(value),
        _ when metric.EndsWith("_kb", StringComparison.Ordinal) => Bytes(value),
        _ when metric.EndsWith("_mb", StringComparison.Ordinal) => Number(value) + " MB",
        _ when metric.EndsWith("_pct", StringComparison.Ordinal) => Percent(value),
        "cls" => Number(value, 3),
        "rps" => Number(value) + " rps",
        _ => Number(value),
    };

    internal static string Plural(int count, string singular, string? plural = null) =>
        $"{count} {(count == 1 ? singular : plural ?? singular + "s")}";
}
