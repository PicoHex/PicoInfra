namespace PicoLog;

public sealed class LoggerFactoryOptions
{
    public LogLevel MinLevel { get; set; } = LogLevel.Debug;

    public int QueueCapacity { get; set; } = 65535;

    public LogQueueFullMode QueueFullMode { get; set; } = LogQueueFullMode.DropOldest;

    public TimeSpan SyncWriteTimeout { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// When <c>true</c> (default), synchronous writes that timeout while a flush is in
    /// progress are silently dropped. When <c>false</c>, the <see cref="TimeoutException"/>
    /// propagates to the caller.
    /// </summary>
    public bool DropMessagesOnFlush { get; set; } = true;

    public Action<string, long>? OnMessagesDropped { get; set; }

    public TimeProvider TimestampProvider { get; set; } = TimeProvider.System;

    public IList<LogFilterRule> FilterRules { get; set; } = [];

    /// <summary>
    /// Maximum time to wait for sink writes to complete during shutdown drain. Defaults to 30
    /// seconds; <see cref="TimeSpan.Zero"/> means wait indefinitely.
    /// </summary>
    /// <remarks>
    /// The deadline is armed on a dedicated watchdog thread that watches a <see cref="Stopwatch"/>
    /// (<c>ShutdownDeadline.CancelAt</c>), not on a <see cref="System.Threading.Timer"/>: a timer
    /// callback is <see cref="ThreadPool"/> work, so under total pool starvation it would not be
    /// honoured at all (<c>docs/release-notes/v2026.11.0.md</c>, "Deadlines a starved ThreadPool
    /// does not honour"). The cancellation therefore reaches the in-flight sink write at the budget
    /// even with every worker occupied; what still needs a worker is the rest of the drain, because
    /// the pipeline itself is pool work.
    /// </remarks>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public LoggerFactoryOptions CreateValidatedCopy()
    {
        if (QueueCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(QueueCapacity));

        if (SyncWriteTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(SyncWriteTimeout));

        if (ShutdownTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ShutdownTimeout));

        return new LoggerFactoryOptions
        {
            MinLevel = MinLevel,
            QueueCapacity = QueueCapacity,
            QueueFullMode = QueueFullMode,
            SyncWriteTimeout = SyncWriteTimeout,
            DropMessagesOnFlush = DropMessagesOnFlush,
            OnMessagesDropped = OnMessagesDropped,
            TimestampProvider = TimestampProvider,
            ShutdownTimeout = ShutdownTimeout,
            FilterRules = [.. FilterRules],
        };
    }
}

public sealed record LogFilterRule(string CategoryPrefix, LogLevel MinLevel);
