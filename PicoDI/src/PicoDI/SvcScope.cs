namespace PicoDI;

/// <summary>
/// Represents a service scope that manages the lifetime of scoped service instances.
/// Supports hierarchical scopes where child scopes are automatically disposed when the parent is disposed.
/// </summary>
/// <remarks>
/// <para>Scope disposal order: when scopes are created in sequence (A → B → C) but
/// disposed out of order (e.g. A before B), the disposal path correctly walks the
/// parent chain via <c>FindNearestActiveAncestor</c> to find the still-active scope.
/// This is safe but non-standard — the expected pattern is LIFO (C → B → A).</para>
/// </remarks>
public sealed partial class SvcScope : ISvcScope
{
    private readonly FrozenDictionary<Type, SvcRuntimeRegistration[]> _registrationCache;

    // Fast singleton instance cache — bypasses registration lookup on warm path.
    // Uses Dictionary+lock for multi-singleton (~20ns), but the last-published singleton
    // is one Volatile.Read of the immutable (type, instance) holder plus a Type reference
    // compare (~2ns): no lock, no dictionary lookup, no allocation on a hit. The pair is a
    // single reference on purpose — two fields published by two Volatile.Writes let a reader
    // pair the NEW type with the PREVIOUS type's instance and hand out the wrong singleton
    // (review-2026-11-02 finding 1: 2 302 wrong objects in 12.8 M resolves). Publishing a
    // pair allocates one holder per (scope, singleton type), i.e. once per scope for a given
    // service, never per resolve.
    private Dictionary<Type, object>? _singletonInstances;
    private readonly Lock _singletonCacheLock = new();
    private SingletonCacheEntry? _lastSingleton; // Volatile: immutable (type, instance) pair

    /// <summary>
    /// Immutable (service type, singleton instance) pair for the warm fast path. Assigned to
    /// <see cref="_lastSingleton"/> with a single volatile write, so a reader that observes the
    /// reference observes a fully built pair: a type next to an instance it does not belong to
    /// (a torn pair) is not representable.
    /// </summary>
    private sealed class SingletonCacheEntry(Type type, object instance)
    {
        internal readonly Type Type = type;
        internal readonly object Instance = instance;
    }

    // Scoped instances: ConcurrentDictionary with Lazy<T>(ExecutionAndPublication).
    // The Lazy wrapper ensures the factory is called at most once per *successful*
    // construction, even under contention where ConcurrentDictionary.GetOrAdd may
    // invoke the valueFactory multiple times. Discarded Lazy wrappers are lightweight
    // and never execute. An entry whose factory threw is removed again by
    // GetOrAddScopedInstance, because Lazy caches the exception too — see the
    // retry/eviction note there.
    private ConcurrentDictionary<SvcRuntimeRegistration, Lazy<object>>? _scopedInstances;

    // Tracks creation order of scoped instances within this scope.
    // Used by the disposal pass to release instances in reverse construction
    // order (LIFO), so a scoped service can safely use its dependencies in
    // its own Dispose method.
    private ConcurrentQueue<(long Order, object Instance)>? _scopedCreationOrder;

    // Transient disposable instances: ConcurrentQueue for lock-free enqueue on the hot path.
    // Lazily initialized — most scopes never resolve transient IDisposable/IAsyncDisposable services.
    private ConcurrentQueue<object>? _trackedTransients;

    private readonly TrackedScopeList _childScopes = new();

    internal SvcContainer? OwningContainer;
    internal SvcScope? ParentScope;
    internal SvcScope? PreviousTrackedScope;
    internal SvcScope? NextTrackedScope;

    private int _disposed; // 0 = not disposed, 1 = disposed (for thread-safe Interlocked operations)

    internal SvcScope(
        FrozenDictionary<Type, SvcRuntimeRegistration[]> registrationCache,
        SvcContainer owner
    )
    {
        _registrationCache = registrationCache;
        Owner = owner;
    }

    /// <summary>
    /// Immutable owning container reference, assigned once at construction.
    /// Unlike <see cref="OwningContainer"/> — which tracking-list bookkeeping
    /// clears when a scope is detached/disposed — this reference is never
    /// cleared, so in-flight singleton creation stays safe when a scope is
    /// disposed concurrently with resolution.
    /// </summary>
    internal SvcContainer Owner { get; }
}
