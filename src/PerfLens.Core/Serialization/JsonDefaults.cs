using System.Text.Json;
using System.Text.Json.Serialization;

namespace PerfLens.Core.Serialization;

public static class JsonDefaults
{
    /// <summary>Config reading: case-insensitive, comments and trailing commas allowed, enums by name.</summary>
    public static readonly JsonSerializerOptions Config = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(), new LenientStringConverter() },
    };

    /// <summary>Results writing/reading: camelCase, indented, enums as strings.</summary>
    public static readonly JsonSerializerOptions Results = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
