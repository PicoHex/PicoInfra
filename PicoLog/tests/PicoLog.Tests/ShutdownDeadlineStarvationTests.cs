namespace PicoLog.Tests;

/// <summary>
/// The pool-starvation contract of <c>LoggerFactory.DisposeAsync</c>'s drain budget — the one
/// deadline of the six a caller can configure (<see cref="LoggerFactoryOptions.ShutdownTimeout"/>).
/// </summary>
/// <remarks>
/// <para>
/// What is measured is the cancellation reaching the in-flight sink write, not how long
/// <c>DisposeAsync</c> took overall: the drain deadline can be made pool-free, the pipeline that
/// has to run afterwards cannot (it is a pool loop, and a starved pool cannot run it whatever the
/// deadline says). The defect <c>docs/release-notes/v2026.11.0.md</c> records is exactly this
/// cancellation never arriving — a <c>CancellationTokenSource(TimeSpan)</c> timer is
/// <c>ThreadPool</c> work — so it is the honest thing to assert.
/// </para>
/// </remarks>
[NotInParallel]
public class ShutdownDeadlineStarvationTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(500);

    /// <summary>16x the budget: past this the cancellation never arrived.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(8);

    /// <summary>Documented bound: a deadline that is honoured lands inside this multiple.</summary>
    private const double HonouredMultiple = 4.0;

    [Test]
    public async Task DisposeAsync_UnderAStarvedPool_CancelsTheInFlightSinkWriteAtTheDrainBudget()
    {
        // THE SITE: the write is already parked inside the sink before the pool is starved, so the
        // only question left is whether the drain's cancel can reach it without a worker.
        var sink = new TokenAwareBlockingSink();
        var options = new LoggerFactoryOptions { ShutdownTimeout = Budget };
        await using var factory = new LoggerFactory([sink], options);
        var logger = factory.CreateLogger("Tests.StarvedDrain");

        await logger.InfoAsync("payload");
        await sink.WriteStarted;

        sink.MeasuresCancellationFrom(Stopwatch.GetTimestamp());

        using (StarvedThreadPool.OccupyAll())
        {
            // Start the drain from a thread of its own; it is only *observed* here, because nothing
            // in the pipeline can be resumed while the pool is occupied.
            var drain = new Thread(() => factory.DisposeAsync().AsTask().GetAwaiter().GetResult())
            {
                IsBackground = true,
                Name = "deadline-probe-drain",
            };
            drain.Start();

            var waited = Stopwatch.StartNew();
            while (sink.ObservedCancellationCount == 0 && waited.Elapsed < Patience)
                Thread.Sleep(10);
        }

        await Assert
            .That(sink.ObservedCancellationCount)
            .IsEqualTo(1)
            .Because(
                "the expired drain budget must cancel the in-flight write even with every worker "
                    + "occupied — before this it was never cancelled at all"
            );
        await Assert
            .That(sink.ObservedCancellationDelay.TotalMilliseconds)
            .IsLessThanOrEqualTo(Budget.TotalMilliseconds * HonouredMultiple)
            .Because(
                $"the drain budget is {Budget.TotalMilliseconds:0} ms; the cancellation became "
                    + $"visible at {sink.ObservedCancellationDelay.TotalMilliseconds:0} ms"
            );
        await Assert
            .That(sink.ObservedCancellationDelay)
            .IsGreaterThanOrEqualTo(Budget)
            .Because("the write must be given its full budget before being cancelled");

        sink.Release();
        await factory.DisposeAsync();
    }

    /// <summary>
    /// A sink whose write parks until released or cancelled, stamping the moment it observes the
    /// cancellation on the thread that observes it.
    /// </summary>
    private sealed class TokenAwareBlockingSink : ILogSink
    {
        private readonly TaskCompletionSource _writeStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _observedCancellationCount;
        private long _originTimestamp;
        private long _observedCancellationDelayTicks = -1;

        public Task WriteStarted => _writeStarted.Task;

        public int ObservedCancellationCount => Volatile.Read(ref _observedCancellationCount);

        public TimeSpan ObservedCancellationDelay
        {
            get
            {
                var ticks = Volatile.Read(ref _observedCancellationDelayTicks);
                return ticks < 0 ? TimeSpan.Zero : new TimeSpan(ticks);
            }
        }

        public void MeasuresCancellationFrom(long originTimestamp) =>
            Volatile.Write(ref _originTimestamp, originTimestamp);

        public async Task WriteAsync(LogEntry entry, CancellationToken cancellationToken = default)
        {
            _writeStarted.TrySetResult();

            try
            {
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stamped HERE, on the thread that observes it — under starvation that is the
                // watchdog thread, which is the whole point of the measurement.
                Volatile.Write(
                    ref _observedCancellationDelayTicks,
                    Stopwatch.GetElapsedTime(Volatile.Read(ref _originTimestamp)).Ticks
                );
                Interlocked.Increment(ref _observedCancellationCount);
            }
        }

        public void Release() => _release.TrySetResult();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
