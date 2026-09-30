namespace PerfLens.Reporting;

/// <summary>Embedded report assets (Chart.js 4.4.7 UMD, report CSS, report JS).</summary>
internal static class Assets
{
    private static readonly Lazy<string> ChartJsLazy = new(() => Read("chart.umd.min.js"));
    private static readonly Lazy<string> CssLazy = new(() => Read("report.css"));
    private static readonly Lazy<string> JsLazy = new(() => Read("report.js"));

    public static string ChartJs => ChartJsLazy.Value;
    public static string Css => CssLazy.Value;
    public static string ReportJs => JsLazy.Value;

    private static string Read(string name)
    {
        var resource = "PerfLens.Reporting.Assets." + name;
        using var stream = typeof(Assets).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded resource '{resource}' is missing from PerfLens.Reporting.");
        using var reader = new StreamReader(stream);
        // Inline scripts must never contain a closing script tag; Chart.js and our JS don't, but guard anyway.
        return reader.ReadToEnd().Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase);
    }
}
