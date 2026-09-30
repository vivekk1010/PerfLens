using PerfLens.Core.Results;

namespace PerfLens.Core.Statistics;

public static class Stats
{
    /// <summary>Computes summary statistics. Percentiles use linear interpolation between closest ranks.</summary>
    public static MetricStats Compute(IEnumerable<double> values)
    {
        var sorted = values.Where(v => !double.IsNaN(v) && !double.IsInfinity(v)).ToArray();
        if (sorted.Length == 0) return new MetricStats();
        Array.Sort(sorted);

        double mean = sorted.Average();
        double variance = sorted.Length > 1
            ? sorted.Sum(v => (v - mean) * (v - mean)) / (sorted.Length - 1)
            : 0;
        double sd = Math.Sqrt(variance);

        return new MetricStats
        {
            Count = sorted.Length,
            Min = sorted[0],
            Max = sorted[^1],
            Mean = mean,
            Median = PercentileSorted(sorted, 50),
            P75 = PercentileSorted(sorted, 75),
            P90 = PercentileSorted(sorted, 90),
            P95 = PercentileSorted(sorted, 95),
            P99 = PercentileSorted(sorted, 99),
            StdDev = sd,
            CoV = mean == 0 ? 0 : sd / mean,
        };
    }

    /// <summary>Percentile (0-100) of an ascending-sorted array.</summary>
    public static double PercentileSorted(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0) return 0;
        if (sorted.Count == 1) return sorted[0];
        percentile = Math.Clamp(percentile, 0, 100);
        double rank = percentile / 100.0 * (sorted.Count - 1);
        int lo = (int)Math.Floor(rank);
        int hi = (int)Math.Ceiling(rank);
        if (lo == hi) return sorted[lo];
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    /// <summary>Reads a statistic by name: min, max, mean, median|p50, p75, p90, p95, p99.</summary>
    public static double Get(MetricStats stats, string statistic) => statistic.ToLowerInvariant() switch
    {
        "min" => stats.Min,
        "max" => stats.Max,
        "mean" or "avg" => stats.Mean,
        "median" or "p50" => stats.Median,
        "p75" => stats.P75,
        "p90" => stats.P90,
        "p95" => stats.P95,
        "p99" => stats.P99,
        _ => throw new ArgumentException($"Unknown statistic '{statistic}'. Use min, max, mean, p50, p75, p90, p95 or p99."),
    };

    /// <summary>Builds a latency histogram with roughly <paramref name="bucketCount"/> equal-width buckets.</summary>
    public static List<HistogramBucket> Histogram(IReadOnlyList<double> values, int bucketCount = 20)
    {
        var result = new List<HistogramBucket>();
        if (values.Count == 0) return result;
        double min = values.Min(), max = values.Max();
        if (max <= min) max = min + 1;
        double width = (max - min) / bucketCount;
        var counts = new long[bucketCount];
        foreach (var v in values)
        {
            int idx = (int)((v - min) / width);
            counts[Math.Clamp(idx, 0, bucketCount - 1)]++;
        }
        for (int i = 0; i < bucketCount; i++)
            result.Add(new HistogramBucket { LowerMs = min + i * width, UpperMs = min + (i + 1) * width, Count = counts[i] });
        return result;
    }
}
