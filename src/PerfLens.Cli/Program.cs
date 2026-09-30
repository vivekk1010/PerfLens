using PerfLens.Cli;
using PerfLens.Cli.Commands;
using PerfLens.Cli.Infrastructure;
using PerfLens.Cli.Pipeline;
using PerfLens.Core.Configuration;

return await CliApp.MainAsync(args);

namespace PerfLens.Cli
{
    public static class ExitCodes
    {
        public const int Success = 0;
        public const int ThresholdFailed = 1;
        public const int InvalidInput = 2;
        public const int RuntimeError = 3;
        public const int Cancelled = 130;
    }

    public static class CliApp
    {
        public const string Usage = """
            PerfLens - web application performance benchmarking (pages in a real browser + API load)

            Usage:
              perflens run -c <config.yaml> [options]     Run a benchmark from a config file
                  --url <baseUrl>          Override target.baseUrl
                  -o, --output <dir>       Output directory (no timestamped subfolder)
                  --baseline <results.json>  Compare with a previous run
                  --label <text>           Label shown in the report (e.g. build number)
                  --iterations <n>         Override browser.iterations
                  --headed                 Show the browser window
                  --no-api | --no-crawl    Skip API load tests / crawling
                  --formats html,json,md,csv
                  --open                   Open the HTML report when done
                  -v, --verbose            Detailed progress
              perflens quick <url> [--pages /,/about] [--crawl] [--max-pages n] [--iterations n] [--api /api/x]... [-o dir]
              perflens init [path] [--force]              Write a commented starter config (default perflens.yaml)
              perflens validate -c <config>               Validate a config and print what will run
              perflens report <results.json> [-o dir] [--formats ...] [--open]   Re-render reports
              perflens install-browsers [chromium|firefox|webkit|all] [--with-deps]
              perflens doctor                             Check the installation
              perflens --version | --help

            Exit codes: 0 passed, 1 thresholds failed, 2 invalid config/arguments, 3 runtime error, 130 cancelled.
            Docs: https://github.com/vivekk1010/PerfLens
            """;

        public static async Task<int> MainAsync(string[] args)
        {
            using var cts = new CancellationTokenSource();
            ConsoleCancelEventHandler onCancel = (_, e) =>
            {
                if (cts.IsCancellationRequested) return;
                e.Cancel = true;
                Console.Error.WriteLine("\nCancelling... (press Ctrl+C again to force quit)");
                cts.Cancel();
            };
            Console.CancelKeyPress += onCancel;
            try
            {
                return await DispatchAsync(args, cts.Token);
            }
            finally
            {
                Console.CancelKeyPress -= onCancel;
            }
        }

        public static async Task<int> DispatchAsync(string[] args, CancellationToken ct)
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                Console.WriteLine(Usage);
                return args.Length == 0 ? ExitCodes.InvalidInput : ExitCodes.Success;
            }
            if (args[0] is "--version" or "version")
            {
                Console.WriteLine($"perflens {RunPipeline.ToolVersion}");
                return ExitCodes.Success;
            }

            var rest = args[1..];
            try
            {
                if (rest.Contains("--help") || rest.Contains("-h"))
                {
                    Console.WriteLine(Usage);
                    return ExitCodes.Success;
                }
                return args[0].ToLowerInvariant() switch
                {
                    "run" => await RunCommands.RunAsync(rest, ct),
                    "quick" => await RunCommands.QuickAsync(rest, ct),
                    "init" => UtilityCommands.Init(rest),
                    "validate" => UtilityCommands.Validate(rest),
                    "report" => await UtilityCommands.ReportAsync(rest, ct),
                    "install-browsers" => UtilityCommands.InstallBrowsers(rest),
                    "doctor" => await UtilityCommands.DoctorAsync(),
                    _ => throw new UsageException($"Unknown command '{args[0]}'. Run 'perflens --help'."),
                };
            }
            catch (UsageException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return ExitCodes.InvalidInput;
            }
            catch (ConfigException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return ExitCodes.InvalidInput;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Console.Error.WriteLine("Cancelled.");
                return ExitCodes.Cancelled;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                if (Environment.GetEnvironmentVariable("PERFLENS_DEBUG") == "1") Console.Error.WriteLine(ex);
                return ExitCodes.RuntimeError;
            }
        }
    }
}
