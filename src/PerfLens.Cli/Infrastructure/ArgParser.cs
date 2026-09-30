namespace PerfLens.Cli.Infrastructure;

public sealed class UsageException(string message) : Exception(message);

/// <summary>
/// Minimal GNU-style argument parser: positionals, --flag, --key value, --key=value, -k value.
/// Repeated options are collected in order.
/// </summary>
public sealed class ParsedArgs
{
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _consumed = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Positionals { get; } = new();

    /// <param name="valueOptions">Option names (without dashes) that take a value, e.g. "config".</param>
    /// <param name="aliases">Short alias to long name, e.g. 'c' -> "config".</param>
    public static ParsedArgs Parse(IReadOnlyList<string> args, ISet<string> valueOptions, IReadOnlyDictionary<char, string>? aliases = null)
    {
        var parsed = new ParsedArgs();
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            string? name = null;
            string? inlineValue = null;

            if (a.StartsWith("--") && a.Length > 2)
            {
                name = a[2..];
                int eq = name.IndexOf('=');
                if (eq >= 0) { inlineValue = name[(eq + 1)..]; name = name[..eq]; }
            }
            else if (a.StartsWith('-') && a.Length == 2 && a[1] != '-')
            {
                if (aliases is null || !aliases.TryGetValue(a[1], out name))
                    throw new UsageException($"Unknown option '{a}'.");
            }
            else
            {
                parsed.Positionals.Add(a);
                continue;
            }

            if (valueOptions.Contains(name))
            {
                var value = inlineValue ?? (i + 1 < args.Count ? args[++i] : throw new UsageException($"Option '--{name}' needs a value."));
                if (!parsed._options.TryGetValue(name, out var list)) parsed._options[name] = list = new();
                list.Add(value);
            }
            else
            {
                if (inlineValue is not null) throw new UsageException($"Option '--{name}' does not take a value.");
                parsed._flags.Add(name);
            }
        }
        return parsed;
    }

    public string? Get(string name)
    {
        _consumed.Add(name);
        return _options.TryGetValue(name, out var v) ? v[^1] : null;
    }

    public IReadOnlyList<string> GetAll(string name)
    {
        _consumed.Add(name);
        return _options.TryGetValue(name, out var v) ? v : Array.Empty<string>();
    }

    public bool Has(string flag)
    {
        _consumed.Add(flag);
        return _flags.Contains(flag);
    }

    public int? GetInt(string name, int min = int.MinValue)
    {
        var v = Get(name);
        if (v is null) return null;
        if (!int.TryParse(v, out var n) || n < min)
            throw new UsageException($"Option '--{name}' must be an integer >= {min} (got '{v}').");
        return n;
    }

    /// <summary>Throws for options/flags that the command never asked about (typos).</summary>
    public void EnsureNoUnknown()
    {
        var unknown = _options.Keys.Concat(_flags).Where(k => !_consumed.Contains(k)).ToList();
        if (unknown.Count > 0)
            throw new UsageException($"Unknown option(s): {string.Join(", ", unknown.Select(u => "--" + u))}");
    }
}
