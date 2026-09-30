namespace PerfLens.Browser.Internal;

internal static class ErrorText
{
    private const int MaxLength = 300;

    /// <summary>First line of the exception message, truncated, with any known secret values masked.</summary>
    public static string Short(Exception ex, IEnumerable<string>? secrets = null) => Short(ex.Message, secrets);

    public static string Short(string? message, IEnumerable<string>? secrets = null)
    {
        var text = (message ?? "").Replace("\r", "");
        var newline = text.IndexOf('\n');
        if (newline >= 0) text = text[..newline];
        text = text.Trim();
        if (secrets is not null)
        {
            foreach (var secret in secrets.Where(s => s.Length > 0))
                text = text.Replace(secret, "***", StringComparison.Ordinal);
        }
        if (text.Length == 0) text = "Unknown error";
        return Truncate(text, MaxLength);
    }

    public static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
