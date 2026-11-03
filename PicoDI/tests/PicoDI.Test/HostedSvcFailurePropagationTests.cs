namespace PicoDI.Test;

/// <summary>
/// review-2026-11-02 finding 2: <c>StartAsync</c>/<c>StopAsync</c> used to swallow every
/// exception into <c>OnError</c> (default <see langword="null"/>), so a hosted service that
/// failed to start reported success to the caller, still received <c>StopAsync</c>, and the
/// services after it were still started.
/// </summary>
/// <remarks>
/// <para>
/// Contract asserted here — the .NET host contract (<c>IHost.StartAsync</c> faults):
/// </para>
/// <list type="bullet">
/// <item>a start failure faults the caller's <c>StartAsync</c> with the service's own exception
/// (same type, same message, not wrapped), so <c>catch (InvalidOperationException)</c> still works;</item>
/// <item>the failure aborts the start sequence — services registered after the failing one are
/// not started;</item>
/// <item>the services that already started are stopped in the exact reverse of the order they
/// started (LIFO) before the exception reaches the caller — a failed start must not leave
/// background work running behind a host the caller was told to throw away;</item>
/// <item>the failing service does <b>not</b> receive <c>StopAsync</c> (its <c>StartAsync</c> did
/// not complete — the same rule as <c>Microsoft.Extensions.Hosting</c>);</item>
/// <item>the host is left <i>stopped</i>, not <i>started</i>: a later <c>StopAsync</c> or
/// <c>DisposeAsync</c> does not stop anything a second time;</item>
/// <item>the diagnostic path stays intact — <c>OnError</c> still reports every suppressed
/// failure, it is just no longer the only channel.</item>
/// </list>
/// </remarks>
public class HostedSvcFailurePropagationTests
{
    /// <summary>StartAsync throws <see cref="InvalidOperationException"/> with message "boom".</summary>
    private sealed class BoomOnStartSvc : IHostedSvc
    {
        private readonly string _name;
        private readonly List<string> _log;

        public BoomOnStartSvc(string name, List<string> log)
        {
            _name = name;
            _log = log;
        }

        public Task StartAsync(CancellationToken ct)
        {
            _log.Add($"start:{_name}");
            throw new InvalidOperationException("boom");
        }

        public Task StopAsync(CancellationToken ct)
        {
            _log.Add($"stop:{_name}");
            return Task.CompletedTask;
        }
    }

    /// <summary>StopAsync throws <see cref="ApplicationException"/> with message "boom-stop".</summary>
    private sealed class BoomOnStopSvc(string name, List<string> log) : IHostedSvc
    {
        public Task StartAsync(CancellationToken ct)
        {
            log.Add($"start:{name}");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            log.Add($"stop:{name}");
            throw new ApplicationException("boom-stop");
        }
    }

    /// <summary>A well-behaved hosted service that records every phase it receives.</summary>
    private class RecordingSvc(string name, List<string> log) : IHostedSvc
    {
        public Task StartAsync(CancellationToken ct)
        {
            log.Add($"start:{name}");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            log.Add($"stop:{name}");
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Same behaviour, distinct type: the container tracks one registration per hosted
    /// <em>type</em> (the last one wins), so two recording services need two types.
    /// </summary>
    private sealed class RecordingSvcTwo(string name, List<string> log) : RecordingSvc(name, log);

    /// <summary>Third distinct recording type, for "the service after the failing one".</summary>
    private sealed class RecordingSvcThree(string name, List<string> log) : RecordingSvc(name, log);

    private sealed class ErrorRecorder
    {
        private readonly Lock _gate = new();

        public List<(Exception Error, string Context)> Entries { get; } = [];

        public void Record(Exception error, string context)
        {
            lock (_gate)
                Entries.Add((error, context));
        }
    }

    // ------------------------------------------------------------------
    // Defect 1 — a start failure must be observable by the caller.
    // ------------------------------------------------------------------

    [Test]
    public async Task StartAsync_HostedSvcThrows_FaultsTheCallerWithTheOriginalException()
    {
        var log = new List<string>();
        var errors = new ErrorRecorder();

        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.OnError = errors.Record;
        container.RegisterHostedSvc<RecordingSvc>(_ => new RecordingSvc("one", log));
        container.RegisterHostedSvc<BoomOnStartSvc>(_ => new BoomOnStartSvc("boom", log));
        container.RegisterHostedSvc<RecordingSvcThree>(_ => new RecordingSvcThree("three", log));
        container.Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SvcHost(container).StartAsync()
        );

        // The service's own exception, not a wrapper: type and message survive.
        await Assert.That(ex.Message).IsEqualTo("boom");

        // The start sequence aborted: "three" was never started.
        await Assert.That(log.Contains("start:three")).IsFalse();

        // Diagnostic path intact — OnError still names the phase and the service.
        await Assert.That(errors.Entries.Count).IsEqualTo(1);
        await Assert.That(errors.Entries[0].Error).IsSameReferenceAs(ex);
        await Assert
            .That(errors.Entries[0].Context.Contains("Start phase", StringComparison.Ordinal))
            .IsTrue();
        await Assert
            .That(
                errors.Entries[0].Context.Contains(nameof(BoomOnStartSvc), StringComparison.Ordinal)
            )
            .IsTrue();
    }

    [Test]
    public async Task StartAsync_HostedSvcThrows_StopsTheAlreadyStartedServicesInReverseOrder()
    {
        var log = new List<string>();

        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterHostedSvc<RecordingSvc>(_ => new RecordingSvc("one", log));
        container.RegisterHostedSvc<RecordingSvcTwo>(_ => new RecordingSvcTwo("two", log));
        container.RegisterHostedSvc<BoomOnStartSvc>(_ => new BoomOnStartSvc("boom", log));
        container.Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SvcHost(container).StartAsync()
        );

        // Started in registration order, rolled back LIFO. The failing service is not
        // stopped: its StartAsync never completed.
        await Assert.That(log.Count).IsEqualTo(5);
        await Assert.That(log[0]).IsEqualTo("start:one");
        await Assert.That(log[1]).IsEqualTo("start:two");
        await Assert.That(log[2]).IsEqualTo("start:boom");
        await Assert.That(log[3]).IsEqualTo("stop:two");
        await Assert.That(log[4]).IsEqualTo("stop:one");
    }

    [Test]
    public async Task StartAsync_HostedSvcThrows_LeavesTheHostStopped_SoDisposeDoesNotStopTwice()
    {
        var log = new List<string>();

        var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterHostedSvc<RecordingSvc>(_ => new RecordingSvc("one", log));
        container.RegisterHostedSvc<BoomOnStartSvc>(_ => new BoomOnStartSvc("boom", log));
        container.Build();
        var host = new SvcHost(container);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        // The one-shot was consumed by the failed attempt: neither a later StartAsync nor
        // DisposeAsync runs another phase on any service.
        await host.StartAsync();
        await Assert.That(log.Count).IsEqualTo(3); // start:one, start:boom, stop:one

        await container.DisposeAsync();
        await Assert.That(log.Count).IsEqualTo(3);
        await Assert.That(log[2]).IsEqualTo("stop:one");
    }

    // ------------------------------------------------------------------
    // Same finding, stop half — a failing StopAsync must not be invisible.
    // ------------------------------------------------------------------

    [Test]
    public async Task StopAsync_HostedSvcThrows_FaultsTheCallerWithAnAggregate()
    {
        var log = new List<string>();
        var errors = new ErrorRecorder();

        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.OnError = errors.Record;
        container.RegisterHostedSvc<BoomOnStopSvc>(_ => new BoomOnStopSvc("boom", log));
        container.RegisterHostedSvc<RecordingSvc>(_ => new RecordingSvc("two", log));
        container.Build();
        var host = new SvcHost(container);

        await host.StartAsync();

        var ex = await Assert.ThrowsAsync<AggregateException>(() => host.StopAsync());

        // Every service is still stopped (shutdown stays fault-tolerant) and the caller
        // gets the failures instead of silence — LIFO: "two" before "boom".
        await Assert.That(log.Count).IsEqualTo(4);
        await Assert.That(log[2]).IsEqualTo("stop:two");
        await Assert.That(log[3]).IsEqualTo("stop:boom");

        await Assert.That(ex.InnerExceptions.Count).IsEqualTo(1);
        await Assert.That(ex.InnerExceptions[0].Message).IsEqualTo("boom-stop");
        await Assert.That(errors.Entries.Count).IsEqualTo(1);
        await Assert
            .That(errors.Entries[0].Context.Contains("Stop phase", StringComparison.Ordinal))
            .IsTrue();
    }

    [Test]
    public async Task SvcHostBuilder_BuildAsync_HostedSvcThrows_FaultsInsteadOfHandingBackAStartedHost()
    {
        var log = new List<string>();

        var builder = new SvcHostBuilder(autoConfigureFromGenerator: false);
        builder.ConfigureServices(container =>
        {
            container.RegisterHostedSvc<RecordingSvc>(_ => new RecordingSvc("one", log));
            container.RegisterHostedSvc<BoomOnStartSvc>(_ => new BoomOnStartSvc("boom", log));
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAsync());

        await Assert.That(ex.Message).IsEqualTo("boom");
        // BuildAsync disposes the container on failure, which means the rollback only ran once.
        await Assert.That(log.Count).IsEqualTo(3); // start:one, start:boom, stop:one

        await builder.DisposeAsync();
    }

    // ------------------------------------------------------------------
    // L5 — the stop deadline is real, and the rollback's failures are read.
    // ------------------------------------------------------------------

    /// <summary>The key this test family pins on <see cref="Exception.Data"/>.</summary>
    private const string RollBackStopFailuresKey = "PicoDI.HostedSvcRollBackStopFailures";

    /// <summary>Short enough to keep the suite fast; the production default stays 30 s.</summary>
    private static readonly TimeSpan TestStopDeadline = TimeSpan.FromMilliseconds(200);

    /// <summary>Starts cleanly; <c>StopAsync</c> ignores its token and never completes.</summary>
    private sealed class HangingOnStopSvc : IHostedSvc
    {
        private readonly TaskCompletionSource<bool> _never = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public bool StopEntered { get; private set; }

        /// <summary>How many times the stop sequence was entered — the retry assertion reads it.</summary>
        public int StopCalls { get; private set; }

        /// <summary>The token the stop phase was handed, for tests that inspect it afterwards.</summary>
        public CancellationToken StopToken { get; private set; }

        /// <summary>Lets the abandoned stop task finish, so the test leaves nothing behind.</summary>
        public void Release() => _never.TrySetResult(true);

        /// <summary>Faults the abandoned stop task after the pass walked away from it.</summary>
        public void Fail(Exception error) => _never.TrySetException(error);

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct)
        {
            StopEntered = true;
            StopToken = ct;
            StopCalls++;
            return _never.Task; // deliberately ignores ct
        }
    }

    [Test]
    public async Task DisposeAsync_HostedSvcIgnoringTheStopToken_AbandonsTheStopAtTheDeadline()
    {
        // review-2026-11-02 defect L5: a CancellationToken only signals, so the 30 s CTS bounded
        // nothing — a service that ignores it hung DisposeAsync forever, exactly what the comment
        // claimed it prevented. The window is now a real wait: the phase is abandoned at the
        // deadline, reported as a TimeoutException, and the rest of the disposal still runs.
        var errors = new ErrorRecorder();
        var svc = new HangingOnStopSvc();
        var container = new SvcContainer(autoConfigureFromGenerator: false)
        {
            HostedSvcStopTimeout = TestStopDeadline,
        };
        container.OnError = errors.Record;
        container.RegisterHostedSvc<HangingOnStopSvc>(_ => svc);
        container.Build();
        var host = new SvcHost(container);
        await host.StartAsync();

        // The 10 s guard is the assertion: without a real deadline this await never returns.
        await container.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(svc.StopEntered).IsTrue();

        // Two reports, and they are the two that exist on this path: the phase-level TimeoutException
        // from StopPhaseAsync and the disposal wrapper's AggregateException carrying it. Exactly one
        // TimeoutException, and it is the first (the phase) entry.
        var timeouts = errors.Entries.Count(e => e.Error is TimeoutException);
        await Assert.That(timeouts).IsEqualTo(1);
        await Assert.That(errors.Entries[0].Error).IsTypeOf<TimeoutException>();
        await Assert
            .That(errors.Entries[0].Error.Message.Contains("deadline", StringComparison.Ordinal))
            .IsTrue();
        await Assert
            .That(errors.Entries[0].Context.Contains("Stop phase", StringComparison.Ordinal))
            .IsTrue();

        svc.Release();
    }

    [Test]
    public async Task StartAsync_RollbackStopIgnoringTheToken_AbandonsItAndReportsItOnTheStartException()
    {
        // Same deadline on the other container-initiated pass: the rollback of a failed start.
        // The caller still receives the START failure (unwrapped, finding 2) and the abandoned
        // stop is folded into its Data instead of being collected and dropped (L5).
        var log = new List<string>();
        var errors = new ErrorRecorder();
        var hanging = new HangingOnStopSvc();
        var container = new SvcContainer(autoConfigureFromGenerator: false)
        {
            HostedSvcStopTimeout = TestStopDeadline,
        };
        container.OnError = errors.Record;
        container.RegisterHostedSvc<HangingOnStopSvc>(_ => hanging);
        container.RegisterHostedSvc<BoomOnStartSvc>(_ => new BoomOnStartSvc("boom", log));
        container.Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new SvcHost(container).StartAsync().WaitAsync(TimeSpan.FromSeconds(10))
        );

        await Assert.That(ex.Message).IsEqualTo("boom");
        await Assert.That(hanging.StopEntered).IsTrue();

        await Assert.That(ex.Data.Contains(RollBackStopFailuresKey)).IsTrue();
        var failures = (List<Exception>)ex.Data[RollBackStopFailuresKey]!;
        await Assert.That(failures.Count).IsEqualTo(1);
        await Assert.That(failures[0]).IsTypeOf<TimeoutException>();
        await Assert
            .That(failures[0].Message.Contains("deadline", StringComparison.Ordinal))
            .IsTrue();

        hanging.Release();
    }

    [Test]
    public async Task StartAsync_RollbackStopThrows_FoldsTheFailureIntoTheStartExceptionData()
    {
        // The other half of L5: RollBackStartedServicesAsync collected stopFailures and never
        // read them, so a failed rollback stop existed only in OnError.
        var log = new List<string>();
        var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterHostedSvc<BoomOnStopSvc>(_ => new BoomOnStopSvc("stop-boom", log));
        container.RegisterHostedSvc<BoomOnStartSvc>(_ => new BoomOnStartSvc("boom", log));
        container.Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SvcHost(container).StartAsync()
        );

        await Assert.That(ex.Message).IsEqualTo("boom");
        await Assert.That(ex.Data.Contains(RollBackStopFailuresKey)).IsTrue();
        var failures = (List<Exception>)ex.Data[RollBackStopFailuresKey]!;
        await Assert.That(failures.Count).IsEqualTo(1);
        await Assert.That(failures[0].Message).IsEqualTo("boom-stop");
    }

    [Test]
    public async Task StartAsync_CleanRollback_AddsNoFailureData()
    {
        // ...and a start failure whose rollback went cleanly carries no such entry: the key is a
        // report about the rollback, not decoration on every failure.
        var log = new List<string>();
        var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterHostedSvc<RecordingSvc>(_ => new RecordingSvc("one", log));
        container.RegisterHostedSvc<BoomOnStartSvc>(_ => new BoomOnStartSvc("boom", log));
        container.Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SvcHost(container).StartAsync()
        );

        await Assert.That(ex.Data.Contains(RollBackStopFailuresKey)).IsFalse();
        await Assert.That(log[2]).IsEqualTo("stop:one");
    }

    // ------------------------------------------------------------------
    // Medium 2 — the budget bounds one SERVICE, not one pass.
    // ------------------------------------------------------------------

    [Test]
    public async Task DisposeAsync_OneSvcIgnoringTheStopToken_DoesNotStarveTheServicesBehindIt()
    {
        // review-2026-11-02 Medium 2: the 30 s budget was taken from the pass, not from the
        // service, so the first service that ignored its token consumed all of it and every
        // service behind it was never stopped at all — while still being recorded as stopped, so
        // nothing could ever stop it later. The budget is now per service: one hang costs one
        // budget, and the remaining services still get their stop.
        var log = new List<string>();
        var errors = new ErrorRecorder();
        var hanging = new HangingOnStopSvc();
        var container = new SvcContainer(autoConfigureFromGenerator: false)
        {
            HostedSvcStopTimeout = TestStopDeadline,
        };
        container.OnError = errors.Record;
        container.RegisterHostedSvc<RecordingSvc>(_ => new RecordingSvc("one", log));
        container.RegisterHostedSvc<RecordingSvcTwo>(_ => new RecordingSvcTwo("two", log));
        // Registered last, so the LIFO stop pass reaches it FIRST: the starved tail is "two"/"one".
        container.RegisterHostedSvc<HangingOnStopSvc>(_ => hanging);
        container.Build();
        await new SvcHost(container).StartAsync();

        // The 10 s guard is the assertion that the pass still terminates.
        await container.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(hanging.StopEntered).IsTrue();

        // ...and the two honest services are still stopped, in LIFO order, behind the hang.
        await Assert
            .That(log.Where(e => e.StartsWith("stop:", StringComparison.Ordinal)).ToArray())
            .IsEquivalentTo(["stop:two", "stop:one"], CollectionOrdering.Matching);

        // Exactly one service was abandoned. Three reports would mean the other two were skipped
        // by an exhausted pass budget — the starvation this test exists to prevent.
        await Assert.That(errors.Entries.Count(e => e.Error is TimeoutException)).IsEqualTo(1);

        hanging.Release();
    }

    [Test]
    public async Task StartAsync_RollbackOneSvcIgnoringTheStopToken_DoesNotStarveTheServicesBehindIt()
    {
        // The rollback of a failed start is the second container-initiated pass and gets the same
        // per-service budget, so a hung service in the rollback does not leave the rest running.
        var log = new List<string>();
        var hanging = new HangingOnStopSvc();
        var container = new SvcContainer(autoConfigureFromGenerator: false)
        {
            HostedSvcStopTimeout = TestStopDeadline,
        };
        container.RegisterHostedSvc<RecordingSvc>(_ => new RecordingSvc("one", log));
        container.RegisterHostedSvc<HangingOnStopSvc>(_ => hanging);
        container.RegisterHostedSvc<BoomOnStartSvc>(_ => new BoomOnStartSvc("boom", log));
        container.Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new SvcHost(container).StartAsync().WaitAsync(TimeSpan.FromSeconds(10))
        );

        await Assert.That(ex.Message).IsEqualTo("boom");
        await Assert.That(hanging.StopEntered).IsTrue();

        // The abandoned service is the ONLY rollback failure: "one" was still stopped.
        var failures = (List<Exception>)ex.Data[RollBackStopFailuresKey]!;
        await Assert.That(failures.Count).IsEqualTo(1);
        await Assert.That(failures[0]).IsTypeOf<TimeoutException>();
        await Assert.That(log[2]).IsEqualTo("stop:one");

        hanging.Release();
    }

    // ------------------------------------------------------------------
    // Medium 1 — an abandoned phase is observed, and its token is not a dead source.
    // ------------------------------------------------------------------

    [Test]
    public async Task DisposeAsync_AbandonedStopPhaseFaultsLater_ReportsItToOnError()
    {
        // review-2026-11-02 Medium 1: the abandoned phase is a local with no continuation, so a
        // fault it raises after the pass walked away has no awaiter — an unobserved task exception
        // at finalization, which with OnError == null is silence. It is now reported.
        var errors = new ErrorRecorder();
        var hanging = new HangingOnStopSvc();
        var container = new SvcContainer(autoConfigureFromGenerator: false)
        {
            HostedSvcStopTimeout = TestStopDeadline,
        };
        container.OnError = errors.Record;
        container.RegisterHostedSvc<HangingOnStopSvc>(_ => hanging);
        container.Build();
        await new SvcHost(container).StartAsync();

        await container.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(hanging.StopEntered).IsTrue();

        // The container is already disposed; the phase it walked away from fails afterwards.
        hanging.Fail(new ApplicationException("late-stop-failure"));

        var observed = await WaitForAsync(
            () =>
                errors.Entries.Any(e =>
                    e.Context.Contains("abandoned", StringComparison.Ordinal)
                    && e.Context.Contains("failed later", StringComparison.Ordinal)
                ),
            TimeSpan.FromSeconds(5)
        );
        await Assert.That(observed).IsTrue();

        var late = errors.Entries.First(e =>
            e.Context.Contains("failed later", StringComparison.Ordinal)
        );

        // The service's own exception, not Task.Exception's aggregate wrapper — the same
        // unwrapped shape the start and stop paths propagate.
        await Assert.That(late.Error).IsTypeOf<ApplicationException>();
        await Assert.That(late.Error.Message).IsEqualTo("late-stop-failure");
        await Assert
            .That(late.Context.Contains(nameof(HangingOnStopSvc), StringComparison.Ordinal))
            .IsTrue();

        // Permanently unretryable, the other half of the finding: the instance is recorded as
        // stopped and the container is disposed, so nothing can run its stop sequence a second
        // time — not a second DisposeAsync (idempotent, returns immediately) and not StopAsync
        // (faults with ObjectDisposedException). OnError is the last thing ever heard about it.
        await container.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await new SvcHost(container).StopAsync()
        );
        await Assert.That(hanging.StopCalls).IsEqualTo(1);
    }

    [Test]
    public async Task DisposeAsync_AbandonedStopPhase_TokenIsCancelledNotADeadSource()
    {
        // Cleared suspicion, recorded as a test: "using var cts" disposes the source while the
        // abandoned phase is still holding the token. The external contract of the token still
        // holds — abandonment is reachable only THROUGH that cancellation, so the token is
        // cancelled before the source is disposed and Register runs its callback without touching
        // it (measured on .NET 10: Register OK, WaitHandle ObjectDisposedException).
        var hanging = new HangingOnStopSvc();
        var container = new SvcContainer(autoConfigureFromGenerator: false)
        {
            HostedSvcStopTimeout = TestStopDeadline,
        };
        container.RegisterHostedSvc<HangingOnStopSvc>(_ => hanging);
        container.Build();
        await new SvcHost(container).StartAsync();

        await container.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        var token = hanging.StopToken;
        await Assert.That(token.CanBeCanceled).IsTrue();
        await Assert.That(token.IsCancellationRequested).IsTrue();

        var ran = false;
        using var registration = token.Register(() => ran = true);
        await Assert.That(ran).IsTrue();

        hanging.Release();
    }

    /// <summary>
    /// Bounded wait for a state another thread produces. The abandoned phase's continuation runs
    /// on the thread pool, so there is nothing to await on this side.
    /// </summary>
    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
                return true;

            await Task.Delay(10);
        }

        return condition();
    }
}
