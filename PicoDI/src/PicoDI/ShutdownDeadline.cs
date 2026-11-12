namespace PicoDI;

/// <summary>
/// One shutdown deadline, enforced without the <see cref="ThreadPool"/> — the two shapes the six
/// shutdown call sites in this repo need, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this type exists.</b> <c>new CancellationTokenSource(timeout)</c>,
/// <see cref="Task.WaitAsync(TimeSpan)"/> and <see cref="SemaphoreSlim"/>'s timeout overload all
/// enforce their budget with a <see cref="Timer"/>, and a timer callback is
/// <see cref="ThreadPool"/> work: the queue only *notices* that the deadline passed, a worker must
/// run the cancel and another must resume the await behind it. Under total pool starvation the wait
/// therefore does not end at all, and a partially starved pool stretches the same budget without
/// bound (measured on this repo: a 500 ms budget still waiting ~20x later — see
/// <c>docs/release-notes/v2026.11.0.md</c>, "Deadlines a starved ThreadPool does not honour").
/// The three same-shaped templates already in the tree — <c>FlushQuiesceCoordinator</c>
/// (<see cref="Stopwatch"/> + <c>waitTask.Wait(remaining)</c>), <c>InternalLoggerQueue</c>
/// (<see cref="Thread.Sleep(int)"/> in a <see cref="Stopwatch"/>-bounded loop) and <c>FileSink</c>
/// (no timer at all) — are the algorithm borrowed here.
/// </para>
/// <para>
/// <b>Shape 1 — blocking, for a site that owns the calling thread.</b>
/// <see cref="WaitOrAbandon(Task, TimeSpan)"/> and
/// <see cref="WaitOrAbandon(SemaphoreSlim, TimeSpan)"/> wait on the caller's own thread, bounded by
/// a <see cref="Stopwatch"/>: the thread that waits is the thread that times out, so no worker has
/// to be scheduled for the deadline to fire. For a <see cref="SemaphoreSlim"/> this is the only
/// spelling that survives a starved pool — measured: <c>WaitAsync(token)</c> never returned while
/// <c>Wait(timeout)</c> returned at 1.08x the budget (538 ms of 500 ms).
/// </para>
/// <para>
/// <b>Shape 2 — escalating, for a site that must not block its caller.</b>
/// <see cref="CancelAt(TimeSpan)"/> returns a <see cref="DeadlineSignal"/> whose
/// <see cref="DeadlineSignal.Token"/> is cancelled by a dedicated watchdog <see cref="Thread"/>
/// watching a <see cref="Stopwatch"/> — never a <see cref="Timer"/>, never
/// <see cref="Task.Delay(TimeSpan)"/>. An <c>await task.WaitAsync(signal.Token)</c> then completes
/// on the watchdog thread: the continuation behind the cancelled wait is inlined onto the thread
/// that performed the cancel instead of being queued to a pool with no worker to give, which is
/// what lets an <c>await</c>-shaped <c>DisposeAsync</c> return at its budget under total
/// starvation. Measured on this repo: the same wait never returned with the timer and returned at
/// 1.03x its budget with the watchdog (514 ms of 500 ms).
/// </para>
/// <para>
/// <b>One watchdog thread, not one per call.</b> Every armed deadline of this package shares a
/// single watchdog <see cref="Thread"/>. It is started by the first deadline that is armed and it
/// exits as soon as no deadline is outstanding, so a process that is not shutting down holds no
/// extra thread, and N shutdowns in a row cost one thread rather than N. One thread instead of one
/// per call also means the deadline of a *later* site is not starved by the shutdown that is still
/// running. The cost of sharing is head-of-line: a cancellation callback that blocks on the
/// watchdog thread delays the deadlines behind it — the same hazard a <see cref="Timer"/> callback
/// has today, minus the starved pool. A callback that throws is observed and dropped: on the timer
/// path it would be an unhandled exception on a pool thread, i.e. a process kill, and one broken
/// callback must not take the watchdog (and with it every later deadline) down.
/// </para>
/// <para>
/// <b>Deliberate copy: this file exists four times.</b> The four packages that need it —
/// <c>PicoDI</c>, <c>PicoLog</c>, <c>PicoCfg</c> and <c>PicoSchedule</c> — share no assembly to put
/// it in. Each references only its own <c>*.Abs</c> package (<c>PicoDI.Abs</c>, <c>PicoLog.Abs</c>,
/// <c>PicoCfg.Abs</c>), those <c>*.Abs</c> packages reference each other not at all, and
/// <c>PicoSchedule</c> references nothing whatsoever ("zero PackageReference by design",
/// <c>PicoSchedule.csproj</c>). A new internal shared project is the two ways it cannot go:
/// <c>PicoLog.csproj</c>'s <c>PicoLogCoreDeclaresNoSerializer</c> target fails the build on any
/// project reference but <c>..\PicoLog.Abs\PicoLog.Abs.csproj</c> (and a project reference becomes
/// a shipped <c>&lt;dependency&gt;</c> for every consumer), and PicoSchedule's zero-dependency rule
/// rejects one outright. So: one small file per package, byte-identical apart from this namespace
/// line — the four copies are *the* drift audit, <c>diff</c> them and one line shows. Every shape
/// is present in every copy even where a package uses only one, which is the price of that
/// identity.
/// </para>
/// <para>
/// <b>AOT/trimming:</b> no reflection, no dynamic code, no timer, no dependency outside the shared
/// framework — <see cref="Thread"/>, <see cref="Monitor"/> and <see cref="Stopwatch"/> only.
/// Nothing here is public; the shipped API surface of every package is unchanged.
/// </para>
/// </remarks>
internal static class ShutdownDeadline
{
    /// <summary>
    /// Guards <see cref="Pending"/> and <see cref="_watchdog"/>, and is the monitor the watchdog
    /// parks on between deadlines. A plain <see cref="object"/> rather than the project's usual
    /// <see cref="Lock"/> because <see cref="Monitor.Wait(object, TimeSpan)"/> is the pool-free
    /// wait this type is about.
    /// </summary>
    private static readonly object Gate = new();

    /// <summary>Deadlines armed and not yet fired or disposed. Guarded by <see cref="Gate"/>.</summary>
    private static readonly List<DeadlineSignal> Pending = [];

    /// <summary>The shared watchdog, or <see langword="null"/> when none is running. Guarded by <see cref="Gate"/>.</summary>
    private static Thread? _watchdog;

    /// <summary>
    /// <see langword="true"/> while the shared watchdog thread is alive — the observable behind
    /// "one thread, not one per call": the deadline that is satisfied goes back to
    /// <see langword="false"/>, so an idle process holds no extra thread, and a test can say so.
    /// </summary>
    internal static bool WatchdogIsRunning
    {
        get
        {
            lock (Gate)
                return _watchdog is not null;
        }
    }

    /// <summary>
    /// Blocks the calling thread until <paramref name="task"/> completes or <paramref name="budget"/>
    /// expires, measuring the budget on a <see cref="Stopwatch"/> rather than with a timer.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the task completed inside the budget;
    /// <see langword="false"/> when the budget expired first — the task is still running, nothing
    /// cancels it, and the failure path belongs to the caller (the same position
    /// <c>await task.WaitAsync(deadline)</c> put it in).
    /// </returns>
    /// <exception cref="Exception">
    /// The task's own exception, unwrapped, when it faulted inside the budget.
    /// <see cref="Task.Wait(TimeSpan)"/> would hand back an <see cref="AggregateException"/>; an
    /// <c>await</c> of the same task throws the task's own exception, so that is what is rethrown
    /// here — swapping the wait must not swap the exception a caller sees.
    /// </exception>
    public static bool WaitOrAbandon(Task task, TimeSpan budget)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (!task.IsCompleted)
        {
            var start = Stopwatch.GetTimestamp();
            while (true)
            {
                var remaining =
                    budget == Timeout.InfiniteTimeSpan
                        ? Timeout.InfiniteTimeSpan
                        : budget - Stopwatch.GetElapsedTime(start);
                if (remaining <= TimeSpan.Zero)
                    return false;

                bool completed;
                try
                {
                    completed = task.Wait(remaining);
                }
                catch (AggregateException ex)
                {
                    ExceptionDispatchInfo.Capture(ex.InnerException ?? ex).Throw();
                    throw; // unreachable: Throw() never returns
                }

                if (completed)
                    break;
            }
        }

        // Completed inside the budget: surface a fault or a cancellation exactly as awaiting it would.
        if (task.IsFaulted || task.IsCanceled)
            task.GetAwaiter().GetResult();

        return true;
    }

    /// <summary>
    /// Blocks the calling thread until <paramref name="gate"/> is entered or
    /// <paramref name="budget"/> expires. The pool-free half of
    /// <see cref="SemaphoreSlim.WaitAsync(TimeSpan)"/>: <see cref="SemaphoreSlim.Wait(TimeSpan)"/>
    /// blocks the calling thread on a monitor and times out against the clock, so the deadline does
    /// not need a worker to fire.
    /// </summary>
    /// <returns><see langword="true"/> when the gate was entered, <see langword="false"/> on timeout.</returns>
    /// <exception cref="ObjectDisposedException">The gate was disposed — reporting it is the caller's job.</exception>
    public static bool WaitOrAbandon(SemaphoreSlim gate, TimeSpan budget)
    {
        ArgumentNullException.ThrowIfNull(gate);

        var start = Stopwatch.GetTimestamp();
        while (true)
        {
            var remaining =
                budget == Timeout.InfiniteTimeSpan
                    ? Timeout.InfiniteTimeSpan
                    : budget - Stopwatch.GetElapsedTime(start);
            if (remaining <= TimeSpan.Zero)
                return false;

            if (gate.Wait(remaining))
                return true;
        }
    }

    /// <summary>
    /// Arms a deadline that fires <paramref name="budget"/> from now on a dedicated watchdog thread
    /// instead of on a timer. <see cref="Timeout.InfiniteTimeSpan"/> arms nothing at all: the
    /// returned <see cref="DeadlineSignal.Token"/> is <see cref="CancellationToken.None"/> and
    /// <see cref="DeadlineSignal.IsExpired"/> stays <see langword="false"/>, which is the
    /// "unbounded" convention the shutdown options use for <see cref="TimeSpan.Zero"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A negative budget other than <see cref="Timeout.InfiniteTimeSpan"/>, matching the
    /// <see cref="CancellationTokenSource"/> constructor this replaces.
    /// </exception>
    public static DeadlineSignal CancelAt(TimeSpan budget) => new(budget);

    /// <summary>Adds a deadline to the shared list, starting the watchdog if it is not running.</summary>
    internal static void Schedule(DeadlineSignal signal, long dueTimestamp)
    {
        lock (Gate)
        {
            signal.DueTimestamp = dueTimestamp;
            Pending.Add(signal);

            if (_watchdog is not null)
            {
                // Possibly sleeping on a later deadline than the one just added: re-read the list.
                Monitor.Pulse(Gate);
                return;
            }

            _watchdog = new Thread(Pump)
            {
                IsBackground = true,
                Name = "PicoHex shutdown deadline watchdog",
            };
            _watchdog.Start();
        }
    }

    /// <summary>Removes a deadline that was satisfied (or disposed) before it fired.</summary>
    internal static void Unschedule(DeadlineSignal signal)
    {
        lock (Gate)
        {
            if (Pending.Remove(signal))
                Monitor.Pulse(Gate);
        }
    }

    /// <summary>
    /// The watchdog: sleeps on <see cref="Gate"/> until the earliest outstanding deadline is due,
    /// fires every deadline that has come due, and exits when none is left.
    /// </summary>
    private static void Pump()
    {
        var due = new List<DeadlineSignal>();

        while (true)
        {
            lock (Gate)
            {
                while (true)
                {
                    if (Pending.Count == 0)
                    {
                        // Idle: exit, and let the next Schedule start a fresh thread.
                        _watchdog = null;
                        return;
                    }

                    var now = Stopwatch.GetTimestamp();
                    var next = long.MaxValue;
                    for (var i = 0; i < Pending.Count; i++)
                    {
                        if (Pending[i].DueTimestamp < next)
                            next = Pending[i].DueTimestamp;
                    }

                    if (next > now)
                    {
                        var wait = Stopwatch.GetElapsedTime(now, next);
                        // Monitor.Wait takes a TimeSpan but rejects anything above int.MaxValue ms.
                        if (wait.TotalMilliseconds > int.MaxValue - 1)
                            wait = TimeSpan.FromMilliseconds(int.MaxValue - 1);
                        Monitor.Wait(Gate, wait);
                        continue;
                    }

                    for (var i = Pending.Count - 1; i >= 0; i--)
                    {
                        if (Pending[i].DueTimestamp > now)
                            continue;
                        due.Add(Pending[i]);
                        Pending.RemoveAt(i);
                    }

                    break;
                }
            }

            // Outside the lock: Cancel() runs every registered callback inline, and a callback is
            // allowed to arm a deadline of its own (which takes Gate).
            for (var i = 0; i < due.Count; i++)
                due[i].Fire();
            due.Clear();
        }
    }
}

/// <summary>
/// One armed deadline: the handle <see cref="ShutdownDeadline.CancelAt(TimeSpan)"/> returns.
/// </summary>
/// <remarks>
/// Dispose it once the phase it bounds is over — that both releases the
/// <see cref="CancellationTokenSource"/> and unarms the deadline, so a shutdown that finishes early
/// does not leave the watchdog thread alive for the rest of the budget.
/// </remarks>
internal sealed class DeadlineSignal : IDisposable
{
    private readonly CancellationTokenSource? _source;
    private readonly long _start;
    private readonly TimeSpan _budget;
    private int _disposed;

    internal DeadlineSignal(TimeSpan budget)
    {
        if (budget < TimeSpan.Zero && budget != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(budget));

        _budget = budget;
        _start = Stopwatch.GetTimestamp();

        if (budget == Timeout.InfiniteTimeSpan)
            return; // no deadline: nothing armed, no thread started, Token is CancellationToken.None

        _source = new CancellationTokenSource();
        ShutdownDeadline.Schedule(this, DueTimestampFor(_start, budget));
    }

    /// <summary>The signal handed to the work being bounded — the same token shape the CTS gave it.</summary>
    public CancellationToken Token => _source?.Token ?? CancellationToken.None;

    /// <summary>
    /// <see langword="true"/> once the budget has been consumed, read from the <see cref="Stopwatch"/>
    /// and therefore true at the deadline even when the watchdog has not run yet — the pool-free
    /// replacement for <c>token.IsCancellationRequested</c> on the "had it already expired?" check.
    /// </summary>
    /// <remarks>
    /// A zero budget reads expired immediately, which is stricter than
    /// <c>new CancellationTokenSource(TimeSpan.Zero)</c> was (that cancelled on the next tick of the
    /// timer, never synchronously, so the check there was racy and this one is not).
    /// </remarks>
    public bool IsExpired => _source is not null && Stopwatch.GetElapsedTime(_start) >= _budget;

    /// <summary>When the watchdog should fire this signal, as a <see cref="Stopwatch"/> timestamp.</summary>
    internal long DueTimestamp { get; set; }

    /// <summary>
    /// Performs the cancel. Called by the watchdog thread only, and never while it holds the pump
    /// lock: <see cref="CancellationTokenSource.Cancel()"/> runs the registered callbacks inline,
    /// which is exactly the point — the continuations waiting on the token are inlined onto this
    /// thread instead of being queued to the starved pool.
    /// </summary>
    internal void Fire()
    {
        var source = _source;
        if (source is null)
            return;

        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Disposed while its deadline was still armed: nothing left to cancel.
        }
        catch (AggregateException)
        {
            // A callback threw. On the timer path this would be an unhandled exception on a pool
            // thread; here it must not take the watchdog — and with it every later deadline — down.
            // The callback's owner owns its own error reporting.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        ShutdownDeadline.Unschedule(this);
        _source?.Dispose();
    }

    private static long DueTimestampFor(long start, TimeSpan budget)
    {
        if (budget <= TimeSpan.Zero)
            return start; // already due: fires on the watchdog's next pass

        var ticks = budget.TotalSeconds * Stopwatch.Frequency;
        return ticks >= long.MaxValue - start ? long.MaxValue : start + (long)ticks;
    }
}
