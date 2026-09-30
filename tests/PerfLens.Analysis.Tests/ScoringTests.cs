using PerfLens.Core.Configuration;
using PerfLens.Core.Results;

namespace PerfLens.Analysis.Tests;

public class ScoringTests
{
    [Theory]
    [InlineData(MetricNames.Lcp, 2500, VitalRating.Good)]
    [InlineData(MetricNames.Lcp, 2501, VitalRating.NeedsImprovement)]
    [InlineData(MetricNames.Lcp, 4000, VitalRating.NeedsImprovement)]
    [InlineData(MetricNames.Lcp, 4001, VitalRating.Poor)]
    [InlineData(MetricNames.Fcp, 1800, VitalRating.Good)]
    [InlineData(MetricNames.Fcp, 3001, VitalRating.Poor)]
    [InlineData(MetricNames.Cls, 0.1, VitalRating.Good)]
    [InlineData(MetricNames.Cls, 0.2, VitalRating.NeedsImprovement)]
    [InlineData(MetricNames.Cls, 0.26, VitalRating.Poor)]
    [InlineData(MetricNames.Tbt, 200, VitalRating.Good)]
    [InlineData(MetricNames.Tbt, 601, VitalRating.Poor)]
    [InlineData(MetricNames.Ttfb, 800, VitalRating.Good)]
    [InlineData(MetricNames.Ttfb, 1000, VitalRating.NeedsImprovement)]
    [InlineData(MetricNames.Ttfb, 1801, VitalRating.Poor)]
    public void RatingBands(string metric, double value, VitalRating expected) =>
        Assert.Equal(expected, VitalRater.Rate(metric, value));

    [Fact]
    public void Rating_MissingValueIsUnknown() => Assert.Equal(VitalRating.Unknown, VitalRater.Rate(MetricNames.Lcp, null));

    [Fact]
    public void VitalRatings_UseConfiguredStatistic()
    {
        var run = new RunBuilder().Page("/p", p => p.Values((i, v) => i.Vitals.LcpMs = v, 1000, 1000, 1000, 5000)).Build();
        var cfg = TestConfig.Default();

        cfg.Thresholds.PageStatistic = "p50";
        Assert.Equal(VitalRating.Good, TestConfig.Analyze(run, cfg).VitalRatings["/p"][MetricNames.Lcp]);

        cfg.Thresholds.PageStatistic = "max";
        Assert.Equal(VitalRating.Poor, TestConfig.Analyze(run, cfg).VitalRatings["/p"][MetricNames.Lcp]);
    }

    [Theory]
    [InlineData(934, 1600)]
    [InlineData(1200, 2400)]
    [InlineData(150, 350)]
    [InlineData(0.1, 0.25)]
    [InlineData(400, 1000)]
    public void LogNormal_ControlPoints(double p10, double median)
    {
        Assert.Equal(0.9, Scoring.LogNormalScore(p10, p10, median), 3);
        Assert.Equal(0.5, Scoring.LogNormalScore(median, p10, median), 3);
    }

    [Fact]
    public void LogNormal_IsMonotonicDecreasing()
    {
        double prev = 1.0;
        foreach (var v in new[] { 0.0, 100, 500, 1000, 1600, 2500, 4000, 8000, 20000 })
        {
            var s = Scoring.LogNormalScore(v, 934, 1600);
            Assert.InRange(s, 0, 1);
            Assert.True(s <= prev, $"score({v})={s} > previous {prev}");
            prev = s;
        }
        Assert.Equal(1, Scoring.LogNormalScore(0, 934, 1600));
    }

    [Fact]
    public void PageScore_FasterPageScoresHigher_AndControlPointsMap()
    {
        static RunResult Run(double fcp, double lcp, double tbt, double cls, double ttfb) =>
            new RunBuilder().Page("/p", p => p.Iterations(3, i =>
            {
                i.Vitals = new WebVitals { FcpMs = fcp, LcpMs = lcp, TbtMs = tbt, Cls = cls };
                i.Timing.TtfbMs = ttfb;
            })).Build();

        var atP10 = Run(934, 1200, 150, 0.1, 400);
        var atMedian = Run(1600, 2400, 350, 0.25, 1000);
        var slow = Run(4000, 6000, 1500, 0.5, 2500);
        TestConfig.Analyze(atP10);
        TestConfig.Analyze(atMedian);
        TestConfig.Analyze(slow);

        Assert.Equal(90, atP10.Pages[0].Summary!.Score, 0);
        Assert.Equal(50, atMedian.Pages[0].Summary!.Score, 0);
        Assert.True(slow.Pages[0].Summary!.Score < atMedian.Pages[0].Summary!.Score);
    }

    [Fact]
    public void PageScore_RenormalizesWhenMetricsMissing()
    {
        var run = new RunBuilder().Page("/p", p => p.Iterations(2, i =>
        {
            i.Vitals = new WebVitals();
            i.Timing.TtfbMs = 1000;
        })).Build();

        TestConfig.Analyze(run);

        Assert.Equal(50, run.Pages[0].Summary!.Score, 0);
    }

    [Fact]
    public void ApiScore_PenalizesErrorsAndLatency()
    {
        var t = new ApiThresholds { P95Ms = 100 };
        var clean = RunBuilder.CleanEndpoint("a");
        Assert.Equal(100, Scoring.ApiScore(clean, t));

        var errors = RunBuilder.CleanEndpoint("b");
        errors.ErrorRatePct = 5;
        Assert.Equal(70, Scoring.ApiScore(errors, t));

        var slow = RunBuilder.CleanEndpoint("c");
        slow.Latency.P95 = 150;
        Assert.Equal(80, Scoring.ApiScore(slow, t));

        var awful = RunBuilder.CleanEndpoint("d");
        awful.ErrorRatePct = 50;
        awful.Latency.P95 = 1000;
        Assert.Equal(0, Scoring.ApiScore(awful, t));

        var down = RunBuilder.CleanEndpoint("e");
        down.Error = "connection refused";
        Assert.Equal(0, Scoring.ApiScore(down, t));
    }

    [Theory]
    [InlineData(100, "A")]
    [InlineData(90, "A")]
    [InlineData(89.9, "B")]
    [InlineData(75, "B")]
    [InlineData(74.9, "C")]
    [InlineData(60, "C")]
    [InlineData(59.9, "D")]
    [InlineData(40, "D")]
    [InlineData(39.9, "F")]
    [InlineData(0, "F")]
    public void GradeBoundaries(double score, string grade) => Assert.Equal(grade, Scoring.Grade(score));

    [Fact]
    public void Overall_Combines70PercentPages30PercentApi()
    {
        Assert.Equal(0, Scoring.Overall([], []));
        Assert.Equal(80, Scoring.Overall([60, 100], []));
        Assert.Equal(50, Scoring.Overall([], [50]));
        Assert.Equal(0.7 * 80 + 0.3 * 50, Scoring.Overall([60, 100], [50]), 1);
    }

    [Fact]
    public void Engine_OverallScoreWeightsPagesEquallyAndIncludesApi()
    {
        var run = new RunBuilder()
            .Page("/ok", p => p.Iterations(3))
            .Page("/down", p => p.Error("net::ERR_CONNECTION_REFUSED"))
            .Endpoint("search")
            .Build();

        var r = TestConfig.Analyze(run);
        double pageMean = (run.Pages[0].Summary!.Score + 0) / 2;

        Assert.Equal(Math.Round(0.7 * pageMean + 0.3 * 100, 1), r.Score, 1);
        Assert.Equal(Scoring.Grade(r.Score), r.Grade);
    }
}
