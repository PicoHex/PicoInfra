namespace PicoDI;

/// <summary>
/// A high-performance, AOT-compatible dependency injection container.
/// Manages service registrations, scope creation, and singleton instance lifecycle.
/// </summary>
/// <remarks>
/// Use <c>DisposeAsync()</c> for proper asynchronous cleanup of hosted services and scopes.
/// </remarks>
public sealed partial class SvcContainer : ISvcContainer
{
    private Dictionary<Type, List<SvcRuntimeRegistration>>? _registrationCache;

    private readonly Lock _registrationLock = new();
    private readonly TrackedScopeList _rootScopes = new();

    /// <summary>
    /// Callback invoked when an exception is caught during disposal or error-recovery paths.
    /// Set this to observe errors that would otherwise be silently swallowed (default: <see langword="null"/>).
    /// </summary>
    /// <remarks>
    /// The <c>string</c> parameter provides human-readable context for the failure.
    /// The callback must not throw; exceptions from the callback itself are silently discarded.
    /// </remarks>
    public Action<Exception, string>? OnError { get; set; }

    /// <summary>
    /// Frozen (optimized) runtime registration cache after Build() is called.
    /// </summary>
    private FrozenDictionary<Type, SvcRuntimeRegistration[]>? _frozenCache;

    /// <summary>
    /// Container-internal root scope used to invoke singleton factories, so
    /// singletons never capture a (possibly short-lived) resolving scope.
    /// Lazily created; not tracked in <see cref="_rootScopes"/>; disposed by
    /// the container.
    /// </summary>
    private SvcScope? _implicitRootScope;

    internal SvcScope GetImplicitRootScope()
    {
        var scope = Volatile.Read(ref _implicitRootScope);
        if (scope is not null)
            return scope;

        var frozen = Volatile.Read(ref _frozenCache);
        if (frozen is null)
        {
            // Covers the race where the container was disposed between a
            // resolution path reading the frozen cache and entering this
            // method. Build() throws ObjectDisposedException for a disposed
            // container — a clean failure instead of a NullReferenceException.
            Build();
            frozen = Volatile.Read(ref _frozenCache);
        }

        if (frozen is null)
            throw new ObjectDisposedException(nameof(SvcContainer));

        lock (_registrationLock)
        {
            return _implicitRootScope ??= new SvcScope(frozen, this);
        }
    }

    private int _disposed;

    /// <inheritdoc />
    public bool IsRegistered(Type serviceType)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        var frozen = Volatile.Read(ref _frozenCache);
        if (frozen is not null)
            return frozen.ContainsKey(serviceType);

        var cache = Volatile.Read(ref _registrationCache);
        return cache is not null && cache.ContainsKey(serviceType);
    }

    /// <summary>
    /// Creates a new instance of <see cref="SvcContainer"/>.
    /// </summary>
    /// <param name="autoConfigureFromGenerator">
    /// When true (default), source-generated configurators are applied automatically:
    /// immediate ones (PicoDI registrations) in the constructor, deferred ones
    /// (PicoMediator auto-subscriptions) on <see cref="Build"/> — the deferred timing
    /// lets manual registrations made before Build win over generated ones.
    /// </param>
    public SvcContainer(bool autoConfigureFromGenerator = true)
    {
        _autoConfigureFromGenerator = autoConfigureFromGenerator;
        Volatile.Write(
            ref _registrationCache,
            new Dictionary<Type, List<SvcRuntimeRegistration>>()
        );
        if (autoConfigureFromGenerator)
            SvcContainerAutoConfiguration.TryApplyConfiguration(this);
    }

    private readonly bool _autoConfigureFromGenerator;

    /// <summary>
    /// Applies the deferred configurators (PicoMediator auto-subscriptions) when
    /// auto-configuration is enabled. Runs on <see cref="Build"/> so manual
    /// registrations made before Build are visible to the configurators' dedup
    /// checks. Immediate configurators already ran in the constructor.
    /// </summary>
    private void ApplyDeferredAutoConfigurationIfEnabled()
    {
        if (_autoConfigureFromGenerator)
            SvcContainerAutoConfiguration.TryApplyDeferredConfiguration(this);
    }

    /// <summary>
    /// Container-local monotonic counter for LIFO singleton disposal ordering.
    /// </summary>
    private long _singletonCreationOrder;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal long NextSingletonCreationOrder() =>
        Interlocked.Increment(ref _singletonCreationOrder);
}
