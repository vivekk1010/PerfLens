namespace PerfLens.Core.Results;

public enum Severity { Info, Low, Medium, High, Critical }

public enum FindingCategory { Performance, Reliability, Network, BestPractice, Regression, Stability }

public enum VitalRating { Good, NeedsImprovement, Poor, Unknown }

public sealed class AnalysisResult
{
    /// <summary>Overall 0-100 score (weighted page scores plus API score).</summary>
    public double Score { get; set; }
    /// <summary>A (90+), B (75+), C (60+), D (40+), F.</summary>
    public string Grade { get; set; } = "F";
    /// <summary>True when no threshold failed (and MinScore met, if configured).</summary>
    public bool Passed { get; set; }
    /// <summary>Statistic used for page thresholds, ratings and charts (e.g. p75).</summary>
    public string PageStatistic { get; set; } = "p75";
    public List<ThresholdResult> Thresholds { get; set; } = new();
    public List<Finding> Findings { get; set; } = new();
    /// <summary>Page name -> metric name -> rating (Core Web Vitals bands).</summary>
    public Dictionary<string, Dictionary<string, VitalRating>> VitalRatings { get; set; } = new();
    public List<RegressionEntry> Regressions { get; set; } = new();
    public string? BaselineRunId { get; set; }
    /// <summary>Short plain-language conclusions for the report header.</summary>
    public List<string> Highlights { get; set; } = new();
}

public sealed class ThresholdResult
{
    /// <summary>"page" or "api".</summary>
    public string Scope { get; set; } = "";
    public string Target { get; set; } = "";
    public string Metric { get; set; } = "";
    public string Statistic { get; set; } = "";
    public double Actual { get; set; }
    public double Limit { get; set; }
    public bool Passed { get; set; }
}

public sealed class Finding
{
    /// <summary>Stable rule id, e.g. PAGE_SLOW_TTFB.</summary>
    public string RuleId { get; set; } = "";
    public Severity Severity { get; set; }
    public FindingCategory Category { get; set; }
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    /// <summary>Page name, endpoint name or resource URL.</summary>
    public string Target { get; set; } = "";
    public string Recommendation { get; set; } = "";
    public string? Metric { get; set; }
    public double? Value { get; set; }
}

public sealed class RegressionEntry
{
    public string Scope { get; set; } = "";
    public string Target { get; set; } = "";
    public string Metric { get; set; } = "";
    public double Baseline { get; set; }
    public double Current { get; set; }
    public double DeltaPct { get; set; }
    public bool Regressed { get; set; }
    public bool Improved { get; set; }
}
