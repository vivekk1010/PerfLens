using System.Text;
using static PerfLens.DemoShop.Layout;

namespace PerfLens.DemoShop;

/// <summary>Server-rendered pages. Each non-baseline page carries a deliberate performance problem.</summary>
internal static class Pages
{
    private const int HeavyTileCount = 48;

    public static void MapPages(this WebApplication app)
    {
        app.MapGet("/", Home);
        app.MapGet("/about", About);
        app.MapGet("/products", Products);
        app.MapGet("/products/{id:int}", ProductDetail);
        app.MapGet("/slow", (Delegate)Slow);
        app.MapGet("/heavy", Heavy);
        app.MapGet("/jank", Jank);
        app.MapGet("/errors", Errors);
        app.MapGet("/dashboard", Dashboard);
    }

    public static IResult NotFound(HttpContext ctx) => Page(ctx, "Not found", $"""
        <section class="narrow">
          <h1>404 &mdash; page not found</h1>
          <p>Nothing lives at <code>{H(ctx.Request.Path)}</code>.</p>
          <p><a href="/">Back to the shop</a></p>
        </section>
        """, StatusCodes.Status404NotFound);

    private static IResult Home(HttpContext ctx)
    {
        (string Href, string Label, string Note)[] links =
        [
            ("/products", "All products", "Client-rendered grid from /api/products"),
            ("/products/1", "Aurora Headphones", "Product detail + reviews API"),
            ("/products/2", "Basalt Desk Lamp", "Product detail + reviews API"),
            ("/products/3", "Cedar Camping Mug", "Product detail + reviews API"),
            ("/about", "About", "Fast, light baseline page"),
            ("/slow", "Slow page", "~700 ms server time (slow TTFB)"),
            ("/heavy", "Heavy page", "Large uncompressed, uncached assets and layout shift"),
            ("/jank", "Jank page", "Long main-thread tasks (high TBT)"),
            ("/errors", "Errors page", "Console errors, uncaught exception, 404 script, flaky API"),
            ("/dashboard", "Dashboard", "Requires login; authenticated APIs"),
            ("/logout", "Logout", "Clears the session (crawlers should skip it)"),
            ("https://example.com", "Example.com", "External link (not crawled)"),
            ("/missing-page", "Missing page", "Broken link (404)"),
        ];
        var cards = string.Join("\n", links.Select(l =>
            $"""      <a class="card link-card" href="{H(l.Href)}"><strong>{H(l.Label)}</strong><span>{H(l.Note)}</span></a>"""));
        return Page(ctx, "Home", $"""
            <section class="hero">
              <h1>Welcome to DemoShop</h1>
              <p>A tiny shop with some pages that are fast and some that are deliberately slow, heavy or broken &mdash;
                 the perfect target for PerfLens.</p>
              <a class="button" href="/products">Browse products</a>
            </section>
            <section class="grid">
            {cards}
            </section>
            """);
    }

    private static IResult About(HttpContext ctx) => Page(ctx, "About", """
        <section class="narrow">
          <h1>About DemoShop</h1>
          <p>DemoShop is a sample ASP.NET Core application that ships with PerfLens. This page is the fast baseline:
             small HTML, one cached stylesheet, no scripts.</p>
          <p>Every other page exercises a different PerfLens finding, from slow server responses to layout shifts.</p>
        </section>
        """);

    private static IResult Products(HttpContext ctx)
    {
        var skeleton = string.Concat(Enumerable.Repeat("""<div class="card product skeleton"><div class="thumb"></div><div class="line"></div><div class="line short"></div></div>""", 8));
        return Page(ctx, "Products", $"""
            <h1>Products</h1>
            <section id="product-grid" class="grid products" aria-busy="true">{skeleton}</section>
            """, scripts: """<script src="/js/products.js" defer></script>""");
    }

    private static IResult ProductDetail(HttpContext ctx, int id, Catalog catalog)
    {
        var p = catalog.Find(id);
        if (p is null) return NotFound(ctx);
        var prev = id > 1 ? $"""<a href="/products/{id - 1}">&larr; Previous</a>""" : "";
        var next = id < Catalog.ProductCount ? $"""<a href="/products/{id + 1}">Next &rarr;</a>""" : "";
        return Page(ctx, p.Name, $"""
            <article id="product" class="product-detail" data-id="{id}">
              <img src="{p.Image}" width="320" height="240" alt="{H(p.Name)}">
              <div>
                <h1 id="product-name">{H(p.Name)}</h1>
                <p id="product-meta" class="muted">Loading details&hellip;</p>
                <p id="product-description"></p>
              </div>
            </article>
            <section>
              <h2>Reviews</h2>
              <ul id="reviews" class="reviews"><li class="muted">Loading reviews&hellip;</li></ul>
            </section>
            <nav class="pager">{prev} <a href="/products">All products</a> {next}</nav>
            """, scripts: """<script src="/js/product-detail.js" defer></script>""");
    }

    private static async Task<IResult> Slow(HttpContext ctx)
    {
        // Deliberate: ~700 ms of "server work" before the first byte.
        await Task.Delay(700, ctx.RequestAborted);
        return Page(ctx, "Slow", """
            <section class="narrow">
              <h1>Slow page</h1>
              <p>This page waited about 700&nbsp;ms on the server before responding. PerfLens should report a slow
                 time to first byte and high server time.</p>
            </section>
            """);
    }

    private static IResult Heavy(HttpContext ctx)
    {
        // Deliberate: big uncompressed/uncached images without width/height, a 300 KB uncompressed
        // bundle, 40+ small requests and a banner inserted after load (layout shift).
        var images = string.Join("\n", HeavyAssets.Images.Select(i => $"""    <img src="/heavy-assets/{i.Name}.bmp" alt="{i.Name}">"""));
        var tiles = new StringBuilder();
        for (int n = 1; n <= HeavyTileCount; n++) tiles.Append($"""<img class="tile" src="/heavy-assets/tile/{n}.svg" alt="">""");
        return Page(ctx, "Heavy", $"""
            <h1>Heavy page</h1>
            <p>Large images, a big JavaScript bundle and many small requests. None of the assets are compressed or
               cached, and a promotional banner appears late, pushing the content down.</p>
            <section class="gallery">
            {images}
            </section>
            <section class="tiles">{tiles}</section>
            """, scripts: """
            <script src="/static/vendor-heavy.js"></script>
            <script>
              window.addEventListener('load', function () {
                window.VendorHeavy && window.VendorHeavy.init();
                setTimeout(function () {
                  var banner = document.createElement('div');
                  banner.className = 'promo-banner';
                  banner.textContent = 'Flash sale! 20% off everything today only.';
                  var main = document.getElementById('main');
                  main.insertBefore(banner, main.firstChild);
                }, 300);
              });
            </script>
            """);
    }

    private static IResult Jank(HttpContext ctx) => Page(ctx, "Jank", """
        <h1 class="jank-title">This page keeps the main thread busy</h1>
        <p class="jank-lead">Several long JavaScript tasks run while and after the page loads, so clicks and scrolling
           would feel sluggish. PerfLens should report high Total Blocking Time and long tasks.</p>
        """, scripts: """
        <script>
          function busy(ms) { var end = performance.now() + ms; var x = 0; while (performance.now() < end) { x += Math.sqrt(x + 1); } return x; }
          // One long task during load (right after first paint) ...
          requestAnimationFrame(function () { setTimeout(function () { busy(200); }, 0); });
          // ... and three more after the load event.
          window.addEventListener('load', function () {
            setTimeout(function () { busy(180); }, 50);
            setTimeout(function () { busy(240); }, 300);
            setTimeout(function () { busy(160); }, 600);
          });
        </script>
        """);

    private static IResult Errors(HttpContext ctx) => Page(ctx, "Errors", """
        <h1>Errors page</h1>
        <p>This page logs a console error, throws an uncaught exception, requests a script that does not exist and
           calls a flaky API.</p>
        <p>Flaky API result: <span id="flaky-result">pending&hellip;</span></p>
        """, scripts: """
        <script src="/static/missing-analytics.js"></script>
        <script>
          console.error('DemoShop: simulated console error for PerfLens');
          setTimeout(function () { throw new Error('DemoShop: simulated uncaught exception'); }, 200);
          fetch('/api/flaky')
            .then(function (r) { document.getElementById('flaky-result').textContent = 'HTTP ' + r.status; })
            .catch(function (e) { document.getElementById('flaky-result').textContent = 'failed: ' + e; });
        </script>
        """);

    private static IResult Dashboard(HttpContext ctx, SessionStore sessions)
    {
        var session = sessions.Get(ctx);
        if (session is null) return Results.Redirect("/login?returnUrl=/dashboard");
        return Page(ctx, "Dashboard", $"""
            <h1 id="dashboard-title">Dashboard</h1>
            <p>Signed in as <strong id="dashboard-user">{H(session.User)}</strong>. <a href="/logout">Logout</a></p>
            <div class="columns">
              <section>
                <h2>Recent orders</h2>
                <table id="orders-table"><thead><tr><th>Order</th><th>Date</th><th>Status</th><th>Items</th><th>Total</th></tr></thead>
                  <tbody><tr><td colspan="5" class="muted">Loading&hellip;</td></tr></tbody></table>
              </section>
              <section>
                <h2>Server stats</h2>
                <table id="stats-table"><tbody><tr><td class="muted">Loading&hellip;</td></tr></tbody></table>
              </section>
            </div>
            """, scripts: """<script src="/js/dashboard.js" defer></script>""");
    }
}
