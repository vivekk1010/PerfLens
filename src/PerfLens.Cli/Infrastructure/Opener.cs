using System.Diagnostics;

namespace PerfLens.Cli.Infrastructure;

internal static class Opener
{
    /// <summary>Best-effort open of a file in the default app (Windows, macOS, Linux desktop, WSL).</summary>
    public static void TryOpen(string path, ConsoleProgressReporter progress)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", path);
            else if (IsWsl())
            {
                var win = RunCapture("wslpath", $"-w \"{path}\"")?.Trim();
                Process.Start("explorer.exe", win ?? path);
            }
            else
                Process.Start("xdg-open", path);
        }
        catch (Exception ex)
        {
            progress.Warn($"Could not open the report automatically ({ex.Message}). Open it manually: {path}");
        }
    }

    private static bool IsWsl() =>
        OperatingSystem.IsLinux() && File.Exists("/proc/version") &&
        File.ReadAllText("/proc/version").Contains("microsoft", StringComparison.OrdinalIgnoreCase);

    private static string? RunCapture(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, UseShellExecute = false });
        if (p is null) return null;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(5000);
        return p.ExitCode == 0 ? output : null;
    }
}
