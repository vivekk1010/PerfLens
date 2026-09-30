using System.Globalization;
using System.Text;

namespace PerfLens.Reporting.Internal;

/// <summary>Minimal RFC 4180 CSV builder (CRLF line endings, invariant culture).</summary>
internal sealed class Csv
{
    private readonly StringBuilder _sb = new();

    public Csv Row(IEnumerable<object?> cells)
    {
        bool first = true;
        foreach (var cell in cells)
        {
            if (!first) _sb.Append(',');
            first = false;
            _sb.Append(Escape(Format(cell)));
        }
        _sb.Append("\r\n");
        return this;
    }

    public Csv Row(params object?[] cells) => Row((IEnumerable<object?>)cells);

    public override string ToString() => _sb.ToString();

    internal static string Format(object? value) => value switch
    {
        null => "",
        string s => NeutralizeFormula(s),
        bool b => b ? "true" : "false",
        double d => double.IsNaN(d) || double.IsInfinity(d) ? "" : Math.Round(d, 3).ToString("0.###", CultureInfo.InvariantCulture),
        float f => ((double)f).ToString("0.###", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => NeutralizeFormula(value.ToString() ?? ""),
    };

    /// <summary>Text from the target app could start with a spreadsheet formula prefix; prefix it with a quote.</summary>
    private static string NeutralizeFormula(string s) =>
        s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + s : s;

    internal static string Escape(string s) =>
        s.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
