namespace PicoDI.Test;

/// <summary>
/// The pool-starvation contract of <see cref="ShutdownDeadline"/> and of the one site in this
/// package that uses it — <c>SvcContainer.DisposeAsync</c>'s per-service stop budget.
/// </summary>
/// <remarks>
/// <para>
/// These are the measurements <c>docs/release-notes/v2026.11.0.md</c> ("Deadlines a starved
/// <c>ThreadPool</c> does not honour") asked for, in the shape that report said would be needed:
/// every <see cref="ThreadPool"/> worker is occupied first, and then the deadline either ends at
/// its budget or it does not. A timer-shaped deadline cannot pass them — that is the point.
/// </para>
/// <para>
/// Everything measured happens on a thread of the probe's own, because under the starvation these
/// tests arrange there is no worker to resume anything on. Hence
/// <see cref="NotInParallelAttribute"/>: nothing else may be starved with them.
/// </para>
/// </remarks>
[NotInParallel]
public class ShutdownDeadlineStarvationTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(500);

    /// <summary>16x the budget: past this the probe is not "late", it is not happening.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(8);

    /// <summary>
    /// The documented bound: a deadline that is honoured lands inside this multiple of its budget.
    /// Wide on purpose — a loaded CI leg stretches scheduling, and every stretched deadline that
    /// still lands means "honoured", which is the whole claim. <see cref="TimeSpan.MaxValue"/> is
    /// what a probe returns when the deadline never fired at all, and it fails this by miles.
    /// </summary>
    private const double HonouredMultiple = 4.0;

    [Test]
    public async Task BlockingWait_UnderAStarvedPool_StillEndsAtItsBudget()
    {
        var never = new TaskCompletionSource().Task;
        TimeSpan waited;

        using (StarvedThreadPool.OccupyAll())
        {
            waited = RunOnItsOwnThread(() => ShutdownDeadline.WaitOrAbandon(never, Budget));
        }

        await Assert
            .That(waited.TotalMilliseconds)
            .IsLessThanOrEqualTo(Budget.TotalMilliseconds * HonouredMultiple)
            .Because(
                $"the blocking shape times its own wait, so a starved pool must not matter: it "
                    + $"returned {(waited == TimeSpan.MaxValue ? "never" : $"{waited.TotalMilliseconds:0} ms")} "
                    + $"for a {Budget.TotalMilliseconds:0} ms budget"
            );
        await Assert
            .That(waited)
            .IsGreaterThanOrEqualTo(Budget)
            .Because(
                "the wait must not end before its budget — the task it waits for never completes"
            );
    }

    [Test]
    public async Task CancelAt_UnderAStarvedPool_CancelsTheAwaitAtItsBudget()
    {
        var never = new TaskCompletionSource().Task;
        var cancelled = false;
        TimeSpan waited;

        using (StarvedThreadPool.OccupyAll())
        {
            // Armed INSIDE the window: the watchdog thread has to start with no worker to help it.
            using var signal = ShutdownDeadline.CancelAt(Budget);
            waited = RunOnItsOwnThread(() =>
            {
                try
                {
                    never.WaitAsync(signal.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }
            });
        }

        await Assert.That(cancelled).IsTrue();
        await Assert
            .That(waited.TotalMilliseconds)
            .IsLessThanOrEqualTo(Budget.TotalMilliseconds * HonouredMultiple)
            .Because(
                "the escalating shape cancels from a dedicated watchdog thread, so the await behind "
                    + "the token must end at the budget even with every worker occupied; it took "
                    + $"{(waited == TimeSpan.MaxValue ? "never" : $"{waited.TotalMilliseconds:0} ms")}"
            );
    }

    [Test]
    public async Task CancelAt_WithAnUnboundedBudget_ArmsNothing()
    {
        // Timeout.InfiniteTimeSpan is the "unbounded" spelling the shutdown options use for
        // TimeSpan.Zero: nothing armed, no thread started, no cancel ever.
        using var signal = ShutdownDeadline.CancelAt(Timeout.InfiniteTimeSpan);

        await Assert.That(signal.Token.CanBeCanceled).IsFalse();
        await Assert.That(signal.IsExpired).IsFalse();
    }

    [Test]
    public async Task BlockingWait_OnAFaultedTask_SurfacesTheTasksOwnException()
    {
        // Exception parity with the `await` it replaces: Task.Wait hands back an AggregateException,
        // an await throws the task's own exception, and a caller must not be able to tell.
        var faulted = Task.FromException(new InvalidOperationException("boom"));

        await Assert
            .That(() => ShutdownDeadline.WaitOrAbandon(faulted, Budget))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task CancelAt_ConcurrentDeadlines_AreAllFiredByOneWatchdogThread()
    {
        // "One thread, not one per call": N deadlines outstanding at once are served by the same
        // thread, and that thread exits once the last one is satisfied.
        const int Deadlines = 5;
        var fireThreads = new ConcurrentBag<int>();
        var signals = new DeadlineSignal[Deadlines];
        for (var i = 0; i < Deadlines; i++)
        {
            signals[i] = ShutdownDeadline.CancelAt(Budget);
            signals[i]
                .Token.Register(
                    static state =>
                        ((ConcurrentBag<int>)state!).Add(Environment.CurrentManagedThreadId),
                    fireThreads
                );
        }

        var waited = Stopwatch.StartNew();
        while (fireThreads.Count < Deadlines && waited.Elapsed < Patience)
            Thread.Sleep(10);

        foreach (var signal in signals)
            signal.Dispose();
        waited.Restart();
        while (ShutdownDeadline.WatchdogIsRunning && waited.Elapsed < Patience)
            Thread.Sleep(10);

        await Assert.That(fireThreads.Count).IsEqualTo(Deadlines);
        await Assert
            .That(fireThreads.Distinct().Count())
            .IsEqualTo(1)
            .Because(
                $"one shared watchdog thread serves every armed deadline; observed: {string.Join(",", fireThreads)}"
            );
        await Assert
            .That(ShutdownDeadline.WatchdogIsRunning)
            .IsFalse()
            .Because(
                "the watchdog exits when no deadline is outstanding — an idle process holds no extra thread"
            );
    }

    [Test]
    public async Task SvcContainer_DisposeAsync_UnderAStarvedPool_AbandonsTheStopAtTheServiceBudget()
    {
        // THE SITE: a hosted service that ignores its token must not hold DisposeAsync past its
        // budget even when the pool has nothing to give. Before this, the CTS timer never fired and
        // the disposal never returned (measured: still waiting at 8 s for a 500 ms budget).
        var svc = new NeverStoppingSvc();
        var errors = new ConcurrentQueue<Exception>();
        var container = new SvcContainer(autoConfigureFromGenerator: false)
        {
            HostedSvcStopTimeout = Budget,
        };
        container.OnError = (error, _) => errors.Enqueue(error);
        container.RegisterHostedSvc<NeverStoppingSvc>(_ => svc);
        container.Build();
        await new SvcHost(container).StartAsync();

        TimeSpan disposed;
        using (StarvedThreadPool.OccupyAll())
        {
            disposed = RunOnItsOwnThread(() =>
                container.DisposeAsync().AsTask().GetAwaiter().GetResult()
            );
        }

        await Assert.That(svc.StopEntered).IsTrue();
        await Assert
            .That(disposed.TotalMilliseconds)
            .IsLessThanOrEqualTo(Budget.TotalMilliseconds * HonouredMultiple)
            .Because(
                $"DisposeAsync must abandon the stop at its {Budget.TotalMilliseconds:0} ms service "
                    + "budget; it returned "
                    + $"{(disposed == TimeSpan.MaxValue ? "never" : $"{disposed.TotalMilliseconds:0} ms")}"
            );
        await Assert
            .That(errors.Any(static error => error is TimeoutException))
            .IsTrue()
            .Because(
                "an abandoned phase is still reported — the deadline changes when, not whether"
            );

        svc.Release();
    }

    /// <summary>
    /// Runs <paramref name="probe"/> on a thread of its own and returns what it took, or
    /// <see cref="TimeSpan.MaxValue"/> when it was still running after <see cref="Patience"/> — the
    /// honest return value for "the deadline was not honoured at all".
    /// </summary>
    private static TimeSpan RunOnItsOwnThread(Action probe)
    {
        var elapsed = Stopwatch.StartNew();
        var thread = new Thread(() => probe()) { IsBackground = true, Name = "deadline-probe" };
        thread.Start();
        return thread.Join(Patience) ? elapsed.Elapsed : TimeSpan.MaxValue;
    }

    /// <summary>A hosted service whose stop never completes and that ignores its token.</summary>
    private sealed class NeverStoppingSvc : IHostedSvc
    {
        private readonly TaskCompletionSource _never = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public volatile bool StopEntered;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopEntered = true;
            return _never.Task; // deliberately ignores cancellationToken
        }

        public void Release() => _never.TrySetResult();
    }
}
