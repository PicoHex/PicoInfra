namespace PicoDI;

public sealed partial class SvcContainer
{
    private List<SvcRuntimeRegistration>? _hostedRegistrations;

    // Long-lived scope for resolving hosted services. Created lazily on first
    // use and disposed with the container. This prevents scoped dependencies
    // captured by hosted singletons from being disposed when a temporary
    // resolution scope goes out of scope.
    private SvcScope? _hostingScope;

    // State machine: Created=0 → Started=1 → Stopped=2
    // Uses Interlocked for atomic one-shot transitions.
    // TryTransitionToStopped uses Exchange (not CAS) to guarantee the state
    // always moves to 2 — this prevents Start from winning a race against Stop.
    private int _hostedState;

    // Serializes StartHostedServicesAsync and StopHostedServicesAsync so that
    // concurrent Start/Stop calls cannot overlap on the same hosted service,
    // which would violate the IHostedSvc contract (StopAsync called while
    // StartAsync is still in flight).
    private readonly SemaphoreSlim _hostingLock = new(1, 1);

    /// <summary>
    /// Hosted-service instances that must not receive another stop sequence: already
    /// stopped (the LIFO pass or the failed-start rollback ran it) or never started (the
    /// start phase threw, so the instance is in an unknown state — the
    /// <c>Microsoft.Extensions.Hosting</c> rule is that such a service is not stopped).
    /// Guarded by <see cref="_hostingLock"/>; keeps the stop phases at most once per instance.
    /// </summary>
    private readonly HashSet<object> _stoppedHostedInstances = new(
        ReferenceEqualityComparer.Instance
    );

    /// <summary>
    /// Wall-clock budget of ONE stop pass that runs on the container's own initiative: the
    /// disposal path and the rollback of a failed start. A cancelled start token must not skip
    /// the cleanup, and a service that ignores cancellation must not hang it forever — the pass
    /// gets this much time in total, after which its remaining phases are abandoned and reported
    /// as <see cref="TimeoutException"/> (review-2026-11-02 defect L5: this used to be a
    /// <c>CancellationTokenSource</c> that bounded nothing, because a token only signals).
    /// </summary>
    /// <remarks>
    /// An instance member, not a static, so a test can shrink the window without affecting a test
    /// that runs in parallel. The caller-driven <c>StopAsync(token)</c> path is not bounded by it:
    /// there the caller's token is the control surface.
    /// </remarks>
    internal TimeSpan HostedSvcStopTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// <see cref="Exception.Data"/> key under which the rollback of a failed start reports the
    /// stop phases that failed while it ran — a <c>List&lt;Exception&gt;</c>, present only when
    /// something failed. The start exception itself stays unwrapped (the caller receives the
    /// service's own exception), so <c>Data</c> is the channel that does not hide it.
    /// </summary>
    internal const string RollBackStopFailuresKey = "PicoDI.HostedSvcRollBackStopFailures";

    private bool TryTransitionToStarted()
    {
        return Interlocked.CompareExchange(ref _hostedState, 1, 0) is 0;
    }

    private bool TryTransitionToStopped()
    {
        return Interlocked.Exchange(ref _hostedState, 2) == 1;
    }

    private void TrackHostedServicesFromRegistrations(
        Dictionary<Type, List<SvcRuntimeRegistration>> cache
    )
    {
        var lastByType = new Dictionary<Type, SvcRuntimeRegistration>();
        foreach (var (_, registrations) in cache)
        {
            foreach (var registration in registrations)
            {
                if (!SvcHostedServiceRegistry.Contains(registration.ServiceType))
                    continue;

                if (registration.Lifetime != SvcLifetime.Singleton)
                {
                    throw new HostedSvcRegistrationException(
                        $"Type '{registration.ServiceType.FullName}' implements IHostedSvc but was registered as {registration.Lifetime}. "
                            + "Hosted services must be registered as Singleton. Use container.RegisterHostedSvc<T>() or register with SvcLifetime.Singleton."
                    );
                }

                lastByType[registration.ServiceType] = registration;
            }
        }

        if (lastByType.Count > 0)
            Volatile.Write(
                ref _hostedRegistrations,
                new List<SvcRuntimeRegistration>(lastByType.Values)
            );
    }

    /// <summary>
    /// Starts all registered hosted services in registration order.
    /// Each service is a one-shot: once started, it cannot be restarted
    /// without creating a new container.
    /// </summary>
    /// <exception cref="Exception">
    /// Rethrown from the failing service's start phase — a failed start faults the caller's
    /// <c>StartAsync</c> with the service's own exception. The services that already started are
    /// stopped in reverse order first, the failing service is not stopped, and the host is left
    /// stopped (later start/stop calls are no-ops). The same failure is also reported to
    /// <see cref="OnError"/>.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The container has already been disposed.</exception>
    internal async Task StartHostedServicesAsync(CancellationToken cancellationToken = default)
    {
        // Lazy build, matching CreateScope() and GetImplicitRootScope(): Build() is what
        // populates _hostedRegistrations. Without this, a SvcHost over a container that was
        // never built (the public `new SvcHost(container)` entry point) read a null list,
        // started nothing, and still consumed the one-shot state — so even after a later
        // Build() a second StartAsync could never start anything (review-2026-11-02
        // finding 4). The call is idempotent and cheap once the container is frozen, and it
        // runs BEFORE the one-shot transition so a failure here (a disposed container throws
        // ObjectDisposedException) does not consume the single start.
        Build();

        if (!TryTransitionToStarted())
            return;

        // Lock acquisition must not be cancelled — the user's token controls
        // individual service start/stop, not infrastructure serialization.
        await _hostingLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            // Re-check: Stop or Dispose may have raced in between
            // TryTransitionToStarted and the lock acquisition.
            if (Volatile.Read(ref _hostedState) != 1 || Volatile.Read(ref _disposed) != 0)
                return;

            await StartHostedServicesCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _hostingLock.Release();
        }
    }

    private async Task StartHostedServicesCoreAsync(CancellationToken cancellationToken)
    {
        var registrations = Volatile.Read(ref _hostedRegistrations);
        if (registrations is null or { Count: 0 })
            return;

        // Services whose start sequence completed, in start order. The rollback walks it
        // backwards, so it sees exactly the services that are actually up.
        var started = new List<(SvcRuntimeRegistration Registration, object Instance)>();

        // Iterate in registration order (forward) — matches IHostedService contract.
        for (int i = 0; i < registrations.Count; i++)
        {
            var registration = registrations[i];
            object? instance = null;
            var phase = "Start";

            try
            {
                instance = registration.GetSingletonInstance();
                if (instance is null)
                {
                    _hostingScope ??= (SvcScope)CreateScope();
                    instance = _hostingScope.GetService(registration.ServiceType);
                }

                if (instance is IHostedLifecycleSvc lifecycle)
                {
                    phase = "Starting";
                    await lifecycle.StartingAsync(cancellationToken).ConfigureAwait(false);
                    phase = "Start";
                    await lifecycle.StartAsync(cancellationToken).ConfigureAwait(false);
                    phase = "Started";
                    await lifecycle.StartedAsync(cancellationToken).ConfigureAwait(false);
                }
                else if (instance is IHostedSvc hosted)
                {
                    phase = "Start";
                    await hosted.StartAsync(cancellationToken).ConfigureAwait(false);
                }

                started.Add((registration, instance));
            }
            catch (Exception ex)
            {
                // Diagnostic channel first — it used to be the ONLY channel, which is
                // what made a failed start report success (review-2026-11-02 finding 2).
                OnError?.Invoke(
                    ex,
                    $"Error in {phase} phase of hosted service '{registration.ServiceType.FullName}'"
                );

                // The service whose start threw is not stopped: its StartAsync never
                // completed, so its state is unknown (Microsoft.Extensions.Hosting rule).
                // Record it anyway so a StopAsync already queued on the hosting lock cannot
                // walk it — the stop phases run at most once per instance, and never
                // after a failed start.
                if (instance is not null)
                    _stoppedHostedInstances.Add(instance);

                // review-2026-11-02 defect L5: this list used to be collected and never read, so
                // a failed rollback stop lived only in OnError. It now travels on the exception
                // the caller receives - Data is the channel that keeps that exception unwrapped.
                var rollBackStopFailures = await RollBackStartedServicesAsync(started)
                    .ConfigureAwait(false);
                if (rollBackStopFailures.Count > 0)
                    ex.Data[RollBackStopFailuresKey] = rollBackStopFailures;

                // The .NET host contract: a failed start faults the caller's StartAsync.
                // Rethrown with the original instance, type, message and stack trace, so
                // `catch (InvalidOperationException)` around StartAsync keeps working.
                throw;
            }
        }
    }

    /// <summary>
    /// Stops the services that already started, newest first, when a start sequence fails.
    /// A host whose start failed is a host the caller was told to throw away; leaving
    /// background work running behind it would leak until finalization.
    /// </summary>
    /// <remarks>
    /// Stop failures here never hide the start failure: the caller receives the start exception,
    /// unwrapped, and the rollback's own failures travel on its <see cref="Exception.Data"/> under
    /// <see cref="RollBackStopFailuresKey"/> (plus <see cref="OnError"/>). The pass gets one
    /// <see cref="HostedSvcStopTimeout"/> budget — it is the token the services receive AND the
    /// deadline their phases are awaited with, so a service that ignores the token is abandoned
    /// instead of hanging the rollback forever. The host is left <i>stopped</i> — a later
    /// <c>StopAsync</c>/<c>DisposeAsync</c> must not run a second stop over services already down.
    /// </remarks>
    private async ValueTask<List<Exception>> RollBackStartedServicesAsync(
        List<(SvcRuntimeRegistration Registration, object Instance)> started
    )
    {
        using var cts = new CancellationTokenSource(HostedSvcStopTimeout);
        var stopFailures = new List<Exception>();

        for (int i = started.Count - 1; i >= 0; i--)
        {
            var (registration, instance) = started[i];
            if (!_stoppedHostedInstances.Add(instance))
                continue;

            await StopInstanceAsync(registration, instance, cts.Token, stopFailures, cts.Token)
                .ConfigureAwait(false);
        }

        Interlocked.Exchange(ref _hostedState, 2);
        return stopFailures;
    }

    /// <summary>
    /// Stops all registered hosted services in reverse registration order (LIFO).
    /// Each service is a one-shot: once stopped, it cannot be restarted
    /// without creating a new container.
    /// </summary>
    /// <exception cref="AggregateException">
    /// One or more stop phases failed. Every service is still stopped before this is thrown; the
    /// individual failures are also reported to <see cref="OnError"/>.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// The container has already been disposed — symmetric with
    /// <see cref="StartHostedServicesAsync"/>, which faults through its lazy <c>Build()</c>.
    /// Disposal has already run the single stop pass, so a normal return here would be
    /// indistinguishable from a shutdown that really happened (review-2026-11-02 defect L4).
    /// </exception>
    internal async Task StopHostedServicesAsync(CancellationToken cancellationToken = default)
    {
        DisposalGuards.ThrowIfDisposed(ref _disposed, nameof(SvcContainer));

        await StopHostedServicesOnceAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The single stop pass: consumes the one-shot state, serializes on the hosting lock and runs
    /// the LIFO stop. Split out of <see cref="StopHostedServicesAsync(CancellationToken)"/> so the
    /// disposal path can run it without the disposed guard — <see cref="DisposeAsync"/> sets the
    /// flag it would be checking, and a disposal path never throws.
    /// </summary>
    /// <param name="cancellationToken">Token handed to the services.</param>
    /// <param name="deadline">
    /// Deadline the individual phases are awaited with, in addition to the token they receive.
    /// Default (not cancellable) for the caller-driven <c>StopAsync</c>: there the caller's token
    /// is the control surface and an unbounded wait is the documented behaviour. The
    /// container-initiated passes pass their <see cref="HostedSvcStopTimeout"/> token here.
    /// </param>
    private async Task StopHostedServicesOnceAsync(
        CancellationToken cancellationToken,
        CancellationToken deadline = default
    )
    {
        if (!TryTransitionToStopped())
            return;

        await _hostingLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await StopHostedServicesCoreAsync(cancellationToken, deadline).ConfigureAwait(false);
        }
        finally
        {
            _hostingLock.Release();
        }
    }

    private async Task StopHostedServicesCoreAsync(
        CancellationToken cancellationToken,
        CancellationToken deadline = default
    )
    {
        var registrations = Volatile.Read(ref _hostedRegistrations);
        if (registrations is null or { Count: 0 })
            return;

        var failures = new List<Exception>();

        // Iterate in reverse order (LIFO) — matches IHostedService contract.
        for (int i = registrations.Count - 1; i >= 0; i--)
        {
            var registration = registrations[i];

            var instance = registration.GetSingletonInstance();
            if (instance is null)
                continue;

            // At most one stop sequence per instance: a rolled-back start or a StopAsync
            // that was already queued on the hosting lock must not stop a service twice.
            if (!_stoppedHostedInstances.Add(instance))
                continue;

            await StopInstanceAsync(registration, instance, cancellationToken, failures, deadline)
                .ConfigureAwait(false);
        }

        if (failures.Count > 0)
        {
            // Every service was still stopped (shutdown stays fault-tolerant), but a failing
            // shutdown hook is no longer invisible to the caller — the shape
            // Microsoft.Extensions.Hosting uses for Host.StopAsync (review-2026-11-02 finding 2).
            throw new AggregateException(
                $"{failures.Count} hosted service stop phase(s) failed. See InnerExceptions for the individual failures.",
                failures
            );
        }
    }

    /// <summary>
    /// Runs the stop sequence of one hosted service. Each phase is isolated: a throwing phase
    /// is reported to <see cref="OnError"/> and added to <paramref name="failures"/>, and the
    /// remaining phases still run — same fault isolation as before, minus the swallowing.
    /// </summary>
    private async Task StopInstanceAsync(
        SvcRuntimeRegistration registration,
        object instance,
        CancellationToken cancellationToken,
        List<Exception> failures,
        CancellationToken deadline = default
    )
    {
        if (instance is IHostedLifecycleSvc lifecycle)
        {
            await StopPhaseAsync(
                    "Stopping",
                    registration,
                    () => lifecycle.StoppingAsync(cancellationToken),
                    failures,
                    deadline
                )
                .ConfigureAwait(false);
            await StopPhaseAsync(
                    "Stop",
                    registration,
                    () => lifecycle.StopAsync(cancellationToken),
                    failures,
                    deadline
                )
                .ConfigureAwait(false);
            await StopPhaseAsync(
                    "Stopped",
                    registration,
                    () => lifecycle.StoppedAsync(cancellationToken),
                    failures,
                    deadline
                )
                .ConfigureAwait(false);
        }
        else if (instance is IHostedSvc hosted)
        {
            await StopPhaseAsync(
                    "Stop",
                    registration,
                    () => hosted.StopAsync(cancellationToken),
                    failures,
                    deadline
                )
                .ConfigureAwait(false);
        }
    }

    private async Task StopPhaseAsync(
        string phase,
        SvcRuntimeRegistration registration,
        Func<Task> action,
        List<Exception> failures,
        CancellationToken deadline = default
    )
    {
        try
        {
            await RunPhaseWithinDeadlineAsync(action, deadline, phase, registration)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            OnError?.Invoke(
                ex,
                $"Error in {phase} phase of hosted service '{registration.ServiceType.FullName}'"
            );
            failures.Add(ex);
        }
    }

    /// <summary>
    /// Awaits one stop phase and enforces the deadline of the pass it belongs to.
    /// </summary>
    /// <remarks>
    /// A <see cref="CancellationTokenSource"/> only signals: handing its token to a service that
    /// ignores it bounds nothing, which is what made <see cref="HostedSvcStopTimeout"/> a claim
    /// rather than a bound (review-2026-11-02 defect L5). Awaiting through
    /// <see cref="Task.WaitAsync(CancellationToken)"/> makes the wait itself end at the deadline;
    /// the abandoned phase is reported as a <see cref="TimeoutException"/> and the pass keeps
    /// going with its remaining phases, so a hung service cannot hold a disposal or a rollback.
    /// A phase whose deadline had already expired is reported without being started, so the
    /// failure says what happened instead of blaming a service that never ran.
    /// </remarks>
    private async Task RunPhaseWithinDeadlineAsync(
        Func<Task> action,
        CancellationToken deadline,
        string phase,
        SvcRuntimeRegistration registration
    )
    {
        if (!deadline.CanBeCanceled)
        {
            await action().ConfigureAwait(false);
            return;
        }

        if (deadline.IsCancellationRequested)
            throw new TimeoutException(
                $"The {phase} phase of hosted service '{registration.ServiceType.FullName}' was not run: the stop pass's {DescribeStopTimeout()} deadline had already expired."
            );

        var phaseTask = action();
        try
        {
            await phaseTask.WaitAsync(deadline).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!phaseTask.IsCompleted)
        {
            throw new TimeoutException(
                $"The {phase} phase of hosted service '{registration.ServiceType.FullName}' did not complete within the stop pass's {DescribeStopTimeout()} deadline and was abandoned. The service ignores the cancellation token it was given."
            );
        }
    }

    private string DescribeStopTimeout() =>
        string.Create(CultureInfo.InvariantCulture, $"{HostedSvcStopTimeout.TotalSeconds} s");

    private async ValueTask StopHostedServicesAsync()
    {
        using var cts = new CancellationTokenSource(HostedSvcStopTimeout);
        try
        {
            await StopHostedServicesOnceAsync(cts.Token, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Disposal never throws; the individual failures were already reported by
            // StopPhaseAsync, this adds the disposal context.
            OnError?.Invoke(ex, "Error stopping hosted services during asynchronous disposal");
        }
    }
}
