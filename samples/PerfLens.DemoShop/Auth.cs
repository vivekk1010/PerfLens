using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using static PerfLens.DemoShop.Layout;

namespace PerfLens.DemoShop;

public sealed record Session(string Token, string User, DateTimeOffset CreatedAt);

/// <summary>In-memory session store keyed by a random token held in the demoshop_session cookie.</summary>
public sealed class SessionStore
{
    public const string CookieName = "demoshop_session";

    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    public int ActiveCount => _sessions.Count;

    public Session Create(string user)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var session = new Session(token, user, DateTimeOffset.UtcNow);
        _sessions[token] = session;
        return session;
    }

    public Session? Get(HttpContext ctx) =>
        ctx.Request.Cookies.TryGetValue(CookieName, out var token) && token is not null && _sessions.TryGetValue(token, out var s)
            ? s
            : null;

    public void Remove(HttpContext ctx)
    {
        if (ctx.Request.Cookies.TryGetValue(CookieName, out var token) && token is not null)
            _sessions.TryRemove(token, out _);
    }
}

internal static class Auth
{
    // DEMO-ONLY credentials. The password comes from DEMOSHOP_PASSWORD (default "perflens-demo").
    // Never reuse this pattern for a real application.
    public const string DemoUser = "demo";
    private static string DemoPassword => Environment.GetEnvironmentVariable("DEMOSHOP_PASSWORD") is { Length: > 0 } p ? p : "perflens-demo";

    public static void MapAuth(this WebApplication app)
    {
        app.MapGet("/login", (HttpContext ctx, string? returnUrl) => LoginPage(ctx, returnUrl, error: null));

        app.MapPost("/login", async (HttpContext ctx, SessionStore sessions, Counters counters) =>
        {
            if (!ctx.Request.HasFormContentType) return Results.BadRequest("Expected a form post.");
            var form = await ctx.Request.ReadFormAsync();
            var user = form["username"].ToString();
            var password = form["password"].ToString();
            var returnUrl = SafeReturnUrl(form["returnUrl"].ToString());

            var ok = user == DemoUser && FixedTimeEquals(password, DemoPassword);
            counters.Login(ok);
            if (!ok) return LoginPage(ctx, returnUrl, "Invalid username or password.", StatusCodes.Status401Unauthorized);

            var session = sessions.Create(user);
            ctx.Response.Cookies.Append(SessionStore.CookieName, session.Token, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = ctx.Request.IsHttps,
                Path = "/",
            });
            return Results.Redirect(returnUrl ?? "/dashboard");
        });

        app.MapGet("/logout", (HttpContext ctx, SessionStore sessions) =>
        {
            sessions.Remove(ctx);
            ctx.Response.Cookies.Delete(SessionStore.CookieName, new CookieOptions { Path = "/" });
            return Results.Redirect("/");
        });
    }

    private static IResult LoginPage(HttpContext ctx, string? returnUrl, string? error, int statusCode = 200)
    {
        var errorHtml = error is null ? "" : $"""<p class="alert" id="login-error" role="alert">{H(error)}</p>""";
        var body = $"""
            <section class="narrow">
              <h1>Sign in</h1>
              <p class="muted">Demo credentials: <code>demo</code> / value of <code>DEMOSHOP_PASSWORD</code> (default <code>perflens-demo</code>).</p>
              {errorHtml}
              <form method="post" action="/login" class="form" id="login-form">
                <input type="hidden" name="returnUrl" value="{H(SafeReturnUrl(returnUrl) ?? "")}">
                <label for="username">Username</label>
                <input id="username" name="username" autocomplete="username" required>
                <label for="password">Password</label>
                <input id="password" name="password" type="password" autocomplete="current-password" required>
                <button id="login-btn" type="submit">Sign in</button>
              </form>
            </section>
            """;
        return Page(ctx, "Login", body, statusCode);
    }

    /// <summary>Only local paths are accepted, to avoid an open redirect.</summary>
    private static string? SafeReturnUrl(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\") ? url : null;

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(a)),
            SHA256.HashData(Encoding.UTF8.GetBytes(b)));
}
