namespace PicoDI;

public sealed partial class SvcContainer
{
    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        // Order of the groups is a dependency order: a disposer may use what it depends on, so
        // a dependency is released after its dependents. Each group keeps its own inner order
        // (hosted services LIFO by start order, singletons LIFO by construction, scopes
        // children-before-parents).
        //
        // 1. Hosted services stop first: their StopAsync may still use any service, including
        //    singletons, and nothing else may run before they are down.
        await StopHostedServicesAsync().ConfigureAwait(false);

        // 2. User root scopes: a scoped Dispose may use the singletons it depends on, so every
        //    scope is disposed before the singleton pass. Until review-2026-11-02 finding 3
        //    this ran AFTER the singletons, so a scoped Dispose touched disposed singletons.
        await DisposeRootScopesAsync().ConfigureAwait(false);

        // 3. The container-internal hosting scope (the scope hosted services are resolved from).
        //    It is also tracked as a root scope, so the pass above normally already disposed it;
        //    this call keeps the order explicit and handles the untracked case.
        await DisposeHostingScopeAsync().ConfigureAwait(false);

        // 4. Container-owned singletons, LIFO by construction order, while the scoped/transient
        //    instances their factories captured are still alive.
        await DisposeSingletonInstancesAsync().ConfigureAwait(false);

        // 5. The implicit root scope owns exactly those captured instances, so it is released
        //    last — after the singletons that captured them.
        await DisposeImplicitRootScopeAsync().ConfigureAwait(false);
    }

    private async ValueTask DisposeImplicitRootScopeAsync()
    {
        var scope = Interlocked.Exchange(ref _implicitRootScope, null);
        if (scope is not null)
        {
            try
            {
                await scope.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex, "Error disposing implicit root scope");
            }
        }
    }

    private async ValueTask DisposeHostingScopeAsync()
    {
        var scope = Interlocked.Exchange(ref _hostingScope, null);
        if (scope is not null)
        {
            try
            {
                await scope.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex, "Error disposing hosting scope");
            }
        }
    }

    private async ValueTask DisposeRootScopesAsync()
    {
        foreach (var scope in DetachAllRootScopes())
        {
            try
            {
                await scope.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex, "Error disposing root scope asynchronously");
            }
        }
    }

    /// <summary>
    /// Enumerates all singleton registrations across frozen and mutable caches.
    /// Prefers the frozen cache when available; falls back to the mutable cache
    /// when Dispose is called before any scope was created.
    /// </summary>
    private IEnumerable<SvcRuntimeRegistration> EnumerateSingletonStates()
    {
        var frozen = Volatile.Read(ref _frozenCache);
        if (frozen is not null)
        {
            foreach (var kvp in frozen)
            foreach (var reg in kvp.Value)
                yield return reg;

            yield break;
        }

        var cache = Volatile.Read(ref _registrationCache);
        if (cache is not null)
        {
            foreach (var kvp in cache)
            foreach (var reg in kvp.Value)
                yield return reg;
        }
    }

    private async ValueTask DisposeSingletonInstancesAsync()
    {
        var disposedInstances = new HashSet<object>();

        // Seal all singleton states before the first pass so that in-flight
        // scope resolutions cannot create new singletons that would be missed
        // by both passes (the race window between the two passes).
        foreach (var registration in EnumerateSingletonStates())
            registration.SealSingletonState();

        // Collect (CreationOrder, instance, registration) tuples so we can
        // dispose container-owned singletons in reverse construction order.
        // A singleton may use its dependencies inside Dispose; LIFO order
        // guarantees those dependencies are still alive at that point.
        // TakeSingletonInstanceUnderLock atomically reads and clears Instance
        // under SyncLock, establishing happens-before with any in-flight
        // GetOrCreateSingletonSlow that wrote the instance under the same lock.
        var owned = new List<(long Order, object Instance)>();
        foreach (var registration in EnumerateSingletonStates())
        {
            var instance = registration.TakeSingletonInstanceUnderLock();
            if (instance is null)
                continue;

            // Skip user-supplied instances (Bug 9): caller retains ownership,
            // matching Microsoft.Extensions.DependencyInjection semantics.
            if (registration.SingletonState is { OwnsInstance: false })
            {
                disposedInstances.Add(instance);
                continue;
            }

            // CreationOrder is 0 for entries that were never constructed by
            // the slow path (defensive — should not happen for OwnsInstance=true
            // when Instance is non-null, but treat them as oldest).
            owned.Add((registration.SingletonState?.CreationOrder ?? 0, instance));
        }

        // Sort descending by CreationOrder so the most recently constructed
        // singleton is disposed first.
        owned.Sort(static (a, b) => b.Order.CompareTo(a.Order));

        foreach (var (_, instance) in owned)
        {
            try
            {
                await DisposalGuards
                    .DisposeInstanceAsync(instance, disposedInstances)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(
                    ex,
                    $"Error disposing service instance of type '{instance.GetType().FullName}'"
                );
            }
        }

        // Second pass: catch singletons that were materialized during the first
        // pass by in-flight factory calls from scopes being concurrently disposed.
        // Same LIFO ordering applies.
        var second = new List<(long Order, object Instance)>();
        foreach (var registration in EnumerateSingletonStates())
        {
            var instance = registration.TakeSingletonInstanceUnderLock();
            if (instance is null || !disposedInstances.Add(instance))
                continue;

            // Skip user-supplied instances (Bug 9).
            if (registration.SingletonState is { OwnsInstance: false })
                continue;

            second.Add((registration.SingletonState?.CreationOrder ?? 0, instance));
        }

        second.Sort(static (a, b) => b.Order.CompareTo(a.Order));

        foreach (var (_, instance) in second)
        {
            try
            {
                await DisposalGuards
                    .DisposeInstanceAsync(instance, disposedInstances)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(
                    ex,
                    $"Error disposing service instance of type '{instance.GetType().FullName}'"
                );
            }
        }

        // Volatile.Write establishes a release fence: all singleton disposal writes
        // (including TakeSingletonInstanceUnderLock and DisposeAsync calls above)
        // are guaranteed to be visible before the null assignments below. This
        // prevents a concurrent scope resolution from observing a non-null cache
        // while singleton instances have already been taken for disposal, ensuring
        // the scope either gets the pre-disposal instance or a null (which it handles).
        //
        // Ordering among the three writes is not required here: all three are
        // executed within the same disposal path, and no concurrent reader can
        // observe intermediate states after _disposed has been set to 1 at the
        // entry of DisposeAsync (checked by DisposalGuards.ThrowIfDisposed).
        Volatile.Write(ref _registrationCache, null);
        Volatile.Write(ref _frozenCache, null);
    }
}
