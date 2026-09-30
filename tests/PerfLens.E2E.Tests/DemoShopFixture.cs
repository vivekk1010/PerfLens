using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace PerfLens.E2E.Tests;

/// <summary>Starts the DemoShop sample as a real Kestrel process on a free port for the test session.</summary>
public sealed class DemoShopFixture : IAsyncLifetime
{
    private Process? _process;

    public string BaseUrl { get; private set; } = "";
    public string RepoRoot { get; } = FindRepoRoot();

    public async Task InitializeAsync()
    {
        var dll = Directory.GetFiles(Path.Combine(RepoRoot, "samples", "PerfLens.DemoShop", "bin"), "PerfLens.DemoShop.dll", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault() ?? throw new InvalidOperationException("DemoShop is not built. Run: dotnet build");

        int port = FreePort();
        BaseUrl = $"http://127.0.0.1:{port}";
        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\" --urls {BaseUrl}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(dll)!,
        };
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        _process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start DemoShop.");
        _process.OutputDataReceived += (_, _) => { };
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited) throw new InvalidOperationException($"DemoShop exited with code {_process.ExitCode}.");
            try
            {
                if ((await http.GetAsync($"{BaseUrl}/health")).IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(250);
        }
        throw new TimeoutException("DemoShop did not become healthy within 30 s.");
    }

    public Task DisposeAsync()
    {
        try
        {
            if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
            _process?.WaitForExit(5000);
        }
        catch (InvalidOperationException) { }
        _process?.Dispose();
        return Task.CompletedTask;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PerfLens.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate PerfLens.sln from the test output folder.");
    }
}

[CollectionDefinition(Name)]
public sealed class DemoShopCollection : ICollectionFixture<DemoShopFixture>
{
    public const string Name = "DemoShop";
}
