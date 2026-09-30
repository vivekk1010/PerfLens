using System.Collections.Concurrent;
using Microsoft.Playwright;

namespace PerfLens.Browser.Internal;

/// <summary>Plain snapshot of one network request observed through Playwright events.</summary>
internal sealed record ObservedRequest
{
    public long Sequence { get; init; }
    public string Url { get; init; } = "";
    /// <summary>URL of the first request in the redirect chain (Resource Timing entries are keyed by it).</summary>
    public string OriginalUrl { get; init; } = "";
    public string Method { get; init; } = "GET";
    public string ResourceType { get; init; } = "other";
    public bool IsMainFrameDocument { get; init; }
    /// <summary>True for intermediate 3xx hops; only the final request of a chain is counted.</summary>
    public bool IsRedirectHop { get; init; }
    public int? Status { get; init; }
    public string? ContentEncoding { get; init; }
    public string? CacheControl { get; init; }
    public string? ContentType { get; init; }
    public long? ContentLength { get; init; }
    public bool Finished { get; init; }
    public bool Failed { get; init; }
    public string? FailureText { get; init; }
    /// <summary>Request start, epoch milliseconds; null when unknown.</summary>
    public double? StartEpochMs { get; init; }
    /// <summary>Response end relative to request start (ms); null when unknown.</summary>
    public double? ResponseEndMs { get; init; }
}

/// <summary>
/// Subscribes to Playwright page events for one iteration. Handlers only record references; all
/// derived data is built in <see cref="Snapshot"/> after the page settled, so no async work is left dangling.
/// </summary>
internal sealed class PageEventCollector : IDisposable
{
    public const int MaxMessages = 50;
    public const int MaxMessageLength = 500;

    private sealed class Record(IRequest request, long sequence)
    {
        public IRequest Request { get; } = request;
        public long Sequence { get; } = sequence;
        public volatile IResponse? Response;
        public volatile bool Finished;
        public volatile bool Failed;
        public volatile string? FailureText;
    }

    private readonly IPage _page;
    private readonly ConcurrentDictionary<IRequest, Record> _records = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentQueue<string> _consoleErrors = new();
    private readonly ConcurrentQueue<string> _pageErrors = new();
    private long _sequence;
    private int _consoleCount;
    private int _pageErrorCount;

    public PageEventCollector(IPage page)
    {
        _page = page;
        _page.Request += OnRequest;
        _page.Response += OnResponse;
        _page.RequestFinished += OnRequestFinished;
        _page.RequestFailed += OnRequestFailed;
        _page.Console += OnConsole;
        _page.PageError += OnPageError;
    }

    public IReadOnlyList<string> ConsoleErrors => _consoleErrors.ToList();
    public IReadOnlyList<string> PageErrors => _pageErrors.ToList();

    public int InFlightCount => _records.Values.Count(r => !r.Finished && !r.Failed);

    /// <summary>Waits (bounded) until every observed request finished or failed.</summary>
    public async Task WaitForInFlightAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (InFlightCount > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(25, ct);
    }

    public List<ObservedRequest> Snapshot() =>
        _records.Values.OrderBy(r => r.Sequence).Select(ToObserved).ToList();

    private Record GetRecord(IRequest request) =>
        _records.GetOrAdd(request, r => new Record(r, Interlocked.Increment(ref _sequence)));

    private void OnRequest(object? sender, IRequest request) => GetRecord(request);

    private void OnResponse(object? sender, IResponse response) => GetRecord(response.Request).Response = response;

    private void OnRequestFinished(object? sender, IRequest request) => GetRecord(request).Finished = true;

    private void OnRequestFailed(object? sender, IRequest request)
    {
        var record = GetRecord(request);
        record.FailureText = request.Failure;
        record.Failed = true;
    }

    private void OnConsole(object? sender, IConsoleMessage message)
    {
        if (message.Type != "error") return;
        if (Interlocked.Increment(ref _consoleCount) > MaxMessages) return;
        _consoleErrors.Enqueue(ErrorText.Truncate(message.Text ?? "", MaxMessageLength));
    }

    private void OnPageError(object? sender, string error)
    {
        if (Interlocked.Increment(ref _pageErrorCount) > MaxMessages) return;
        _pageErrors.Enqueue(ErrorText.Truncate(error ?? "", MaxMessageLength));
    }

    private static ObservedRequest ToObserved(Record r)
    {
        var request = r.Request;
        var response = r.Response;
        IDictionary<string, string>? headers = null;
        try { headers = response?.Headers; }
        catch (PlaywrightException) { }

        var root = request;
        while (root.RedirectedFrom is { } previous) root = previous;

        bool mainFrameDocument = false;
        if (request.ResourceType == "document" && request.IsNavigationRequest)
        {
            try { mainFrameDocument = request.Frame.ParentFrame is null; }
            catch (PlaywrightException) { }
        }

        double? start = null, responseEnd = null;
        try
        {
            var timing = request.Timing;
            if (timing.StartTime > 0) start = timing.StartTime;
            if (timing.ResponseEnd > 0) responseEnd = timing.ResponseEnd;
        }
        catch (Exception ex) when (ex is PlaywrightException or NullReferenceException) { }

        return new ObservedRequest
        {
            Sequence = r.Sequence,
            Url = request.Url,
            OriginalUrl = root.Url,
            Method = request.Method,
            ResourceType = request.ResourceType ?? "other",
            IsMainFrameDocument = mainFrameDocument,
            IsRedirectHop = request.RedirectedTo is not null,
            Status = response?.Status,
            ContentEncoding = Header(headers, "content-encoding"),
            CacheControl = Header(headers, "cache-control"),
            ContentType = Header(headers, "content-type"),
            ContentLength = long.TryParse(Header(headers, "content-length"), out var len) && len >= 0 ? len : null,
            Finished = r.Finished,
            Failed = r.Failed,
            FailureText = r.FailureText,
            StartEpochMs = start,
            ResponseEndMs = responseEnd,
        };
    }

    private static string? Header(IDictionary<string, string>? headers, string name) =>
        headers is not null && headers.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v) ? v : null;

    public void Dispose()
    {
        _page.Request -= OnRequest;
        _page.Response -= OnResponse;
        _page.RequestFinished -= OnRequestFinished;
        _page.RequestFailed -= OnRequestFailed;
        _page.Console -= OnConsole;
        _page.PageError -= OnPageError;
    }
}
