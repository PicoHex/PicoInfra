namespace PicoCfg;

internal static class CfgSnapshotComposer
{
    public static ICfgSnapshot CreateSnapshot(
        IReadOnlyList<ICfgSnapshot> providerSnapshots,
        Func<IReadOnlyDictionary<string, string>, int, CfgSnapshot> snapshotFactory
    )
    {
        return providerSnapshots.Count switch
        {
            0 => CfgSnapshot.Empty,
            1 => providerSnapshots[0],
            _ => CreateMultiProviderSnapshot(providerSnapshots, snapshotFactory),
        };
    }

    public static bool SequenceEqual(
        IReadOnlyList<ICfgSnapshot> left,
        IReadOnlyList<ICfgSnapshot> right
    )
    {
        if (left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            if (!ReferenceEquals(left[i], right[i]))
                return false;
        }

        return true;
    }

    private static ICfgSnapshot CreateMultiProviderSnapshot(
        IReadOnlyList<ICfgSnapshot> providerSnapshots,
        Func<IReadOnlyDictionary<string, string>, int, CfgSnapshot> snapshotFactory
    )
    {
        // Flatten native snapshots on the reload path so steady-state reads stay on a single dictionary lookup.
        return TryCreateFlattenedSnapshot(providerSnapshots, snapshotFactory, out var snapshot)
            ? snapshot
            : CreateCompositeFallbackSnapshot(providerSnapshots);
    }

    private static bool TryCreateFlattenedSnapshot(
        IReadOnlyList<ICfgSnapshot> providerSnapshots,
        Func<IReadOnlyDictionary<string, string>, int, CfgSnapshot> snapshotFactory,
        out ICfgSnapshot snapshot
    )
    {
        snapshot = CfgSnapshot.Empty;
        var capacity = 0;

        // Validate all snapshots are native CfgSnapshot before allocating the merge array.
        for (var i = 0; i < providerSnapshots.Count; i++)
        {
            if (providerSnapshots[i] is not CfgSnapshot cfgSnapshot)
                return false;

            capacity += cfgSnapshot.Values.Count;
        }

        var dictionaries = new IReadOnlyDictionary<string, string>[providerSnapshots.Count];
        for (var i = 0; i < providerSnapshots.Count; i++)
            dictionaries[i] = ((CfgSnapshot)providerSnapshots[i]).Values;

        var mergedValues = new Dictionary<string, string>(capacity);
        foreach (var t in dictionaries)
        {
            // Merge in provider order so later providers override earlier ones.
            foreach (var (key, value) in t)
                mergedValues[key] = value;
        }

        snapshot = snapshotFactory(
            mergedValues,
            ConfigDataComparer.ComputeFingerprint(mergedValues)
        );
        return true;
    }

    private static ICfgSnapshot CreateCompositeFallbackSnapshot(
        IReadOnlyList<ICfgSnapshot> providerSnapshots
    )
    {
        // Arbitrary ICfgSnapshot implementations can have custom lookup behavior, so fallback preserves
        // provider order and resolves values at read time instead of flattening away that behavior.
        // This keeps custom semantics intact, but steady-state reads become a provider scan rather than
        // the single dictionary lookup used by fully native composed snapshots.
        return new CompositeCfgSnapshot(providerSnapshots);
    }

    internal sealed class CompositeCfgSnapshot(IReadOnlyList<ICfgSnapshot> snapshots) : ICfgSnapshot
    {
        private ViewSlot[]? _caseInsensitiveViews;

        /// <summary>TEST HOOK — number of successful provider value enumerations
        /// performed while building case-insensitive views. Contract: exactly one
        /// successful build per provider, lazily on the first exact miss inside it
        /// — never per lookup, and never for lookups that hit exactly. A failed
        /// build is not counted and is retried by the next lookup.</summary>
        internal long ProviderValuePasses;

        public bool TryGetValue(string path, out string? value)
        {
            // Exact lookups first: a provider's own TryGetValue may resolve keys
            // outside GetAllValues, and the common exact-hit path must not build
            // any view. Each provider's case-insensitive view is built lazily on
            // the first exact miss within it; snapshots are immutable, so a view
            // never goes stale.
            var views = ViewSlots();

            for (var i = snapshots.Count - 1; i >= 0; i--)
            {
                if (snapshots[i].TryGetValue(path, out value))
                    return true;

                if (views[i].Get().TryGetValue(path, out value))
                    return true;
            }

            value = null;
            return false;
        }

        private ViewSlot[] ViewSlots() =>
            LazyInitializer.EnsureInitialized(ref _caseInsensitiveViews, CreateViewSlots);

        private ViewSlot[] CreateViewSlots()
        {
            var slots = new ViewSlot[snapshots.Count];
            for (var i = 0; i < slots.Length; i++)
            {
                var index = i;
                slots[i] = new ViewSlot(() => BuildCaseInsensitiveView(snapshots[index]));
            }
            return slots;
        }

        /// <summary>Per-provider case-insensitive view. Built exactly once under
        /// concurrency (waiters reuse the winner) — unlike <c>Lazy</c>, a failed
        /// build is NOT cached: the view is published only on success, so a
        /// transient enumeration failure is retried by the next lookup. A provider
        /// that re-enters this same view while it is building (a circular config
        /// graph) fails fast instead of recursing until the stack overflows.</summary>
        private sealed class ViewSlot(Func<Dictionary<string, string>> factory)
        {
            private readonly object _gate = new();
            private Dictionary<string, string>? _view;
            private bool _building;

            public Dictionary<string, string> Get()
            {
                var view = Volatile.Read(ref _view);
                if (view is not null)
                    return view;

                lock (_gate)
                {
                    view = _view;
                    if (view is not null)
                        return view;

                    // Only reachable through Monitor re-entrancy: another thread
                    // would be blocked on _gate rather than observe _building.
                    if (_building)
                        throw new InvalidOperationException(
                            "Re-entrant case-insensitive view build: a provider's "
                                + "GetAllValues() must not resolve through the snapshot "
                                + "that contains it."
                        );

                    _building = true;
                    try
                    {
                        view = factory(); // throws propagate; nothing is cached
                        Volatile.Write(ref _view, view); // published only on success
                        return view;
                    }
                    finally
                    {
                        _building = false;
                    }
                }
            }
        }

        private Dictionary<string, string> BuildCaseInsensitiveView(ICfgSnapshot snapshot)
        {
            var view = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, candidate) in snapshot.GetAllValues())
                view.TryAdd(key, candidate); // first enumerated key wins (previous semantics)
            ProviderValuePasses++;
            return view;
        }

        /// <summary>
        /// Returns all configuration values from all providers merged with the same
        /// case-insensitive, highest-precedence rules as <see cref="TryGetValue"/>:
        /// one entry per case-insensitive key, so enumeration agrees with lookup
        /// instead of surfacing duplicate case variants. Providers that cannot
        /// enumerate their keys contribute nothing here (exact lookup still works).
        /// </summary>
        public IReadOnlyDictionary<string, string> GetAllValues()
        {
            var views = ViewSlots();
            var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = snapshots.Count - 1; i >= 0; i--)
            {
                foreach (var (key, value) in views[i].Get())
                    merged.TryAdd(key, value); // highest precedence wins key text and value
            }
            return merged;
        }
    }
}
