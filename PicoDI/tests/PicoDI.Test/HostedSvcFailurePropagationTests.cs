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
/// <item>the services that already started are stopped in reverse registration order (LIFO)
/// before the exception reaches the caller — a failed start must not leave background work
/// running behind a host the caller was told to throw away;</item>
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
}
