using PerfLens.Analysis.Rules;
using PerfLens.Core.Abstractions;
using PerfLens.Core.Configuration;
using PerfLens.Core.Results;

namespace PerfLens.Analysis;

/// <summary>
/// Turns raw measurements into summaries, Web Vital ratings, scores, thresholds, findings, baseline regressions
/// and highlights. Deterministic; tolerates partial data. Also stores the result in <see cref="RunResult.Analysis"/>.
/// </summary>
public sealed class AnalysisEngine : IAnalysisEngine
{
    private readonly IReadOnlyList<IFindingRule> _rules;

    public AnalysisEngine() : this(FindingRules.Default) { }

    public AnalysisEngine(IEnumerable<IFindingRule> rules) => _rules = rules.ToList();

    public AnalysisResult Analyze(RunResult run, PerfLensConfig config, RunResult? baseline)
    {
        ArgumentNullException.ThrowIfNull(run);
        config ??= new PerfLensConfig();
        var result = new AnalysisResult();
        var ctx = new AnalysisContext(run, config, result);
        result.PageStatistic = ctx.Statistic;

        foreach (var page in ctx.Pages)
            page.Summary = PageSummarizer.Summarize(page);

        foreach (var page in ctx.Pages)
        {
            result.VitalRatings[page.Name] = VitalRater.RatePage(ctx, page);
            page.Summary!.Score = Scoring.PageScore(page.Summary, ctx.Statistic);
        }

        var pageScores = ctx.Pages.Select(p => p.Summary!.Score).ToList();
        var apiScores = ctx.Endpoints.Select(e => Scoring.ApiScore(e, ctx.EffectiveApiThresholds(e))).ToList();
        result.Score = Scoring.Overall(pageScores, apiScores);
        result.Grade = Scoring.Grade(result.Score);

        result.Thresholds = ThresholdEvaluator.Evaluate(ctx);

        if (baseline is not null)
        {
            result.BaselineRunId = baseline.RunId;
            result.Regressions = BaselineComparer.Compare(ctx, baseline);
        }

        result.Findings = _rules
            .SelectMany(rule => RunRule(rule, ctx))
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Target, StringComparer.Ordinal)
            .ThenBy(f => f.RuleId, StringComparer.Ordinal)
            .ThenBy(f => f.Title, StringComparer.Ordinal)
            .ToList();

        result.Passed = result.Thresholds.All(t => t.Passed)
                        && (config.Thresholds?.MinScore is not { } min || result.Score >= min);

        result.Highlights = HighlightWriter.Write(ctx);

        run.Analysis = result;
        return result;
    }

    /// <summary>A faulty rule must not abort the analysis; it is reported as an Info finding instead.</summary>
    private static List<Finding> RunRule(IFindingRule rule, AnalysisContext ctx)
    {
        try
        {
            return rule.Evaluate(ctx).ToList();
        }
        catch (Exception ex)
        {
            return
            [
                new Finding
                {
                    RuleId = "ANALYSIS_RULE_ERROR", Severity = Severity.Info, Category = FindingCategory.Reliability,
                    Target = rule.GetType().Name,
                    Title = $"Analysis rule {rule.GetType().Name} failed",
                    Detail = ex.Message,
                    Recommendation = "Report this as a PerfLens bug; the other findings are unaffected.",
                },
            ];
        }
    }
}
