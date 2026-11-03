namespace PicoLog;

/// <summary>Batched sink that ships log entries to a Seq server over HTTP.
/// </summary>
/// <remarks>
/// <b>Ownership:</b> the supplied <see cref="HttpClient"/> is a caller-owned
/// dependency — the sink never disposes it, so DI / <c>IHttpClientFactory</c>
/// clients can be shared across sinks and other consumers.
/// </remarks>
public sealed class SeqSink : IBatchingLogSink, IFlushableLogSink
{
    private const int MaxBatchEntries = 100;
    private const int MaxBatchBytes = 256 * 1024;
    private static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromSeconds(2);
    private const int DefaultMaxRetries = 3;
    private const int DefaultRetryBaseDelayMs = 100;

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly Lock _bufferLock = new();
    private readonly List<LogEntry> _buffer = [];
    private readonly TimeSpan _flushInterval;
    private readonly int _maxRetries;
    private readonly int _retryBaseDelayMs;
    private readonly CancellationTokenSource _timerCts = new();
    private readonly Task _timerTask;

    // Serializes threshold-triggered fire-and-forget drains and lets DisposeAsync
    // wait for one in flight before disposing the HttpClient mid-request.
    private readonly SemaphoreSlim _thresholdDrainGate = new(1, 1);
    private long _bufferBytes;
    private long _failureCount;
    private long _lastFailureTicks;
    private readonly bool _enableConsoleFallback;
    private int _disposed;

    // Guards admission after disposal: set under _bufferLock by DisposeAsync so a
    // write racing disposal cannot buffer entries nothing would ever drain.
    private bool _closed;

    /// <summary>TEST HOOK — writes rejected because the sink was already closed.
    /// Instance-scoped so tests do not observe the process-wide metric counter.</summary>
    internal long RejectedAfterClose;

    public long FailureCount => Interlocked.Read(ref _failureCount);
    public DateTimeOffset? LastFailureTime =>
        Interlocked.Read(ref _lastFailureTicks) == 0
            ? null
            : new DateTimeOffset(Interlocked.Read(ref _lastFailureTicks), TimeSpan.Zero);

    public SeqSink(
        HttpClient httpClient,
        string? apiKey = null,
        TimeSpan? flushInterval = null,
        bool enableConsoleFallback = false,
        int maxRetries = DefaultMaxRetries,
        int retryBaseDelayMs = DefaultRetryBaseDelayMs
    )
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKey = apiKey ?? string.Empty;
        _flushInterval = flushInterval ?? DefaultFlushInterval;
        _enableConsoleFallback = enableConsoleFallback;
        _maxRetries = maxRetries > 0 ? maxRetries : DefaultMaxRetries;
        _retryBaseDelayMs = retryBaseDelayMs > 0 ? retryBaseDelayMs : DefaultRetryBaseDelayMs;
        _timerTask = RunPeriodicFlushAsync(_timerCts.Token);
    }

    public Task WriteAsync(LogEntry entry, CancellationToken ct = default)
    {
        BufferEntries([entry]);
        return Task.CompletedTask;
    }

    public ValueTask WriteBatchAsync(IReadOnlyList<LogEntry> batch, CancellationToken ct = default)
    {
        BufferEntries(batch);
        return ValueTask.CompletedTask;
    }

    public async ValueTask FlushAsync(CancellationToken ct = default)
    {
        IReadOnlyList<LogEntry> batch;
        lock (_bufferLock)
        {
            if (_buffer.Count == 0)
                return;
            batch = DrainBuffer();
        }
        await SendBatchAsync(batch, ct).ConfigureAwait(false);
    }

    private async Task RunPeriodicFlushAsync(CancellationToken ct)
    {
        // PeriodicTimer provides more reliable timing than Task.Delay under
        // ThreadPool saturation because it uses a single reusable native timer
        // rather than a new timer-per-iteration. On CI runners with many
        // parallel tests, ThreadPool starvation can delay Task.Delay callbacks
        // by seconds, causing periodic flushes to stall and tests to time out.
        using var timer = new PeriodicTimer(_flushInterval);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                    break;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await DrainAndSendAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort: errors are logged inside DrainAndSendAsync
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _timerCts.Cancel();
        try
        {
            await _timerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }

        // Close admission BEFORE the final flush: entries accepted up to this
        // point are drained by the flush below; writes racing disposal are
        // rejected and counted instead of landing in a buffer nothing would
        // ever drain (the timer is cancelled and the gate wait only covers
        // already-started drains).
        lock (_bufferLock)
            _closed = true;

        await FlushAsync().ConfigureAwait(false);

        // The size-threshold drain is fire-and-forget: wait for an in-flight
        // send so the batch it already took is not abandoned. Closing admission
        // above guarantees no new drain can start, so after this handshake the
        // gate has no users left and can be disposed.
        await _thresholdDrainGate.WaitAsync().ConfigureAwait(false);
        _thresholdDrainGate.Release();
        _thresholdDrainGate.Dispose();

        _timerCts.Dispose();
        // The HttpClient is caller-owned and deliberately NOT disposed here.
    }

    private void BufferEntries(IReadOnlyList<LogEntry> entries)
    {
        lock (_bufferLock)
        {
            if (_closed)
            {
                // Disposal already flushed and will never drain again; reject
                // explicitly (visible in metrics) instead of buffering forever.
                RejectedAfterClose++;
                PicoLogMetrics.RecordRejectedAfterShutdown();
                return;
            }

            foreach (var entry in entries)
            {
                _buffer.Add(entry);
                _bufferBytes += EstimateEntryBytes(entry);
            }

            if (_buffer.Count >= MaxBatchEntries || _bufferBytes >= MaxBatchBytes)
                _ = SafeDrainAsync();
        }
    }

    /// <summary>
    /// Fire-and-forget drain that observes exceptions via await.
    /// Unlike a raw <c>_ = DrainAndSendAsync()</c>, exceptions are caught and
    /// logged here rather than becoming unobserved task exceptions.
    /// </summary>
    private async Task SafeDrainAsync()
    {
        try
        {
            await _thresholdDrainGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await DrainAndSendAsync().ConfigureAwait(false);
            }
            finally
            {
                _thresholdDrainGate.Release();
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SeqSink] SafeDrain error: {ex}");
        }
    }

    private static long EstimateEntryBytes(LogEntry entry) =>
        (entry.Message?.Length ?? 0) * 2 + 128;

    private async Task DrainAndSendAsync()
    {
        IReadOnlyList<LogEntry> batch;
        lock (_bufferLock)
        {
            if (_buffer.Count == 0)
                return;
            batch = DrainBuffer();
        }
        await SendBatchAsync(batch, CancellationToken.None).ConfigureAwait(false);
    }

    private IReadOnlyList<LogEntry> DrainBuffer()
    {
        var result = _buffer.ToArray();
        _buffer.Clear();
        _bufferBytes = 0;
        return result;
    }

    private async Task SendBatchAsync(IReadOnlyList<LogEntry> batch, CancellationToken ct)
    {
        // Serialize once — the JSON is immutable across retries.
        var json = SerializeBatch(batch);

        for (var attempt = 0; attempt < _maxRetries; attempt++)
        {
            // HttpRequestMessage and StringContent are single-use;
            // recreate them for every attempt.
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/events/raw")
            {
                Content = content,
            };

            if (_apiKey.Length > 0)
                request.Headers.Add("X-Seq-ApiKey", _apiKey);

            try
            {
                var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                Interlocked.Exchange(ref _failureCount, 0);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                if (attempt == _maxRetries - 1)
                {
                    Interlocked.Increment(ref _failureCount);
                    Interlocked.Exchange(ref _lastFailureTicks, DateTimeOffset.UtcNow.Ticks);
                    FallbackToConsole(batch, ex);
                    return;
                }
                await Task.Delay(_retryBaseDelayMs * (int)Math.Pow(2, attempt), ct)
                    .ConfigureAwait(false);
            }
        }
    }

    private void FallbackToConsole(IReadOnlyList<LogEntry> batch, Exception ex)
    {
        if (!_enableConsoleFallback)
            return;

        try
        {
            foreach (var entry in batch)
                Console.WriteLine(
                    $"[SeqSink fallback] {entry.Timestamp:O} [{entry.Level}] {entry.Message}"
                );
        }
        catch
        {
            /* best-effort fallback */
        }
        _ = ex; // suppress unused warning
    }

    private static string SerializeBatch(IReadOnlyList<LogEntry> batch)
    {
        var sb = new StringBuilder();
        sb.Append("{\"Events\":[");

        for (var i = 0; i < batch.Count; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append(SerializeEntry(batch[i]));
        }

        sb.Append("]}");
        return sb.ToString();
    }

    private static string SerializeEntry(LogEntry entry)
    {
        var sb = new StringBuilder();
        sb.Append("{\"@t\":\"");
        sb.Append(entry.Timestamp.ToString("O"));
        sb.Append("\",\"@mt\":\"");
        sb.Append(EscapeJson(entry.Message ?? string.Empty));
        sb.Append("\",\"@l\":\"");
        sb.Append(entry.Level.ToString());
        sb.Append('"');

        if (entry.Properties is { Count: > 0 })
        {
            foreach (var prop in entry.Properties)
            {
                sb.Append(",\"");
                sb.Append(EscapeJson(prop.Key));
                sb.Append("\":\"");
                sb.Append(EscapeJson(prop.Value?.ToString() ?? "null"));
                sb.Append('"');
            }
        }

        if (entry.Exception is not null)
        {
            sb.Append(",\"@x\":\"");
            sb.Append(EscapeJson(entry.Exception.ToString()));
            sb.Append('"');
        }

        sb.Append('}');
        return sb.ToString();
    }

    private const string HexDigits = "0123456789abcdef";

    /// <summary>
    /// Escapes <paramref name="value"/> for embedding inside a JSON string literal.
    /// </summary>
    /// <remarks>
    /// Hand-rolled on purpose: PicoLog deliberately references only PicoLog.Abs, so it cannot
    /// call the PicoSerDe/PicoJetson writer. Adopting that writer is a dependency decision for
    /// the maintainer, not a cleanup.
    /// <para>
    /// The table is complete for JSON (RFC 8259 §7): <c>"</c> → <c>\"</c>, <c>\</c> → <c>\\</c>,
    /// <c>\b \f \n \r \t</c> as the two-character short forms, and <b>every other</b> code point
    /// below U+0020 → <c>\u00XX</c> (lowercase hex). The five-call
    /// <see cref="string.Replace(string, string)"/> chain this replaces let U+0000, U+0001,
    /// U+0008, U+000B, U+000C, U+001F … through raw, which is invalid JSON inside a string
    /// literal — the server rejects the entire batch instead of logging it.
    /// </para>
    /// <para>
    /// Unpaired surrogates cannot be represented in UTF-8 at all, so they are emitted as the
    /// explicit escape <c>\ufffd</c> (U+FFFD REPLACEMENT CHARACTER): the same character the
    /// transport's UTF-8 encoder used to substitute silently, now chosen deliberately. Well-formed
    /// surrogate pairs (emoji) pass through untouched, and U+2028/U+2029 stay raw — both are
    /// legal unescaped in JSON.
    /// </para>
    /// <para>
    /// <b>Cost.</b> The common case (nothing to escape) is one <c>O(n)</c> scan that returns the
    /// same string instance — zero allocations, versus five full scans for the Replace chain.
    /// When escaping is needed: one scan, one <see cref="StringBuilder"/> and one result string,
    /// with clean runs bulk-copied by <c>Append(string, int, int)</c>; the Replace chain paid up
    /// to five full-length intermediate copies instead. Escaped output grows by at most 5 chars
    /// per escaped character (<c>\u00XX</c>).
    /// </para>
    /// </remarks>
    private static string EscapeJson(string value)
    {
        if (!NeedsEscaping(value))
            return value;

        // Worst case is 6 chars per input char, but escaped payloads are rare and usually
        // short; a modest head start beats over-allocating every log message.
        var sb = new StringBuilder(value.Length + 8);
        var runStart = 0;
        var i = 0;

        while (i < value.Length)
        {
            if (!NeedsEscape(value, i))
            {
                i++;
                continue;
            }

            if (i > runStart)
                sb.Append(value, runStart, i - runStart);

            switch (value[i])
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (value[i] < 0x20)
                        sb.Append("\\u00")
                            .Append(HexDigits[value[i] >> 4])
                            .Append(HexDigits[value[i] & 0xF]);
                    else
                        sb.Append("\\ufffd"); // unpaired surrogate: not representable in UTF-8
                    break;
            }

            runStart = ++i;
        }

        if (runStart < value.Length)
            sb.Append(value, runStart, value.Length - runStart);

        return sb.ToString();
    }

    /// <summary>
    /// True when <paramref name="value"/> holds at least one character <see cref="EscapeJson"/>
    /// has to rewrite — the fast-path gate.
    /// </summary>
    private static bool NeedsEscaping(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (NeedsEscape(value, i))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when the char at <paramref name="index"/> must be rewritten: C0 control, quote,
    /// backslash, or a surrogate that is not half of a well-formed pair. A low surrogate is fine
    /// when the preceding char is a high surrogate — the pair is copied through as-is.
    /// </summary>
    private static bool NeedsEscape(string value, int index)
    {
        var c = value[index];
        if (c < 0x20 || c == '"' || c == '\\')
            return true;
        if (char.IsHighSurrogate(c))
            return index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]);
        if (char.IsLowSurrogate(c))
            return index == 0 || !char.IsHighSurrogate(value[index - 1]);
        return false;
    }
}
