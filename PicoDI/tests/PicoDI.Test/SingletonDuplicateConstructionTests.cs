namespace PicoDI.Test;

/// <summary>
/// Pins behaviour 1 of the threading/lifetime pair: <b>singleton construction is not single-flight</b>.
/// <para>
/// Concurrent first resolves of one unbuilt singleton all miss it and all run the factory. Measured
/// on this tree with a gate inside the factory (every build held before it can be published, 2/4/8/16
/// threads, three trials each): the factory ran exactly once per thread — 2, 4, 8 and 16 calls — every
/// call produced a distinct instance, exactly <i>one</i> instance was returned to all callers, and
/// <c>threads - 1</c> duplicates were disposed (1, 3, 7, 15). That exactness is the gate's doing, which
/// is why the assertion below pins the count to <c>Threads</c> rather than to "more than one".
/// </para>
/// <para>
/// With the plain barrier-only race a caller actually hits, the count is scheduler- and
/// machine-dependent — 20 trials per size measured 1–2 calls at 2 threads (18/20 over one) and, at
/// 4/8/16 threads, 2–4 / 2–5 / 3–7 on the reviewer's machine (20/20 over one). So that race is
/// described here, not pinned: it only ever guarantees "more than one", which is precisely the weak
/// pin this test does not use.
/// </para>
/// <para>
/// The duplicate build is deliberate, not a leak: the factory runs outside
/// <c>SvcRuntimeSingletonState.SyncLock</c> so a factory that transitively resolves a service depending
/// on it cannot self-deadlock (the classic double-checked-locking deadlock), so two threads can be
/// inside it at once. The first to take the lock publishes; each loser's copy is disposed before its
/// caller returns the published instance — the duplicate may already hold a handle, so it must be
/// released, and it is never handed out. A singleton factory must therefore be idempotent and free of
/// irreversible side effects, and only one result is ever published. <c>PicoDI/README.md</c> (Thread
/// Safety) states this; this test is the tripwire for making construction single-flight.
/// </para>
/// </summary>
[NotInParallel]
public class SingletonDuplicateConstructionTests
{
    private const int Threads = 8;

    /// <summary>Counts its own disposals, so "the duplicates were disposed" is an instance fact.</summary>
    private sealed class Built : IDisposable
    {
        public int DisposeCount;

        public void Dispose() => Interlocked.Increment(ref DisposeCount);
    }

    [Test]
    public async Task ConcurrentFirstResolve_OneFactoryCallPerThread_OneInstancePublished_ExtrasDisposed()
    {
        var container = new SvcContainer(autoConfigureFromGenerator: false);
        using var gate = new ManualResetEventSlim(false);
        var built = new System.Collections.Concurrent.ConcurrentBag<Built>();
        var returned = new System.Collections.Concurrent.ConcurrentBag<Built>();
        var factoryCalls = 0;
        var entered = 0;
        var completed = 0;

        container.RegisterSingleton<Built>(_ =>
        {
            var candidate = new Built();
            built.Add(candidate);
            Interlocked.Increment(ref factoryCalls);
            Interlocked.Increment(ref entered);

            // Hold every build before it can be published. A caller cannot publish a singleton it
            // has not returned from the factory yet, so this turns "the threads were probably
            // aligned" into "every thread that is going to build is already building" — the
            // duplicate count below is then a fact about the code, not about the scheduler.
            gate.Wait(TimeSpan.FromSeconds(30));
            return candidate;
        });
        container.Build();

        var scopes = new ISvcScope[Threads];
        for (var i = 0; i < Threads; i++)
            scopes[i] = container.CreateScope();

        using var start = new Barrier(Threads);
        var workers = new Thread[Threads];
        for (var i = 0; i < Threads; i++)
        {
            var scope = scopes[i];
            workers[i] = new Thread(() =>
            {
                start.SignalAndWait();
                returned.Add(scope.GetService<Built>());
                Interlocked.Increment(ref completed);
            })
            {
                IsBackground = true,
                Name = $"singleton-duplicate-{i}",
            };
            workers[i].Start();
        }

        // Liveness, not timing: while the gate is held nobody can publish, so each worker is either
        // sitting in the factory or already finished and the sum reaches Threads either way.
        var aligned = SpinWait.SpinUntil(
            () => Volatile.Read(ref entered) + Volatile.Read(ref completed) >= Threads,
            TimeSpan.FromSeconds(30)
        );
        gate.Set();
        foreach (var worker in workers)
            worker.Join(TimeSpan.FromSeconds(30));

        try
        {
            var calls = Volatile.Read(ref factoryCalls);
            var published = returned.Distinct().ToArray();
            var duplicatesDisposed = built.Count(static b => Volatile.Read(ref b.DisposeCount) > 0);

            // The pin: construction is neither single-flight nor partly serialised, so every thread
            // that missed the unbuilt singleton runs the factory. The gate in the factory above is
            // what makes the count exact — it holds each build until every caller is either inside
            // the factory or already finished, so all Threads callers are inside at once and each of
            // them builds. "More than one" would not be enough: a regression that serialises even
            // some callers (e.g. a lock taken on the even-numbered builds) halves the count and would
            // still satisfy > 1, while this equality goes red.
            await Assert
                .That(calls)
                .IsEqualTo(Threads)
                .Because(
                    $"every thread that misses the unbuilt singleton must run the factory; the "
                        + $"in-factory gate holds each build until all callers are inside, so the "
                        + $"count is exactly the thread count (measured {calls} factory calls for "
                        + $"{Threads} threads)"
                );

            // ... but only one of those builds is ever published, and every caller gets it.
            await Assert
                .That(published.Length)
                .IsEqualTo(1)
                .Because("exactly one instance may be published, however many were built");
            await Assert
                .That(returned.Count(r => ReferenceEquals(r, published[0])))
                .IsEqualTo(Threads)
                .Because("every caller must receive the published instance, never a loser's copy");

            // ... and each loser's copy is disposed, so the duplicates do not leak.
            await Assert
                .That(duplicatesDisposed)
                .IsEqualTo(calls - 1)
                .Because($"every duplicate build must be disposed ({calls} built, one published)");
            await Assert
                .That(Volatile.Read(ref published[0].DisposeCount))
                .IsEqualTo(0)
                .Because("the published instance is container-owned, not a duplicate");

            await Assert
                .That(aligned)
                .IsTrue()
                .Because(
                    "all workers must reach the factory or finish; otherwise the race never ran"
                );
        }
        finally
        {
            foreach (var scope in scopes)
                await scope.DisposeAsync();
        }

        // The one survivor is owned by the container and released with it.
        await container.DisposeAsync();
        await Assert
            .That(Volatile.Read(ref returned.First().DisposeCount))
            .IsEqualTo(1)
            .Because("the published singleton is disposed once, by the container");
    }
}
