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
    /// Grace period for the stop phases that run on the container's own initiative: the
    /// disposal path and the rollback of a failed start. A cancelled start token must not
    /// skip the cleanup, and a service that ignores cancellation must not hang it forever.
    /// </summary>
    private static readonly TimeSpan HostedSvcStopTimeout = TimeSpan.FromSeconds(30);

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
        // Identity is the REGISTRATION, not the service type. A hosted type may be registered
        // more than once — two instances, two factories, the same implementation registered twice
        // with different configuration — and every one of those registrations is a live service
        // that must start and stop. Keeping only the last registration per type silently dropped
        // the earlier ones: they never started and never stopped, and the host still reported a
        // successful start.
        List<SvcRuntimeRegistration>? hosted = null;
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

                // Registration order is start order; the stop pass walks this list backwards.
                (hosted ??= []).Add(registration);
            }
        }

        if (hosted is not null)
            Volatile.Write(ref _hostedRegistrations, hosted);
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

        // One start per object. Two registrations can hand back the same instance (the same
        // object registered twice): starting it twice would be a second start on a service that
        // is already running, while the stop pass runs at most once per instance — start and stop
        // would then be asymmetric.
        var startedInstances = new HashSet<object>(ReferenceEqualityComparer.Instance);

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
                    // Resolve THIS registration's instance. Resolving by service type answers
                    // with the last registration's singleton, which would start that one twice
                    // and leave the other registration's instance never started.
                    instance = _hostingScope.ResolveRegistrationInstance(registration);
                }

                if (!startedInstances.Add(instance))
                    continue;

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

                await RollBackStartedServicesAsync(started).ConfigureAwait(false);

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
    /// Stop failures here are diagnostics only (<see cref="OnError"/>): the caller receives the
    /// start failure, never a stop failure that hides it. The stop phases use their own
    /// <see cref="HostedSvcStopTimeout"/> window because the start token may already be cancelled.
    /// The host is left <i>stopped</i> — a later <c>StopAsync</c>/<c>DisposeAsync</c> must not run
    /// a second stop over services that are already down.
    /// </remarks>
    private async ValueTask RollBackStartedServicesAsync(
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

            await StopInstanceAsync(registration, instance, cts.Token, stopFailures)
                .ConfigureAwait(false);
        }

        Interlocked.Exchange(ref _hostedState, 2);
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
    internal async Task StopHostedServicesAsync(CancellationToken cancellationToken = default)
    {
        if (!TryTransitionToStopped())
            return;

        await _hostingLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await StopHostedServicesCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _hostingLock.Release();
        }
    }

    private async Task StopHostedServicesCoreAsync(CancellationToken cancellationToken)
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

            await StopInstanceAsync(registration, instance, cancellationToken, failures)
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
        List<Exception> failures
    )
    {
        if (instance is IHostedLifecycleSvc lifecycle)
        {
            await StopPhaseAsync(
                    "Stopping",
                    registration,
                    () => lifecycle.StoppingAsync(cancellationToken),
                    failures
                )
                .ConfigureAwait(false);
            await StopPhaseAsync(
                    "Stop",
                    registration,
                    () => lifecycle.StopAsync(cancellationToken),
                    failures
                )
                .ConfigureAwait(false);
            await StopPhaseAsync(
                    "Stopped",
                    registration,
                    () => lifecycle.StoppedAsync(cancellationToken),
                    failures
                )
                .ConfigureAwait(false);
        }
        else if (instance is IHostedSvc hosted)
        {
            await StopPhaseAsync(
                    "Stop",
                    registration,
                    () => hosted.StopAsync(cancellationToken),
                    failures
                )
                .ConfigureAwait(false);
        }
    }

    private async Task StopPhaseAsync(
        string phase,
        SvcRuntimeRegistration registration,
        Func<Task> action,
        List<Exception> failures
    )
    {
        try
        {
            await action().ConfigureAwait(false);
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

    private async ValueTask StopHostedServicesAsync()
    {
        using var cts = new CancellationTokenSource(HostedSvcStopTimeout);
        try
        {
            await StopHostedServicesAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Disposal never throws; the individual failures were already reported by
            // StopPhaseAsync, this adds the disposal context.
            OnError?.Invoke(ex, "Error stopping hosted services during asynchronous disposal");
        }
    }
}
