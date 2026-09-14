namespace PicoDI;

public sealed partial class SvcScope
{
    private static string FormatSourceGenReminder(string typeName) =>
        $"Use PicoDI.Gen source generator or register with a factory delegate for '{typeName}'.";

    /// <summary>
    /// Core resolution logic shared by GetService and TryGetService.
    /// Returns null when the service is not found — caller decides whether to throw.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private object? ResolveServiceCore(Type serviceType)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        DisposalGuards.ThrowIfDisposed(ref _disposed, nameof(SvcScope));

        // Fast path: direct Volatile.Read for single-singleton (~2ns)
        if (ReferenceEquals(Volatile.Read(ref _lastSingletonType), serviceType))
            return Volatile.Read(ref _lastSingletonInstance)!;

        // Medium path: Dictionary+lock for multi-singleton (~20ns)
        if (_singletonInstances is not null)
        {
            lock (_singletonCacheLock)
            {
                if (_singletonInstances.TryGetValue(serviceType, out var cached))
                    return cached;
            }
        }

        if (!_registrationCache.TryGetValue(serviceType, out var registrations))
            return null;

        var last = registrations[^1];
        if (last.Lifetime == SvcLifetime.Singleton)
        {
            var instance = last.GetSingletonInstance();
            if (instance is not null)
            {
                CacheSingleton(serviceType, instance);
                return instance;
            }
            var created = GetOrCreateSingletonSlow(serviceType, last);
            CacheSingleton(serviceType, created);
            return created;
        }

        return ResolveByLifetime(serviceType, last);
    }

    /// <inheritdoc />
    public object GetService(Type serviceType) =>
        ResolveServiceCore(serviceType) ?? HandleServiceNotFound(serviceType);

    /// <inheritdoc />
    public bool TryGetService(Type serviceType, [NotNullWhen(true)] out object? service)
    {
        service = ResolveServiceCore(serviceType);
        return service is not null;
    }

    /// <summary>
    /// Core multi-resolution logic shared by GetServices and TryGetServices.
    /// Returns null when the service type is not registered.
    /// </summary>
    private IReadOnlyList<object>? ResolveServicesCore(Type serviceType)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        DisposalGuards.ThrowIfDisposed(ref _disposed, nameof(SvcScope));

        if (!_registrationCache.TryGetValue(serviceType, out var registrations))
            return null;

        return registrations.Select(r => ResolveByLifetime(serviceType, r)).ToArray();
    }

    /// <inheritdoc />
    public bool TryGetServices(
        Type serviceType,
        [NotNullWhen(true)] out IReadOnlyList<object>? services
    )
    {
        services = ResolveServicesCore(serviceType);
        return services is not null;
    }

    /// <inheritdoc />
    public IReadOnlyList<object> GetServices(Type serviceType)
    {
        return ResolveServicesCore(serviceType)
            ?? throw new PicoDiException(
                $"Service type '{serviceType.FullName}' is not registered."
            );
    }

    private void CacheSingleton(Type serviceType, object instance)
    {
        Volatile.Write(ref _lastSingletonType, serviceType);
        Volatile.Write(ref _lastSingletonInstance, instance);
        var cache = LazyInitializer.EnsureInitialized(ref _singletonInstances);
        lock (_singletonCacheLock)
            cache.TryAdd(serviceType, instance);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private object ResolveByLifetime(Type serviceType, SvcRuntimeRegistration registration) =>
        registration.Lifetime switch
        {
            SvcLifetime.Transient => ResolveTransient(serviceType, registration),
            SvcLifetime.Scoped => GetOrAddScopedInstance(registration),
            SvcLifetime.Singleton => GetOrCreateSingleton(serviceType, registration),
            _ => ThrowUnknownLifetime(registration.Lifetime),
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private object ResolveTransient(Type serviceType, SvcRuntimeRegistration registration)
    {
        if (registration.Factory is null)
            throw new PicoDiException(
                $"No factory registered for transient service '{serviceType.FullName}'. "
                    + FormatSourceGenReminder(serviceType.FullName ?? serviceType.Name)
            );

        var instance = registration.Factory(this);

        if (instance is IDisposable or IAsyncDisposable)
        {
            // Enqueue for lifecycle tracking. The disposal drain and this enqueue
            // run concurrently — when the scope is being disposed, the instance may
            // be disposed both by the drain and by the re-check below. All disposable
            // and async-disposable implementations MUST be idempotent (multiple calls
            // to Dispose/DisposeAsync must be safe no-ops after the first).
            var queue = LazyInitializer.EnsureInitialized(ref _trackedTransients);
            queue.Enqueue(instance);

            // Guard: concurrent DisposeAsync may have blown away the old queue
            // between the implicit first _disposed check (ResolveByLifetime does
            // not check _disposed for transients) and the call above, creating an
            // orphan collection. Drain the abandoned queue to prevent leaking
            // instances enqueued by other racing threads. The current instance
            // is skipped here — it will be disposed by the re-check below.
            if (Volatile.Read(ref _disposed) != 0)
            {
                var abandoned = Interlocked.Exchange(ref _trackedTransients, null);
                if (abandoned is not null)
                {
                    while (abandoned.TryDequeue(out var orphan))
                    {
                        if (!ReferenceEquals(orphan, instance))
                            DisposeTrackedInstance(orphan);
                    }
                }
            }
        }

        // Re-check after enqueue: if the scope was disposed during factory
        // execution, the instance was enqueued into a tracking collection
        // that may have been drained. Dispose it explicitly to prevent leaks.
        if (Volatile.Read(ref _disposed) is 0)
            return instance;
        DisposeTrackedInstance(instance);
        throw new ObjectDisposedException(nameof(SvcScope));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private object GetOrCreateSingleton(Type serviceType, SvcRuntimeRegistration registration)
    {
        return registration.GetSingletonInstance()
            ?? GetOrCreateSingletonSlow(serviceType, registration);
    }

    /// <summary>
    /// Creates a singleton instance with deadlock-safe double-checked locking.
    /// The factory is invoked OUTSIDE the lock. If two threads race to create,
    /// the winner's result is stored; the loser's duplicate is disposed.
    /// This avoids the classic DCL deadlock: factory A resolving singleton B
    /// whose factory resolves singleton A.
    /// </summary>
    /// <remarks>
    /// <b>Important:</b> The singleton factory may be called multiple times under
    /// thread contention. Only one result is stored; duplicates are disposed.
    /// Factories <b>must be idempotent</b> and free of irreversible side effects
    /// (e.g., registering global callbacks, incrementing external counters).
    /// <para>
    /// <b>Thread safety:</b> A narrow race window remains between the moment this
    /// method stores the instance under <see cref="SvcRuntimeSingletonState.SyncLock"/>
    /// and the caller receiving the reference. If the container is disposed
    /// concurrently, the disposal path may dispose the instance the caller is
    /// about to use (use-after-free). Closing this window would require reference
    /// counting, which is disproportionately expensive for a micro DI container.
    /// Callers should not resolve services while the container is being disposed —
    /// this matches Microsoft.Extensions.DependencyInjection behavior.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private object GetOrCreateSingletonSlow(Type serviceType, SvcRuntimeRegistration registration)
    {
        var singletonState =
            registration.SingletonState
            ?? throw new PicoDiException(
                $"No factory or instance registered for singleton service '{serviceType.FullName}'. "
                    + FormatSourceGenReminder(serviceType.FullName ?? serviceType.Name)
            );

        // Fast path: instance already created while we were entering
        var fastInstance = Volatile.Read(ref singletonState.Instance);
        if (fastInstance != null)
            return fastInstance;

        // Build outside the lock — prevents deadlock when factory transitively
        // resolves a service that depends on this singleton. Singleton factories
        // run against the container-internal root scope (E1 fix) so singletons
        // never capture a possibly short-lived resolving scope.
        var candidate =
            registration.Factory != null
                ? registration.Factory(Owner.GetImplicitRootScope())
                : throw new PicoDiException(
                    $"No factory or instance registered for singleton service '{serviceType.FullName}'. "
                        + FormatSourceGenReminder(serviceType.FullName ?? serviceType.Name)
                );

        lock (singletonState.SyncLock)
        {
            // Container disposal has started — discard the candidate.
            if (singletonState.Sealed)
            {
                DisposeTrackedInstance(candidate);
                throw new ObjectDisposedException(
                    nameof(SvcContainer),
                    $"Cannot create singleton '{serviceType.FullName}': the container is being disposed."
                );
            }

            var current = Volatile.Read(ref singletonState.Instance);
            if (current != null)
            {
                // Another thread won the race. Dispose our duplicate then return the winner.
                DisposeTrackedInstance(candidate);
                return current;
            }

            // Tag the construction order BEFORE publishing the instance so the
            // disposal pass can release singletons in reverse construction
            // order (LIFO) — a singleton may rely on its dependencies inside
            // its own Dispose method. Owner is immutable and never cleared by
            // scope tracking, so this is safe even when the resolving scope was
            // disposed while the factory was running.
            singletonState.CreationOrder = Owner.NextSingletonCreationOrder();
            Volatile.Write(ref singletonState.Instance, candidate);
            return candidate;
        }
    }

    /// <summary>
    /// Scope-local monotonic counter for LIFO scoped-instance disposal ordering.
    /// </summary>
    private long _scopedCreationCounter;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long NextScopedCreationOrder() => Interlocked.Increment(ref _scopedCreationCounter);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private object GetOrAddScopedInstance(SvcRuntimeRegistration registration)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(SvcScope));

        var instances = LazyInitializer.EnsureInitialized(ref _scopedInstances);

        // Guard: concurrent DisposeAsync may have created an orphan dictionary
        // between the first _disposed check and the call above. Null it out so
        // the field stays clean after dispose.
        if (Volatile.Read(ref _disposed) != 0)
        {
            // Atomically swap out the scoped-instances dictionary so the field
            // stays clean after dispose. Best-effort drain: dispose any already-
            // materialized values (Lazy.Value succeeded before the disposed check).
            var abandoned = Interlocked.Exchange(ref _scopedInstances, null);
            if (abandoned is not null)
            {
                foreach (var abandonedLazy in abandoned.Values)
                {
                    if (abandonedLazy.IsValueCreated)
                    {
                        try
                        {
                            DisposeTrackedInstance(abandonedLazy.Value);
                        }
                        catch
                        { /* best-effort */
                        }
                    }
                }
            }

            throw new ObjectDisposedException(nameof(SvcScope));
        }

        var scope = this;
        var lazy = instances.GetOrAdd(
            registration,
            _ => new Lazy<object>(
                () =>
                {
                    if (Volatile.Read(ref scope._disposed) != 0)
                        throw new ObjectDisposedException(nameof(SvcScope));

                    var instance =
                        registration.Factory != null
                            ? registration.Factory(scope)
                            : throw new PicoDiException(
                                $"No factory registered for scoped service '{registration.ServiceType.FullName}'. "
                                    + FormatSourceGenReminder(
                                        registration.ServiceType.FullName
                                            ?? registration.ServiceType.Name
                                    )
                            );

                    var order = LazyInitializer.EnsureInitialized(ref scope._scopedCreationOrder);
                    order.Enqueue((NextScopedCreationOrder(), instance));
                    return instance;
                },
                LazyThreadSafetyMode.ExecutionAndPublication
            )
        );

        var instance = lazy.Value;

        if (Volatile.Read(ref _disposed) != 0)
        {
            DisposeTrackedInstance(instance);
            throw new ObjectDisposedException(nameof(SvcScope));
        }

        return instance;
    }

    /// <summary>
    /// Synchronously disposes a tracked instance that was created during a race
    /// with scope/container disposal. This method must be synchronous because its
    /// callers are factory delegates or non-async resolution paths that need
    /// guaranteed disposal before returning (or throwing).
    ///
    /// INTENTIONAL sync-over-async bridge: race-path orphan cleanup requires deterministic
    /// disposal for file handles, sockets, and transactions. Fire-and-forget is unsafe here.
    /// Types that implement both <c>IDisposable</c> and <c>IAsyncDisposable</c>
    /// take the faster sync path (checked first); only <c>IAsyncDisposable</c>-only
    /// types may block here, which is acceptable in AOT-first environments without
    /// a <c>SynchronizationContext</c>.
    /// </summary>
    private void DisposeTrackedInstance(object instance)
    {
        switch (instance)
        {
            case IDisposable d:
                try
                {
                    d.Dispose();
                }
                catch (Exception ex)
                {
                    Owner.OnError?.Invoke(ex, "Error disposing instance");
                }

                break;
            case IAsyncDisposable ad:
                try
                {
                    ad.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Owner.OnError?.Invoke(ex, "Error disposing instance");
                }

                break;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static object HandleServiceNotFound(Type serviceType)
    {
        throw new PicoDiException(
            $"Service type '{serviceType.FullName}' is not registered. "
                + "Ensure the service is registered explicitly or that the source generator "
                + "can discover its implementation from referenced assemblies."
        );
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object ThrowUnknownLifetime(SvcLifetime lifetime) =>
        throw new ArgumentOutOfRangeException(
            nameof(SvcLifetime),
            lifetime,
            $"Unknown service lifetime '{lifetime}'."
        );
}
