using PerfLens.Core.Results;

namespace PerfLens.Analysis;

/// <summary>Core Web Vitals rating bands (value &lt;= Good is good, value &gt; Poor is poor).</summary>
public static class VitalRater
{
    public sealed record Band(string Metric, string ShortName, double Good, double Poor);

    private static readonly Dictionary<string, string> ShortNames = new()
    {
        [MetricNames.Fcp] = "FCP", [MetricNames.Lcp] = "LCP", [MetricNames.Cls] = "CLS",
        [MetricNames.Tbt] = "TBT", [MetricNames.Ttfb] = "TTFB",
    };

    public static readonly IReadOnlyList<Band> Bands = MetricNames.VitalBands
        .Select(kv => new Band(kv.Key, ShortNames[kv.Key], kv.Value.Good, kv.Value.Poor))
        .ToList();

    public static VitalRating Rate(string metric, double? value)
    {
        var band = Bands.FirstOrDefault(b => b.Metric == metric);
        if (band is null || value is null || double.IsNaN(value.Value)) return VitalRating.Unknown;
        if (value <= band.Good) return VitalRating.Good;
        return value > band.Poor ? VitalRating.Poor : VitalRating.NeedsImprovement;
    }

    internal static Dictionary<string, VitalRating> RatePage(AnalysisContext ctx, PageResult page) =>
        Bands.ToDictionary(b => b.Metric, b => Rate(b.Metric, ctx.Stat(page, b.Metric)));
}
