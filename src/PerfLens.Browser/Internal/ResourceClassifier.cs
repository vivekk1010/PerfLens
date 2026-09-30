namespace PerfLens.Browser.Internal;

/// <summary>Maps Playwright resource types and Resource Timing initiator types onto the report's resource buckets.</summary>
internal static class ResourceClassifier
{
    public const string Document = "document";
    public const string Script = "script";
    public const string Stylesheet = "stylesheet";
    public const string Image = "image";
    public const string Font = "font";
    public const string Xhr = "xhr";
    public const string Fetch = "fetch";
    public const string Media = "media";
    public const string Other = "other";

    public static bool IsApi(string playwrightResourceType) =>
        playwrightResourceType is Xhr or Fetch;

    public static string FromPlaywright(string? resourceType, string url) => resourceType switch
    {
        Document or Script or Stylesheet or Image or Font or Xhr or Fetch or Media => resourceType,
        _ => FromExtension(url) ?? Other,
    };

    public static string FromInitiator(string? initiatorType, string url) => initiatorType switch
    {
        "navigation" or "iframe" or "frame" => Document,
        "script" => Script,
        "img" or "image" or "input" or "body" => FromExtension(url) ?? Image,
        "xmlhttprequest" => Xhr,
        "fetch" or "beacon" => Fetch,
        "video" or "audio" or "track" => Media,
        _ => FromExtension(url) ?? Other,
    };

    public static string? FromExtension(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        return Path.GetExtension(u.AbsolutePath).ToLowerInvariant() switch
        {
            ".js" or ".mjs" or ".cjs" => Script,
            ".css" => Stylesheet,
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" or ".webp" or ".avif" or ".ico" or ".bmp" => Image,
            ".woff" or ".woff2" or ".ttf" or ".otf" or ".eot" => Font,
            ".mp4" or ".webm" or ".mp3" or ".ogg" or ".wav" or ".m4a" or ".mov" or ".m3u8" => Media,
            _ => null,
        };
    }
}
