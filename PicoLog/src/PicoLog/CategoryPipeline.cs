namespace PicoLog;

/// <summary>
/// Processes log entries for a single category through a dedicated background task.
/// Each <see cref="CategoryPipeline"/> owns one <see cref="InternalLoggerQueue"/> (bounded channel)
/// and one processing task. When many unique categories are created (e.g. 100+), each category
/// consumes its own channel buffer and background task. For dynamic / high-cardinality category
/// scenarios, consider reusing categories or limiting category count to avoid resource pressure.
/// </summary>
internal sealed class CategoryPipeline : IAsyncDisposable
{
    private readonly string _categoryName;
    private readonly LoggerFactoryRuntime _runtime;
    private readonly InternalLogSinkDispatcher _sinkDispatcher;
    private readonly InternalLoggerQueue _queue;
    private readonly Task _processingTask;
    private readonly int _queueDepthProviderId;
    private readonly SemaphoreSlim _flushSemaphore = new(1, 1);
    private readonly FlushQuiesceCoordinator _flushQuiesceCoordinator = new();
    private int _disposeState;
    private int _activeDequeuedEntries;
    private int _activeDispatchOperations;
    private long _droppedEntries;
    private Exception? _processingException;

    public CategoryPipeline(string categoryName, LoggerFactoryRuntime runtime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryName);

        _categoryName = categoryName;
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _sinkDispatcher = new InternalLogSinkDispatcher(_runtime);
        _queue = new InternalLoggerQueue(_runtime);
        _queueDepthProviderId = PicoLogMetrics.RegisterQueueDepthProvider(
            _queue.GetQueuedEntryCount
        );
        _processingTask = ProcessEntriesAsync();
    }

    public void Write(LogEntry entry)
    {
        if (_runtime.CanFastPath)
        {
            _sinkDispatcher.DispatchEntrySync(entry);
            LogEntryPool.Return(entry);
            return;
        }

        try
        {
            EnterWriteOperationSync();
        }
        catch (TimeoutException) when (_runtime.DropMessagesOnFlush)
        {
            // Flush is in progress and the sync write timed out.
            // DropMessagesOnFlush is enabled — drop this entry rather than
            // blocking the calling thread indefinitely.
            ReportDroppedMessage();
            return;
        }

        try
        {
            HandleWriteResult(_queue.TryEnqueueSync(entry));
        }
        finally
        {
            ExitWriteOperation();
        }
    }

    public async Task WriteAsync(LogEntry entry, CancellationToken cancellationToken)
    {
        await EnterWriteOperationAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var writeTask = _queue.TryEnqueueAsync(entry, cancellationToken);
            if (writeTask.IsCompletedSuccessfully)
            {
                HandleWriteResult(writeTask.Result);
                return;
            }

            HandleWriteResult(await writeTask.ConfigureAwait(false));
        }
        finally
        {
            ExitWriteOperation();
        }
    }

    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);

        await _flushSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);

            await BlockWritesAsync(cancellationToken).ConfigureAwait(false);
            await WaitForIdleAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            ResumeWrites();
            _flushSemaphore.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        // Acquire the flush semaphore BEFORE completing the queue to prevent
        // a race with FlushAsync. Once we hold the semaphore, no concurrent
        // FlushAsync can proceed — it either detects _disposeState != 0
        // or blocks on the semaphore acquisition.
        await _flushSemaphore.WaitAsync().ConfigureAwait(false);

        DeadlineSignal? drainDeadline = null;

        try
        {
            var shutdownTimeout = _runtime.ShutdownTimeout;
            // Not a CancellationTokenSource(TimeSpan): that budget is a Timer callback, i.e.
            // ThreadPool work, so under a starved pool the in-flight sink write was cancelled late
            // or never (docs/release-notes/v2026.11.0.md, "Deadlines a starved ThreadPool does not
            // honour"). The deadline's own token is cancelled by a dedicated watchdog thread at the
            // budget, and the sink write waiting on it resumes on that thread. TimeSpan.Zero still
            // means unbounded: no deadline is armed at all.
            drainDeadline =
                shutdownTimeout > TimeSpan.Zero ? ShutdownDeadline.CancelAt(shutdownTimeout) : null;

            if (drainDeadline is not null)
                _sinkDispatcher.BeginDrain(drainDeadline.Token);

            _queue.Complete();

            await _processingTask.ConfigureAwait(false);

            PicoLogMetrics.UnregisterQueueDepthProvider(_queueDepthProviderId);

            if (_processingException is { } ex)
                ExceptionDispatchInfo.Throw(ex);
        }
        finally
        {
            _sinkDispatcher.Dispose();
            drainDeadline?.Dispose();
            _flushSemaphore.Dispose();
        }
    }

    private void HandleWriteResult(LogWriteResult result)
    {
        switch (result)
        {
            case LogWriteResult.AcceptedAfterEviction or LogWriteResult.DroppedNewWrite:
                ReportDroppedMessage();
                break;
            case LogWriteResult.RejectedAfterShutdown:
                _runtime.RecordRejectedAfterShutdown();
                break;
        }
    }

    private void ReportDroppedMessage()
    {
        var dropped = Interlocked.Increment(ref _droppedEntries);
        _runtime.ReportDroppedMessages(_categoryName, dropped);
    }

    private static readonly int MaxBatchSize = 64;

    private async Task ProcessEntriesAsync()
    {
        try
        {
            if (_runtime.QueueFullMode == LogQueueFullMode.Wait)
            {
                await ProcessEntriesSequentiallyAsync().ConfigureAwait(false);
                return;
            }

            var batch = new List<LogEntry>(MaxBatchSize);

            while (await _queue.WaitToReadAsync().ConfigureAwait(false))
            {
                while (batch.Count < MaxBatchSize && _queue.TryRead(out var entry))
                {
                    BeginDequeuedEntry();
                    batch.Add(entry);
                    EndDequeuedEntry();
                }

                if (batch.Count == 0)
                    continue;

                BeginDispatch();

                try
                {
                    await _sinkDispatcher.DispatchBatchAsync(batch).ConfigureAwait(false);
                }
                finally
                {
                    EndDispatch();
                    batch.Clear();
                }
            }
        }
        catch (Exception ex)
        {
            _processingException = ex;
        }
    }

    private async Task ProcessEntriesSequentiallyAsync()
    {
        while (await _queue.WaitToReadAsync().ConfigureAwait(false))
        {
            while (_queue.TryRead(out var entry))
            {
                BeginDequeuedEntry();
                EndDequeuedEntry();
                BeginDispatch();

                try
                {
                    await _sinkDispatcher.DispatchEntryAsync(entry).ConfigureAwait(false);
                }
                finally
                {
                    EndDispatch();
                }
            }
        }
    }

    private void EnterWriteOperationSync() =>
        _flushQuiesceCoordinator.EnterWriteOperationSync(_runtime.SyncWriteTimeout);

    private ValueTask EnterWriteOperationAsync(CancellationToken cancellationToken) =>
        _flushQuiesceCoordinator.EnterWriteOperationAsync(cancellationToken);

    private void ExitWriteOperation() => _flushQuiesceCoordinator.ExitWriteOperation();

    private ValueTask BlockWritesAsync(CancellationToken cancellationToken) =>
        _flushQuiesceCoordinator.BlockWritesAsync(cancellationToken);

    private ValueTask WaitForIdleAsync(CancellationToken cancellationToken) =>
        _flushQuiesceCoordinator.WaitForIdleAsync(IsOwnerIdleUnderLock, cancellationToken);

    private void ResumeWrites() => _flushQuiesceCoordinator.ResumeWrites();

    private void BeginDispatch() =>
        _flushQuiesceCoordinator.BeginOwnerActivity(() => _activeDispatchOperations++);

    private void BeginDequeuedEntry() =>
        _flushQuiesceCoordinator.BeginOwnerActivity(() => _activeDequeuedEntries++);

    private void EndDequeuedEntry() =>
        _flushQuiesceCoordinator.EndOwnerActivity(
            () => _activeDequeuedEntries--,
            IsOwnerIdleUnderLock
        );

    private void EndDispatch() =>
        _flushQuiesceCoordinator.EndOwnerActivity(
            () => _activeDispatchOperations--,
            IsOwnerIdleUnderLock
        );

    private bool IsOwnerIdleUnderLock() =>
        _activeDequeuedEntries == 0
        && _activeDispatchOperations == 0
        && _queue.GetQueuedEntryCount() == 0;
}
