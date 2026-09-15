namespace PicoDI.Abs;

/// <summary>
/// Shared registry for source-generated module configuration (PicoDI, PicoMediator).
/// Generated code registers configurators by stable id; containers apply the
/// snapshot exactly once. Ordinal-sorted application order keeps behavior
/// deterministic across assemblies.
/// </summary>
/// <remarks>
/// <para>
/// Two groups exist:
/// <list type="bullet">
/// <item><b>immediate</b> — applied when a container is constructed (and by
/// explicit <c>ConfigureGeneratedServices()</c> calls). PicoDI's compile-time
/// registration markers rely on this timing: they throw
/// <c>SourceGeneratorRequiredException</c> unless the container is already
/// marked as generated-configured.</item>
/// <item><b>deferred</b> — applied by <c>Build()</c> (or an explicit apply such
/// as <c>AddPicoMediator()</c>). Deferring lets manual registrations made
/// before Build win over generated ones via the configurators' per-service
/// dedup — used by PicoMediator's auto-subscriptions.</item>
/// </list>
/// </para>
/// <para>
/// All public methods are lock-protected: module initializers race with
/// concurrent container creation. Configurators always run OUTSIDE the lock —
/// running them under the lock deadlocks with the loader lock when a
/// configurator's first JIT of a cold assembly triggers that assembly's module
/// initializer (2026-08-05 deadlock regression, mirrored in both consumers).
/// </para>
/// </remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public static class GeneratorConfiguratorRegistry
{
    private static readonly object Sentinel = new();
    private static readonly Lock RegistryLock = new();
    private static readonly Group Immediate = new();
    private static readonly Group Deferred = new();

    private sealed class Group
    {
        public readonly Dictionary<string, Action<ISvcContainer>> Configurators = new(
            StringComparer.Ordinal
        );
        public Action<ISvcContainer>[]? SortedSnapshot;
        public readonly ConditionalWeakTable<ISvcContainer, object> Applied = new();

        public void Clear()
        {
            Configurators.Clear();
            SortedSnapshot = null;
            Applied.Clear();
        }
    }

    /// <summary>
    /// Registers an immediate configurator using a stable identifier so repeated
    /// module initializers replace the existing registration instead of appending
    /// a duplicate. Applied when the container is constructed.
    /// </summary>
    public static void Register(string configuratorId, Action<ISvcContainer> configurator) =>
        RegisterCore(Immediate, configuratorId, configurator);

    /// <summary>
    /// Registers a deferred configurator using a stable identifier. Applied by
    /// <c>Build()</c> (or an explicit apply such as <c>AddPicoMediator()</c>), so
    /// manual registrations made before Build win over generated ones.
    /// </summary>
    public static void RegisterDeferred(
        string configuratorId,
        Action<ISvcContainer> configurator
    ) => RegisterCore(Deferred, configuratorId, configurator);

    private static void RegisterCore(
        Group group,
        string configuratorId,
        Action<ISvcContainer> configurator
    )
    {
        ArgumentNullException.ThrowIfNull(configuratorId);
        ArgumentNullException.ThrowIfNull(configurator);

        lock (RegistryLock)
        {
            group.Configurators[configuratorId] = configurator;
            group.SortedSnapshot = null;
        }
    }

    /// <summary>True when any immediate configurator has been registered.</summary>
    public static bool HasAny
    {
        get
        {
            lock (RegistryLock)
            {
                return Immediate.Configurators.Count > 0;
            }
        }
    }

    /// <summary>True when any deferred configurator has been registered.</summary>
    public static bool HasAnyDeferred
    {
        get
        {
            lock (RegistryLock)
            {
                return Deferred.Configurators.Count > 0;
            }
        }
    }

    /// <summary>
    /// Applies all immediate configurators to the given container exactly once.
    /// If any configurator throws, the container is NOT marked as applied,
    /// allowing retries on subsequent calls.
    /// </summary>
    public static bool TryApply(ISvcContainer container) => TryApplyCore(Immediate, container);

    /// <summary>
    /// Applies all deferred configurators to the given container exactly once.
    /// If any configurator throws, the container is NOT marked as applied,
    /// allowing retries on subsequent calls.
    /// </summary>
    public static bool TryApplyDeferred(ISvcContainer container) =>
        TryApplyCore(Deferred, container);

    private static bool TryApplyCore(Group group, ISvcContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);

        // Bookkeeping under the lock; configurators run OUTSIDE it (loader-lock
        // deadlock regression, see class remarks).
        Action<ISvcContainer>[]? snapshot;
        lock (RegistryLock)
        {
            if (group.Applied.TryGetValue(container, out _))
                return false;

            snapshot = group.SortedSnapshot;
            if (snapshot is null)
            {
                if (group.Configurators.Count is 0)
                    return false;

                var list = new List<KeyValuePair<string, Action<ISvcContainer>>>(
                    group.Configurators
                );
                list.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
                snapshot = new Action<ISvcContainer>[list.Count];
                for (var i = 0; i < list.Count; i++)
                    snapshot[i] = list[i].Value;
                group.SortedSnapshot = snapshot;
            }

            // Mark applied BEFORE running the configurators: two concurrent
            // callers for the same container must not both run them.
            group.Applied.Add(container, Sentinel);
        }

        try
        {
            foreach (var configurator in snapshot)
                configurator(container);
            return true;
        }
        catch
        {
            // Roll back the marker so a later call can retry (documented
            // contract: "if any configurator throws, the container is NOT
            // marked as applied").
            lock (RegistryLock)
                group.Applied.Remove(container);
            throw;
        }
    }

    /// <summary>True when the container has been marked as immediately configured.</summary>
    public static bool HasApplied(ISvcContainer container) => HasAppliedCore(Immediate, container);

    /// <summary>True when the container has been marked as deferred-configured.</summary>
    public static bool HasAppliedDeferred(ISvcContainer container) =>
        HasAppliedCore(Deferred, container);

    private static bool HasAppliedCore(Group group, ISvcContainer container)
    {
        lock (RegistryLock)
        {
            return group.Applied.TryGetValue(container, out _);
        }
    }

    /// <summary>Marks the container as configured without running configurators (idempotent).</summary>
    public static void MarkApplied(ISvcContainer container)
    {
        lock (RegistryLock)
        {
            if (!Immediate.Applied.TryGetValue(container, out _))
                Immediate.Applied.Add(container, Sentinel);
        }
    }

    /// <summary>Clears all registrations and applied markers for isolated test execution.</summary>
    public static void Clear()
    {
        lock (RegistryLock)
        {
            Immediate.Clear();
            Deferred.Clear();
        }
    }
}
