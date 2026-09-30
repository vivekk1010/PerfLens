using System.Text.Json;

namespace PerfLens.Browser.Internal;

/// <summary>Shape of the JSON returned by <see cref="MetricsScript.Collect"/>.</summary>
internal sealed class MetricsSnapshot
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public bool Observers { get; set; }
    public NavigationSnapshot? Navigation { get; set; }
    public double? Fcp { get; set; }
    public double? Lcp { get; set; }
    public string? LcpElement { get; set; }
    public double? Cls { get; set; }
    public double? Tbt { get; set; }
    public int LongTaskCount { get; set; }
    public List<ResourceTiming> Resources { get; set; } = new();
    public int? DomNodes { get; set; }
    public double? JsHeapUsed { get; set; }

    public static MetricsSnapshot Parse(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new MetricsSnapshot()
            : JsonSerializer.Deserialize<MetricsSnapshot>(json, Options) ?? new MetricsSnapshot();
}

internal sealed class NavigationSnapshot
{
    public string? Name { get; set; }
    public double Redirect { get; set; }
    public double Dns { get; set; }
    public double Connect { get; set; }
    public double Tls { get; set; }
    public double Server { get; set; }
    public double Ttfb { get; set; }
    public double Download { get; set; }
    public double DomInteractive { get; set; }
    public double DomContentLoaded { get; set; }
    public double Load { get; set; }
    public double Duration { get; set; }
    public double TransferSize { get; set; }
    public double EncodedBodySize { get; set; }
    public double DecodedBodySize { get; set; }
    public int? ResponseStatus { get; set; }
}

internal sealed class ResourceTiming
{
    public string Name { get; set; } = "";
    public string? InitiatorType { get; set; }
    public double StartTime { get; set; }
    public double Duration { get; set; }
    public double TransferSize { get; set; }
    public double EncodedBodySize { get; set; }
    public double DecodedBodySize { get; set; }
    public int? ResponseStatus { get; set; }
}
