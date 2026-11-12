namespace PicoCfg.Tests;

/// <summary>
/// The pool-starvation contract of <c>CfgRoot.DisposeAsync</c>'s two reload-gate waits, and of the
/// blocking shape they moved onto.
/// </summary>
/// <remarks>
/// <c>docs/release-notes/v2026.11.0.md</c> ("Deadlines a starved <c>ThreadPool</c> does not
/// honour") measured the defect these close: with every worker occupied, the timer behind
/// <c>SemaphoreSlim.WaitAsync(timeout)</c> never fires, so the drain budget did nothing at all. The
/// blocking <c>SemaphoreSlim.Wait(timeout)</c> is timed by the calling thread and cannot be starved
/// — measured on this repo, <c>WaitAsync(token)</c> never returned where <c>Wait(timeout)</c>
/// returned at 1.18x the budget, which is why this site takes the blocking shape even though it is
/// a disposal path.
/// </remarks>
[NotInParallel]
public class ShutdownDeadlineStarvationTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(500);

    /// <summary>16x one budget: past this the probe is not late, the deadline never fired.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Documented bound, counted in budgets: the primary gate wait, then the secondary one, each
    /// getting its own budget — so 2x is the floor and this multiple leaves room for scheduling.
    /// </summary>
    private const double HonouredMultiple = 5.0;

    [Test]
    public async Task DisposeAsync_WithAStuckReload_UnderAStarvedPool_ReturnsAtItsTwoBudgets()
    {
        // THE SITE: a reload that holds _reloadGate and ignores cancellation. Both drain waits are
        // bounded by a budget, so DisposeAsync must return at ~2x the budget with every worker
        // occupied. Measured before this change: still running at 8 s.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new BlockingProvider(release.Task);
        var root = TestCfgFactory.CreateRoot(
            [provider],
            reloadDrainTimeout: Budget,
            reloadDrainRetryTimeout: Budget
        );

        var reload = root.ReloadAsync().AsTask();
        await provider.Entered.Task.WaitAsync(Patience);

        TimeSpan disposed;
        using (StarvedThreadPool.OccupyAll())
        {
            disposed = RunOnItsOwnThread(() =>
                root.DisposeAsync().AsTask().GetAwaiter().GetResult()
            );
        }

        release.TrySetResult();

        await Assert
            .That(disposed.TotalMilliseconds)
            .IsLessThanOrEqualTo(Budget.TotalMilliseconds * HonouredMultiple)
            .Because(
                $"DisposeAsync must give up the gate at its budget instead of waiting for a worker; "
                    + "it returned "
                    + $"{(disposed == TimeSpan.MaxValue ? "never" : $"{disposed.TotalMilliseconds:0} ms")}"
            );

        // Same reporting as before: the stuck reload is not faulted with ObjectDisposedException.
        Exception? reloadFailure = null;
        try
        {
            await reload.WaitAsync(Patience);
        }
        catch (Exception ex)
        {
            reloadFailure = ex;
        }

        await Assert.That(reloadFailure is ObjectDisposedException).IsFalse();
    }

    /// <summary>
    /// Runs <paramref name="probe"/> on its own thread; <see cref="TimeSpan.MaxValue"/> means it was
    /// still running after <see cref="Patience"/>.
    /// </summary>
    private static TimeSpan RunOnItsOwnThread(Action probe)
    {
        var elapsed = Stopwatch.StartNew();
        var thread = new Thread(() => probe()) { IsBackground = true, Name = "deadline-probe" };
        thread.Start();
        return thread.Join(Patience) ? elapsed.Elapsed : TimeSpan.MaxValue;
    }

    [Test]
    public async Task BlockingSemaphoreWait_UnderAStarvedPool_EndsAtItsBudget()
    {
        // The primitive itself, without the root around it: a gate that nobody releases must time
        // out on the caller's thread, not on a worker that is not there.
        using var gate = new SemaphoreSlim(1, 1);
        gate.Wait(); // held for the duration

        var entered = true;
        TimeSpan waited;
        using (StarvedThreadPool.OccupyAll())
        {
            waited = RunOnItsOwnThread(() =>
                entered = ShutdownDeadline.WaitOrAbandon(gate, Budget)
            );
        }

        await Assert.That(entered).IsFalse();
        await Assert
            .That(waited.TotalMilliseconds)
            .IsLessThanOrEqualTo(Budget.TotalMilliseconds * HonouredMultiple)
            .Because(
                "the blocking wait times itself; it took "
                    + $"{(waited == TimeSpan.MaxValue ? "never" : $"{waited.TotalMilliseconds:0} ms")}"
            );
        await Assert.That(waited).IsGreaterThanOrEqualTo(Budget);
    }

    /// <summary>A provider whose reload enters the gate and then never finishes.</summary>
    private sealed class BlockingProvider(Task release) : ICfgProvider
    {
        public readonly TaskCompletionSource Entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public ICfgSnapshot Snapshot { get; } = new EmptySnapshot();

        public async ValueTask<bool> ReloadAsync(CancellationToken ct = default)
        {
            Entered.TrySetResult();
            await release.ConfigureAwait(false); // deliberately ignores cancellation
            return false;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EmptySnapshot : ICfgSnapshot
    {
        public bool TryGetValue(string path, out string? value)
        {
            value = null;
            return false;
        }

        public IReadOnlyDictionary<string, string> GetAllValues() =>
            new Dictionary<string, string>();
    }
}
