namespace PerfLens.DemoShop;

/// <summary>Thread-safe request counters exposed via /api/stats and used for deterministic flaky/slow behaviour.</summary>
public sealed class Counters
{
    private long _total;
    private long _api;
    private long _pages;
    private long _slow;
    private long _flaky;
    private long _flakyFailures;
    private long _logins;
    private long _failedLogins;

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public void CountRequest(PathString path)
    {
        Interlocked.Increment(ref _total);
        if (path.StartsWithSegments("/api")) Interlocked.Increment(ref _api);
        else if (!Path.HasExtension(path.Value)) Interlocked.Increment(ref _pages);
    }

    public long NextSlow() => Interlocked.Increment(ref _slow);
    public long NextFlaky() => Interlocked.Increment(ref _flaky);
    public void FlakyFailure() => Interlocked.Increment(ref _flakyFailures);
    public void Login(bool success)
    {
        if (success) Interlocked.Increment(ref _logins);
        else Interlocked.Increment(ref _failedLogins);
    }

    public object Snapshot(int activeSessions) => new
    {
        totalRequests = Interlocked.Read(ref _total),
        apiRequests = Interlocked.Read(ref _api),
        pageRequests = Interlocked.Read(ref _pages),
        slowCalls = Interlocked.Read(ref _slow),
        flakyCalls = Interlocked.Read(ref _flaky),
        flakyFailures = Interlocked.Read(ref _flakyFailures),
        logins = Interlocked.Read(ref _logins),
        failedLogins = Interlocked.Read(ref _failedLogins),
        activeSessions,
        uptimeSeconds = Math.Round((DateTimeOffset.UtcNow - StartedAt).TotalSeconds, 1),
    };
}
