using System.Text;

namespace PerfLens.DemoShop;

/// <summary>
/// Deterministic generated assets. The /heavy-assets/* and /static/vendor-heavy.js payloads are
/// deliberately large and served without compression or cache headers.
/// </summary>
public sealed class HeavyAssets
{
    public static readonly (string Name, int Width, int Height)[] Images =
    [
        ("hero-1", 460, 400),
        ("gallery-2", 400, 400),
        ("gallery-3", 380, 380),
        ("gallery-4", 440, 420),
    ];

    private readonly Dictionary<string, byte[]> _images;

    public byte[] VendorJs { get; }

    public HeavyAssets()
    {
        _images = Images.Select((img, i) => (img.Name, Bytes: Bitmap(img.Width, img.Height, seed: (uint)(i + 1) * 2654435761u)))
            .ToDictionary(x => x.Name, x => x.Bytes, StringComparer.OrdinalIgnoreCase);
        VendorJs = Encoding.UTF8.GetBytes(BuildVendorJs(300 * 1024));
    }

    public byte[]? Image(string name) => _images.GetValueOrDefault(name);

    public static bool IsUncompressedPath(PathString path) =>
        path.StartsWithSegments("/heavy-assets") || path.StartsWithSegments("/static/vendor-heavy.js");

    /// <summary>24-bit uncompressed BMP with a gradient plus pseudo-random noise (so it stays large even if compressed).</summary>
    private static byte[] Bitmap(int width, int height, uint seed)
    {
        int rowSize = (width * 3 + 3) & ~3;
        int pixelBytes = rowSize * height;
        var data = new byte[54 + pixelBytes];
        void I32(int offset, int value) => BitConverter.TryWriteBytes(data.AsSpan(offset, 4), value);
        void I16(int offset, short value) => BitConverter.TryWriteBytes(data.AsSpan(offset, 2), value);

        data[0] = (byte)'B';
        data[1] = (byte)'M';
        I32(2, data.Length);
        I32(10, 54);
        I32(14, 40);
        I32(18, width);
        I32(22, height);
        I16(26, 1);
        I16(28, 24);
        I32(34, pixelBytes);
        I32(38, 2835);
        I32(42, 2835);

        uint state = seed;
        for (int y = 0; y < height; y++)
        {
            int row = 54 + y * rowSize;
            for (int x = 0; x < width; x++)
            {
                state = state * 1664525u + 1013904223u;
                int noise = (int)(state >> 27);
                int p = row + x * 3;
                data[p] = (byte)(x * 255 / width ^ noise);
                data[p + 1] = (byte)(y * 255 / height + noise);
                data[p + 2] = (byte)((x + y + (int)(seed & 0xff)) & 0xff);
            }
        }
        return data;
    }

    private static string BuildVendorJs(int targetBytes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("/* vendor-heavy.js: generated filler bundle for PerfLens DemoShop (deliberately large and uncompressed). */");
        sb.AppendLine("(function () {");
        sb.AppendLine("  var registry = {};");
        sb.AppendLine("  function reg(name, fn) { registry[name] = fn; }");
        int i = 0;
        while (sb.Length < targetBytes)
        {
            sb.Append("  reg('module").Append(i).Append("', function (a, b) { var s = ").Append(i % 97)
              .Append("; for (var i = 0; i < a; i++) { s = (s * 31 + b + i) % 1000003; } return s + ' filler text for module ")
              .Append(i).AppendLine("'; });");
            i++;
        }
        sb.Append("  window.VendorHeavy = { count: ").Append(i).AppendLine(", registry: registry, init: function () {");
        sb.AppendLine("    var total = 0; for (var k in registry) { total += registry[k](3, 7).length; } return total; } };");
        sb.AppendLine("})();");
        return sb.ToString();
    }
}

internal static class Assets
{
    public static void MapAssets(this WebApplication app)
    {
        app.MapGet("/img/product/{id:int}.svg", (HttpContext ctx, int id) =>
        {
            if (id is < 1 or > Catalog.ProductCount) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "public, max-age=3600";
            return Results.Text(ProductSvg(id), "image/svg+xml");
        });

        // Deliberately: no Cache-Control / ETag / Last-Modified and (see Program.cs) no compression.
        app.MapGet("/heavy-assets/{name}.bmp", (string name, HeavyAssets assets) =>
            assets.Image(name) is { } bytes ? Results.Bytes(bytes, "image/bmp") : Results.NotFound());

        app.MapGet("/heavy-assets/tile/{n:int}.svg", (int n) =>
            n is < 1 or > 100 ? Results.NotFound() : Results.Text(TileSvg(n), "image/svg+xml"));

        app.MapGet("/static/vendor-heavy.js", (HeavyAssets assets) =>
            Results.Bytes(assets.VendorJs, "application/javascript; charset=utf-8"));
    }

    private static string ProductSvg(int id)
    {
        var hue = id * 47 % 360;
        return $"""<svg xmlns="http://www.w3.org/2000/svg" width="160" height="120" viewBox="0 0 160 120"><rect width="160" height="120" rx="12" fill="hsl({hue},70%,88%)"/><circle cx="80" cy="52" r="28" fill="hsl({hue},60%,45%)"/><text x="80" y="106" font-family="sans-serif" font-size="14" text-anchor="middle" fill="#333">#{id}</text></svg>""";
    }

    private static string TileSvg(int n)
    {
        var hue = n * 29 % 360;
        return $"""<svg xmlns="http://www.w3.org/2000/svg" width="48" height="48"><rect width="48" height="48" fill="hsl({hue},65%,60%)"/><text x="24" y="30" font-size="14" text-anchor="middle" fill="#fff">{n}</text></svg>""";
    }
}
