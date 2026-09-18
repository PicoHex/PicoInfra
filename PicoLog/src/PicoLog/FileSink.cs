namespace PicoLog;

public sealed class FileSink : ILogSink, IFlushableLogSink
{
    private readonly Channel<string> _channel;
    private readonly ILogFormatter _formatter;
    private readonly FileSinkOptions _options;
    private readonly string _baseFilePath;
    private readonly Lock _fileLock = new();
    private StreamWriter _writer;
    private readonly Task _processingTask;
    private readonly SemaphoreSlim _flushSemaphore = new(1, 1);
    private readonly FlushQuiesceCoordinator _flushQuiesceCoordinator = new();
    private int _disposeState;
    private int _activeDequeuedMessages;
    private int _activeBatchOperations;
    private int _rotationIndex;
    private long _lastRotationTimestamp;
    private Exception? _processingException;

    public FileSink(ILogFormatter formatter, string filePath = FileSinkOptions.DefaultFilePath)
        : this(formatter, new FileSinkOptions { FilePath = filePath }) { }

    public FileSink(ILogFormatter formatter, FileSinkOptions options)
    {
        _formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
        _options = (
            options ?? throw new ArgumentNullException(nameof(options))
        ).CreateValidatedCopy();

        _baseFilePath = Path.GetFullPath(_options.FilePath);
        var directory =
            Path.GetDirectoryName(_baseFilePath)
            ?? throw new ArgumentException(
                $"File path must not be a root directory: '{_options.FilePath}'",
                nameof(options)
            );

        if (!Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        _channel = Channel.CreateBounded<string>(
            new BoundedChannelOptions(_options.QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
            }
        );

        var fileStream = new FileStream(
            _baseFilePath,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true
        );

        _writer = new StreamWriter(fileStream, Encoding.UTF8);
        _lastRotationTimestamp = DateTime.UtcNow.Ticks;
        // Seed the rotation counter from files already on disk so that a
        // process restart continues after the highest existing rotation index
        // instead of overwriting the previous run's oldest retained file.
        _rotationIndex = GetExistingRotationIndex();
        _processingTask = ProcessWritesAsync();
    }

    public async Task WriteAsync(LogEntry entry, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            PicoLogMetrics.RecordRejectedAfterShutdown();
            return;
        }

        var message = _formatter.Format(entry);

        // Participate in the flush protocol: a pending flush blocks new writes
        // until it has drained and flushed, so the processing task never touches
        // the (non-thread-safe) StreamWriter concurrently with FlushAsync.
        await _flushQuiesceCoordinator
            .EnterWriteOperationAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await _channel.Writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            // Channel closed means the sink is shutting down.
            // Writes arriving after channel completion are expected
            // and silently discarded — the entry was already in flight
            // when disposal began.
        }
        finally
        {
            _flushQuiesceCoordinator.ExitWriteOperation();
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
            await _writer.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            ResumeWrites();
            _flushSemaphore.Release();
        }
    }

    private async Task ProcessWritesAsync()
    {
        try
        {
            var batch = new List<string>(_options.BatchSize);

            while (await _channel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (true)
                {
                    BeginDequeuedMessage();

                    if (!_channel.Reader.TryRead(out var message))
                    {
                        EndDequeuedMessage();
                        break;
                    }

                    batch.Add(message);

                    try
                    {
                        BeginBatch();

                        try
                        {
                            await DrainBatchAsync(batch).ConfigureAwait(false);
                        }
                        finally
                        {
                            EndBatch();
                        }
                    }
                    finally
                    {
                        EndDequeuedMessage();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _processingException = ex;
            // Surface the failure via telemetry immediately — the pipeline has
            // stopped, so waiting until DisposeAsync would hide it indefinitely.
            PicoLogMetrics.RecordSinkFailure();
        }
    }

    private async ValueTask DrainBatchAsync(List<string> batch)
    {
        if (_options.AllowFlushInterrupt)
        {
            while (batch.Count < _options.BatchSize)
            {
                if (IsFlushPending())
                    break;

                // Synchronous read — no timer, no CancellationTokenSource, no Task.WhenAny.
                // If a message is already available, add it and keep filling the batch.
                // If not, flush immediately. This avoids the ARM64 timer reliability issue
                // entirely while preserving batching under load (messages arrive faster than
                // the processing loop can drain).
                if (!_channel.Reader.TryRead(out var message))
                    break;

                batch.Add(message);
            }
        }
        else
        {
            while (batch.Count < _options.BatchSize && _channel.Reader.TryRead(out var message))
            {
                batch.Add(message);
            }
        }

        foreach (var message in batch)
            await _writer.WriteLineAsync(message).ConfigureAwait(false);

        await _writer.FlushAsync().ConfigureAwait(false);
        batch.Clear();

        await RotateIfNeededAsync().ConfigureAwait(false);
    }

    private async ValueTask RotateIfNeededAsync()
    {
        // Called from the single _processingTask; all rotation decisions happen
        // under _fileLock to coordinate with concurrent FlushAsync.
        // There is no TOCTOU between check and execute because both the check
        // and the mutation are inside the same lock scope.
        lock (_fileLock)
        {
            bool needsSizeRotation =
                _options.MaxFileSizeBytes > 0
                && _writer.BaseStream.Length >= _options.MaxFileSizeBytes;

            bool needsTimeRotation =
                _options.RotationInterval > TimeSpan.Zero
                && (DateTime.UtcNow.Ticks - _lastRotationTimestamp)
                    >= _options.RotationInterval.Ticks;

            if (!needsSizeRotation && !needsTimeRotation)
                return;

            _writer.Dispose();

            var rotatedPath = GetRotatedFilePath();
            // overwrite:false — with a correctly seeded index the target path is
            // always new, so any collision is an unexpected state that must surface
            // instead of silently destroying previously retained log data.
            File.Move(_baseFilePath, rotatedPath, overwrite: false);
            CleanUpOldFiles();

            var fs = new FileStream(
                _baseFilePath,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true
            );
            _writer = new StreamWriter(fs, Encoding.UTF8);
            _rotationIndex++;
        }
    }

    private string GetRotatedFilePath()
    {
        var dir = Path.GetDirectoryName(_baseFilePath)!;
        var name = Path.GetFileNameWithoutExtension(_baseFilePath);
        var ext = Path.GetExtension(_baseFilePath);
        return Path.Combine(dir, $"{name}.{_rotationIndex + 1}{ext}");
    }

    private void CleanUpOldFiles()
    {
        if (_options.MaxRetainedFiles <= 0)
            return;

        var dir = Path.GetDirectoryName(_baseFilePath)!;
        var name = Path.GetFileNameWithoutExtension(_baseFilePath);
        var ext = Path.GetExtension(_baseFilePath);
        var rotatedFiles = Directory
            .GetFiles(dir, $"{name}.*{ext}")
            // Only files THIS sink produced ({name}.{n}{ext}, n >= 1) are subject
            // to retention. A foreign file matching the glob (e.g. app.error.log)
            // parses as a non-index and must never be deleted.
            .Where(f => TryGetRotationIndex(f, name, out _))
            .OrderBy(f => GetRotationIndexFromFile(f, name))
            .ToList();

        while (rotatedFiles.Count > _options.MaxRetainedFiles)
        {
            try
            {
                File.Delete(rotatedFiles[0]);
            }
            catch
            {
                // Retention cleanup failures must not be silent: record them so
                // the sink-failure counter surfaces the problem to telemetry.
                PicoLogMetrics.RecordSinkFailure();
            }
            rotatedFiles.RemoveAt(0);
        }
    }

    private int GetExistingRotationIndex()
    {
        var dir = Path.GetDirectoryName(_baseFilePath)!;
        var name = Path.GetFileNameWithoutExtension(_baseFilePath);
        var ext = Path.GetExtension(_baseFilePath);
        var maxIndex = 0;

        foreach (var file in Directory.GetFiles(dir, $"{name}.*{ext}"))
        {
            var index = GetRotationIndexFromFile(file, name);
            if (index > maxIndex)
                maxIndex = index;
        }

        return maxIndex;
    }

    private static int GetRotationIndexFromFile(string file, string name) =>
        TryGetRotationIndex(file, name, out var index) ? index : 0;

    /// <summary>Parses the rotation index from a "{name}.{n}{ext}" file name.
    /// Returns false for anything that is not a sink-produced rotated file
    /// (missing prefix, non-numeric suffix, or index below 1).</summary>
    private static bool TryGetRotationIndex(string file, string name, out int index)
    {
        index = 0;
        var fileName = Path.GetFileNameWithoutExtension(file);
        if (
            fileName.Length <= name.Length
            || !fileName.StartsWith(name + ".", StringComparison.Ordinal)
        )
            return false;

        var suffix = fileName[(name.Length + 1)..];
        return int.TryParse(suffix, out index) && index > 0;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        await _flushSemaphore.WaitAsync().ConfigureAwait(false);

        try
        {
            _channel.Writer.TryComplete();
            await ShutdownAsync().ConfigureAwait(false);
        }
        finally
        {
            _flushSemaphore.Release();
        }
    }

    private async Task ShutdownAsync()
    {
        Exception? processingException = null;

        try
        {
            await _processingTask.ConfigureAwait(false);
            processingException = _processingException;
        }
        catch (Exception ex)
        {
            processingException = ex;
        }

        try
        {
            await _writer.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (processingException is null)
        {
            processingException = ex;
        }
        finally
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
        }

        if (processingException is not null)
            ExceptionDispatchInfo.Throw(processingException);
    }

    private ValueTask BlockWritesAsync(CancellationToken cancellationToken) =>
        _flushQuiesceCoordinator.BlockWritesAsync(cancellationToken);

    private ValueTask WaitForIdleAsync(CancellationToken cancellationToken) =>
        _flushQuiesceCoordinator.WaitForIdleAsync(IsOwnerIdleUnderLock, cancellationToken);

    private void ResumeWrites() => _flushQuiesceCoordinator.ResumeWrites();

    private void BeginBatch() =>
        _flushQuiesceCoordinator.BeginOwnerActivity(() => _activeBatchOperations++);

    private void BeginDequeuedMessage() =>
        _flushQuiesceCoordinator.BeginOwnerActivity(() => _activeDequeuedMessages++);

    private void EndDequeuedMessage() =>
        _flushQuiesceCoordinator.EndOwnerActivity(
            () => _activeDequeuedMessages--,
            IsOwnerIdleUnderLock
        );

    private void EndBatch() =>
        _flushQuiesceCoordinator.EndOwnerActivity(
            () => _activeBatchOperations--,
            IsOwnerIdleUnderLock
        );

    private bool IsFlushPending() => _flushQuiesceCoordinator.IsFlushPending();

    private bool IsOwnerIdleUnderLock() =>
        _activeDequeuedMessages == 0 && _activeBatchOperations == 0 && _channel.Reader.Count == 0;
}
