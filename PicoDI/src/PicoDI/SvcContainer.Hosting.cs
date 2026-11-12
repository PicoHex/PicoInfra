namespace PicoDI;

public sealed partial class SvcContainer
{
    private List<SvcRuntimeRegistration>? _hostedRegistrations;

    /// <summary>
    /// Hosted-service instances that finished their start sequence, in <b>start</b> order, with the
    /// position their registration had in <see cref="_hostedRegistrations"/>. This is the list the
    /// stop pass walks backwards: the registration list cannot answer "in which order did these
    /// start", because it is grouped by service type and one object may be named by several
    /// registrations (see <see cref="TrackHostedServicesFromRegistrations"/>).
    /// </summary>
    /// <remarks>Guarded by <see cref="_hostingLock"/>: written by the start pass, read by the stop pass.</remarks>
    private List<StartedHostedSvc>? _startedHostedServices;

    /// <summary>
    /// One hosted registration that finished its start sequence: the registration that started it,
    /// the object it started, and that registration's position in <see cref="_hostedRegistrations"/> —
    /// the position used to tell N registrations of one type apart in diagnostics.
    /// </summary>
    private readonly record struct StartedHostedSvc(
        SvcRuntimeRegistration Registration,
        object Instance,
        int RegistrationIndex,
        int RegistrationCount
    );

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
    /// Wall-clock budget of ONE hosted service inside a stop pass that runs on the container's own
    /// initiative: the disposal path and the rollback of a failed start. A cancelled start token
    /// must not skip the cleanup, and a service that ignores cancellation must not hang it forever
    /// — each service gets this much time in total, after which its remaining phases are abandoned
    /// and reported as <see cref="TimeoutException"/> (review-2026-11-02 defect L5: this used to be
    /// a <c>CancellationTokenSource</c> that bounded nothing, because a token only signals).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per service, not per pass (review-2026-11-02 Medium 2): a shared budget meant the first
    /// service that ignored its token consumed all of it, and every service behind it was never
    /// stopped at all — while still being recorded as stopped, so nothing could ever stop it later.
    /// The cost of one hung service is now one budget; the cost of N hung services is N budgets,
    /// which is the bound <c>StopHostedServicesAsync</c> already promises ("every service is still
    /// stopped"). A lifecycle service's three stop phases share its budget.
    /// </para>
    /// <para>
    /// An instance member, not a static, so a test can shrink the window without affecting a test
    /// that runs in parallel. The caller-driven <c>StopAsync(token)</c> path is not bounded by it:
    /// there the caller's token is the control surface.
    /// </para>
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

                // Start order is per-type group, then registration order inside the group: this
                // list is built by walking `cache`, a Dictionary<Type, List<...>>, so registering
                // A1, B, A2 starts a1, a2, b — NOT a1, b, a2. The stop pass does not re-walk it;
                // it walks the start-ordered _startedHostedServices backwards, so stop is the exact
                // reverse of start whatever the grouping is (rev-hosted-dedup finding 3).
                (hosted ??= []).Add(registration);
            }
        }

        if (hosted is not null)
            Volatile.Write(ref _hostedRegistrations, hosted);
    }

    /// <summary>
    /// Starts all registered hosted services in tracked-registration order.
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

        // Services whose start sequence completed, in start order, kept as container state so the
        // stop pass reverses the same order it saw. A local used to be enough because the stop pass
        // re-walked the registration list instead — which stops a shared instance at its LAST
        // registration while it started at its FIRST (rev-hosted-dedup finding 1).
        var started = new List<StartedHostedSvc>(registrations.Count);
        _startedHostedServices = started;

        // One start per object. Two registrations can hand back the same instance (the same
        // object registered twice): starting it twice would be a second start on a service that
        // is already running, while the stop pass runs at most once per instance — start and stop
        // would then be asymmetric.
        var startedInstances = new HashSet<object>(ReferenceEqualityComparer.Instance);

        // Iterate in start order (forward) — matches the IHostedService contract; the stop pass
        // walks this same list backwards.
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

                started.Add(new StartedHostedSvc(registration, instance, i, registrations.Count));
            }
            catch (Exception ex)
            {
                // Diagnostic channel first — it used to be the ONLY channel, which is
                // what made a failed start report success (review-2026-11-02 finding 2).
                // The label names the registration's position, so N registrations of one type
                // do not produce N byte-identical messages (rev-hosted-dedup finding 4).
                OnError?.Invoke(
                    ex,
                    $"Error in {phase} phase of {DescribeRegistration(registration, i, registrations.Count)}"
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
                // the caller receives — Data is the channel that keeps that exception unwrapped.
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
    /// <see cref="RollBackStopFailuresKey"/> (plus <see cref="OnError"/>). Each service gets its own
    /// <see cref="HostedSvcStopTimeout"/> stop budget — see
    /// <see cref="StopInstanceWithinBudgetAsync"/> — so a service that ignores the token costs one
    /// budget and is abandoned instead of hanging the rollback forever, and the services behind it
    /// still get stopped. The host is left <i>stopped</i> — a later
    /// <c>StopAsync</c>/<c>DisposeAsync</c> must not run a second stop over services already down.
    /// </remarks>
    private async ValueTask<List<Exception>> RollBackStartedServicesAsync(
        List<StartedHostedSvc> started
    )
    {
        var stopFailures = new List<Exception>();

        for (int i = started.Count - 1; i >= 0; i--)
        {
            var entry = started[i];
            if (!_stoppedHostedInstances.Add(entry.Instance))
                continue;

            await StopInstanceWithinBudgetAsync(entry, stopFailures).ConfigureAwait(false);
        }

        Interlocked.Exchange(ref _hostedState, 2);
        return stopFailures;
    }

    /// <summary>
    /// Stops every started hosted service in reverse <i>start</i> order (LIFO).
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
    /// disposal path (<see cref="StopHostedServicesDuringDisposalAsync"/>) can run it without the
    /// disposed guard — <see cref="DisposeAsync"/> sets the flag it would be checking, and a
    /// disposal path never throws.
    /// </summary>
    /// <param name="cancellationToken">Token handed to the services.</param>
    /// <param name="boundEachServiceToBudget">
    /// <c>true</c> for the passes the container runs on its own initiative (disposal, failed-start
    /// rollback): every service is then stopped inside its own <see cref="HostedSvcStopTimeout"/>
    /// budget. <c>false</c> (default) for the caller-driven <c>StopAsync</c>: there the caller's
    /// token is the control surface and an unbounded wait is the documented behaviour.
    /// </param>
    private async Task StopHostedServicesOnceAsync(
        CancellationToken cancellationToken,
        bool boundEachServiceToBudget = false
    )
    {
        if (!TryTransitionToStopped())
            return;

        await _hostingLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await StopHostedServicesCoreAsync(cancellationToken, boundEachServiceToBudget)
                .ConfigureAwait(false);
        }
        finally
        {
            _hostingLock.Release();
        }
    }

    private async Task StopHostedServicesCoreAsync(
        CancellationToken cancellationToken,
        bool boundEachServiceToBudget
    )
    {
        // The list the start pass built: every started registration, in start order. The
        // registration list is the wrong list to walk — it is grouped by service type and names a
        // shared object once per registration, so reversing it stops that object at its LAST
        // registration position while a later-started service is still running
        // (rev-hosted-dedup finding 1).
        var started = _startedHostedServices;
        if (started is null or { Count: 0 })
            return;

        var failures = new List<Exception>();

        // Iterate in reverse START order (LIFO) — matches IHostedService contract.
        for (int i = started.Count - 1; i >= 0; i--)
        {
            var entry = started[i];

            // At most one stop sequence per instance: a rolled-back start or a StopAsync
            // that was already queued on the hosting lock must not stop a service twice.
            if (!_stoppedHostedInstances.Add(entry.Instance))
                continue;

            if (boundEachServiceToBudget)
            {
                // Disposal and the failed-start rollback run on the container's own initiative:
                // nobody is waiting with a token there, so each service gets its own budget.
                await StopInstanceWithinBudgetAsync(entry, failures).ConfigureAwait(false);
            }
            else
            {
                // The caller-driven StopAsync: the caller's token is the control surface, and an
                // unbounded wait is the documented behaviour.
                await StopInstanceAsync(entry, cancellationToken, failures).ConfigureAwait(false);
            }
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
    /// Runs the stop sequence of one hosted service inside its own
    /// <see cref="HostedSvcStopTimeout"/> budget — one budget per <i>service</i>, not per pass
    /// (review-2026-11-02 Medium 2).
    /// </summary>
    /// <remarks>
    /// A shared pass budget made the first service that ignored its token consume all of it, so
    /// every service behind it was never stopped at all (<c>StopPhaseAsync</c> reported it as "not
    /// run: the deadline had already expired") while still being recorded in
    /// <see cref="_stoppedHostedInstances"/> — nothing could ever stop it later. A per-service budget
    /// keeps the bound honest in both directions: a hang costs one service's budget instead of
    /// silently starving the rest, and the pass still terminates, with at most N budgets for N
    /// services. The token is both the cancellation signal the service receives and the deadline
    /// its phases are awaited with, so a service that <i>honours</i> it is told to wrap up at its
    /// own deadline rather than at a shared one it may never reach.
    /// </remarks>
    private async Task StopInstanceWithinBudgetAsync(
        StartedHostedSvc started,
        List<Exception> failures
    )
    {
        // Armed by the watchdog thread, not by a CancellationTokenSource(TimeSpan) timer: a timer
        // callback is ThreadPool work, so under a starved pool the token was cancelled late or never
        // (docs/release-notes/v2026.11.0.md, "Deadlines a starved ThreadPool does not honour").
        // The token is still the signal the service receives — it now arrives at the deadline.
        using var budget = ShutdownDeadline.CancelAt(HostedSvcStopTimeout);
        await StopInstanceAsync(started, budget.Token, failures, budget).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the stop sequence of one hosted service. Each phase is isolated: a throwing phase
    /// is reported to <see cref="OnError"/> and added to <paramref name="failures"/>, and the
    /// remaining phases still run — same fault isolation as before, minus the swallowing.
    /// </summary>
    /// <remarks>
    /// A non-cancellable <c>deadline</c> (the caller-driven <c>StopAsync</c> path) means no budget:
    /// there the caller's token is the only control surface. Otherwise it is this service's stop
    /// budget — its phases are awaited through it and at most one of them can consume it, so a
    /// lifecycle service reports its remaining phases as "not run" rather than blaming them.
    /// </remarks>
    private async Task StopInstanceAsync(
        StartedHostedSvc started,
        CancellationToken cancellationToken,
        List<Exception> failures,
        DeadlineSignal? deadline = null
    )
    {
        var label = DescribeRegistration(
            started.Registration,
            started.RegistrationIndex,
            started.RegistrationCount
        );

        if (started.Instance is IHostedLifecycleSvc lifecycle)
        {
            await StopPhaseAsync(
                    "Stopping",
                    label,
                    () => lifecycle.StoppingAsync(cancellationToken),
                    failures,
                    deadline
                )
                .ConfigureAwait(false);
            await StopPhaseAsync(
                    "Stop",
                    label,
                    () => lifecycle.StopAsync(cancellationToken),
                    failures,
                    deadline
                )
                .ConfigureAwait(false);
            await StopPhaseAsync(
                    "Stopped",
                    label,
                    () => lifecycle.StoppedAsync(cancellationToken),
                    failures,
                    deadline
                )
                .ConfigureAwait(false);
        }
        else if (started.Instance is IHostedSvc hosted)
        {
            await StopPhaseAsync(
                    "Stop",
                    label,
                    () => hosted.StopAsync(cancellationToken),
                    failures,
                    deadline
                )
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Diagnostic label for one hosted registration: the service type plus the registration's
    /// position in the tracked list. Without the position, N registrations of one type produce N
    /// byte-identical messages (rev-hosted-dedup finding 4). Only the registered service type is
    /// named — never the resolved instance's runtime type, which may be an internal decorator, so
    /// nothing internal leaks into a message the caller can see.
    /// </summary>
    private static string DescribeRegistration(
        SvcRuntimeRegistration registration,
        int registrationIndex,
        int registrationCount
    ) =>
        $"hosted service '{registration.ServiceType.FullName}' (registration {registrationIndex + 1} of {registrationCount})";

    private async Task StopPhaseAsync(
        string phase,
        string serviceLabel,
        Func<Task> action,
        List<Exception> failures,
        DeadlineSignal? deadline = null
    )
    {
        try
        {
            await RunPhaseWithinDeadlineAsync(action, deadline, phase, serviceLabel)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            OnError?.Invoke(ex, $"Error in {phase} phase of {serviceLabel}");
            failures.Add(ex);
        }
    }

    /// <summary>
    /// Awaits one stop phase and enforces the budget of the service it belongs to.
    /// </summary>
    /// <remarks>
    /// A <see cref="CancellationTokenSource"/> only signals: handing its token to a service that
    /// ignores it bounds nothing, which is what made <see cref="HostedSvcStopTimeout"/> a claim
    /// rather than a bound (review-2026-11-02 defect L5). Awaiting through
    /// <see cref="Task.WaitAsync(CancellationToken)"/> makes the wait itself end at the deadline,
    /// and the deadline is <see cref="DeadlineSignal"/> rather than a <see cref="CancellationTokenSource"/>
    /// timer precisely so that a starved pool cannot defer it: the cancel is performed by a
    /// dedicated watchdog thread and the wait's continuation is inlined onto it, instead of both
    /// being queued to a pool that has no worker to give
    /// (<c>docs/release-notes/v2026.11.0.md</c>, "Deadlines a starved ThreadPool does not honour",
    /// records the timer-shaped defect this replaced). The abandoned phase is reported as a
    /// <see cref="TimeoutException"/> and the pass keeps
    /// going with its remaining services, so a hung service cannot hold a disposal or a rollback.
    /// The budget belongs to one service, so "the pass keeps going" includes the services behind
    /// the hung one instead of starving them (review-2026-11-02 Medium 2). A phase whose budget had
    /// already expired — the second and third phase of a lifecycle service whose first one consumed
    /// it — is reported without being started, so the failure says what happened instead of blaming
    /// a service that never ran.
    /// </remarks>
    private async Task RunPhaseWithinDeadlineAsync(
        Func<Task> action,
        DeadlineSignal? deadline,
        string phase,
        string serviceLabel
    )
    {
        if (deadline is null)
        {
            await action().ConfigureAwait(false);
            return;
        }

        if (deadline.IsExpired)
            throw new TimeoutException(
                $"The {phase} phase of {serviceLabel} was not run: its {DescribeStopTimeout()} stop budget had already expired."
            );

        var phaseTask = action();
        try
        {
            // The wait is unchanged; only where its cancel comes from is: the deadline's token is
            // cancelled by the watchdog thread at the budget, so the completion below is inlined
            // onto that thread instead of queued to a pool that may have no worker to give.
            await phaseTask.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!phaseTask.IsCompleted)
        {
            // Nothing is waiting for the phase now: the only observer it can still have is the one
            // this attaches (review-2026-11-02 Medium 1).
            ObserveAbandonedPhase(phaseTask, phase, serviceLabel);

            throw new TimeoutException(
                $"The {phase} phase of {serviceLabel} did not complete within its {DescribeStopTimeout()} stop deadline and was abandoned. It keeps running while the shutdown proceeds, and it cannot be stopped again."
            );
        }
    }

    /// <summary>
    /// Attaches the only observer an abandoned phase has left.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An abandoned phase is not cancelled — <see cref="Task.WaitAsync(CancellationToken)"/> stops
    /// waiting, the task keeps running, and nothing awaits it again. A fault it raises later
    /// therefore has no awaiter: it surfaces as an unobserved task exception when the task is
    /// finalized, and <see cref="OnError"/> defaults to <see langword="null"/>, so in a default
    /// configuration the whole thing is silent (review-2026-11-02 Medium 1). The service is also
    /// already recorded in <see cref="_stoppedHostedInstances"/>, so no later `StopAsync` can run
    /// it a second time — this report is the last thing that will ever be heard about it.
    /// </para>
    /// <para>
    /// Observability only: it cancels nothing, retries nothing and delays nothing, and it is the
    /// same exception the start and stop paths propagate — the service's own, not
    /// <see cref="Task.Exception"/>'s aggregate wrapper. It cannot fire twice, because a task
    /// completes once.
    /// </para>
    /// <para>
    /// Why <see cref="OnError"/> and not the <see cref="Exception.Data"/> bag the rollback failures
    /// use — the decision review-2026-11-02 Medium 1 asks to justify: the abandonment itself is
    /// already surfaced synchronously on both passes. The rollback folds its
    /// <see cref="TimeoutException"/> into <see cref="RollBackStopFailuresKey"/> and rethrows the
    /// start exception; <see cref="DisposeAsync"/> reports it to <see cref="OnError"/>, because a
    /// disposal path never throws and a <see cref="ValueTask"/> has no result to carry one. This
    /// later fault arrives <i>after</i> that call returned, so folding it into the same list would
    /// give one bag two meanings and would append to a <c>List&lt;Exception&gt;</c> the caller may
    /// already be enumerating — from a thread-pool thread, on a list that is not thread-safe.
    /// Reporting it here keeps "present only when something failed" a property of one call rather
    /// than of an instant in time. There is no retry surface to offer either: the instance is in
    /// <see cref="_stoppedHostedInstances"/>, and after disposal <c>StopAsync</c> faults, so a
    /// second stop of this instance cannot happen.
    /// </para>
    /// </remarks>
    private void ObserveAbandonedPhase(Task phaseTask, string phase, string serviceLabel)
    {
        _ = phaseTask.ContinueWith(
            static (task, state) =>
            {
                var (container, phaseName, serviceName) = ((SvcContainer, string, string))state!;
                var error =
                    task.Exception!.InnerExceptions.Count == 1
                        ? task.Exception.InnerException!
                        : task.Exception;

                container.OnError?.Invoke(
                    error,
                    $"The {phaseName} phase of {serviceName} was abandoned at the stop deadline and failed later"
                );
            },
            (this, phase, serviceLabel),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    private string DescribeStopTimeout() =>
        string.Create(CultureInfo.InvariantCulture, $"{HostedSvcStopTimeout.TotalSeconds} s");

    /// <summary>
    /// The stop pass the disposal path runs — named for its caller (review-2026-11-02 Low 4) so
    /// that the throwing <see cref="StopHostedServicesAsync(CancellationToken)"/> cannot be reached
    /// by accident from a path that must never throw.
    /// </summary>
    /// <remarks>
    /// Swallows what the pass throws into <see cref="OnError"/>: the individual failures were
    /// already reported by <c>StopPhaseAsync</c>, this only adds the disposal context.
    /// </remarks>
    private async ValueTask StopHostedServicesDuringDisposalAsync()
    {
        try
        {
            await StopHostedServicesOnceAsync(default, boundEachServiceToBudget: true)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Disposal never throws; the individual failures were already reported by
            // StopPhaseAsync, this adds the disposal context.
            OnError?.Invoke(ex, "Error stopping hosted services during asynchronous disposal");
        }
    }
}
