namespace PicoDI.Test;

/// <summary>
/// Regression test for finding 1 of <c>review-2026-11-02/rev-gaps.md</c>: the singleton
/// fast path could hand back <b>another</b> singleton's instance.
///
/// Bug: <c>ResolveServiceCore</c> resolved the warm singleton through two independent
/// <c>Volatile.Read</c>s — <c>_lastSingletonType</c> first, <c>_lastSingletonInstance</c>
/// second — while <c>CacheSingleton</c> published those two fields with two independent
/// <c>Volatile.Write</c>s. A reader that loaded the type before another thread's publish and
/// the instance after it paired the <i>new</i> type with the <i>previous</i> type's instance:
/// it returned a perfectly valid object of the wrong type — never null, never throwing at the
/// container, so the caller either got a silent wrong type or an <c>InvalidCastException</c> at
/// its own cast. The dictionary hit does not re-publish, so each (scope, type) publishes the
/// pair exactly once — the window is every scope's first resolves, i.e. warm-up.
///
/// Shape: two long-lived workers are released together onto one fresh scope; each resolves its
/// own singleton type a few times, which forces the two-write publication of the pair and then
/// reads it back. Both singletons are pre-built, so no factory runs during the race — the only
/// thing being measured is the pair publication.
///
/// Sizing is measured, not guessed. The review measured 2 302 wrong objects in 12.8 M resolves
/// over 1.6 M fresh scopes (4 rounds: 240 / 1 164 / 91 / 807 — i.e. between 1 in 2 750 and
/// 1 in 35 000 resolves; worst round 91 in 3.2 M). At 200 000 fresh scopes x 8 resolves =
/// 1 600 000 resolves, even the <i>sparsest</i> measured rate predicts ~46 wrong objects per
/// run, so a pre-fix zero would be a ~1e-20 event. Measured on the unfixed tree here: five
/// runs gave 30 / 26 / 183 / 92 / 59 wrong objects (390 in 8.0 M resolves, 1 in 20 500; both
/// directions, never null, no exception), each run ~480 ms. After the fix the count is
/// structurally zero: the pair travels as one reference, so a torn pair cannot be observed
/// at all. (A single-core machine cannot interleave the two workers; the test is otherwise
/// deterministic because it asserts an outcome — zero wrong objects — not a timing.)
/// </summary>
[NotInParallel]
public class SingletonFastPathRaceTests
{
    public interface IA;

    public interface IB;

    private sealed class AImpl : IA;

    private sealed class BImpl : IB;

    private const int FreshScopes = 200_000;
    private const int ResolvesPerWorker = 4;

    [Test]
    public async Task FreshScopes_TwoSingletonTypes_ConcurrentlyResolved_NeverHandOutTheOtherInstance()
    {
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.Register<IA>(static _ => new AImpl(), SvcLifetime.Singleton);
        container.Register<IB>(static _ => new BImpl(), SvcLifetime.Singleton);
        container.Build();

        // Pre-build both singletons once. From here on every resolve takes the
        // "instance already exists -> publish the (type, instance) pair -> return it" path,
        // which is the publication this test tears; no factory is invoked below.
        await using (var prime = container.CreateScope())
        {
            await Assert.That(prime.GetService(typeof(IA))).IsTypeOf<AImpl>();
            await Assert.That(prime.GetService(typeof(IB))).IsTypeOf<BImpl>();
        }

        var wrong = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        long resolves = 0;
        long nulls = 0;

        SvcScope? current = null;
        var go = 0;
        var done = 0;

        Thread StartWorker(Type want)
        {
            var worker = new Thread(() =>
            {
                var seen = 0;
                while (true)
                {
                    while (Volatile.Read(ref go) <= seen)
                    {
                        if (Volatile.Read(ref done) < 0)
                            return;
                        Thread.SpinWait(30);
                    }

                    seen = Volatile.Read(ref go);
                    if (seen < 0)
                        return;

                    var scope = Volatile.Read(ref current);
                    if (scope is null)
                        return;

                    try
                    {
                        for (var k = 0; k < ResolvesPerWorker; k++)
                        {
                            try
                            {
                                var resolved = scope.GetService(want);
                                if (resolved is null)
                                    Interlocked.Increment(ref nulls);
                                else if (!want.IsInstanceOfType(resolved))
                                    wrong.AddOrUpdate(
                                        $"want {want.Name} got {resolved.GetType().Name}",
                                        1,
                                        static (_, v) => v + 1
                                    );
                            }
                            catch (Exception ex)
                            {
                                errors.Enqueue(ex);
                            }
                            finally
                            {
                                Interlocked.Increment(ref resolves);
                            }
                        }
                    }
                    finally
                    {
                        // Always published, so a throwing resolve can never strand the main
                        // loop waiting for this worker.
                        Interlocked.Increment(ref done);
                    }
                }
            })
            {
                IsBackground = true,
                Name = $"fastpath-race-{want.Name}",
            };
            worker.Start();
            return worker;
        }

        var workerA = StartWorker(typeof(IA));
        var workerB = StartWorker(typeof(IB));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            for (var i = 0; i < FreshScopes; i++)
            {
                var scope = (SvcScope)container.CreateScope();

                // Release both workers onto the same fresh scope, wait until both have run
                // their resolves, then retire the scope. Every iteration is a first
                // publication for this scope, so the pair is written twice per scope —
                // once per type — which is exactly the window the defect lives in.
                Volatile.Write(ref current, scope);
                Volatile.Write(ref done, 0);
                Volatile.Write(ref go, i + 1);
                while (Volatile.Read(ref done) < 2)
                    Thread.SpinWait(50);
                Volatile.Write(ref current, null);

                await scope.DisposeAsync();
            }
        }
        finally
        {
            Volatile.Write(ref go, -1);
            Volatile.Write(ref done, -1);
            workerA.Join(TimeSpan.FromSeconds(30));
            workerB.Join(TimeSpan.FromSeconds(30));
        }

        stopwatch.Stop();

        var wrongTotal = wrong.Values.Sum();
        var detail = string.Join(", ", wrong.Select(static kv => $"{kv.Key} x{kv.Value}"));
        Console.WriteLine(
            $"FASTPATH_RACE scopes={FreshScopes} resolves={Interlocked.Read(ref resolves)} "
                + $"elapsedMs={stopwatch.ElapsedMilliseconds} WRONG_OBJECT={wrongTotal} "
                + $"nulls={Interlocked.Read(ref nulls)} errors={errors.Count} [{detail}]"
        );

        await Assert
            .That(wrongTotal)
            .IsEqualTo(0)
            .Because(
                "the singleton fast path must never return another singleton type's instance "
                    + $"(measured mismatches: {detail})"
            );

        // The counters prove the race actually ran: both workers resolved their service in
        // every one of the scopes. Without this, a worker that died early would leave the
        // wrong-object assertion trivially (and falsely) green.
        await Assert
            .That(Interlocked.Read(ref resolves))
            .IsEqualTo((long)FreshScopes * 2 * ResolvesPerWorker);
        await Assert.That(Interlocked.Read(ref nulls)).IsEqualTo(0);
        await Assert.That(errors.Count).IsEqualTo(0);
    }
}
