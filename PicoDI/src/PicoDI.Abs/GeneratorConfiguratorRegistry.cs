namespace PicoDI.Abs;

/// <summary>
/// Shared registry for source-generated module configuration (PicoDI, PicoMediator).
/// Generated code registers configurators by stable id; containers apply the
/// snapshot exactly once. Ordinal-sorted application order keeps behavior
/// deterministic across assemblies.
/// </summary>
/// <remarks>
/// All public methods are lock-protected: module initializers race with
/// concurrent container creation. Configurators always run OUTSIDE the lock —
/// running them under the lock deadlocks with the loader lock when a
/// configurator's first JIT of a cold assembly triggers that assembly's module
/// initializer (2026-08-05 deadlock regression, mirrored in both consumers).
/// </remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public static class GeneratorConfiguratorRegistry
{
    private static readonly object Sentinel = new();
    private static readonly Lock RegistryLock = new();
    private static readonly Dictionary<string, Action<ISvcContainer>> Configurators = new(
        StringComparer.Ordinal
    );
    private static Action<ISvcContainer>[]? SortedSnapshot;
    private static readonly ConditionalWeakTable<ISvcContainer, object> Applied = new();

    /// <summary>
    /// Registers a configurator using a stable identifier so repeated module
    /// initializers replace the existing registration instead of appending a
    /// duplicate.
    /// </summary>
    public static void Register(string configuratorId, Action<ISvcContainer> configurator)
    {
        ArgumentNullException.ThrowIfNull(configuratorId);
        ArgumentNullException.ThrowIfNull(configurator);

        lock (RegistryLock)
        {
            Configurators[configuratorId] = configurator;
            SortedSnapshot = null;
        }
    }

    /// <summary>True when any configurator has been registered.</summary>
    public static bool HasAny
    {
        get
        {
            lock (RegistryLock)
            {
                return Configurators.Count > 0;
            }
        }
    }

    /// <summary>
    /// Applies all registered configurators to the given container exactly
    /// once. If any configurator throws, the container is NOT marked as
    /// applied, allowing retries on subsequent calls.
    /// </summary>
    public static bool TryApply(ISvcContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);

        // Bookkeeping under the lock; configurators run OUTSIDE it (loader-lock
        // deadlock regression, see class remarks).
        Action<ISvcContainer>[]? snapshot;
        lock (RegistryLock)
        {
            if (Applied.TryGetValue(container, out _))
                return false;

            snapshot = SortedSnapshot;
            if (snapshot is null)
            {
                if (Configurators.Count is 0)
                    return false;

                var list = new List<KeyValuePair<string, Action<ISvcContainer>>>(Configurators);
                list.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
                snapshot = new Action<ISvcContainer>[list.Count];
                for (var i = 0; i < list.Count; i++)
                    snapshot[i] = list[i].Value;
                SortedSnapshot = snapshot;
            }

            // Mark applied BEFORE running the configurators: two concurrent
            // callers for the same container must not both run them.
            Applied.Add(container, Sentinel);
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
                Applied.Remove(container);
            throw;
        }
    }

    /// <summary>True when the container has been marked as configured.</summary>
    public static bool HasApplied(ISvcContainer container)
    {
        lock (RegistryLock)
        {
            return Applied.TryGetValue(container, out _);
        }
    }

    /// <summary>Marks the container as configured without running configurators (idempotent).</summary>
    public static void MarkApplied(ISvcContainer container)
    {
        lock (RegistryLock)
        {
            if (!Applied.TryGetValue(container, out _))
                Applied.Add(container, Sentinel);
        }
    }

    /// <summary>Clears all registrations and applied markers for isolated test execution.</summary>
    public static void Clear()
    {
        lock (RegistryLock)
        {
            Configurators.Clear();
            SortedSnapshot = null;
            Applied.Clear();
        }
    }
}
