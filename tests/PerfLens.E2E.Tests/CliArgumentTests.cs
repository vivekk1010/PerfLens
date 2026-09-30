using PerfLens.Cli;

namespace PerfLens.E2E.Tests;

public sealed class CliArgumentTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("perflens-cli-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { }
    }

    private static Task<int> Cli(params string[] args) => CliApp.DispatchAsync(args, CancellationToken.None);

    [Fact]
    public async Task Version_and_help_succeed()
    {
        Assert.Equal(ExitCodes.Success, await Cli("--version"));
        Assert.Equal(ExitCodes.Success, await Cli("--help"));
        Assert.Equal(ExitCodes.Success, await Cli("run", "--help"));
    }

    [Fact]
    public async Task No_arguments_and_unknown_command_are_usage_errors()
    {
        Assert.Equal(ExitCodes.InvalidInput, await Cli());
        Assert.Equal(ExitCodes.InvalidInput, await Cli("explode"));
    }

    [Fact]
    public async Task Missing_or_invalid_config_is_exit_code_2()
    {
        Assert.Equal(ExitCodes.InvalidInput, await Cli("run", "-c", Path.Combine(_work, "nope.yaml")));

        var bad = Path.Combine(_work, "bad.yaml");
        await File.WriteAllTextAsync(bad, "target:\n  baseUrl: not-a-url\n");
        Assert.Equal(ExitCodes.InvalidInput, await Cli("validate", "-c", bad));
        Assert.Equal(ExitCodes.InvalidInput, await Cli("run", "-c", bad));
    }

    [Fact]
    public async Task Unknown_option_is_rejected()
    {
        var cfg = Path.Combine(_work, "ok.yaml");
        await File.WriteAllTextAsync(cfg, "target:\n  baseUrl: http://127.0.0.1:1\npages:\n  - path: /\n");
        Assert.Equal(ExitCodes.InvalidInput, await Cli("run", "-c", cfg, "--iteratoins", "3"));
        Assert.Equal(ExitCodes.InvalidInput, await Cli("run", "-c", cfg, "--iterations", "zero"));
    }

    [Fact]
    public async Task Init_writes_a_valid_template_and_refuses_to_overwrite()
    {
        var path = Path.Combine(_work, "perflens.yaml");
        Assert.Equal(ExitCodes.Success, await Cli("init", path));
        Assert.True(new FileInfo(path).Length > 200);
        Assert.Contains("crawl:", await File.ReadAllTextAsync(path));
        Assert.Equal(ExitCodes.Success, await Cli("validate", "-c", path));
        Assert.Equal(ExitCodes.InvalidInput, await Cli("init", path));
        Assert.Equal(ExitCodes.Success, await Cli("init", path, "--force"));
    }

    [Fact]
    public async Task Unreachable_target_still_produces_a_report()
    {
        var cfg = Path.Combine(_work, "down.yaml");
        await File.WriteAllTextAsync(cfg, """
            name: Down
            target: { baseUrl: "http://127.0.0.1:9" }
            pages: [ { name: Home, path: / } ]
            browser: { iterations: 1, warmupIterations: 0, navigationTimeoutMs: 5000, settleMs: 0 }
            """);
        var output = Path.Combine(_work, "down-out");
        int exit = await Cli("run", "-c", cfg, "-o", output);
        Assert.Equal(ExitCodes.ThresholdFailed, exit);
        Assert.True(File.Exists(Path.Combine(output, "report.html")));
    }
}
