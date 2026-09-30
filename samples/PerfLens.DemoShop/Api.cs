using System.Text.Json;

namespace PerfLens.DemoShop;

/// <summary>JSON APIs used by the pages and by PerfLens load tests. Delays are fixed so results are repeatable.</summary>
internal static class Api
{
    private const int MaxEchoBytes = 1024 * 1024;

    public static void MapApi(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Text("ok"));

        var api = app.MapGroup("/api");

        api.MapGet("/products", async (Catalog catalog, CancellationToken ct) =>
        {
            await Task.Delay(15, ct);
            return Results.Json(catalog.Products.Select(p => new { p.Id, p.Name, p.Category, p.Price, p.Rating, p.Image }));
        });

        api.MapGet("/products/{id:int}", (int id, Catalog catalog) =>
            catalog.Find(id) is { } p ? Results.Json(p) : Results.Json(new { error = "product not found" }, statusCode: 404));

        api.MapGet("/products/{id:int}/reviews", async (int id, Catalog catalog, CancellationToken ct) =>
        {
            if (catalog.Find(id) is null) return Results.Json(new { error = "product not found" }, statusCode: 404);
            await Task.Delay(40, ct);
            return Results.Json(catalog.ReviewsFor(id));
        });

        api.MapGet("/orders", async (HttpContext ctx, SessionStore sessions, Catalog catalog, CancellationToken ct) =>
        {
            var session = sessions.Get(ctx);
            if (session is null) return Results.Json(new { error = "unauthorized" }, statusCode: 401);
            await Task.Delay(30, ct);
            return Results.Json(catalog.OrdersFor(session.User));
        });

        api.MapGet("/stats", (Counters counters, SessionStore sessions) => Results.Json(counters.Snapshot(sessions.ActiveCount)));

        api.MapGet("/slow", async (Counters counters, CancellationToken ct) =>
        {
            var n = counters.NextSlow();
            var delay = 150 + (int)(n * 73 % 201); // cycles deterministically through 150-350 ms
            await Task.Delay(delay, ct);
            return Results.Json(new { call = n, delayMs = delay });
        });

        api.MapGet("/flaky", (Counters counters) =>
        {
            var n = counters.NextFlaky();
            if (n % 10 == 0)
            {
                counters.FlakyFailure();
                return Results.Json(new { call = n, error = "simulated failure (every 10th call)" }, statusCode: 500);
            }
            return Results.Json(new { call = n, ok = true });
        });

        api.MapPost("/echo", async (HttpRequest request, CancellationToken ct) =>
        {
            if (request.ContentLength > MaxEchoBytes) return Results.Json(new { error = "body too large" }, statusCode: 413);
            try
            {
                using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
                return Results.Json(new { echo = doc.RootElement.Clone(), receivedAt = DateTimeOffset.UtcNow });
            }
            catch (JsonException ex)
            {
                return Results.Json(new { error = "invalid JSON", detail = ex.Message }, statusCode: 400);
            }
        });

        api.MapGet("/search", async (string? q, Catalog catalog, CancellationToken ct) =>
        {
            await Task.Delay(60, ct);
            var results = catalog.Search(q);
            return Results.Json(new { query = q ?? "", count = results.Count, results });
        });
    }
}
