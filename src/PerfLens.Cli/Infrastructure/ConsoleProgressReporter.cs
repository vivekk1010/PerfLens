using PerfLens.Core.Abstractions;

namespace PerfLens.Cli.Infrastructure;

public sealed class ConsoleProgressReporter(bool verbose) : IProgressReporter
{
    private static readonly object Gate = new();
    private readonly bool _color = !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;

    public void Info(string message) => Write("   ", message, ConsoleColor.Gray, Console.Out);
    public void Warn(string message) => Write("WARN ", message, ConsoleColor.Yellow, Console.Out);
    public void Error(string message) => Write("ERROR ", message, ConsoleColor.Red, Console.Error);

    public void Debug(string message)
    {
        if (verbose) Write("  · ", message, ConsoleColor.DarkGray, Console.Out);
    }

    public void Stage(string message) => Write("\n==> ", message, ConsoleColor.Cyan, Console.Out);
    public void Success(string message) => Write("OK ", message, ConsoleColor.Green, Console.Out);

    private void Write(string prefix, string message, ConsoleColor color, TextWriter writer)
    {
        lock (Gate)
        {
            if (_color) Console.ForegroundColor = color;
            writer.WriteLine(prefix + message);
            if (_color) Console.ResetColor();
        }
    }
}
