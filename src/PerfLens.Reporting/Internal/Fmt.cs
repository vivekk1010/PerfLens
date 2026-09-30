using System.Globalization;

namespace PerfLens.Reporting.Internal;

/// <summary>Invariant-culture number formatting shared by all writers.</summary>
internal static class Fmt
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public const string NA = "n/a";

    public static string Ms(double? ms)
    {
        if (ms is not { } v || double.IsNaN(v) || double.IsInfinity(v)) return NA;
        return Math.Abs(v) < 1000 ? v.ToString("0", Inv) + " ms" : (v / 1000).ToString("0.00", Inv) + " s";
    }

    public static string Bytes(double? bytes)
    {
        if (bytes is not { } b || double.IsNaN(b)) return NA;
        if (Math.Abs(b) < 1024) return b.ToString("0", Inv) + " B";
        return Kb(b / 1024);
    }

    public static string Kb(double? kb)
    {
        if (kb is not { } k || double.IsNaN(k)) return NA;
        return Math.Abs(k) < 1024 ? k.ToString("0.0", Inv) + " KB" : (k / 1024).ToString("0.00", Inv) + " MB";
    }

    public static string Cls(double? v) => v is { } x && !double.IsNaN(x) ? x.ToString("0.000", Inv) : NA;

    public static string Pct(double? v) => v is { } x && !double.IsNaN(x) ? x.ToString("0.0", Inv) + "%" : NA;

    public static string SignedPct(double v) => (v > 0 ? "+" : "") + v.ToString("0.0", Inv) + "%";

    public static string Num(double? v, string format = "0.##") =>
        v is { } x && !double.IsNaN(x) && !double.IsInfinity(x) ? x.ToString(format, Inv) : NA;

    public static string Int(long v) => v.ToString("N0", Inv);

    public static string Score(double? v) => v is { } x ? x.ToString("0", Inv) : NA;

    /// <summary>Formats a metric value by its unit from <see cref="Core.Results.MetricNames.Display"/>.</summary>
    public static string ByUnit(double? value, string unit) => unit switch
    {
        "ms" => Ms(value),
        "KB" => Kb(value),
        "MB" => value is { } v ? v.ToString("0.0", Inv) + " MB" : NA,
        "" => Cls(value),
        "pct" => Pct(value),
        _ => Num(value, "0.#"),
    };

    /// <summary>Formats by metric name (the unit is looked up) for metrics that may not be in Display.</summary>
    public static string Metric(string metric, double? value) =>
        ByUnit(value, Core.Results.MetricNames.Display.TryGetValue(metric, out var d) ? d.Unit : UnitGuess(metric));

    private static string UnitGuess(string metric)
    {
        var m = metric.ToLowerInvariant();
        if (m.Contains("pct") || m.Contains("rate")) return "pct";
        if (m.EndsWith("_ms") || m.Contains("latency") || (m.StartsWith('p') && m.Length <= 3 && char.IsDigit(m[^1]))) return "ms";
        if (m == "cls") return "";
        if (m.EndsWith("_kb")) return "KB";
        return "count";
    }

    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalSeconds < 60) return span.TotalSeconds.ToString("0.0", Inv) + " s";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m {span.Seconds}s";
        return $"{(int)span.TotalHours}h {span.Minutes}m {span.Seconds}s";
    }

    public static string Date(DateTimeOffset d) => d == default ? NA : d.ToString("yyyy-MM-dd HH:mm:ss zzz", Inv);

    public static string Inv0(double v) => v.ToString("R", Inv);
}
