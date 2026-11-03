namespace PicoLog;

public static class PicoLogMetrics
{
    public const string MeterName = "PicoLog";
    public const string EntriesEnqueuedName = "picolog.entries.enqueued";
    public const string EntriesDroppedName = "picolog.entries.dropped";
    public const string SinkFailuresName = "picolog.sinks.failures";
    public const string ShutdownRejectedWritesName = "picolog.writes.rejected_after_shutdown";
    public const string QueuedEntriesName = "picolog.queue.entries";
    public const string ShutdownDrainDurationName = "picolog.shutdown.drain.duration";

    private static readonly Meter Meter = new(MeterName);
    private static readonly ConcurrentDictionary<int, Func<long>> QueueDepthProviders = new();
    private static readonly Counter<long> EntriesEnqueued = Meter.CreateCounter<long>(
        EntriesEnqueuedName
    );
    private static readonly Counter<long> EntriesDropped = Meter.CreateCounter<long>(
        EntriesDroppedName
    );
    private static readonly Counter<long> SinkFailures = Meter.CreateCounter<long>(
        SinkFailuresName
    );
    private static readonly Counter<long> ShutdownRejectedWrites = Meter.CreateCounter<long>(
        ShutdownRejectedWritesName
    );
#pragma warning disable IDE0052 // ObservableGauge is kept alive via Meter registration
    private static readonly ObservableGauge<long> QueuedEntries = Meter.CreateObservableGauge<long>(
        QueuedEntriesName,
        ObserveQueuedEntries
    );
#pragma warning restore IDE0052

    private static readonly Histogram<double> ShutdownDrainDuration = Meter.CreateHistogram<double>(
        ShutdownDrainDurationName,
        unit: "ms"
    );

    internal static int RegisterQueueDepthProvider(Func<long> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var providerId = Interlocked.Increment(ref _nextProviderId);
        QueueDepthProviders[providerId] = provider;
        return providerId;
    }

    internal static void UnregisterQueueDepthProvider(int providerId) =>
        QueueDepthProviders.TryRemove(providerId, out _);

    internal static void RecordEntryAccepted() => EntriesEnqueued.Add(1);

    internal static void RecordDroppedEntry() => EntriesDropped.Add(1);

    internal static void RecordSinkFailure() => SinkFailures.Add(1);

    /// <summary>
    /// Records one write rejected because its sink was already closed — the documented
    /// <c>picolog.writes.rejected_after_shutdown</c> counter.
    /// </summary>
    /// <remarks>
    /// <b>Public on purpose.</b> The counter is documented for every sink, and a sink that ships
    /// in its own package (<c>SeqSink</c>, in <c>PicoLog.Json</c>) is outside this assembly, so an
    /// <c>internal</c> recorder would either lose the signal for that sink or split the one meter
    /// across two assemblies. Recording is idempotent and stateless beyond the shared instrument,
    /// so exposing it costs an additive public member and no behaviour: the cost of the
    /// alternative (<c>InternalsVisibleTo</c> the sink package, or moving the meter to PicoLog.Abs)
    /// is a wider hole than the one it plugs. Everything else here stays <c>internal</c>.
    /// </remarks>
    public static void RecordRejectedAfterShutdown() => ShutdownRejectedWrites.Add(1);

    internal static void RecordShutdownDrainDuration(TimeSpan duration) =>
        ShutdownDrainDuration.Record(duration.TotalMilliseconds);

    private static int _nextProviderId;

    private static IEnumerable<Measurement<long>> ObserveQueuedEntries()
    {
        var totalQueuedEntries = QueueDepthProviders.Values.Sum(provider => provider());

        yield return new Measurement<long>(totalQueuedEntries);
    }
}
