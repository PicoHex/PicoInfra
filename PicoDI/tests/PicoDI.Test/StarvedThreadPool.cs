namespace PicoDI.Test;

/// <summary>
/// Occupies every <see cref="ThreadPool"/> worker for as long as it is alive — the measurement
/// harness for "is this deadline honoured when there is no worker to enforce it?".
/// </summary>
/// <remarks>
/// <para>
/// A deadline that needs a pool worker to fire (a <see cref="Timer"/> callback, and the
/// continuation behind a timer-shaped wait) is not honoured at all while this is alive; one that
/// never needs a worker is. That is the whole comparison in
/// <c>docs/release-notes/v2026.11.0.md</c> ("Deadlines a starved <c>ThreadPool</c> does not
/// honour"), reproduced on demand.
/// </para>
/// <para>
/// It caps the pool before it blocks it on purpose: an uncapped pool injects a fresh worker roughly
/// every 500 ms once every thread is blocked, which is *partial* starvation — the state that
/// stretches a deadline instead of defeating it. <see cref="OccupyAll"/> returns only once
/// <see cref="ThreadPool.GetAvailableThreads(out int, out int)"/> reads zero, and throws when it
/// cannot get there, so a probe that stopped starving the pool fails loudly instead of passing
/// quietly.
/// </para>
/// <para>
/// A test that uses it must not <c>await</c> while it is alive (there is no worker to resume it on)
/// and must dispose it before its first assertion — everything it measures happens on a thread of
/// its own. Mark such a test <see cref="NotInParallelAttribute"/>, or it starves the tests running
/// beside it.
/// </para>
/// </remarks>
internal sealed class StarvedThreadPool : IDisposable
{
    /// <summary>
    /// The pool is capped here rather than at 1 so the runtime keeps a normal shape (a pool of one
    /// is special-cased in places), and both workers are then blocked anyway.
    /// </summary>
    private const int Cap = 2;

    private readonly ManualResetEventSlim _gate = new(false);
    private readonly int _minWorkers;
    private readonly int _minIo;
    private readonly int _maxWorkers;
    private readonly int _maxIo;
    private int _disposed;

    private StarvedThreadPool(int minWorkers, int minIo, int maxWorkers, int maxIo)
    {
        _minWorkers = minWorkers;
        _minIo = minIo;
        _maxWorkers = maxWorkers;
        _maxIo = maxIo;
    }

    /// <summary>
    /// Caps the pool, blocks every worker it has, and returns once no worker is available.
    /// </summary>
    public static StarvedThreadPool OccupyAll()
    {
        ThreadPool.GetMinThreads(out var minWorkers, out var minIo);
        ThreadPool.GetMaxThreads(out var maxWorkers, out var maxIo);

        // Min first, then max: SetMaxThreads refuses a value below the current minimum.
        ThreadPool.SetMinThreads(Cap, minIo);
        ThreadPool.SetMaxThreads(Cap, minIo);

        ThreadPool.GetMaxThreads(out var cappedMax, out _);
        var workers = cappedMax <= Cap ? (int)cappedMax : Math.Max(minWorkers, Cap);

        var pool = new StarvedThreadPool(minWorkers, minIo, maxWorkers, maxIo);
        for (var i = 0; i < workers; i++)
        {
            ThreadPool.UnsafeQueueUserWorkItem(
                static gate => ((ManualResetEventSlim)gate!).Wait(),
                pool._gate
            );
        }

        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < TimeSpan.FromSeconds(10))
        {
            ThreadPool.GetAvailableThreads(out var available, out _);
            if (available == 0)
                return pool;

            Thread.Sleep(5);
        }

        pool.Dispose();
        throw new InvalidOperationException(
            $"could not occupy every ThreadPool worker ({workers} blocked, {Cap} cap): the "
                + "starvation probe would not measure anything, so it fails instead of passing"
        );
    }

    /// <summary>Releases the workers and restores the pool's original limits.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _gate.Set();

        // Max first, then min: SetMaxThreads refuses a value below the current minimum.
        ThreadPool.SetMaxThreads(_maxWorkers, _maxIo);
        ThreadPool.SetMinThreads(_minWorkers, _minIo);

        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < TimeSpan.FromSeconds(10))
        {
            ThreadPool.GetAvailableThreads(out var available, out _);
            if (available > 0)
                return;

            Thread.Sleep(5);
        }
    }
}
