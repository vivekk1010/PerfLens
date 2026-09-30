using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PerfLens.Core.Abstractions;

namespace PerfLens.Load.Tests;

public sealed class TestServerFixture : IAsyncLifetime
{
    private WebApplication? _app;
    private int _flakyCounter;

    public string BaseUrl { get; private set; } = "";

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();

        app.MapGet("/fast", () => Results.Json(new { ok = true, items = new[] { 1, 2, 3 } }));
        app.MapGet("/slow", async (CancellationToken ct) =>
        {
            // Task.Delay can fire a millisecond or two early; guarantee at least 100 ms.
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            TimeSpan remaining;
            while ((remaining = TimeSpan.FromMilliseconds(100) - System.Diagnostics.Stopwatch.GetElapsedTime(start)) > TimeSpan.Zero)
                await Task.Delay(remaining < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : remaining, ct);
            return Results.Json(new { ok = true });
        });
        app.MapGet("/flaky", () =>
            Interlocked.Increment(ref _flakyCounter) % 4 == 0 ? Results.StatusCode(500) : Results.Json(new { ok = true }));
        app.MapPost("/echo", async (HttpRequest req) =>
        {
            using var reader = new StreamReader(req.Body);
            string body = await reader.ReadToEndAsync();
            return Results.Text(body, req.ContentType ?? "text/plain");
        });
        app.MapGet("/auth", (HttpRequest req) =>
            req.Headers["X-Api-Key"] == "k1" && req.Cookies["sid"] == "abc"
                ? Results.Json(new { ok = true })
                : Results.StatusCode(401));
        app.MapGet("/timeout", async (CancellationToken ct) =>
        {
            try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { }
            return Results.Ok();
        });

        await app.StartAsync();
        _app = app;
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
            .Addresses.First();
        BaseUrl = address.Replace("[::]", "127.0.0.1").TrimEnd('/');
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class ServerCollection : ICollectionFixture<TestServerFixture>
{
    public const string Name = "server";
}

public sealed class RecordingProgress : IProgressReporter
{
    public ConcurrentQueue<string> Messages { get; } = new();
    public void Info(string message) => Messages.Enqueue("INFO " + message);
    public void Warn(string message) => Messages.Enqueue("WARN " + message);
    public void Error(string message) => Messages.Enqueue("ERROR " + message);
    public void Debug(string message) => Messages.Enqueue("DEBUG " + message);
}
