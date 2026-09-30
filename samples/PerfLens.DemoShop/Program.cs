// PerfLens DemoShop: a small, deterministic web shop used as a realistic target for PerfLens
// end-to-end tests and demos. Several pages contain DELIBERATE performance problems
// (slow TTFB, heavy uncompressed assets, layout shift, long tasks, console errors) so that
// PerfLens findings fire. Do not "fix" them.
using System.IO.Compression;
using Microsoft.AspNetCore.ResponseCompression;
using PerfLens.DemoShop;

// wwwroot is copied next to the binary, so static files work no matter which directory the
// app is started from (`dotnet run`, `dotnet bin/.../PerfLens.DemoShop.dll`, CI).
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});

// Default to http://127.0.0.1:5080 unless --urls / ASPNETCORE_URLS / launch settings specify otherwise.
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://127.0.0.1:5080");

builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.Providers.Add<BrotliCompressionProvider>();
    o.Providers.Add<GzipCompressionProvider>();
    o.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["image/svg+xml"]);
});
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

builder.Services.AddSingleton<Catalog>();
builder.Services.AddSingleton<Counters>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<HeavyAssets>();

var app = builder.Build();

// Generate the large /heavy payloads once at startup.
_ = app.Services.GetRequiredService<HeavyAssets>();

var counters = app.Services.GetRequiredService<Counters>();
app.Use(async (ctx, next) =>
{
    counters.CountRequest(ctx.Request.Path);
    await next();
});

// Everything is compressed except the deliberately heavy /heavy assets.
app.UseWhen(ctx => !HeavyAssets.IsUncompressedPath(ctx.Request.Path), branch => branch.UseResponseCompression());

app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "public, max-age=3600",
});

app.MapPages();
app.MapAuth();
app.MapApi();
app.MapAssets();
app.MapFallback((HttpContext ctx) => Pages.NotFound(ctx));

app.Run();
