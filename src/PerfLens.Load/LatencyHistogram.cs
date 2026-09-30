using PerfLens.Core.Results;
using PerfLens.Core.Statistics;

namespace PerfLens.Load;

/// <summary>Equal-width latency histogram whose range is clipped so a few outliers don't squash the chart.</summary>
public static class LatencyHistogram
{
    /// <summary>
    /// Builds <paramref name="bucketCount"/> buckets over [min, percentile(<paramref name="clipPercentile"/>)].
    /// Values above the clip point are counted in the last bucket, so counts always sum to the input size.
    /// </summary>
    public static List<HistogramBucket> Build(IReadOnlyList<double> values, int bucketCount = 20, double clipPercentile = 99.9)
    {
        var result = new List<HistogramBucket>();
        if (values.Count == 0 || bucketCount <= 0) return result;

        var sorted = values.ToArray();
        Array.Sort(sorted);
        double min = sorted[0];
        double upper = Math.Min(Stats.PercentileSorted(sorted, clipPercentile), sorted[^1]);
        if (upper <= min) upper = sorted[^1];
        if (upper <= min) upper = min + 1;

        double width = (upper - min) / bucketCount;
        var counts = new long[bucketCount];
        foreach (var v in sorted)
        {
            int idx = (int)((v - min) / width);
            counts[Math.Clamp(idx, 0, bucketCount - 1)]++;
        }

        for (int i = 0; i < bucketCount; i++)
        {
            result.Add(new HistogramBucket
            {
                LowerMs = min + i * width,
                UpperMs = i == bucketCount - 1 ? Math.Max(upper, sorted[^1]) : min + (i + 1) * width,
                Count = counts[i],
            });
        }
        return result;
    }
}
