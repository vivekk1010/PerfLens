using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PerfLens.Browser.Tests;

/// <summary>Tiny Kestrel site on a dynamic loopback port used by the browser tests.</summary>
public sealed class TestSite : IAsyncLifetime
{
    public const string SessionCookie = "perflens_session";
    public const string Username = "alice";
    public const string Password = "s3cret-pw";

    // 1x1 transparent PNG.
    private static readonly byte[] Pixel = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

    private WebApplication? _app;

    public string BaseUrl { get; private set; } = "";

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();

        app.MapGet("/", () => Html("Home", """
            <h1>Home</h1>
            <a href="/a">Page A</a>
            <a href="/b#section">Page B</a>
            <a href="/missing">Missing</a>
            <a href="/logout">Log out</a>
            <a href="https://external.example.invalid/page">External</a>
            <a href="mailto:someone@example.com">Mail</a>
            <a href="/files/report.pdf">Report</a>
            """));

        app.MapGet("/a", () => Html("Page A", """
            <link rel="stylesheet" href="/static/site.css">
            <script src="/static/app.js"></script>
            <h1 class="title">Page A</h1>
            <img id="hero" src="/static/pixel.png" width="200" height="100">
            <script>fetch('/api/data').then(r => r.json()).then(d => { document.title = 'A ' + d.items.length; });</script>
            """));

        app.MapGet("/b", () => Html("Page B", """
            <h1>Page B</h1>
            <p id="section">Busy page</p>
            <script>
              console.error('boom from page b');
              addEventListener('load', () => setTimeout(() => { const end = performance.now() + 200; while (performance.now() < end) {} }, 100));
            </script>
            """));

        app.MapGet("/logout", (HttpContext ctx) =>
        {
            ctx.Response.Cookies.Delete(SessionCookie);
            return Html("Logged out", "<p>bye</p>");
        });

        app.MapGet("/login", () => Html("Login", """
            <form method="post" action="/login">
              <input id="username" name="username">
              <input id="password" name="password" type="password">
              <button id="submit" type="submit">Sign in</button>
            </form>
            """));

        app.MapPost("/login", async (HttpContext ctx) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            if (form["username"] == Username && form["password"] == Password)
            {
                ctx.Response.Cookies.Append(SessionCookie, "token-123", new CookieOptions { Path = "/", HttpOnly = true });
                return Results.Redirect("/secure");
            }
            return Results.Redirect("/login?error=1");
        });

        app.MapGet("/secure", (HttpContext ctx) =>
            ctx.Request.Cookies.ContainsKey(SessionCookie)
                ? Html("Secure", "<h1 id=\"welcome\">Welcome back</h1>")
                : Results.Redirect("/login"));

        app.MapGet("/api/data", () => Results.Json(new { items = new[] { 1, 2, 3 } }));

        app.MapGet("/static/site.css", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "public, max-age=3600";
            return Results.Text("body { font-family: sans-serif; } .title { color: #333; }", "text/css");
        });
        app.MapGet("/static/app.js", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "public, max-age=3600";
            return Results.Text("window.appLoaded = true;", "application/javascript");
        });
        app.MapGet("/static/pixel.png", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "public, max-age=3600";
            return Results.Bytes(Pixel, "image/png");
        });

        await app.StartAsync();
        BaseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _app = app;
    }

    public async Task DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }

    private static IResult Html(string title, string body) =>
        Results.Content($"<!doctype html><html><head><meta charset=\"utf-8\"><title>{title}</title></head><body>{body}</body></html>", "text/html; charset=utf-8");
}

[CollectionDefinition(Name)]
public sealed class BrowserCollection : ICollectionFixture<TestSite>
{
    public const string Name = "browser";
}
