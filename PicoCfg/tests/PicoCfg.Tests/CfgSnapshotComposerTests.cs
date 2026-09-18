namespace PicoCfg.Tests;

public class CfgSnapshotComposerTests
{
    [Test]
    public async Task CreateSnapshot_WithNoProviderSnapshots_ReturnsCfgSnapshotEmpty()
    {
        var snapshot = CfgSnapshotComposer.CreateSnapshot([], CreateSnapshot);

        await Assert.That(snapshot).IsSameReferenceAs(CfgSnapshot.Empty);
    }

    [Test]
    public async Task CreateSnapshot_WithSingleProviderSnapshot_ReturnsSameSnapshotReference()
    {
        ICfgSnapshot providerSnapshot = new DelegatingSnapshot(path =>
            path == "key" ? "value" : null
        );

        var snapshot = CfgSnapshotComposer.CreateSnapshot([providerSnapshot], CreateSnapshot);

        await Assert.That(snapshot).IsSameReferenceAs(providerSnapshot);
    }

    [Test]
    public async Task CreateSnapshot_WithOnlyCfgSnapshots_CallsSnapshotFactoryWithMergedVisibleValues()
    {
        IReadOnlyDictionary<string, string>? mergedValues = null;
        var first = CreateSnapshot(
            new Dictionary<string, string> { ["shared"] = "first", ["first-only"] = "1" },
            10
        );
        var second = CreateSnapshot(
            new Dictionary<string, string> { ["shared"] = "second", ["second-only"] = "2" },
            20
        );

        var snapshot = CfgSnapshotComposer.CreateSnapshot(
            [first, second],
            (values, fingerprint) =>
            {
                mergedValues = new Dictionary<string, string>(values);
                return new CfgSnapshot(values, fingerprint);
            }
        );

        await Assert.That(mergedValues).IsNotNull();
        await Assert.That(mergedValues!["shared"]).IsEqualTo("second");
        await Assert.That(mergedValues["first-only"]).IsEqualTo("1");
        await Assert.That(mergedValues["second-only"]).IsEqualTo("2");
        await Assert.That(GetValue(snapshot, "shared")).IsEqualTo("second");
    }

    [Test]
    public async Task CreateSnapshot_WithOnlyCfgSnapshots_PassesMergedFingerprintToSnapshotFactory()
    {
        IReadOnlyDictionary<string, string>? mergedValues = null;
        int? observedFingerprint = null;
        var first = CreateSnapshot(new Dictionary<string, string> { ["alpha"] = "1" }, 1);
        var second = CreateSnapshot(new Dictionary<string, string> { ["beta"] = "2" }, 2);

        _ = CfgSnapshotComposer.CreateSnapshot(
            [first, second],
            (values, fingerprint) =>
            {
                mergedValues = new Dictionary<string, string>(values);
                observedFingerprint = fingerprint;
                return new CfgSnapshot(values, fingerprint);
            }
        );

        await Assert.That(mergedValues).IsNotNull();
        var expectedFingerprint = ConfigDataComparer.ComputeFingerprint(mergedValues!);
        await Assert.That(observedFingerprint).IsEqualTo(expectedFingerprint);
    }

    [Test]
    public async Task CreateSnapshot_WithNonCfgSnapshot_FallsBackToCompositeLookup()
    {
        ICfgSnapshot first = CreateSnapshot(
            new Dictionary<string, string> { ["shared"] = "first" }
        );
        ICfgSnapshot second = new DelegatingSnapshot(path => path == "shared" ? "second" : null);

        var snapshot = CfgSnapshotComposer.CreateSnapshot([first, second], CreateSnapshot);

        await Assert.That(GetValue(snapshot, "shared")).IsEqualTo("second");
    }

    [Test]
    public async Task CreateSnapshot_WithCompositeFallback_UsesEarlierProviderWhenLaterProviderMisses()
    {
        ICfgSnapshot first = new DelegatingSnapshot(path => path == "first-only" ? "first" : null);
        ICfgSnapshot second = new DelegatingSnapshot(path => path == "shared" ? "second" : null);

        var snapshot = CfgSnapshotComposer.CreateSnapshot([first, second], CreateSnapshot);

        await Assert.That(GetValue(snapshot, "first-only")).IsEqualTo("first");
    }

    [Test]
    public async Task CreateSnapshot_WithCompositeFallback_MissingKeyReturnsMissing()
    {
        ICfgSnapshot first = new DelegatingSnapshot(path => path == "alpha" ? "1" : null);
        ICfgSnapshot second = new DelegatingSnapshot(path => path == "beta" ? "2" : null);

        var snapshot = CfgSnapshotComposer.CreateSnapshot([first, second], CreateSnapshot);

        await Assert.That(GetValue(snapshot, "missing")).IsNull();
    }

    [Test]
    public async Task SequenceEqual_WithDifferentCounts_ReturnsFalse()
    {
        var equal = CfgSnapshotComposer.SequenceEqual(
            [CreateSnapshot(new Dictionary<string, string> { ["alpha"] = "1" })],
            []
        );

        await Assert.That(equal).IsFalse();
    }

    [Test]
    public async Task SequenceEqual_WithSameReferencesInSameOrder_ReturnsTrue()
    {
        ICfgSnapshot first = CreateSnapshot(new Dictionary<string, string> { ["alpha"] = "1" });
        ICfgSnapshot second = CreateSnapshot(new Dictionary<string, string> { ["beta"] = "2" });

        var equal = CfgSnapshotComposer.SequenceEqual([first, second], [first, second]);

        await Assert.That(equal).IsTrue();
    }

    [Test]
    public async Task SequenceEqual_WithEquivalentButDistinctSnapshots_ReturnsFalse()
    {
        ICfgSnapshot first = CreateSnapshot(new Dictionary<string, string> { ["alpha"] = "1" });
        ICfgSnapshot second = CreateSnapshot(new Dictionary<string, string> { ["alpha"] = "1" });

        var equal = CfgSnapshotComposer.SequenceEqual([first], [second]);

        await Assert.That(equal).IsFalse();
    }

    [Test]
    public async Task SequenceEqual_WithSameReferencesInDifferentOrder_ReturnsFalse()
    {
        ICfgSnapshot first = CreateSnapshot(new Dictionary<string, string> { ["alpha"] = "1" });
        ICfgSnapshot second = CreateSnapshot(new Dictionary<string, string> { ["beta"] = "2" });

        var equal = CfgSnapshotComposer.SequenceEqual([first, second], [second, first]);

        await Assert.That(equal).IsFalse();
    }

    private static CfgSnapshot CreateSnapshot(
        IReadOnlyDictionary<string, string> values,
        int fingerprint
    )
    {
        return new CfgSnapshot(values, fingerprint);
    }

    private static CfgSnapshot CreateSnapshot(IReadOnlyDictionary<string, string> values)
    {
        return new CfgSnapshot(values, ConfigDataComparer.ComputeFingerprint(values));
    }

    private static string? GetValue(ICfgSnapshot snapshot, string path)
    {
        return snapshot.TryGetValue(path, out var value) ? value : null;
    }

    [Test]
    public async Task CreateSnapshot_WithCompositeFallback_BuildsCaseInsensitiveViewsOnce()
    {
        // The fallback resolves case-insensitively per provider; those
        // per-provider views must be built once, not re-enumerated per lookup.
        ICfgSnapshot lower = new EnumerableSnapshot(
            new Dictionary<string, string> { ["KEY"] = "low" }
        );
        ICfgSnapshot higher = new EnumerableSnapshot(
            new Dictionary<string, string> { ["Key"] = "high" }
        );
        var snapshot = CfgSnapshotComposer.CreateSnapshot([lower, higher], CreateSnapshot);

        await Assert.That(GetValue(snapshot, "key")).IsEqualTo("high");
        for (var i = 0; i < 3; i++)
            await Assert.That(GetValue(snapshot, "absent")).IsNull();

        var composite = (CfgSnapshotComposer.CompositeCfgSnapshot)snapshot;
        await Assert.That(composite.ProviderValuePasses).IsEqualTo(2); // one pass per provider
    }

    [Test]
    public async Task CreateSnapshot_WithCompositeFallback_ExactHitsDoNotBuildViews()
    {
        // The common exact-hit path must not pay for case-insensitive views: they
        // are only needed when a lookup misses exactly within a provider.
        ICfgSnapshot lower = new EnumerableSnapshot(
            new Dictionary<string, string> { ["shared"] = "low" }
        );
        ICfgSnapshot higher = new EnumerableSnapshot(
            new Dictionary<string, string> { ["shared"] = "high" }
        );
        var snapshot = CfgSnapshotComposer.CreateSnapshot([lower, higher], CreateSnapshot);

        await Assert.That(GetValue(snapshot, "shared")).IsEqualTo("high");

        var composite = (CfgSnapshotComposer.CompositeCfgSnapshot)snapshot;
        await Assert.That(composite.ProviderValuePasses).IsEqualTo(0);
    }

    [Test]
    public async Task CreateSnapshot_WithCompositeFallback_MergesValuesCaseInsensitively()
    {
        // Enumeration must agree with lookup: one logical entry per
        // case-insensitive key, with the highest-precedence provider winning
        // both the key text and the value.
        ICfgSnapshot lower = new EnumerableSnapshot(
            new Dictionary<string, string> { ["KEY"] = "low" }
        );
        ICfgSnapshot higher = new EnumerableSnapshot(
            new Dictionary<string, string> { ["Key"] = "high" }
        );
        var snapshot = CfgSnapshotComposer.CreateSnapshot([lower, higher], CreateSnapshot);

        var all = snapshot.GetAllValues();
        await Assert.That(all.Count).IsEqualTo(1);
        await Assert.That(all.Keys.Single()).IsEqualTo("Key");
        await Assert.That(all["KEY"]).IsEqualTo("high");
    }

    [Test]
    public async Task CreateSnapshot_WithCompositeFallback_ConcurrentFirstMiss_BuildsViewOnce()
    {
        // Exact-once view construction: a second concurrent first miss inside the
        // same provider must wait for the in-flight build, not enumerate again.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new BlockingEnumerableSnapshot(
            new Dictionary<string, string> { ["Key"] = "high" },
            release.Task
        );
        ICfgSnapshot other = new EnumerableSnapshot(
            new Dictionary<string, string> { ["other"] = "x" }
        );
        var snapshot = CfgSnapshotComposer.CreateSnapshot([other, probe], CreateSnapshot);

        var first = Task.Run(() => GetValue(snapshot, "key"));
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); // first enumeration in flight

        // Release shortly after the second call blocks. The release delay only
        // controls when the blocked call proceeds; no assertion depends on it.
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            release.TrySetResult();
        });

        // Runs on the test thread: a correct slot blocks here until release and
        // reuses the published view; a duplicate build would enumerate again now.
        var second = GetValue(snapshot, "key");

        await Assert.That(probe.EnumerationCount).IsEqualTo(1);
        await Assert.That(await first).IsEqualTo("high");
        await Assert.That(second).IsEqualTo("high");
    }

    [Test]
    public async Task CreateSnapshot_WithCompositeFallback_TransientEnumerationFailure_IsRetried()
    {
        // A failed view build must not poison the provider: the next lookup retries
        // (Lazy caches factory exceptions and would rethrow them forever).
        ICfgSnapshot flaky = new FlakyEnumerableSnapshot(
            new Dictionary<string, string> { ["Key"] = "high" },
            failuresBeforeSuccess: 1
        );
        ICfgSnapshot other = new EnumerableSnapshot(
            new Dictionary<string, string> { ["other"] = "x" }
        );
        var snapshot = CfgSnapshotComposer.CreateSnapshot([other, flaky], CreateSnapshot);

        await Assert.That(() => GetValue(snapshot, "key")).Throws<InvalidOperationException>();

        // the failure was transient — the next lookup must retry, not rethrow
        Exception? retryFailure = null;
        string? value = null;
        try
        {
            value = GetValue(snapshot, "key");
        }
        catch (Exception ex)
        {
            retryFailure = ex;
        }

        await Assert.That(retryFailure).IsNull();
        await Assert.That(value).IsEqualTo("high");
    }

    [Test]
    public async Task CreateSnapshot_WithCompositeFallback_ReentrantEnumeration_FailsFast()
    {
        // A provider whose GetAllValues() resolves through the snapshot that
        // contains it must fail fast with a clear exception instead of recursing
        // until the stack overflows. Re-entry depth is bounded so the test itself
        // cannot crash the process.
        ICfgSnapshot[] holder = new ICfgSnapshot[1];
        ICfgSnapshot reentrant = new ReentrantEnumerableSnapshot(
            new Dictionary<string, string> { ["Key"] = "high" },
            () => holder[0].TryGetValue("key", out _),
            reentries: 5
        );
        ICfgSnapshot other = new EnumerableSnapshot(
            new Dictionary<string, string> { ["other"] = "x" }
        );
        holder[0] = CfgSnapshotComposer.CreateSnapshot([other, reentrant], CreateSnapshot);

        Exception? failure = null;
        try
        {
            _ = GetValue(holder[0], "key");
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        await Assert.That(failure).IsTypeOf<InvalidOperationException>();
        await Assert.That(failure!.Message).Contains("Re-entrant");
    }

    private sealed class ReentrantEnumerableSnapshot(
        IReadOnlyDictionary<string, string> values,
        Func<bool> reenter,
        int reentries
    ) : ICfgSnapshot
    {
        private int _remainingReentries = reentries;

        public bool TryGetValue(string path, out string? value) =>
            values.TryGetValue(path, out value); // exact lookup never hits the queried key

        public IReadOnlyDictionary<string, string> GetAllValues()
        {
            if (_remainingReentries-- > 0)
                _ = reenter(); // resolves through the composite that contains this provider
            return values;
        }
    }

    private sealed class FlakyEnumerableSnapshot(
        IReadOnlyDictionary<string, string> values,
        int failuresBeforeSuccess
    ) : ICfgSnapshot
    {
        private int _enumerations;

        public bool TryGetValue(string path, out string? value) =>
            values.TryGetValue(path, out value);

        public IReadOnlyDictionary<string, string> GetAllValues()
        {
            if (Interlocked.Increment(ref _enumerations) <= failuresBeforeSuccess)
                throw new InvalidOperationException("transient enumeration failure");
            return values;
        }
    }

    private sealed class BlockingEnumerableSnapshot(
        IReadOnlyDictionary<string, string> values,
        Task release
    ) : ICfgSnapshot
    {
        private int _enumerations;

        public readonly TaskCompletionSource Entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public int EnumerationCount => Volatile.Read(ref _enumerations);

        public bool TryGetValue(string path, out string? value) =>
            values.TryGetValue(path, out value);

        public IReadOnlyDictionary<string, string> GetAllValues()
        {
            Interlocked.Increment(ref _enumerations);
            Entered.TrySetResult();
            release.GetAwaiter().GetResult(); // GetAllValues is synchronous
            return values;
        }
    }

    private sealed class EnumerableSnapshot(IReadOnlyDictionary<string, string> values)
        : ICfgSnapshot
    {
        public bool TryGetValue(string path, out string? value) =>
            values.TryGetValue(path, out value);

        public IReadOnlyDictionary<string, string> GetAllValues() => values;
    }

    private sealed class DelegatingSnapshot(Func<string, string?> resolver) : ICfgSnapshot
    {
        public bool TryGetValue(string path, out string? value)
        {
            value = resolver(path);
            return value is not null;
        }

        public IReadOnlyDictionary<string, string> GetAllValues() =>
            new Dictionary<string, string>();
    }
}
