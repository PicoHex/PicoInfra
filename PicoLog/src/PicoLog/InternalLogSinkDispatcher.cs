namespace PicoLog;

internal interface IConsoleFallbackSink : ILogSink;

internal sealed class InternalLogSinkDispatcher : IDisposable
{
    private readonly ILogSink[] _sinks;
    private readonly LoggerFactoryRuntime _runtime;
    private readonly ILogSink? _consoleFallbackSink;
    private readonly CancellationTokenSource _drainCancellationSource = new();
    private CancellationTokenRegistration _drainCancellationRegistration;
    private int _disposeState;

    // Assign once at startup (single-threaded); read on rare sink-failure path.
    // Public so consumers (e.g. PicoLog.DI) can wire their own error observers.
    public Action<string, Exception>? OnFallbackError;

    public InternalLogSinkDispatcher(LoggerFactoryRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _sinks = _runtime.Sinks;
        _consoleFallbackSink = ResolveLastRegisteredConsoleFallbackSink(_sinks);
    }

    public void BeginDrain(CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled || Volatile.Read(ref _disposeState) != 0)
            return;

        _drainCancellationRegistration = cancellationToken.UnsafeRegister(
            static state => ((InternalLogSinkDispatcher)state!).CancelDrain(),
            this
        );
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        _drainCancellationRegistration.Dispose();
        _drainCancellationSource.Dispose();
    }

    /// <summary>
    /// Synchronously dispatches a log entry to all sinks. Only used on the fast path
    /// when all sinks implement <see cref="IFastLogSink"/> (WriteAsync never blocks).
    /// </summary>
    internal void DispatchEntrySync(LogEntry entry)
    {
        foreach (ILogSink sink in _sinks)
        {
            try
            {
                // Fast sinks always return a completed task — safe to call synchronously
                sink.WriteAsync(entry).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _runtime.RecordSinkFailure();
                HandleSinkWriteFailureAsync(sink, entry, ex).GetAwaiter().GetResult();
            }
        }
    }

    /// <summary>
    /// Dispatches a log entry to all registered sinks sequentially.
    /// A slow sink (e.g. a remote HTTP sink) will delay subsequent sinks
    /// for the same entry. This is a deliberate design choice to avoid
    /// unbounded concurrency per pipeline.
    /// </summary>
    internal async Task DispatchEntryAsync(LogEntry entry)
    {
        var token = _drainCancellationSource.Token;

        foreach (ILogSink sink in _sinks)
            await WriteToSinkAsync(sink, entry, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Dispatches a batch of log entries to all registered sinks.
    /// For sinks implementing <see cref="IBatchingLogSink"/>, the entire batch
    /// is sent in one call. For other sinks, entries are dispatched individually.
    /// </summary>
    internal async Task DispatchBatchAsync(IReadOnlyList<LogEntry> batch)
    {
        if (batch.Count == 0)
            return;

        var token = _drainCancellationSource.Token;

        foreach (ILogSink sink in _sinks)
        {
            if (sink is IBatchingLogSink batchingSink)
            {
                await WriteBatchToSinkAsync(batchingSink, batch, token).ConfigureAwait(false);
            }
            else
            {
                foreach (var entry in batch)
                    await WriteToSinkAsync(sink, entry, token).ConfigureAwait(false);
            }
        }
    }

    private async Task WriteBatchToSinkAsync(
        IBatchingLogSink sink,
        IReadOnlyList<LogEntry> batch,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await sink.WriteBatchAsync(batch, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _runtime.RecordSinkFailure();
            // Fall back to per-entry write for error reporting
            foreach (var entry in batch)
                await HandleSinkWriteFailureAsync(sink, entry, ex).ConfigureAwait(false);
        }
    }

    private void CancelDrain()
    {
        try
        {
            _drainCancellationSource.Cancel();
        }
        catch (ObjectDisposedException)
        { /* CTS may already be disposed */
        }
    }

    private async Task WriteToSinkAsync(
        ILogSink sink,
        LogEntry entry,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await sink.WriteAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _runtime.RecordSinkFailure();
            await HandleSinkWriteFailureAsync(sink, entry, ex).ConfigureAwait(false);
        }
    }

    private async Task HandleSinkWriteFailureAsync(
        ILogSink failingSink,
        LogEntry originalEntry,
        Exception exception
    )
    {
        if (!ShouldWriteFailureToConsoleFallback(failingSink))
        {
            WriteSinkFailureToDebug(originalEntry, exception);
            return;
        }

        await WriteFailureToConsoleFallbackAsync(originalEntry, exception).ConfigureAwait(false);
    }

    private bool ShouldWriteFailureToConsoleFallback(ILogSink failingSink) =>
        _consoleFallbackSink is not null && !ReferenceEquals(_consoleFallbackSink, failingSink);

    private static ILogSink? ResolveLastRegisteredConsoleFallbackSink(ILogSink[] sinks) =>
        sinks.LastOrDefault(static sink => sink is IConsoleFallbackSink);

    private static void WriteSinkFailureToDebug(LogEntry originalEntry, Exception exception)
    {
#if DEBUG
        Debug.WriteLine($"Sink write error for '{originalEntry.Category}': {exception}");
#endif
    }

    private static void WriteConsoleFallbackFailureToDebug(Exception fallbackException)
    {
#if DEBUG
        Debug.WriteLine($"Fallback sink write error: {fallbackException}");
#endif
    }

    private async Task WriteFailureToConsoleFallbackAsync(
        LogEntry originalEntry,
        Exception exception
    )
    {
        if (_consoleFallbackSink is not { } fallback)
            return;

        LogEntry errorEntry = CreateFallbackErrorEntry(originalEntry, exception);

        try
        {
            await fallback.WriteAsync(errorEntry).ConfigureAwait(false);
        }
        catch (Exception fallbackException)
        {
            OnFallbackError?.Invoke("fallback-sink-write", fallbackException);
            WriteConsoleFallbackFailureToDebug(fallbackException);
        }
    }

    private LogEntry CreateFallbackErrorEntry(LogEntry originalEntry, Exception exception) =>
        new()
        {
            Timestamp = _runtime.TimestampProvider.GetLocalNow(),
            Level = LogLevel.Error,
            Category = "PicoLog.SinkFailure",
            Message = $"Failed to write log entry to sink: {originalEntry.Message}",
            Exception = exception,
        };
}
