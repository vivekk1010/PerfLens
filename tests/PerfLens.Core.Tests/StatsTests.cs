using PerfLens.Core.Results;
using PerfLens.Core.Statistics;

namespace PerfLens.Core.Tests;

public class StatsTests
{
    [Fact]
    public void Compute_Empty_ReturnsZeroCount() => Assert.Equal(0, Stats.Compute([]).Count);

    [Fact]
    public void Compute_SingleValue()
    {
        var s = Stats.Compute([42]);
        Assert.Equal(1, s.Count);
        Assert.Equal(42, s.Min);
        Assert.Equal(42, s.P99);
        Assert.Equal(0, s.StdDev);
        Assert.Equal(0, s.CoV);
    }

    [Fact]
    public void Compute_InterpolatesPercentiles_AndUsesSampleStdDev()
    {
        var s = Stats.Compute([4, 1, 3, 2]);
        Assert.Equal(4, s.Count);
        Assert.Equal(1, s.Min);
        Assert.Equal(4, s.Max);
        Assert.Equal(2.5, s.Mean);
        Assert.Equal(2.5, s.Median);
        Assert.Equal(3.25, s.P75, 10);
        Assert.Equal(3.7, s.P90, 10);
        Assert.Equal(3.85, s.P95, 10);
        Assert.Equal(3.97, s.P99, 10);
        Assert.Equal(Math.Sqrt(5.0 / 3), s.StdDev, 10);
        Assert.Equal(s.StdDev / 2.5, s.CoV, 10);
    }

    [Fact]
    public void Compute_IgnoresNaNAndInfinity()
    {
        var s = Stats.Compute([double.NaN, 10, double.PositiveInfinity, 20, double.NegativeInfinity]);
        Assert.Equal(2, s.Count);
        Assert.Equal(15, s.Mean);
    }

    [Fact]
    public void Compute_ZeroMean_CoVIsZero() => Assert.Equal(0, Stats.Compute([-1, 1]).CoV);

    [Fact]
    public void PercentileSorted_ClampsAndHandlesEdges()
    {
        Assert.Equal(0, Stats.PercentileSorted([], 50));
        Assert.Equal(1, Stats.PercentileSorted([1, 2, 3], -10));
        Assert.Equal(3, Stats.PercentileSorted([1, 2, 3], 150));
        Assert.Equal(2, Stats.PercentileSorted([1, 2, 3], 50));
    }

    [Theory]
    [InlineData("min", 1)]
    [InlineData("MAX", 9)]
    [InlineData("mean", 5)]
    [InlineData("avg", 5)]
    [InlineData("median", 2)]
    [InlineData("p50", 2)]
    [InlineData("p75", 3)]
    [InlineData("p90", 4)]
    [InlineData("P95", 6)]
    [InlineData("p99", 7)]
    public void Get_ByName(string name, double expected)
    {
        var s = new MetricStats { Min = 1, Max = 9, Mean = 5, Median = 2, P75 = 3, P90 = 4, P95 = 6, P99 = 7 };
        Assert.Equal(expected, Stats.Get(s, name));
    }

    [Fact]
    public void Get_UnknownStatistic_Throws() => Assert.Throws<ArgumentException>(() => Stats.Get(new MetricStats(), "p42"));

    [Fact]
    public void Histogram_CountsAllValues()
    {
        var values = Enumerable.Range(0, 100).Select(i => (double)i).ToList();
        var h = Stats.Histogram(values, 10);
        Assert.Equal(10, h.Count);
        Assert.Equal(100, h.Sum(b => b.Count));
        Assert.Equal(0, h[0].LowerMs);
        Assert.Equal(99, h[^1].UpperMs, 10);
    }

    [Fact]
    public void Histogram_EmptyAndConstant()
    {
        Assert.Empty(Stats.Histogram([]));
        var h = Stats.Histogram([5, 5, 5], 4);
        Assert.Equal(3, h.Sum(b => b.Count));
        Assert.Equal(3, h[0].Count);
    }
}
