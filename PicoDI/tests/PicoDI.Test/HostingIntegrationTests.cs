namespace PicoDI.Test;

/// <summary>
/// Integration tests for the full hosting lifecycle.
/// Verifies startup order, stop LIFO order, DI injection, exception isolation, and Dispose integration.
/// </summary>
public class HostingIntegrationTests
{
    #region Test Service Classes

    // Unique marker interfaces so each hosted-service registration
    // gets a distinct service type and does not collide during resolution.
    private interface ISvcA : IHostedSvc { }

    private interface ISvcB : IHostedSvc { }

    private interface ISvcC : IHostedSvc { }

    private sealed class OrderedSvc(List<string> log, string name) : ISvcA, ISvcB, ISvcC
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            log.Add($"Start:{name}");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            log.Add($"Stop:{name}");
            return Task.CompletedTask;
        }
    }

    private sealed class FailingStartSvc : IHostedSvc
    {
        public Task StartAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Start failed");

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FailingStopSvc : IHostedSvc
    {
        public bool Started { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Stop failed");
    }

    private sealed class DependentSvc : IHostedSvc
    {
        public ISimpleService Dep { get; }

        public DependentSvc(ISimpleService dep) => Dep = dep;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TrackingHostedSvc : IHostedSvc
    {
        public bool Started { get; private set; }
        public bool Stopped { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Stopped = true;
            return Task.CompletedTask;
        }
    }

    private sealed class TokenCapturingSvc : IHostedSvc
    {
        public CancellationToken? StartToken { get; private set; }
        public CancellationToken? StopToken { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            StartToken = cancellationToken;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopToken = cancellationToken;
            return Task.CompletedTask;
        }
    }

    #endregion

    #region Startup Order Tests

    [Test]
    public async Task MultipleServices_StartInRegistrationOrder()
    {
        // Arrange
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        var log = new List<string>();

        container.RegisterHostedSvc<ISvcA>(_ => new OrderedSvc(log, "A"));
        container.RegisterHostedSvc<ISvcB>(_ => new OrderedSvc(log, "B"));
        container.RegisterHostedSvc<ISvcC>(_ => new OrderedSvc(log, "C"));
        container.Build();
        var host = new SvcHost(container);

        // Act
        await host.StartAsync();

        // Assert — sequence, not membership: the bare overload is a SET comparison, so a
        // start pass walked in reverse (C,B,A) produced the same three markers and left this
        // test green (review-2026-11-02 test-validity finding 2).
        await Assert
            .That(log)
            .IsEquivalentTo(["Start:A", "Start:B", "Start:C"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task MultipleServices_StopInReverseOrder()
    {
        // Arrange
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        var log = new List<string>();

        container.RegisterHostedSvc<ISvcA>(_ => new OrderedSvc(log, "A"));
        container.RegisterHostedSvc<ISvcB>(_ => new OrderedSvc(log, "B"));
        container.RegisterHostedSvc<ISvcC>(_ => new OrderedSvc(log, "C"));
        container.Build();
        var host = new SvcHost(container);

        await host.StartAsync();

        // Act
        await host.StopAsync();

        // Assert — LIFO: C stops first, then B, then A. CollectionOrdering.Matching is the point
        // of this assertion: with the default (set) comparison the SAME six strings satisfy a
        // forward shutdown, so inverting the stop loop to FIFO left this test green
        // (review-2026-11-02 test-validity finding 1).
        await Assert
            .That(log)
            .IsEquivalentTo(
                ["Start:A", "Start:B", "Start:C", "Stop:C", "Stop:B", "Stop:A"],
                CollectionOrdering.Matching
            );
    }

    #endregion

    #region DI Injection Test

    [Test]
    public async Task HostedService_InjectsOtherDI()
    {
        // Arrange
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);

        // Register the dependency that DependentSvc will inject
        container.RegisterSingleton<ISimpleService>(static _ => new SimpleService());

        // Register the hosted service whose constructor takes ISimpleService
        container.RegisterHostedSvc<DependentSvc>(scope => new DependentSvc(
            scope.GetService<ISimpleService>()
        ));
        container.Build();
        var host = new SvcHost(container);

        // Act & Assert — should not throw
        await host.StartAsync();
    }

    #endregion

    #region Exception Isolation Tests

    [Test]
    public async Task StartAsync_ExceptionAbortsStart_AndLaterServicesNeverStart()
    {
        // Arrange
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        var trackingSvc = new TrackingHostedSvc();

        container.RegisterHostedSvc<FailingStartSvc>(_ => new FailingStartSvc());
        container.RegisterHostedSvc<TrackingHostedSvc>(_ => trackingSvc);
        container.Build();
        var host = new SvcHost(container);

        // Act — review-2026-11-02 finding 2: the failure faults the caller. It used to be
        // swallowed into OnError (default null) and the start reported success.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        await Assert.That(ex.Message).IsEqualTo("Start failed");

        // Assert — the service after the failing one is never started, and nothing is stopped:
        // the failing service's StartAsync never completed, so it is not stopped either.
        await Assert.That(trackingSvc.Started).IsFalse();
        await Assert.That(trackingSvc.Stopped).IsFalse();
    }

    [Test]
    public async Task StopAsync_ExceptionStillStopsOthers_ButFaultsTheCaller()
    {
        // Arrange
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        var trackingSvc = new TrackingHostedSvc();

        container.RegisterHostedSvc<FailingStopSvc>(_ => new FailingStopSvc());
        container.RegisterHostedSvc<TrackingHostedSvc>(_ => trackingSvc);
        container.Build();
        var host = new SvcHost(container);

        // Materialize singletons first
        await host.StartAsync();

        // Act — every service is still stopped (shutdown stays fault-tolerant), but the failure
        // reaches the caller as an AggregateException (finding 2, stop half).
        var ex = await Assert.ThrowsAsync<AggregateException>(() => host.StopAsync());
        await Assert.That(ex.InnerExceptions.Count).IsEqualTo(1);
        await Assert.That(ex.InnerExceptions[0].Message).IsEqualTo("Stop failed");

        // Assert — normal service still stopped despite the failing one
        await Assert.That(trackingSvc.Stopped).IsTrue();
    }

    #endregion

    #region Dispose Integration Test

    [Test]
    public async Task DisposeAsync_CallsStopBeforeDispose()
    {
        // Arrange
        var trackingSvc = new TrackingHostedSvc();

        var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterHostedSvc<TrackingHostedSvc>(_ => trackingSvc);
        container.Build();
        var host = new SvcHost(container);

        // Materialize the singleton
        await host.StartAsync();
        await Assert.That(trackingSvc.Started).IsTrue();

        // Act — DisposeAsync must call StopAsync before cleaning up
        await container.DisposeAsync();

        // Assert — StopAsync was called as part of the disposal pipeline.
        // This pins THAT the stop ran. The ORDER the name claims (stop before the container is
        // torn down) is pinned at the pipeline itself, in
        // ContainerDisposalOrderTests.ContainerDisposal_StopsHostedSvc_ThenUserScope_ThenSingleton,
        // which compares the four disposal markers with CollectionOrdering.Matching; moving the
        // stop group after the scope/singleton passes turns that test red and leaves this one
        // green (test-validity audit row 6, measured).
        await Assert.That(trackingSvc.Stopped).IsTrue();
    }

    #endregion

    #region CancellationToken Propagation Test

    [Test]
    public async Task CancellationToken_PropagatedToHostedService()
    {
        // Arrange
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        var tokenSvc = new TokenCapturingSvc();

        container.RegisterHostedSvc<TokenCapturingSvc>(_ => tokenSvc);
        container.Build();
        var host = new SvcHost(container);

        // Act — start with a specific token
        using var startCts = new CancellationTokenSource();
        await host.StartAsync(startCts.Token);

        // Assert — token was propagated to the service
        await Assert.That(tokenSvc.StartToken.HasValue).IsTrue();
        await Assert.That(tokenSvc.StartToken!.Value.Equals(startCts.Token)).IsTrue();

        // Act — stop with a specific token
        using var stopCts = new CancellationTokenSource();
        await host.StopAsync(stopCts.Token);

        // Assert — token was propagated to StopAsync
        await Assert.That(tokenSvc.StopToken.HasValue).IsTrue();
        await Assert.That(tokenSvc.StopToken!.Value.Equals(stopCts.Token)).IsTrue();
    }

    #endregion

    #region Lifecycle Service Test Helpers

    private interface ILifecycleA : IHostedLifecycleSvc { }

    private interface ILifecycleB : IHostedLifecycleSvc { }

    private sealed class LifecycleTrackingSvc(List<string> log, string name)
        : ILifecycleA,
            ILifecycleB
    {
        public Task StartingAsync(CancellationToken ct)
        {
            log.Add($"Starting:{name}");
            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken ct)
        {
            log.Add($"Start:{name}");
            return Task.CompletedTask;
        }

        public Task StartedAsync(CancellationToken ct)
        {
            log.Add($"Started:{name}");
            return Task.CompletedTask;
        }

        public Task StoppingAsync(CancellationToken ct)
        {
            log.Add($"Stopping:{name}");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            log.Add($"Stop:{name}");
            return Task.CompletedTask;
        }

        public Task StoppedAsync(CancellationToken ct)
        {
            log.Add($"Stopped:{name}");
            return Task.CompletedTask;
        }
    }

    private sealed class SlowStartingLifecycleSvc : IHostedLifecycleSvc
    {
        public bool Started { get; private set; }

        public async Task StartingAsync(CancellationToken ct) => await Task.Delay(200, ct);

        public async Task StartAsync(CancellationToken ct)
        {
            await Task.Delay(100, ct);
            Started = true;
        }

        public Task StartedAsync(CancellationToken ct) => Task.CompletedTask;

        public Task StoppingAsync(CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public Task StoppedAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FailingLifecyclePhaseSvc(string failingPhase) : IHostedLifecycleSvc
    {
        public bool SubsequentPhaseRun { get; private set; }

        public Task StartingAsync(CancellationToken ct) => FailIf("Starting");

        public Task StartAsync(CancellationToken ct)
        {
            SubsequentPhaseRun = true;
            return FailIf("Start");
        }

        public Task StartedAsync(CancellationToken ct) => FailIf("Started");

        public Task StoppingAsync(CancellationToken ct) => FailIf("Stopping");

        public Task StopAsync(CancellationToken ct) => FailIf("Stop");

        public Task StoppedAsync(CancellationToken ct) => FailIf("Stopped");

        private Task FailIf(string phase) =>
            phase == failingPhase
                ? throw new InvalidOperationException($"Fail:{phase}")
                : Task.CompletedTask;
    }

    /// <summary>
    /// Lifecycle service whose stop phase is genuinely asynchronous: <see cref="Stopped"/> is set
    /// only after <see cref="Release"/> opens the gate. A service that sets the flag synchronously
    /// (`Stopped = true; return Task.CompletedTask;`) cannot tell a waited stop from a
    /// fire-and-forget one — the flag is already true when the abandoned call returns
    /// (test-validity audit row 9).
    /// </summary>
    private sealed class GatedStopLifecycleSvc : IHostedLifecycleSvc
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _stopEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public bool Started { get; private set; }
        public bool Stopped { get; private set; }

        /// <summary>Completes once the stop phase has started and is parked on the gate.</summary>
        public Task StopEntered => _stopEntered.Task;

        /// <summary>Opens the gate, letting the stop phase run to completion.</summary>
        public void Release() => _release.TrySetResult();

        public Task StartingAsync(CancellationToken ct) => Task.CompletedTask;

        public Task StartAsync(CancellationToken ct)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public Task StartedAsync(CancellationToken ct) => Task.CompletedTask;

        public Task StoppingAsync(CancellationToken ct) => Task.CompletedTask;

        public async Task StopAsync(CancellationToken ct)
        {
            _stopEntered.TrySetResult();
            await _release.Task.WaitAsync(ct).ConfigureAwait(false);
            Stopped = true;
        }

        public Task StoppedAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class TrackingLifecycleSvc : IHostedLifecycleSvc
    {
        public bool Started { get; private set; }
        public bool Stopped { get; private set; }

        public Task StartingAsync(CancellationToken ct) => Task.CompletedTask;

        public Task StartAsync(CancellationToken ct)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public Task StartedAsync(CancellationToken ct) => Task.CompletedTask;

        public Task StoppingAsync(CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct)
        {
            Stopped = true;
            return Task.CompletedTask;
        }

        public Task StoppedAsync(CancellationToken ct) => Task.CompletedTask;
    }

    #endregion

    #region Lifecycle Phase Ordering Tests

    [Test]
    public async Task LifecycleService_PhaseExecutionOrder()
    {
        // Arrange
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        var log = new List<string>();

        container.RegisterHostedSvc<ILifecycleA>(_ => new LifecycleTrackingSvc(log, "A"));
        container.Build();
        var host = new SvcHost(container);

        // Act
        await host.StartAsync();

        // Assert — phases run in order: Starting → Start → Started, as a SEQUENCE: the set
        // comparison this used to use cannot tell it from Starting → Started → Start
        // (review-2026-11-02 test-validity finding 3).
        await Assert
            .That(log)
            .IsEquivalentTo(["Starting:A", "Start:A", "Started:A"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task LifecycleService_StopPhaseExecutionOrder()
    {
        // Arrange
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        var log = new List<string>();

        container.RegisterHostedSvc<ILifecycleA>(_ => new LifecycleTrackingSvc(log, "A"));
        container.Build();
        var host = new SvcHost(container);

        await host.StartAsync();

        // Act
        await host.StopAsync();

        // Assert — stop phases run in order: Stopping → Stop → Stopped
        await Assert
            .That(log)
            .IsEquivalentTo(
                ["Starting:A", "Start:A", "Started:A", "Stopping:A", "Stop:A", "Stopped:A"],
                CollectionOrdering.Matching
            );
    }

    [Test]
    public async Task MultipleLifecycleServices_StartInRegistrationOrder()
    {
        // Arrange
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        var log = new List<string>();

        container.RegisterHostedSvc<ILifecycleA>(_ => new LifecycleTrackingSvc(log, "A"));
        container.RegisterHostedSvc<ILifecycleB>(_ => new LifecycleTrackingSvc(log, "B"));
        container.Build();
        var host = new SvcHost(container);

        // Act
        await host.StartAsync();

        // Assert — A completes all phases before B starts
        await Assert
            .That(log)
            .IsEquivalentTo(
                ["Starting:A", "Start:A", "Started:A", "Starting:B", "Start:B", "Started:B"],
                CollectionOrdering.Matching
            );
    }

    [Test]
    public async Task MultipleLifecycleServices_StopInReverseOrder()
    {
        // Arrange
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        var log = new List<string>();

        container.RegisterHostedSvc<ILifecycleA>(_ => new LifecycleTrackingSvc(log, "A"));
        container.RegisterHostedSvc<ILifecycleB>(_ => new LifecycleTrackingSvc(log, "B"));
        container.Build();
        var host = new SvcHost(container);

        await host.StartAsync();

        // Act
        await host.StopAsync();

        // Assert — B stops first (LIFO), then A
        await Assert
            .That(log)
            .IsEquivalentTo(
                [
                    "Starting:A",
                    "Start:A",
                    "Started:A",
                    "Starting:B",
                    "Start:B",
                    "Started:B",
                    "Stopping:B",
                    "Stop:B",
                    "Stopped:B",
                    "Stopping:A",
                    "Stop:A",
                    "Stopped:A",
                ],
                CollectionOrdering.Matching
            );
    }

    #endregion

    #region Start/Stop Completion Tests (Regression: async void + fire-and-forget)

    [Test]
    public async Task StartHostedServicesAsync_CompletesBeforeReturn()
    {
        // Arrange — a slow-starting lifecycle service
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        var svc = new SlowStartingLifecycleSvc();

        container.RegisterHostedSvc<SlowStartingLifecycleSvc>(_ => svc);
        container.Build();
        var host = new SvcHost(container);

        // Act — StartAsync should not return until phases are complete
        await host.StartAsync();

        // Assert — the service's phases actually completed
        await Assert.That(svc.Started).IsTrue();
    }

    [Test]
    public async Task StartHostedServicesAsync_AwaitReturnsAfterStart()
    {
        // Arrange
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        var svc = new TrackingLifecycleSvc();

        container.RegisterHostedSvc<TrackingLifecycleSvc>(_ => svc);
        container.Build();
        var host = new SvcHost(container);

        // Act
        await host.StartAsync();

        // Assert — after StartAsync completes, Started is true
        // (was broken when TryRunPhase was async void fire-and-forget)
        await Assert.That(svc.Started).IsTrue();
    }

    [Test]
    public async Task StopHostedServicesAsync_AwaitReturnsAfterStop()
    {
        // Arrange — a service whose stop phase is ASYNC: `Stopped` only becomes true after the
        // gate below is opened. The old body used a service that set the flag synchronously, so
        // dropping the `await` on the stop (fire-and-forget) left this test green — the flag was
        // already set when the abandoned call returned (test-validity audit row 9).
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        var svc = new GatedStopLifecycleSvc();

        container.RegisterHostedSvc<GatedStopLifecycleSvc>(_ => svc);
        container.Build();
        var host = new SvcHost(container);

        await host.StartAsync();

        // Act
        var stop = host.StopAsync();

        // Assert — the returned task is still PENDING while the stop phase runs: the phase is
        // awaited, not handed to the thread pool.
        await svc.StopEntered.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert
            .That(stop.IsCompleted)
            .IsFalse()
            .Because("StopAsync must not complete while a stop phase is still running");

        svc.Release();
        await stop.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert — and the stop phase ran to completion before the await returned
        await Assert.That(svc.Stopped).IsTrue();
    }

    [Test]
    public async Task Dispose_WaitsForHostedServicesToStop()
    {
        // Arrange — the same gated service, so only a disposal that really waits can observe
        // Stopped == true. This is the HOST disposal entry point (SvcHost.DisposeAsync); it used
        // to await container.DisposeAsync() instead, a byte-identical twin of the next test, so
        // this half of the family covered nothing (test-validity audit row 9). PicoDI exposes no
        // synchronous Dispose — ISvcContainer/IHost/ISvcScope are IAsyncDisposable only.
        var svc = new GatedStopLifecycleSvc();
        var container = new SvcContainer(autoConfigureFromGenerator: false);

        container.RegisterHostedSvc<GatedStopLifecycleSvc>(_ => svc);
        container.Build();

        var host = new SvcHost(container);
        await host.StartAsync();
        await Assert.That(svc.Started).IsTrue();

        // Act — disposing the host must call StopAsync and actually wait for it
        var dispose = host.DisposeAsync();

        // Assert — disposal is still in flight while the stop phase runs
        await svc.StopEntered.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert
            .That(dispose.IsCompleted)
            .IsFalse()
            .Because("host disposal must wait for the hosted services, not abandon them");

        svc.Release();
        await dispose.AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        // Assert — after Dispose returns, the service has stopped
        await Assert.That(svc.Stopped).IsTrue();
    }

    [Test]
    public async Task DisposeAsync_WaitsForHostedServicesToStop()
    {
        // Arrange — the container entry point of the same contract (the pipeline below SvcHost).
        var svc = new GatedStopLifecycleSvc();
        var container = new SvcContainer(autoConfigureFromGenerator: false);

        container.RegisterHostedSvc<GatedStopLifecycleSvc>(_ => svc);
        container.Build();

        await new SvcHost(container).StartAsync();
        await Assert.That(svc.Started).IsTrue();

        // Act — DisposeAsync must call StopAsync and actually wait for it
        var dispose = container.DisposeAsync();

        // Assert — disposal is still in flight while the stop phase runs
        await svc.StopEntered.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert
            .That(dispose.IsCompleted)
            .IsFalse()
            .Because("DisposeAsync must wait for the hosted services, not abandon them");

        svc.Release();
        await dispose.AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        // Assert — after DisposeAsync returns, the service has stopped
        await Assert.That(svc.Stopped).IsTrue();
    }

    #endregion

    #region Lifecycle Exception Isolation Tests

    [Test]
    public async Task LifecycleService_ExceptionInStartPhase_AbortsTheRemainingPhases()
    {
        // Arrange — StartingAsync throws, so StartAsync/StartedAsync must not run
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        var svc = new FailingLifecyclePhaseSvc("Starting");

        container.RegisterHostedSvc<FailingLifecyclePhaseSvc>(_ => svc);
        container.Build();
        var host = new SvcHost(container);

        // Act — finding 2: the failed start faults the caller
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        await Assert.That(ex.Message).IsEqualTo("Fail:Starting");

        // Assert — the phase chain stopped at the failing phase
        await Assert.That(svc.SubsequentPhaseRun).IsFalse();
    }

    [Test]
    public async Task LifecycleService_StartExceptionAbortsLaterServices()
    {
        // Arrange
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        var failingSvc = new FailingLifecyclePhaseSvc("Start");
        var trackingSvc = new TrackingLifecycleSvc();

        container.RegisterHostedSvc<FailingLifecyclePhaseSvc>(_ => failingSvc);
        container.RegisterHostedSvc<TrackingLifecycleSvc>(_ => trackingSvc);
        container.Build();
        var host = new SvcHost(container);

        // Act — finding 2: the failure faults the caller instead of reporting success
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        await Assert.That(ex.Message).IsEqualTo("Fail:Start");

        // Assert — the second service never started
        await Assert.That(trackingSvc.Started).IsFalse();
    }

    [Test]
    public async Task LifecycleService_StopExceptionStillStopsOtherServices_ButFaultsTheCaller()
    {
        // Arrange
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        var failingSvc = new FailingLifecyclePhaseSvc("Stop");
        var trackingSvc = new TrackingLifecycleSvc();

        container.RegisterHostedSvc<FailingLifecyclePhaseSvc>(_ => failingSvc);
        container.RegisterHostedSvc<TrackingLifecycleSvc>(_ => trackingSvc);
        container.Build();
        var host = new SvcHost(container);

        await host.StartAsync();

        // Act — finding 2, stop half: shutdown stays fault-tolerant, but the failure reaches
        // the caller as an AggregateException.
        var ex = await Assert.ThrowsAsync<AggregateException>(() => host.StopAsync());
        await Assert.That(ex.InnerExceptions[0].Message).IsEqualTo("Fail:Stop");

        // Assert — the second service stopped despite the first failing
        await Assert.That(trackingSvc.Stopped).IsTrue();
    }

    #endregion
}
