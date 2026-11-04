namespace PicoLog.Tests;

[NotInParallel]
public sealed class LogEntryPoolTests
{
    [Before(Test)]
    public void ClearPool()
    {
        // Other test classes may have left entries in the shared static pool.
        // Drain it so each test starts in a known-empty state.
        while (LogEntryPool.Count > 0)
            LogEntryPool.Rent();
    }

    [Test]
    public async Task Rent_ReturnsInstance_WithDefaultValues()
    {
        var entry = LogEntryPool.Rent();

        await Assert.That(entry).IsNotNull();
        await Assert.That(entry.Timestamp).IsEqualTo(default(DateTimeOffset));
        await Assert.That(entry.Message).IsNull();
        await Assert.That(entry.Category).IsNull();
    }

    [Test]
    public async Task ReturnAndRent_ReusesInstance()
    {
        var first = LogEntryPool.Rent();
        first.Message = "hello";
        LogEntryPool.Return(first);

        var second = LogEntryPool.Rent();

        await Assert.That(ReferenceEquals(first, second)).IsTrue();
        // After Rent, the entry should be reset
        await Assert.That(second.Message).IsNull();
    }

    [Test]
    public async Task RentMultipleTimes_WhenPoolEmpty_ReturnsNewInstances()
    {
        var a = LogEntryPool.Rent();
        var b = LogEntryPool.Rent();

        await Assert.That(ReferenceEquals(a, b)).IsFalse();
    }

    [Test]
    public async Task Return_WhenPoolExceedsMaxSize_DropsExcessEntries()
    {
        // 🔴 RED: MaxPoolSize = 256 is declared but never enforced.
        // Return() enqueues unconditionally, allowing unbounded growth.
        // After fix, the pool should drop entries beyond MaxPoolSize.

        var overflow = 300; // > MaxPoolSize (256)
        var entries = new LogEntry[overflow];

        // Rent and return many entries to trigger pool overflow
        for (var i = 0; i < overflow; i++)
            entries[i] = LogEntryPool.Rent();

        for (var i = 0; i < overflow; i++)
            LogEntryPool.Return(entries[i]);

        // Assert: the pool keeps EXACTLY MaxPoolSize (256) and drops only the excess.
        // `Count <= 256` was satisfied by any bound at all: bounding at 100, at 1, or dropping
        // every entry left this test green, so the constant the name quotes was never pinned
        // (test-validity audit row 12).
        await Assert.That(LogEntryPool.Count).IsEqualTo(256);

        // Assert: the entries that survived are the ones handed back — the pool bounds its queue,
        // it does not throw the batch away.
        var pooled = LogEntryPool.Rent();
        await Assert.That(pooled).IsNotNull();
        await Assert
            .That(entries.Any(entry => ReferenceEquals(entry, pooled)))
            .IsTrue()
            .Because("Rent hands back a returned entry, not a fresh one");

        // Return it for cleanup
        LogEntryPool.Return(pooled);

        // Restore pool to empty state so other tests are not affected
        while (LogEntryPool.Count > 0)
            LogEntryPool.Rent();
    }

    // ---------------------------------------------------------------------
    // Concurrency. The pool's bound lives inside Return() as a
    // check-then-act, so the bound is only real if it holds when Return() is
    // called from several threads at the same instant. These tests are the
    // measurement of that, and none of them asserts on timing.
    //
    // The geometry that makes the race visible: the pool is filled to
    // bound - 1, and then every thread calls Return() at once. There is room
    // for exactly one of them, so the *correct* settled count is one exact
    // number, the bound — not a range and not a rate. An unguarded
    // `if (Count >= Max) return; Enqueue(...)` lets two or more callers read
    // the same stale count and all enqueue, which leaves the pool above its own
    // bound once the storm has settled.
    //
    // A plain "many threads Rent/Return in a loop" storm cannot see this bug at
    // all: every Return there is matched by that thread's own Rent, so the free
    // capacity is always at least the number of concurrent Returners and the
    // count never gets near the bound (measured: it peaks at the thread count).
    // The last slot is the only place the check-then-act can be wrong.
    // ---------------------------------------------------------------------
    private const int MaxPoolSize = 256;

    /// <summary>
    /// Re-derives the pool's bound from behaviour: hand entries back one at a
    /// time and the count stops rising exactly where <c>Return</c> starts
    /// dropping. Sequential, so the answer is deterministic, and it needs no
    /// access to the private <c>MaxPoolSize</c> constant.
    /// </summary>
    private static int MeasureCapacity()
    {
        while (LogEntryPool.Count > 0)
            LogEntryPool.Rent();

        // 1024 is a generous ceiling: a pool that never stops growing is
        // unbounded, which is its own (loud) failure.
        const int Ceiling = 1024;
        var previous = 0;
        for (var i = 0; i < Ceiling; i++)
        {
            LogEntryPool.Return(new LogEntry());
            var current = LogEntryPool.Count;
            if (current == previous)
                return current;

            previous = current;
        }

        throw new InvalidOperationException(
            $"Return never stopped growing the pool (reached >{Ceiling}): no bound is enforced."
        );
    }

    /// <summary>Sequentially normalises the pool to exactly <paramref name="target"/> entries.</summary>
    private static void FillTo(int target)
    {
        while (LogEntryPool.Count > 0)
            LogEntryPool.Rent();

        while (LogEntryPool.Count < target)
            LogEntryPool.Return(new LogEntry());
    }

    /// <summary>Records <paramref name="value"/> into <paramref name="slot"/> when it is a new maximum.</summary>
    private static void RecordMax(ref int slot, int value)
    {
        var current = Volatile.Read(ref slot);
        while (value > current)
        {
            var seen = Interlocked.CompareExchange(ref slot, value, current);
            if (seen == current)
                return;

            current = seen;
        }
    }

    /// <summary>
    /// Starts every worker and waits for all of them, so no background thread can
    /// still be touching the shared static pool when the test moves on.
    /// </summary>
    private static bool RunAll(Thread[] workers)
    {
        foreach (var worker in workers)
            worker.Start();

        var joined = true;
        foreach (var worker in workers)
            joined &= worker.Join(TimeSpan.FromSeconds(120));

        return joined;
    }

    [Test]
    public async Task Return_WhenThreadsRaceForTheLastSlot_KeepsThePoolAtItsBound()
    {
        const int Threads = 8;
        const int Rounds = 512;
        var errors = new ConcurrentQueue<Exception>();
        var perThreadMax = new int[Threads];

        var bound = MeasureCapacity();

        // Pin the constant the class declares, so "the bound" cannot drift.
        await Assert
            .That(bound)
            .IsEqualTo(MaxPoolSize)
            .Because("LogEntryPool.MaxPoolSize is 256, and Return must enforce it");

        // Three gates per round: release the storm, confirm every Return has
        // returned, then measure. Nothing writes to the pool between the second
        // gate and the read, so the value is settled, never a sampled guess.
        using var gate = new Barrier(Threads);
        var workers = new Thread[Threads];
        for (var t = 0; t < Threads; t++)
        {
            var id = t;
            workers[id] = new Thread(() =>
            {
                var observed = 0;
                for (var round = 0; round < Rounds; round++)
                {
                    // The coordinator alone prepares the pool; everyone else is
                    // parked on the gate, so each round starts at exactly bound - 1.
                    if (id == 0)
                    {
                        try
                        {
                            FillTo(bound - 1);
                        }
                        catch (Exception ex)
                        {
                            errors.Enqueue(ex);
                        }
                    }

                    gate.SignalAndWait();

                    try
                    {
                        // One free slot, Threads callers racing for it.
                        LogEntryPool.Return(new LogEntry());
                    }
                    catch (Exception ex)
                    {
                        errors.Enqueue(ex);
                    }

                    gate.SignalAndWait();

                    try
                    {
                        RecordMax(ref observed, LogEntryPool.Count);
                    }
                    catch (Exception ex)
                    {
                        errors.Enqueue(ex);
                    }

                    // Hold the next round's fill until every thread has measured.
                    gate.SignalAndWait();
                }

                perThreadMax[id] = observed;
            })
            {
                IsBackground = true,
                Name = $"LogEntryPool burst {id}",
            };
        }

        var joined = RunAll(workers);

        var settledMax = perThreadMax.Max();

        // Leave the shared static pool in the state the other tests expect,
        // whatever happened above.
        FillTo(0);

        await Assert.That(joined).IsTrue().Because("every burst worker finished within 120 s");
        await Assert
            .That(errors)
            .IsEmpty()
            .Because(
                $"Return must never throw under concurrency, but saw {errors.FirstOrDefault()}"
            );
        await Assert
            .That(settledMax)
            .IsEqualTo(bound)
            .Because(
                $"the pool has room for exactly one of {Threads} simultaneous Returns, so over "
                    + $"{Rounds} rounds the settled count must never leave {bound}"
            );
    }

    [Test]
    public async Task ReturnAndRent_WhenThePoolIsFull_HandsBackTheSameInstances()
    {
        var bound = MeasureCapacity();

        // MeasureCapacity leaves the pool full; take everything back out.
        var returned = new LogEntry[bound];
        for (var i = 0; i < bound; i++)
            returned[i] = LogEntryPool.Rent();

        await Assert.That(LogEntryPool.Count).IsEqualTo(0);

        foreach (var entry in returned)
            LogEntryPool.Return(entry);

        // The bound keeps the whole batch — it bounds the queue, it does not
        // throw the entries away.
        await Assert.That(LogEntryPool.Count).IsEqualTo(bound);

        var rentedAgain = new LogEntry[bound];
        for (var i = 0; i < bound; i++)
            rentedAgain[i] = LogEntryPool.Rent();

        var originals = new HashSet<LogEntry>(returned, ReferenceEqualityComparer.Instance);
        var reused = rentedAgain.Count(entry => originals.Contains(entry));

        await Assert
            .That(reused)
            .IsEqualTo(bound)
            .Because(
                "every Rent out of a full pool must hand back a pooled instance — a bound "
                    + "implemented by dropping the entries (or by allocating on every Rent) is not a pool"
            );
    }

    [Test]
    public async Task RentAndReturn_UnderAConcurrentStorm_NeverHandsOneEntryToTwoThreads()
    {
        // Not a measurement of the bound (see the note above: a Rent/Return storm
        // cannot reach it). This is the guard on the other half of a bounded
        // concurrent enqueue: the slot claim must never be entered twice for one
        // entry, because that would put the same instance in the queue twice and
        // hand it to two loggers at once.
        const int Threads = 8;
        const int Iterations = 20_000;
        var errors = new ConcurrentQueue<Exception>();
        var held = new ConcurrentDictionary<LogEntry, byte>(ReferenceEqualityComparer.Instance);
        var doubleHandOuts = 0;
        var perThreadRented = new int[Threads];

        using var start = new Barrier(Threads);
        var workers = new Thread[Threads];
        for (var t = 0; t < Threads; t++)
        {
            var id = t;
            workers[id] = new Thread(() =>
            {
                var rented = 0;
                start.SignalAndWait();
                try
                {
                    for (var i = 0; i < Iterations; i++)
                    {
                        var entry = LogEntryPool.Rent();
                        rented++;

                        // An entry may not be in two hands at once. `held` is the
                        // set of instances currently held by some thread.
                        if (!held.TryAdd(entry, 0))
                            Interlocked.Increment(ref doubleHandOuts);

                        entry.Message = "storm";

                        held.TryRemove(entry, out _);
                        LogEntryPool.Return(entry);
                    }
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                }

                perThreadRented[id] = rented;
            })
            {
                IsBackground = true,
                Name = $"LogEntryPool storm {id}",
            };
        }

        var joined = RunAll(workers);

        FillTo(0);

        await Assert.That(joined).IsTrue().Because("every storm worker finished within 120 s");
        await Assert
            .That(errors)
            .IsEmpty()
            .Because(
                $"Rent/Return must never throw under concurrency, but saw {errors.FirstOrDefault()}"
            );
        await Assert
            .That(perThreadRented.Min())
            .IsEqualTo(Iterations)
            .Because("every worker completed its full Rent/Return cycle");
        await Assert
            .That(doubleHandOuts)
            .IsEqualTo(0)
            .Because(
                "one entry handed to two threads is a double enqueue, and it would show up here"
            );
        await Assert
            .That(held.IsEmpty)
            .IsTrue()
            .Because("every instance a worker rented was given back exactly once");
    }
}
