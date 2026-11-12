namespace PicoLog;

/// <summary>Batched sink that ships log entries to a Seq server over HTTP.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where it lives.</b> This is a sink <em>implementation</em>, so it ships in
/// <c>PicoLog.Json</c> — the package that owns JSON — not in the core <c>PicoLog</c> package,
/// whose job is the log model, the levels, the message templates and the sink abstraction only.
/// It therefore keeps <see cref="PicoLog"/> as its namespace: <c>using PicoLog;</c> and
/// <c>new SeqSink(...)</c> compile unchanged once the consumer references <c>PicoLog.Json</c>.
/// </para>
/// <para>
/// <b>Ownership:</b> the supplied <see cref="HttpClient"/> is a caller-owned
/// dependency — the sink never disposes it, so DI / <c>IHttpClientFactory</c>
/// clients can be shared across sinks and other consumers.
/// </para>
/// </remarks>
public sealed class SeqSink : IBatchingLogSink, IFlushableLogSink
{
    /// <summary>Batch size that <b>starts a drain</b>. A trigger, not a cap — see <see cref="MaxBufferedEntries"/>.</summary>
    private const int MaxBatchEntries = 100;

    /// <summary>Batch bytes that <b>starts a drain</b>. A trigger, not a cap — see <see cref="MaxBufferedBytes"/>.</summary>
    private const int MaxBatchBytes = 256 * 1024;

    /// <summary>
    /// Hard cap on the buffered entries: 20 batches, i.e. the in-flight one plus the 19 a stalled
    /// flush tick can leave queued behind it. Past it the OLDEST entries are dropped.
    /// </summary>
    /// <remarks>
    /// <see cref="MaxBatchEntries"/> and <see cref="MaxBatchBytes"/> are trigger thresholds: they
    /// start a drain, and the only appender (<see cref="BufferEntries"/>) used to neither drop nor
    /// refuse, so a flush tick that could not run (a starved <see cref="ThreadPool"/>, a stalled
    /// HTTP call) let <see cref="_buffer"/> grow without bound — memory growth, and every entry in
    /// it lost if the process exits. This is the bound, and it is the policy the pipeline queue
    /// already has: drop-oldest, keep the newest, never block and never throw on the write path.
    /// </remarks>
    private const int MaxBufferedEntries = MaxBatchEntries * 20;

    /// <summary>
    /// Hard cap on the buffered bytes, the same 20x factor so the two dimensions trip together.
    /// </summary>
    /// <remarks>
    /// Sized as a memory ceiling rather than a delivery budget: 5 MiB of buffered log entries is
    /// already evidence that nothing is draining, so holding more only delays the loss.
    /// </remarks>
    private const long MaxBufferedBytes = (long)MaxBatchBytes * 20;
    private static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromSeconds(2);
    private const int DefaultMaxRetries = 3;
    private const int DefaultRetryBaseDelayMs = 100;

    /// <summary>Seq's batch endpoint expects CLEF JSON with an explicit charset.</summary>
    private static readonly MediaTypeHeaderValue JsonContentType = new("application/json")
    {
        CharSet = "utf-8",
    };

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

    /// <summary>
    /// TEST HOOK — entries dropped because the buffer was at <see cref="MaxBufferedEntries"/> /
    /// <see cref="MaxBufferedBytes"/>.
    /// </summary>
    /// <remarks>
    /// Instance-scoped, like <see cref="RejectedAfterClose"/>. The public meter cannot carry this
    /// one: the only "an entry was dropped" instrument is
    /// <c>PicoLogMetrics.RecordDroppedEntry</c>, which is <c>internal</c> to the <c>PicoLog</c>
    /// assembly this sink does not live in, and the public counterpart on the meter
    /// (<c>RecordRejectedAfterShutdown</c>) means "rejected after shutdown" — a different event.
    /// Making the drop recorder public would be a shipped-API change, so the drop is counted here
    /// and traced instead; a caller observes it as "the oldest entries never reached the server"
    /// plus this counter.
    /// </remarks>
    internal long DroppedByCap;

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

            DropOldestPastTheCap();

            if (_buffer.Count >= MaxBatchEntries || _bufferBytes >= MaxBatchBytes)
                _ = SafeDrainAsync();
        }
    }

    /// <summary>
    /// Drops the OLDEST buffered entries until the buffer is inside
    /// <see cref="MaxBufferedEntries"/> / <see cref="MaxBufferedBytes"/>. Called under
    /// <see cref="_bufferLock"/> by the only appender.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Oldest first because the newest entries are the ones that explain whatever is happening now —
    /// the same choice the pipeline queue makes when it evicts. The newest entry is always kept: one
    /// entry that is bigger than the whole byte cap cannot be split, and dropping it would mean the
    /// sink silently refuses to buffer at all, which is a different policy.
    /// </para>
    /// <para>
    /// What a caller observes when this trips: nothing on the write path — <see cref="WriteAsync"/>
    /// and <see cref="WriteBatchAsync"/> still complete, never block and never throw — and on the
    /// wire, the oldest entries of the window simply never arrive. The evidence is
    /// <see cref="DroppedByCap"/> (plus a trace line), which is what a test asserts on.
    /// </para>
    /// </remarks>
    private void DropOldestPastTheCap()
    {
        var drop = 0;
        while (
            _buffer.Count - drop > 1
            && (_buffer.Count - drop > MaxBufferedEntries || _bufferBytes > MaxBufferedBytes)
        )
        {
            _bufferBytes -= EstimateEntryBytes(_buffer[drop]);
            drop++;
        }

        if (drop == 0)
            return;

        _buffer.RemoveRange(0, drop);
        DroppedByCap += drop;
        Trace.WriteLine(
            $"[SeqSink] buffer cap tripped: dropped {drop} oldest entr(y/ies); {DroppedByCap} dropped in total"
        );
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
        // Serialize once — the UTF-8 body is immutable across retries, and a request
        // message (and its content) that has been sent cannot be sent again, so only
        // the envelope is recreated per attempt.
        var body = BuildBatchBody(batch);

        for (var attempt = 0; attempt < _maxRetries; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/events/raw")
            {
                Content = new ByteArrayContent(body)
                {
                    Headers = { ContentType = JsonContentType },
                },
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

    /// <summary>
    /// Serializes <paramref name="batch"/> into the UTF-8 body of one Seq request:
    /// <c>{"Events":[{"@t":…,"@mt":…,"@l":…,…,"@x":…}, …]}</c>.
    /// </summary>
    /// <remarks>
    /// Everything — quotes, separators, escaping — comes from <see cref="JsonWriter"/>, so no call
    /// site appends a comma or a quote by hand and no string is built per entry: the writer stages
    /// UTF-8 bytes into the caller's buffer. That is what the previous hand-rolled escaper could not
    /// do while this sink lived in the core package, which references only <c>PicoLog.Abs</c> and is
    /// therefore not allowed a serializer.
    /// </remarks>
    internal static void WriteBatch(IBufferWriter<byte> buffer, IReadOnlyList<LogEntry> batch)
    {
        var writer = new JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WritePropertyName("Events"u8);
        writer.WriteStartArray();
        for (var i = 0; i < batch.Count; i++)
            WriteEntry(ref writer, batch[i]);
        writer.WriteEndArray();
        writer.WriteEndObject();
        // The outermost WriteEndObject already committed the document; Flush is the explicit
        // statement of that contract, and a no-op when nothing is pending.
        writer.Flush();
    }

    /// <summary>Writes one CLEF event object.</summary>
    /// <remarks>
    /// Member order, member names and the stringification of every value are unchanged from the
    /// hand-written version this replaces, so the wire bytes stay identical for payloads that need
    /// no escaping. <c>"key":"null"</c> for a null property value is deliberate: it is what the sink
    /// has always sent, not a JSON null, and changing it here would change the wire.
    /// </remarks>
    private static void WriteEntry(ref JsonWriter writer, LogEntry entry)
    {
        writer.WriteStartObject();
        writer.WriteStringProperty("@t"u8, entry.Timestamp.ToString("O"));
        writer.WriteStringProperty("@mt"u8, entry.Message ?? string.Empty);
        writer.WriteStringProperty("@l"u8, entry.Level.ToString());

        if (entry.Properties is { Count: > 0 })
        {
            foreach (var prop in entry.Properties)
            {
                // The name arrives as UTF-16 text, so it goes through the char overload and
                // never pays a UTF-8 encode of its own; the value is stringified exactly as
                // before (current culture, "null" for a null value).
                writer.WritePropertyName(prop.Key);
                writer.WriteString(prop.Value?.ToString() ?? "null");
            }
        }

        if (entry.Exception is not null)
            writer.WriteStringProperty("@x"u8, entry.Exception.ToString());

        writer.WriteEndObject();
    }

    /// <summary>
    /// Serializes <paramref name="batch"/> into the exact bytes that go on the wire.
    /// </summary>
    /// <remarks>
    /// The bytes are built into <see cref="SerializerExtensions.RentWriter"/> — the thread-local
    /// <see cref="ArrayBufferWriter{T}"/> the rest of this package already serializes through — and
    /// copied out once. That copy is the one allocation the batch owes: it outlives the request.
    /// Going through a <see cref="string"/> instead would add a second buffer (the UTF-16 text) and
    /// a third pass (<see cref="System.Net.Http.StringContent"/> re-encodes the text to UTF-8 to
    /// send it), which is why the body is handed to <see cref="ByteArrayContent"/> here.
    /// </remarks>
    internal static byte[] BuildBatchBody(IReadOnlyList<LogEntry> batch)
    {
        var buffer = SerializerExtensions.RentWriter();
        WriteBatch(buffer, batch);
        return buffer.WrittenSpan.ToArray();
    }
}
