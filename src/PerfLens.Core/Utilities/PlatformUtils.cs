namespace PerfLens.Core.Utilities;

public static class PlatformUtils
{
    /// <summary>
    /// Playwright has no official browser builds for Ubuntu 20.04. The 22.04 builds run there, so
    /// point Playwright at them unless the user already chose an override.
    /// </summary>
    public static void ApplyPlaywrightHostOverride()
    {
        if (!OperatingSystem.IsLinux()) return;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PLAYWRIGHT_HOST_PLATFORM_OVERRIDE"))) return;
        try
        {
            var osRelease = File.Exists("/etc/os-release") ? File.ReadAllText("/etc/os-release") : "";
            if (osRelease.Contains("ID=ubuntu") && osRelease.Contains("VERSION_ID=\"20.04\""))
                Environment.SetEnvironmentVariable("PLAYWRIGHT_HOST_PLATFORM_OVERRIDE", "ubuntu22.04-x64");
        }
        catch (IOException) { }
    }
}
