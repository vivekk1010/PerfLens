using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using PerfLens.Core.Abstractions;
using PerfLens.Core.Configuration;
using PerfLens.Core.Results;
using PerfLens.Core.Statistics;
using PerfLens.Core.Utilities;

namespace PerfLens.Load;

/// <summary>
/// HTTP load generator supporting closed (fixed virtual users) and open (fixed arrival rate) models.
/// In open mode latency is measured from the intended start time to correct for coordinated omission.
/// </summary>
public sealed class HttpApiLoadRunner : IApiLoadRunner
{
    /// <summary>Open-mode cap on concurrently outstanding requests; arrivals beyond it are counted as "dropped".</summary>
    public const int MaxInFlight = 10_000;

    public const string ErrorTimeout = "timeout";
    public const string ErrorConnection = "connection_error";
    public const string ErrorUnexpectedStatus = "unexpected_status";
    public const string ErrorTls = "tls_error";
    public const string ErrorOther = "other";
    public const string ErrorDropped = "dropped";

    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(5);

    private readonly Func<ApiLoadRequest, HttpMessageHandler> _handlerFactory;

    public HttpApiLoadRunner() : this(CreateDefaultHandler)
    {
    }

    /// <param name="handlerFactory">Creates the message handler for one endpoint run; the runner disposes it.</param>
    public HttpApiLoadRunner(Func<ApiLoadRequest, HttpMessageHandler> handlerFactory)
    {
        _handlerFactory = handlerFactory ?? throw new ArgumentNullException(nameof(handlerFactory));
    }

    public static SocketsHttpHandler CreateDefaultHandler(ApiLoadRequest request)
    {
        var profile = NormalizeProfile(request.Profile);
        int connections = profile.Mode == LoadMode.Open
            ? (int)Math.Clamp(Math.Ceiling(profile.RatePerSecond * profile.TimeoutMs / 1000.0), 256, MaxInFlight)
            : Math.Max(profile.Concurrency * 2, 256);

        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            MaxConnectionsPerServer = connections,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromMilliseconds(profile.TimeoutMs),
        };
        if (request.IgnoreHttpsErrors)
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        return handler;
    }

    public async Task<EndpointLoadResult> RunAsync(ApiLoadRequest request, IProgressReporter progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        progress ??= NullProgress.Instance;
        ct.ThrowIfCancellationRequested();

        var endpoint = request.Endpoint ?? new ApiEndpointConfig();
        var profile = NormalizeProfile(request.Profile);
        string method = string.IsNullOrWhiteSpace(endpoint.Method) ? "GET" : endpoint.Method.Trim().ToUpperInvariant();
        string redactedUrl = UrlUtils.RedactSecrets(request.AbsoluteUrl ?? "");
        string label = $"{method} {DisplayPath(redactedUrl)}";

        var result = new EndpointLoadResult
        {
            Name = string.IsNullOrWhiteSpace(endpoint.Name) ? label : endpoint.Name,
            Method = method,
            Url = redactedUrl,
            Source = request.Source,
            ProfileDescription = DescribeProfile(profile),
        };

        progress.Info($"{label}  {ShortProfile(profile)} ...");

        RunState state;
        try
        {
            var template = RequestTemplate.Create(request, method);
            using var handler = _handlerFactory(request);
            using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
            state = new RunState(template, client, profile, ct);

            using var reporterCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var reporter = ReportProgressAsync(state, progress, label, reporterCts.Token);
            try
            {
                if (profile.Mode == LoadMode.Open) await RunOpenAsync(state).ConfigureAwait(false);
                else await RunClosedAsync(state).ConfigureAwait(false);
            }
            finally
            {
                reporterCts.Cancel();
                try { await reporter.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            result.ErrorTypes[Classify(ex)] = 1;
            progress.Warn($"{label}  failed: {ex.Message}");
            return result;
        }

        Aggregate(state, result);

        if (result.Error is not null)
            progress.Warn($"{label}  {result.Error}");
        progress.Info(
            $"{label}  {ShortProfile(profile)} ... {result.TotalRequests} req, " +
            $"{result.Rps.ToString("0.0", CultureInfo.InvariantCulture)} rps, " +
            $"p95 {result.Latency.P95.ToString("0", CultureInfo.InvariantCulture)} ms, " +
            $"{result.ErrorRatePct.ToString("0.0", CultureInfo.InvariantCulture)}% errors");
        return result;
    }

    private static async Task RunClosedAsync(RunState state)
    {
        var workers = new Task<List<Sample>>[state.Profile.Concurrency];
        for (int i = 0; i < workers.Length; i++)
            workers[i] = Task.Run(() => ClosedWorkerAsync(state), state.Ct);
        var lists = await Task.WhenAll(workers).ConfigureAwait(false);
        foreach (var list in lists) state.WorkerSamples.Add(list);
        state.Ct.ThrowIfCancellationRequested();
    }

    private static async Task<List<Sample>> ClosedWorkerAsync(RunState state)
    {
        var samples = new List<Sample>();
        while (!state.Stopped)
        {
            double now = state.NowMs();
            if (now >= state.MeasuredEndMs) break;
            bool measured = now >= state.MeasuredStartMs;
            if (measured && !state.TryBeginMeasured()) break;

            var sample = await ExecuteAsync(state, now).ConfigureAwait(false);
            if (measured)
            {
                samples.Add(sample);
                state.OnCompleted(sample);
            }
            // A refused connection fails in microseconds; without a pause each VU would spin and flood the samples.
            if (sample.ErrorType is ErrorConnection or ErrorTls)
            {
                try { await Task.Delay(50, state.Ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        return samples;
    }

    private static async Task RunOpenAsync(RunState state)
    {
        var ct = state.Ct;
        double intervalMs = 1000.0 / state.Profile.RatePerSecond;
        long next = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            double now = state.NowMs();
            bool done = false;

            while (true)
            {
                double due = next * intervalMs;
                if (due >= state.MeasuredEndMs || state.Stopped) { done = true; break; }
                if (due > now) break;
                next++;

                bool measured = due >= state.MeasuredStartMs;
                if (Volatile.Read(ref state.InFlight) >= MaxInFlight)
                {
                    if (measured) Interlocked.Increment(ref state.Dropped);
                    continue;
                }
                if (measured && !state.TryBeginMeasured()) { done = true; break; }

                Interlocked.Increment(ref state.InFlight);
                _ = Task.Run(() => FireOpenAsync(state, due, measured), CancellationToken.None);
            }
            if (done) break;

            double waitMs = next * intervalMs - state.NowMs();
            if (waitMs >= 1) await Task.Delay(TimeSpan.FromMilliseconds(waitMs), ct).ConfigureAwait(false);
            else if (waitMs > 0) await Task.Yield();
        }

        double deadline = state.NowMs() + state.Profile.TimeoutMs + 1000;
        while (Volatile.Read(ref state.InFlight) > 0 && state.NowMs() < deadline)
            await Task.Delay(5, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    private static async Task FireOpenAsync(RunState state, double intendedMs, bool measured)
    {
        try
        {
            var sample = await ExecuteAsync(state, intendedMs).ConfigureAwait(false);
            if (measured)
            {
                state.OpenSamples.Enqueue(sample);
                state.OnCompleted(sample);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            state.RecordFirstError(ex);
        }
        finally
        {
            Interlocked.Decrement(ref state.InFlight);
        }
    }

    private static async Task<Sample> ExecuteAsync(RunState state, double intendedMs)
    {
        string? error = null;
        int status = 0;
        long bytes = 0;

        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(state.Ct))
        {
            cts.CancelAfter(state.Profile.TimeoutMs);
            try
            {
                using var message = state.Template.CreateMessage();
                using var response = await state.Client
                    .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);
                status = (int)response.StatusCode;
                bytes = await DrainAsync(response.Content, cts.Token).ConfigureAwait(false);
                if (!state.Template.IsSuccess(status)) error = ErrorUnexpectedStatus;
            }
            catch (OperationCanceledException) when (state.Ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                error = Classify(ex);
                state.RecordFirstError(ex);
            }
        }

        double end = state.NowMs();
        return new Sample(end - state.MeasuredStartMs, Math.Max(0, end - intendedMs), status, error is null, error, bytes);
    }

    private static async Task<long> DrainAsync(HttpContent content, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                total += read;
            return total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static string Classify(Exception ex)
    {
        switch (ex)
        {
            case OperationCanceledException or TimeoutException:
                return ErrorTimeout;
            case HttpRequestException hre:
                if (HasInner<AuthenticationException>(hre) || hre.HttpRequestError == HttpRequestError.SecureConnectionError)
                    return ErrorTls;
                if (hre.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError ||
                    HasInner<SocketException>(hre) || hre.StatusCode is null)
                    return ErrorConnection;
                return ErrorOther;
            case AuthenticationException:
                return ErrorTls;
            case SocketException or IOException:
                return ErrorConnection;
            default:
                return ErrorOther;
        }
    }

    private static bool HasInner<T>(Exception ex) where T : Exception
    {
        for (var e = ex.InnerException; e is not null; e = e.InnerException)
            if (e is T) return true;
        return false;
    }

    private static void Aggregate(RunState state, EndpointLoadResult result)
    {
        var samples = state.Snapshot();
        long total = samples.Length;
        long successful = 0, bytes = 0;
        double maxCompletion = 0;
        bool anyResponse = false;
        var latencies = new double[samples.Length];

        for (int i = 0; i < samples.Length; i++)
        {
            ref readonly var s = ref samples[i];
            latencies[i] = s.LatencyMs;
            bytes += s.Bytes;
            if (s.Success) successful++;
            if (s.CompletedOffsetMs > maxCompletion) maxCompletion = s.CompletedOffsetMs;
            if (s.Status > 0)
            {
                anyResponse = true;
                string code = s.Status.ToString(CultureInfo.InvariantCulture);
                result.StatusCodes[code] = result.StatusCodes.GetValueOrDefault(code) + 1;
            }
            if (s.ErrorType is not null)
                result.ErrorTypes[s.ErrorType] = result.ErrorTypes.GetValueOrDefault(s.ErrorType) + 1;
        }

        long dropped = Interlocked.Read(ref state.Dropped);
        if (dropped > 0) result.ErrorTypes[ErrorDropped] = dropped;

        double durationSec = state.MaxReached
            ? maxCompletion / 1000.0
            : state.Profile.DurationSeconds;
        durationSec = Math.Max(durationSec, 0.001);

        result.TotalRequests = total;
        result.SuccessfulRequests = successful;
        result.FailedRequests = total - successful;
        result.ErrorRatePct = total == 0 ? 0 : (total - successful) * 100.0 / total;
        result.DurationSeconds = durationSec;
        result.Rps = total / durationSec;
        result.BytesReceived = bytes;
        result.Latency = Stats.Compute(latencies);
        result.Histogram = LatencyHistogram.Build(latencies, 20);
        result.Timeline = BuildTimeline(samples, durationSec);

        if (total == 0)
            result.Error = state.FirstError ?? "No requests completed during the measured phase.";
        else if (!anyResponse && state.FirstError is not null)
            result.Error = state.FirstError;
    }

    private static List<TimelinePoint> BuildTimeline(Sample[] samples, double durationSec)
    {
        int seconds = Math.Max(1, (int)Math.Ceiling(durationSec - 0.05));
        var buckets = new List<double>[seconds];
        var errors = new long[seconds];
        for (int i = 0; i < seconds; i++) buckets[i] = new List<double>();

        foreach (var s in samples)
        {
            int idx = Math.Clamp((int)Math.Floor(s.CompletedOffsetMs / 1000.0), 0, seconds - 1);
            buckets[idx].Add(s.LatencyMs);
            if (!s.Success) errors[idx]++;
        }

        var timeline = new List<TimelinePoint>(seconds);
        for (int i = 0; i < seconds; i++)
        {
            var values = buckets[i];
            var point = new TimelinePoint { Second = i, Requests = values.Count, Errors = errors[i] };
            if (values.Count > 0)
            {
                values.Sort();
                point.P50Ms = Stats.PercentileSorted(values, 50);
                point.P95Ms = Stats.PercentileSorted(values, 95);
                point.MaxMs = values[^1];
            }
            timeline.Add(point);
        }
        return timeline;
    }

    private static async Task ReportProgressAsync(RunState state, IProgressReporter progress, string label, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(ProgressInterval);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            double now = state.NowMs();
            string phase = now < state.MeasuredStartMs ? "warm-up" : "measuring";
            double elapsed = Math.Max(0, now - state.MeasuredStartMs) / 1000.0;
            progress.Debug(
                $"{label}  {phase} {elapsed.ToString("0", CultureInfo.InvariantCulture)}s: " +
                $"{Interlocked.Read(ref state.Completed)} req, {Interlocked.Read(ref state.Errors)} errors, " +
                $"{Volatile.Read(ref state.InFlight)} in flight");
        }
    }

    internal static LoadProfile NormalizeProfile(LoadProfile? profile)
    {
        profile ??= new LoadProfile();
        return new LoadProfile
        {
            Mode = profile.Mode,
            Concurrency = Math.Max(1, profile.Concurrency),
            RatePerSecond = profile.RatePerSecond > 0 && double.IsFinite(profile.RatePerSecond) ? profile.RatePerSecond : 1,
            DurationSeconds = Math.Max(1, profile.DurationSeconds),
            WarmupSeconds = Math.Max(0, profile.WarmupSeconds),
            TimeoutMs = profile.TimeoutMs > 0 ? profile.TimeoutMs : 10_000,
            MaxRequests = profile.MaxRequests is { } m ? Math.Max(0, m) : null,
        };
    }

    internal static string DescribeProfile(LoadProfile p)
    {
        var sb = new StringBuilder();
        sb.Append(p.Mode == LoadMode.Open
            ? $"open, {p.RatePerSecond.ToString("0.##", CultureInfo.InvariantCulture)} rps"
            : $"closed, {p.Concurrency} VUs");
        sb.Append(CultureInfo.InvariantCulture, $", {p.DurationSeconds}s");
        if (p.WarmupSeconds > 0) sb.Append(CultureInfo.InvariantCulture, $" (+{p.WarmupSeconds}s warm-up)");
        if (p.MaxRequests is { } max) sb.Append(CultureInfo.InvariantCulture, $", max {max} req");
        return sb.ToString();
    }

    private static string ShortProfile(LoadProfile p) => p.Mode == LoadMode.Open
        ? $"open {p.RatePerSecond.ToString("0.##", CultureInfo.InvariantCulture)} rps {p.DurationSeconds}s"
        : $"closed {p.Concurrency} VUs {p.DurationSeconds}s";

    private static string DisplayPath(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.PathAndQuery : url;

    private readonly record struct Sample(
        double CompletedOffsetMs, double LatencyMs, int Status, bool Success, string? ErrorType, long Bytes);

    private sealed class RunState
    {
        private readonly long _t0 = Stopwatch.GetTimestamp();
        private int _measuredStarted;
        private volatile bool _stopped;
        private string? _firstError;

        public long InFlight;
        public long Dropped;
        public long Completed;
        public long Errors;

        public RunState(RequestTemplate template, HttpClient client, LoadProfile profile, CancellationToken ct)
        {
            Template = template;
            Client = client;
            Profile = profile;
            Ct = ct;
            MeasuredStartMs = profile.WarmupSeconds * 1000.0;
            MeasuredEndMs = MeasuredStartMs + profile.DurationSeconds * 1000.0;
        }

        public RequestTemplate Template { get; }
        public HttpClient Client { get; }
        public LoadProfile Profile { get; }
        public CancellationToken Ct { get; }
        public double MeasuredStartMs { get; }
        public double MeasuredEndMs { get; }
        public bool Stopped => _stopped;
        public bool MaxReached { get; private set; }
        public string? FirstError => Volatile.Read(ref _firstError);
        public ConcurrentBag<List<Sample>> WorkerSamples { get; } = new();
        public ConcurrentQueue<Sample> OpenSamples { get; } = new();

        public double NowMs() => Stopwatch.GetElapsedTime(_t0).TotalMilliseconds;

        public bool TryBeginMeasured()
        {
            if (Profile.MaxRequests is not { } max) return true;
            if (Interlocked.Increment(ref _measuredStarted) <= max) return true;
            MaxReached = true;
            _stopped = true;
            return false;
        }

        public void OnCompleted(in Sample sample)
        {
            Interlocked.Increment(ref Completed);
            if (!sample.Success) Interlocked.Increment(ref Errors);
        }

        public void RecordFirstError(Exception ex) =>
            Interlocked.CompareExchange(ref _firstError, ex.Message, null);

        public Sample[] Snapshot()
        {
            var all = new List<Sample>(OpenSamples.ToArray());
            foreach (var list in WorkerSamples) all.AddRange(list);
            return all.ToArray();
        }
    }

    private sealed class RequestTemplate
    {
        private static readonly HashSet<string> ContentHeaderNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Content-Type", "Content-Encoding", "Content-Language", "Content-Location", "Content-MD5",
            "Content-Range", "Content-Disposition", "Expires", "Last-Modified", "Allow",
        };

        private readonly HttpMethod _method;
        private readonly Uri _uri;
        private readonly List<KeyValuePair<string, string>> _requestHeaders = new();
        private readonly List<KeyValuePair<string, string>> _contentHeaders = new();
        private readonly byte[]? _body;
        private string? _contentType;
        private readonly HashSet<int> _expected;

        private RequestTemplate(HttpMethod method, Uri uri, byte[]? body, string? contentType, HashSet<int> expected)
        {
            _method = method;
            _uri = uri;
            _body = body;
            _contentType = contentType;
            _expected = expected;
        }

        public static RequestTemplate Create(ApiLoadRequest request, string method)
        {
            if (!Uri.TryCreate(request.AbsoluteUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new ArgumentException($"Invalid absolute http(s) URL: '{UrlUtils.RedactSecrets(request.AbsoluteUrl ?? "")}'.");

            var endpoint = request.Endpoint ?? new ApiEndpointConfig();
            var httpMethod = new HttpMethod(method);
            bool sendBody = endpoint.Body is not null && httpMethod != HttpMethod.Get && httpMethod != HttpMethod.Head;

            string? contentType = sendBody ? (string.IsNullOrWhiteSpace(endpoint.ContentType) ? null : endpoint.ContentType) : null;
            var template = new RequestTemplate(
                httpMethod, uri,
                sendBody ? Encoding.UTF8.GetBytes(endpoint.Body!) : null,
                contentType,
                new HashSet<int>(endpoint.ExpectedStatus ?? new List<int>()));

            string? cookieHeader = null;
            foreach (var (name, value) in request.Headers ?? new Dictionary<string, string>())
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)) { cookieHeader = value; continue; }
                if (ContentHeaderNames.Contains(name))
                {
                    if (!sendBody) continue;
                    if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                        template._contentType = value;
                    else
                        template._contentHeaders.Add(new(name, value));
                    continue;
                }
                template._requestHeaders.Add(new(name, value));
            }

            if (request.Cookies is { Count: > 0 } cookies)
            {
                string joined = string.Join("; ", cookies.Where(c => !string.IsNullOrEmpty(c.Key)).Select(c => $"{c.Key}={c.Value}"));
                cookieHeader = string.IsNullOrWhiteSpace(cookieHeader) ? joined : $"{cookieHeader}; {joined}";
            }
            if (!string.IsNullOrWhiteSpace(cookieHeader))
                template._requestHeaders.Add(new("Cookie", cookieHeader));

            return template;
        }

        public bool IsSuccess(int status) => _expected.Count > 0 ? _expected.Contains(status) : status is >= 200 and <= 399;

        public HttpRequestMessage CreateMessage()
        {
            var message = new HttpRequestMessage(_method, _uri);
            foreach (var (name, value) in _requestHeaders)
                message.Headers.TryAddWithoutValidation(name, value);

            if (_body is not null)
            {
                var content = new ByteArrayContent(_body);
                if (_contentType is not null) content.Headers.TryAddWithoutValidation("Content-Type", _contentType);
                foreach (var (name, value) in _contentHeaders)
                    content.Headers.TryAddWithoutValidation(name, value);
                message.Content = content;
            }
            return message;
        }
    }

    private sealed class NullProgress : IProgressReporter
    {
        public static readonly NullProgress Instance = new();
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
        public void Debug(string message) { }
    }
}
