using PerfLens.Core.Results;

namespace PerfLens.Analysis.Rules;

/// <summary>A single diagnostic rule. Rules must be deterministic and must not mutate the context.</summary>
public interface IFindingRule
{
    IEnumerable<Finding> Evaluate(AnalysisContext context);
}

/// <summary>The built-in rule set, in evaluation order.</summary>
public static class FindingRules
{
    public static IReadOnlyList<IFindingRule> Default { get; } =
    [
        new PageUnavailableRule(),
        new PageIterationFailuresRule(),
        new VitalRatingRule(),
        new SlowServerRule(),
        new HeavyPageRule(),
        new TooManyRequestsRule(),
        new LargeImageRule(),
        new LargeScriptRule(),
        new UncompressedTextRule(),
        new MissingCacheHeadersRule(),
        new ConsoleErrorsRule(),
        new FailedRequestsRule(),
        new HighVarianceRule(),
        new PageApiCallRule(),
        new NPlusOneApiRule(),
        new BrokenLinkRule(),
        new ApiLoadRule(),
        new ApiDegradationRule(),
        new RegressionRule(),
    ];
}

/// <summary>Base class for rules evaluated once per available (measured) page.</summary>
public abstract class PageRule : IFindingRule
{
    public IEnumerable<Finding> Evaluate(AnalysisContext context) =>
        context.Pages.Where(p => !AnalysisContext.IsUnavailable(p)).SelectMany(p => Evaluate(context, p));

    protected abstract IEnumerable<Finding> Evaluate(AnalysisContext ctx, PageResult page);

    protected static string Label(AnalysisContext ctx) => ctx.Statistic;

    protected static string ListResources(IEnumerable<ResourceEntry> resources, int max = 5) =>
        string.Join(", ", resources.Take(max).Select(r => $"{AnalysisContext.ShortUrl(r.Url)} ({Formatting.Bytes(AnalysisContext.Size(r) / 1024.0)})"));
}
