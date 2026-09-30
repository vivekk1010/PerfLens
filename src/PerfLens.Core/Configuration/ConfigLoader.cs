using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PerfLens.Core.Serialization;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace PerfLens.Core.Configuration;

public sealed class ConfigException(string message) : Exception(message);

public static partial class ConfigLoader
{
    /// <summary>Loads YAML (.yml/.yaml) or JSON config, substitutes ${ENV:NAME} / ${NAME} and validates.</summary>
    public static PerfLensConfig Load(string path)
    {
        if (!File.Exists(path)) throw new ConfigException($"Config file not found: {path}");
        var text = File.ReadAllText(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var config = ext is ".yml" or ".yaml" ? FromYaml(text) : FromJson(text);
        Validate(config);
        return config;
    }

    public static PerfLensConfig FromJson(string json) => DeserializeJson(SubstituteEnvironment(json, jsonEscape: true));

    private static PerfLensConfig DeserializeJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<PerfLensConfig>(json, JsonDefaults.Config)
                   ?? throw new ConfigException("Config is empty.");
        }
        catch (JsonException ex)
        {
            throw new ConfigException($"Invalid config: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses YAML into a JSON tree. Environment placeholders are substituted per scalar before typing,
    /// so <c>iterations: ${ENV:N}</c> becomes a number while quoted scalars always stay strings.
    /// </summary>
    public static PerfLensConfig FromYaml(string yaml)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new ConfigException($"Invalid YAML config (line {ex.Start.Line}): {ex.InnerException?.Message ?? ex.Message}");
        }
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is YamlScalarNode { Value: null or "" })
            throw new ConfigException("Config is empty.");

        var missing = new List<string>();
        var tree = ToJson(stream.Documents[0].RootNode, missing);
        if (missing.Count > 0)
            throw new ConfigException($"Environment variable(s) not set: {string.Join(", ", missing.Distinct())}");
        return DeserializeJson(tree?.ToJsonString() ?? "null");
    }

    private static JsonNode? ToJson(YamlNode node, List<string> missing)
    {
        switch (node)
        {
            case YamlMappingNode map:
                var obj = new JsonObject();
                foreach (var (k, v) in map.Children)
                    obj[((YamlScalarNode)k).Value ?? ""] = ToJson(v, missing);
                return obj;
            case YamlSequenceNode seq:
                var arr = new JsonArray();
                foreach (var item in seq.Children) arr.Add(ToJson(item, missing));
                return arr;
            case YamlScalarNode scalar:
                var raw = scalar.Value ?? "";
                string value;
                try { value = SubstituteEnvironment(raw); }
                catch (ConfigException)
                {
                    missing.AddRange(EnvPattern().Matches(raw).Select(m => m.Groups[1].Value)
                        .Where(n => Environment.GetEnvironmentVariable(n) is null));
                    value = raw;
                }
                if (scalar.Style is ScalarStyle.Plain) return TypedScalar(value);
                return JsonValue.Create(value);
            default:
                return null;
        }
    }

    private static JsonNode? TypedScalar(string s)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (s is "" or "~" or "null" or "Null" or "NULL") return null;
        if (bool.TryParse(s, out var b)) return JsonValue.Create(b);
        if (long.TryParse(s, System.Globalization.NumberStyles.AllowLeadingSign, inv, out var l) && !HasLeadingZero(s)) return JsonValue.Create(l);
        if (double.TryParse(s, System.Globalization.NumberStyles.Float, inv, out var d) && !HasLeadingZero(s) && double.IsFinite(d)) return JsonValue.Create(d);
        return JsonValue.Create(s);
    }

    /// <summary>"007" is kept as text (ids, zip codes); "0", "0.5" are numbers.</summary>
    private static bool HasLeadingZero(string s)
    {
        var t = s.TrimStart('-', '+');
        return t.Length > 1 && t[0] == '0' && t[1] != '.';
    }

    [GeneratedRegex(@"\$\{(?:ENV:)?([A-Za-z_][A-Za-z0-9_]*)(?::-([^}]*))?\}")]
    private static partial Regex EnvPattern();

    /// <summary>Replaces ${ENV:NAME}, ${NAME} and ${NAME:-default}. Unset variables without default raise an error.</summary>
    public static string SubstituteEnvironment(string text, bool jsonEscape = false)
    {
        var missing = new List<string>();
        var result = EnvPattern().Replace(text, m =>
        {
            var value = Environment.GetEnvironmentVariable(m.Groups[1].Value);
            if (value is null && m.Groups[2].Success) value = m.Groups[2].Value;
            if (value is null) { missing.Add(m.Groups[1].Value); return m.Value; }
            return jsonEscape ? JsonEncodedText.Encode(value).ToString() : value;
        });
        if (missing.Count > 0)
            throw new ConfigException($"Environment variable(s) not set: {string.Join(", ", missing.Distinct())}");
        return result;
    }

    public static void Validate(PerfLensConfig c)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(c.Target.BaseUrl))
            errors.Add("target.baseUrl is required.");
        else if (!Uri.TryCreate(c.Target.BaseUrl, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
            errors.Add($"target.baseUrl must be an absolute http(s) URL (got '{c.Target.BaseUrl}').");

        bool apiOnly = c.Api.Enabled && c.Pages.Count == 0 && !c.Crawl.Enabled;
        if (c.Pages.Count == 0 && !c.Crawl.Enabled && !c.Api.Enabled)
            errors.Add("Nothing to measure: add pages, enable crawl, or enable api.");

        for (int i = 0; i < c.Pages.Count; i++)
        {
            var p = c.Pages[i];
            if (string.IsNullOrWhiteSpace(p.Path)) errors.Add($"pages[{i}].path is required.");
            if (string.IsNullOrWhiteSpace(p.Name)) p.Name = p.Path;
            if (p.Iterations is < 1) errors.Add($"pages[{i}].iterations must be >= 1.");
        }
        var dupes = c.Pages.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dupes.Count > 0) errors.Add($"Duplicate page names: {string.Join(", ", dupes)}.");

        if (!apiOnly)
        {
            if (c.Browser.Iterations < 1) errors.Add("browser.iterations must be >= 1.");
            if (c.Browser.WarmupIterations < 0) errors.Add("browser.warmupIterations must be >= 0.");
            if (c.Browser.CpuThrottlingRate < 1) errors.Add("browser.cpuThrottlingRate must be >= 1.");
            if (c.Browser.NetworkProfile == NetworkProfile.Custom && c.Browser.CustomNetwork is null)
                errors.Add("browser.customNetwork is required when networkProfile is Custom.");
        }

        if (c.Crawl.Enabled)
        {
            if (c.Crawl.MaxDepth < 0) errors.Add("crawl.maxDepth must be >= 0.");
            if (c.Crawl.MaxPages < 1) errors.Add("crawl.maxPages must be >= 1.");
            foreach (var rx in c.Crawl.Include.Concat(c.Crawl.Exclude))
            {
                try { _ = new Regex(rx); }
                catch (ArgumentException) { errors.Add($"Invalid crawl regex: {rx}"); }
            }
        }

        switch (c.Auth.Type)
        {
            case AuthType.Login when c.Auth.Login is null || c.Auth.Login.Steps.Count == 0:
                errors.Add("auth.login.steps are required when auth.type is Login.");
                break;
            case AuthType.StorageState when string.IsNullOrWhiteSpace(c.Auth.StorageStatePath):
                errors.Add("auth.storageStatePath is required when auth.type is StorageState.");
                break;
            case AuthType.Headers when c.Auth.Headers.Count == 0:
                errors.Add("auth.headers are required when auth.type is Headers.");
                break;
        }

        if (c.Api.Enabled)
        {
            if (c.Api.Endpoints.Count == 0 && !c.Api.UseDiscoveredEndpoints)
                errors.Add("api.endpoints is empty and api.useDiscoveredEndpoints is false.");
            if (c.Api.UseDiscoveredEndpoints && c.Pages.Count == 0 && !c.Crawl.Enabled)
                errors.Add("api.useDiscoveredEndpoints needs pages or crawl to discover calls from.");
            for (int i = 0; i < c.Api.Endpoints.Count; i++)
            {
                var e = c.Api.Endpoints[i];
                if (string.IsNullOrWhiteSpace(e.Url)) errors.Add($"api.endpoints[{i}].url is required.");
                if (string.IsNullOrWhiteSpace(e.Name)) e.Name = $"{e.Method.ToUpperInvariant()} {e.Url}";
                ValidateProfile(e.Profile, $"api.endpoints[{i}].profile", errors);
            }
            ValidateProfile(c.Api.Profile, "api.profile", errors);
        }

        try { _ = Statistics.Stats.Get(new Results.MetricStats(), c.Thresholds.PageStatistic); }
        catch (ArgumentException ex) { errors.Add($"thresholds.pageStatistic: {ex.Message}"); }

        var formats = new[] { "html", "json", "md", "csv" };
        foreach (var f in c.Output.Formats.Where(f => !formats.Contains(f.ToLowerInvariant())))
            errors.Add($"Unknown output format '{f}'. Use html, json, md or csv.");

        if (errors.Count > 0)
            throw new ConfigException("Invalid configuration:\n  - " + string.Join("\n  - ", errors));
    }

    private static void ValidateProfile(LoadProfile? p, string path, List<string> errors)
    {
        if (p is null) return;
        if (p.DurationSeconds < 1) errors.Add($"{path}.durationSeconds must be >= 1.");
        if (p.WarmupSeconds < 0) errors.Add($"{path}.warmupSeconds must be >= 0.");
        if (p.Mode == LoadMode.Closed && p.Concurrency < 1) errors.Add($"{path}.concurrency must be >= 1.");
        if (p.Mode == LoadMode.Open && p.RatePerSecond <= 0) errors.Add($"{path}.ratePerSecond must be > 0.");
        if (p.TimeoutMs < 1) errors.Add($"{path}.timeoutMs must be >= 1.");
    }
}
