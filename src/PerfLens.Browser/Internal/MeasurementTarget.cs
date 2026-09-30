using PerfLens.Core.Configuration;
using PerfLens.Core.Results;

namespace PerfLens.Browser.Internal;

/// <summary>A page scheduled for measurement: configured or discovered by the crawler.</summary>
internal sealed record MeasurementTarget(string Name, string Url, PageSource Source, int Depth, PageConfig? Config);
