using System.Net;

namespace PerfLens.DemoShop;

/// <summary>Shared HTML layout rendered with plain C# string templates.</summary>
internal static class Layout
{
    public static string H(string? value) => WebUtility.HtmlEncode(value ?? "");

    public static IResult Page(HttpContext ctx, string title, string body, int statusCode = 200, string head = "", string scripts = "") =>
        Results.Content(Render(ctx, title, body, head, scripts), "text/html; charset=utf-8", statusCode: statusCode);

    public static string Render(HttpContext ctx, string title, string body, string head = "", string scripts = "")
    {
        var signedIn = ctx.RequestServices.GetRequiredService<SessionStore>().Get(ctx) is not null;
        var account = signedIn
            ? """<a href="/logout">Logout</a>"""
            : """<a href="/login">Login</a>""";
        return $"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>{H(title)} · DemoShop</title>
              <link rel="icon" href="/favicon.svg" type="image/svg+xml">
              <link rel="stylesheet" href="/css/site.css">
              {head}
            </head>
            <body>
              <header class="site-header">
                <a class="brand" href="/">DemoShop</a>
                <nav>
                  <a href="/">Home</a>
                  <a href="/products">Products</a>
                  <a href="/about">About</a>
                  <a href="/slow">Slow</a>
                  <a href="/heavy">Heavy</a>
                  <a href="/jank">Jank</a>
                  <a href="/errors">Errors</a>
                  <a href="/dashboard">Dashboard</a>
                  {account}
                </nav>
              </header>
              <main id="main">
            {body}
            </main>
              <footer class="site-footer">PerfLens DemoShop &mdash; a deliberately imperfect demo target.</footer>
              {scripts}
            </body>
            </html>
            """;
    }
}
