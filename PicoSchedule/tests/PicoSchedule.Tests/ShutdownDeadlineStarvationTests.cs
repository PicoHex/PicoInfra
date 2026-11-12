namespace PicoSchedule.Tests;

/// <summary>
/// The pool-starvation contract of <see cref="Scheduler.StopAsync"/>'s two bounded waits.
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/release-notes/v2026.11.0.md</c> ("Deadlines a starved <c>ThreadPool</c> does not
/// honour") measured the defect: <c>WaitAsync(TimeSpan.FromSeconds(5))</c> bounds its wait with a
/// timer, and a timer callback is <c>ThreadPool</c> work, so with every worker occupied the join
/// did not end at five seconds — it ended whenever a worker freed up, or never.
/// </para>
/// <para>
/// The wait is still non-blocking on purpose: <see cref="Scheduler.StopAsync"/> returns a pending
/// <c>ValueTask</c> while the loop is still winding down, and an existing test asserts exactly
/// that, so blocking the caller was not an option here. What changed is where the cancel comes
/// from — a dedicated watchdog thread instead of the timer.
/// </para>
/// </remarks>
[NotInParallel]
public class ShutdownDeadlineStarvationTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(500);

    /// <summary>16x the budget: past this the deadline never fired.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Documented bound in budgets: the tick-loop join gets one budget, the in-flight sink drain
    /// (skipped here — nothing is in flight) another. 2x is the floor; the rest is scheduling room.
    /// </summary>
    private const double HonouredMultiple = 5.0;

    [Test]
    public async Task StopAsync_UnderAStarvedPool_AbandonsTheTickLoopJoinAtItsBudget()
    {
        // THE SITE. The loop is a pool worker's job, so under total starvation it cannot observe
        // the cancel at all: the join has to be abandoned by the deadline, not by the loop exiting.
        var scheduler = new Scheduler(new SchedulerOptions { Clock = new FakeClock(Start) })
        {
            StopJoinBudget = Budget,
        };
        scheduler.Start();

        TimeSpan stopped;
        using (StarvedThreadPool.OccupyAll())
        {
            stopped = RunOnItsOwnThread(() =>
                scheduler.StopAsync().AsTask().GetAwaiter().GetResult()
            );
        }

        await Assert
            .That(stopped.TotalMilliseconds)
            .IsLessThanOrEqualTo(Budget.TotalMilliseconds * HonouredMultiple)
            .Because(
                $"StopAsync must abandon the join at its {Budget.TotalMilliseconds:0} ms budget; it "
                    + "returned "
                    + $"{(stopped == TimeSpan.MaxValue ? "never" : $"{stopped.TotalMilliseconds:0} ms")}"
            );
        await Assert.That(scheduler.IsRunning).IsFalse();
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
}
