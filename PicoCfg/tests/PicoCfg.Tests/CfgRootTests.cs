namespace PicoCfg.Tests;

public class CfgRootTests
{
    private static ICfgSnapshot SnapshotOf(CfgRoot root) =>
        ((IInternalCfgRootSnapshotAccessor)root).CurrentSnapshot;

    [Test]
    public async Task Snapshot_WithNoProviders_ReturnsMissingValues()
    {
        var root = TestCfgFactory.CreateRoot([]);

        await Assert.That(root.GetValue("key")).IsNull();
    }

    [Test]
    public async Task Snapshot_WithSingleProvider_ReturnsProviderValue()
    {
        var provider = new MockProvider([new Dictionary<string, string> { ["key"] = "value" }]);
        var root = TestCfgFactory.CreateRoot([provider]);

        await Assert.That(root.GetValue("key")).IsEqualTo("value");
    }

    [Test]
    public async Task Snapshot_WithMultipleProviders_UsesLastProviderValue()
    {
        var provider1 = new MockProvider([new Dictionary<string, string> { ["key"] = "first" }]);
        var provider2 = new MockProvider([new Dictionary<string, string> { ["key"] = "second" }]);
        var root = TestCfgFactory.CreateRoot([provider1, provider2]);

        await Assert.That(root.GetValue("key")).IsEqualTo("second");
    }

    [Test]
    public async Task ReloadAsync_RefreshesSnapshotFromProviders()
    {
        var provider = new MockProvider([
            new Dictionary<string, string> { ["key"] = "before" },
            new Dictionary<string, string> { ["key"] = "after" },
        ]);
        var root = TestCfgFactory.CreateRoot([provider]);
        var originalSnapshot = SnapshotOf(root);

        var changed = await root.ReloadAsync();

        await Assert.That(provider.ReloadCount).IsEqualTo(1);
        await Assert.That(changed).IsTrue();
        await Assert.That(SnapshotOf(root)).IsNotSameReferenceAs(originalSnapshot);
        await Assert.That(root.GetValue("key")).IsEqualTo("after");
        await Assert.That(originalSnapshot.GetValue("key")).IsEqualTo("before");
    }

    [Test]
    public async Task ReloadAsync_WithoutDataChange_KeepsCurrentSnapshot()
    {
        var provider = new MockProvider([
            new Dictionary<string, string> { ["key"] = "same" },
            new Dictionary<string, string> { ["key"] = "same" },
        ]);
        var root = TestCfgFactory.CreateRoot([provider]);
        var originalSnapshot = SnapshotOf(root);

        var changed = await root.ReloadAsync();

        await Assert.That(changed).IsFalse();
        await Assert.That(SnapshotOf(root)).IsSameReferenceAs(originalSnapshot);
    }

    [Test]
    public async Task ReloadAsync_WhenProviderReloadFails_PublishesObservedProviderStateBeforeRethrowing()
    {
        var provider1 = new MockProvider([
            new Dictionary<string, string> { ["key"] = "before" },
            new Dictionary<string, string> { ["key"] = "after" },
        ]);
        var provider2 = new FailingReloadProvider();
        var root = TestCfgFactory.CreateRoot([provider1, provider2]);
        var originalSnapshot = SnapshotOf(root);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waitTask = root.WaitForChangeAsync(cts.Token).AsTask();

        await Assert.That(async () => await root.ReloadAsync()).Throws<InvalidOperationException>();
        await waitTask;
        await Assert.That(SnapshotOf(root)).IsNotSameReferenceAs(originalSnapshot);
        await Assert.That(root.GetValue("key")).IsEqualTo("after");
        await Assert.That(originalSnapshot.GetValue("key")).IsEqualTo("before");
    }

    [Test]
    public async Task ReloadAsync_WhenProviderReloadFailsWithoutObservedChange_KeepsCurrentSnapshotAndSignal()
    {
        var provider1 = new MockProvider([new Dictionary<string, string> { ["key"] = "same" }]);
        var provider2 = new FailingReloadProvider();
        var root = TestCfgFactory.CreateRoot([provider1, provider2]);
        var originalSnapshot = SnapshotOf(root);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.That(async () => await root.ReloadAsync()).Throws<InvalidOperationException>();
        await Assert.That(SnapshotOf(root)).IsSameReferenceAs(originalSnapshot);
        await Assert
            .That(async () => await root.WaitForChangeAsync(cts.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(root.GetValue("key")).IsEqualTo("same");
    }

    [Test]
    public async Task ReloadAsync_RunsProvidersInParallel()
    {
        var coordination = new ParallelReloadCoordination(expectedConcurrentReloads: 2);
        var provider1 = new ConcurrentReloadProvider("first", "value1", coordination);
        var provider2 = new ConcurrentReloadProvider("second", "value2", coordination);
        var root = TestCfgFactory.CreateRoot([provider1, provider2]);

        var changed = await root.ReloadAsync();

        await Assert.That(changed).IsTrue();
        await Assert.That(provider1.MaxConcurrentReloads).IsEqualTo(1);
        await Assert.That(provider2.MaxConcurrentReloads).IsEqualTo(1);
        await Assert.That(coordination.MaxObservedConcurrentReloads).IsEqualTo(2);
        await Assert.That(root.GetValue("first")).IsEqualTo("value1");
        await Assert.That(root.GetValue("second")).IsEqualTo("value2");
    }

    [Test]
    public async Task ReloadAsync_ConcurrentCalls_AreSerialized()
    {
        var provider = new GatedReloadProvider();
        var root = TestCfgFactory.CreateRoot([provider]);

        var reload1 = root.ReloadAsync().AsTask();
        await provider.WaitForFirstEntryAsync();
        var reload2 = root.ReloadAsync().AsTask();

        await provider.AllowFirstReloadToComplete();
        await reload1;
        await reload2;

        await Assert.That(provider.MaxConcurrentReloads).IsEqualTo(1);
    }

    [Test]
    public async Task WaitForChangeAsync_CompletesWhenRootReloadUpdatesSnapshot()
    {
        var provider = new MockProvider([
            new Dictionary<string, string> { ["key"] = "before" },
            new Dictionary<string, string> { ["key"] = "after" },
        ]);
        var root = TestCfgFactory.CreateRoot([provider]);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waitTask = root.WaitForChangeAsync(cts.Token).AsTask();
        var changed = await root.ReloadAsync();

        await Assert.That(changed).IsTrue();
        await waitTask;
        await Assert.That(waitTask.IsCompletedSuccessfully).IsTrue();
    }

    [Test]
    public async Task WaitForChangeAsync_DoesNotCompleteUntilRootSnapshotChanges()
    {
        var provider = new MockProvider([new Dictionary<string, string> { ["key"] = "value" }]);
        var root = TestCfgFactory.CreateRoot([provider]);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var waitTask = root.WaitForChangeAsync(cts.Token).AsTask();
        provider.NotifyChanged();

        await Assert.That(async () => await waitTask).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task WaitForChangeAsync_NewWaitTracksNextReload()
    {
        var provider = new MockProvider([
            new Dictionary<string, string> { ["key"] = "before" },
            new Dictionary<string, string> { ["key"] = "after" },
        ]);
        var root = TestCfgFactory.CreateRoot([provider]);

        using var firstCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var firstWait = root.WaitForChangeAsync(firstCts.Token).AsTask();
        var changed = await root.ReloadAsync();
        await firstWait;

        using var secondCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var secondWait = root.WaitForChangeAsync(secondCts.Token).AsTask();

        await Assert.That(changed).IsTrue();
        await Assert.That(firstWait.IsCompletedSuccessfully).IsTrue();
        await Assert.That(async () => await secondWait).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task DisposeAsync_DisposesProviders()
    {
        var provider = new MockProvider([new Dictionary<string, string> { ["key"] = "value" }]);
        var root = TestCfgFactory.CreateRoot([provider]);

        await root.DisposeAsync();

        await Assert.That(provider.DisposeCalled).IsTrue();
    }

    [Test]
    public async Task DisposeAsync_WhenProviderDisposeFails_DisposesRemainingProviders()
    {
        var firstProvider = new MockProvider(
            [new Dictionary<string, string> { ["first"] = "value" }],
            disposeException: new InvalidOperationException("First dispose failed.")
        );
        var secondProvider = new MockProvider(
            [new Dictionary<string, string> { ["second"] = "value" }],
            disposeException: new InvalidOperationException("Second dispose failed.")
        );
        var root = TestCfgFactory.CreateRoot([firstProvider, secondProvider]);

        var dispose = async () => await root.DisposeAsync();

        await Assert.That(dispose).Throws<AggregateException>();
        await Assert.That(firstProvider.DisposeCalled).IsTrue();
        await Assert.That(secondProvider.DisposeCalled).IsTrue();
    }

    [Test]
    public async Task DisposeAsync_WhenSingleProviderDisposeFails_RethrowsOriginalException()
    {
        var provider = new MockProvider(
            [new Dictionary<string, string> { ["key"] = "value" }],
            disposeException: new InvalidOperationException("Dispose failed.")
        );
        var root = TestCfgFactory.CreateRoot([provider]);

        await Assert
            .That(async () => await root.DisposeAsync())
            .Throws<InvalidOperationException>();
        await Assert.That(provider.DisposeCalled).IsTrue();
    }

    [Test]
    public async Task ReloadAsync_WhenProviderCancellationOccurs_PublishesObservedProviderStateBeforeRethrowing()
    {
        using var cancelSource = new CancellationTokenSource();
        var provider1 = new MockProvider([
            new Dictionary<string, string> { ["key"] = "before" },
            new Dictionary<string, string> { ["key"] = "after" },
        ]);
        var provider2 = new CancelingReloadProvider(cancelSource);
        var root = TestCfgFactory.CreateRoot([provider1, provider2]);
        var originalSnapshot = SnapshotOf(root);
        using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waitTask = root.WaitForChangeAsync(waitCts.Token).AsTask();

        await Assert
            .That(async () => await root.ReloadAsync(cancelSource.Token))
            .Throws<OperationCanceledException>();
        await waitTask;
        await Assert.That(SnapshotOf(root)).IsNotSameReferenceAs(originalSnapshot);
        await Assert.That(root.GetValue("key")).IsEqualTo("after");
        await Assert.That(originalSnapshot.GetValue("key")).IsEqualTo("before");
    }

    [Test]
    public async Task ReloadAsync_WhenLaterProviderThrowsSynchronously_WaitsForStartedReloadsBeforePublishing()
    {
        var provider1 = new DeferredPublishingProvider("key", "before", "after");
        var provider2 = new FailingReloadProvider();
        var root = TestCfgFactory.CreateRoot([provider1, provider2]);
        var originalSnapshot = SnapshotOf(root);

        var reloadTask = root.ReloadAsync().AsTask();
        await provider1.WaitForReloadStartedAsync();
        await Assert.That(reloadTask.IsCompleted).IsFalse();

        provider1.CompleteReload();

        await Assert.That(async () => await reloadTask).Throws<InvalidOperationException>();
        await Assert.That(SnapshotOf(root)).IsNotSameReferenceAs(originalSnapshot);
        await Assert.That(root.GetValue("key")).IsEqualTo("after");
        await Assert.That(originalSnapshot.GetValue("key")).IsEqualTo("before");
    }

    [Test]
    public async Task ReloadAsync_WhenLaterProviderThrowsSynchronously_RethrowsCapturedFailureAfterStartedFaultsSettle()
    {
        var expected = new InvalidOperationException("Synchronous start failure.");
        var provider1 = new DeferredFailingProvider(
            new ApplicationException("Deferred reload failure.")
        );
        var provider2 = new ThrowingReloadProvider(expected);
        var root = TestCfgFactory.CreateRoot([provider1, provider2]);

        var reloadTask = root.ReloadAsync().AsTask();
        await provider1.WaitForReloadStartedAsync();
        provider1.FailReload();

        var thrown = await Assert
            .That(async () => await reloadTask)
            .Throws<InvalidOperationException>();
        await Assert.That(thrown).IsSameReferenceAs(expected);
    }

    [Test]
    public async Task DisposeAsync_ConcurrentCalls_DisposeProvidersOnlyOnce()
    {
        var provider = new CountingDisposeProvider();
        var root = TestCfgFactory.CreateRoot([provider]);

        await Task.WhenAll(
            root.DisposeAsync().AsTask(),
            root.DisposeAsync().AsTask(),
            root.DisposeAsync().AsTask()
        );

        await Assert.That(provider.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task ReloadAsync_WhenProviderSequenceChangesButVisibleValueStaysOverridden_PublishesNewSnapshot()
    {
        var provider1 = new MockProvider([
            new Dictionary<string, string> { ["shared"] = "first-before" },
            new Dictionary<string, string> { ["shared"] = "first-after" },
        ]);
        var provider2 = new MockProvider([
            new Dictionary<string, string> { ["shared"] = "second" },
        ]);
        var root = TestCfgFactory.CreateRoot([provider1, provider2]);
        var originalSnapshot = SnapshotOf(root);

        var changed = await root.ReloadAsync();

        await Assert.That(changed).IsTrue();
        await Assert.That(SnapshotOf(root)).IsNotSameReferenceAs(originalSnapshot);
        await Assert.That(root.GetValue("shared")).IsEqualTo("second");
    }

    [Test]
    public async Task Snapshot_WithCustomSnapshots_UsesCompositeLookupSemantics()
    {
        var provider1 = new StaticProvider(
            new DelegatingSnapshot(path => path == "shared" ? "first" : null)
        );
        var provider2 = new StaticProvider(
            new DelegatingSnapshot(path => path == "shared" ? "second" : null)
        );
        var root = TestCfgFactory.CreateRoot([provider1, provider2]);

        await Assert.That(root.GetValue("shared")).IsEqualTo("second");
    }

    [Test]
    public async Task Dispose_WithStuckReload_DoesNotFaultTheReload()
    {
        // Mechanism: disposal must not destroy the reload gate while a
        // non-cooperative reload still holds it — its Release() would throw
        // ObjectDisposedException and mask the reload's real outcome.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new BlockingProvider(release.Task);
        var root = TestCfgFactory.CreateRoot(
            [provider],
            reloadDrainTimeout: TimeSpan.FromMilliseconds(50),
            reloadDrainRetryTimeout: TimeSpan.FromMilliseconds(50)
        );

        var reload = root.ReloadAsync().AsTask();
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); // reload holds the gate
        await root.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        release.TrySetResult();
        Exception? failure = null;
        try
        {
            await reload.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        await Assert.That(failure is ObjectDisposedException).IsFalse();
    }

    private sealed class BlockingProvider(Task release) : ICfgProvider
    {
        public readonly TaskCompletionSource Entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public ICfgSnapshot Snapshot { get; } = new EmptySnapshot();

        public async ValueTask<bool> ReloadAsync(CancellationToken ct = default)
        {
            Entered.TrySetResult();
            await release.ConfigureAwait(false); // deliberately ignores cancellation
            return false;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EmptySnapshot : ICfgSnapshot
    {
        public bool TryGetValue(string path, out string? value)
        {
            value = null;
            return false;
        }

        public IReadOnlyDictionary<string, string> GetAllValues() =>
            new Dictionary<string, string>();
    }

    [Test]
    public async Task CompositeSnapshot_MixedCaseDuplicateKeys_ResolveWithCaseInsensitivePrecedence()
    {
        // Custom (non-native) snapshots go through the composite fallback. Keys
        // that differ only by case must resolve with case-insensitive precedence
        // (the later provider wins) regardless of which case the caller asks for.
        var lower = new CaseSensitiveProvider(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["KEY"] = "low" }
        );
        var higher = new CaseSensitiveProvider(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Key"] = "high" }
        );
        var root = TestCfgFactory.CreateRoot([lower, higher]); // later provider wins

        await Assert.That(root.GetValue("KEY")).IsEqualTo("high");
        await Assert.That(root.GetValue("Key")).IsEqualTo("high");
        await Assert.That(root.GetValue("key")).IsEqualTo("high");
    }

    private sealed class CaseSensitiveProvider(IReadOnlyDictionary<string, string> values)
        : ICfgProvider
    {
        public ICfgSnapshot Snapshot { get; } = new CaseSensitiveSnapshot(values);

        public ValueTask<bool> ReloadAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(false);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CaseSensitiveSnapshot(IReadOnlyDictionary<string, string> values)
        : ICfgSnapshot
    {
        public bool TryGetValue(string path, out string? value) =>
            values.TryGetValue(path, out value);

        public IReadOnlyDictionary<string, string> GetAllValues() => values;
    }

    private sealed class MockProvider : ICfgProvider
    {
        private readonly IReadOnlyList<IReadOnlyDictionary<string, string>> _snapshots;
        private readonly Exception? _disposeException;
        private readonly bool _shouldFailOnReload;
        private int _index;
        private ControllableMockChangeSignal _changeSignal = new();

        public MockProvider(
            IReadOnlyList<IReadOnlyDictionary<string, string>> snapshots,
            Exception? disposeException = null,
            bool shouldFailOnReload = false
        )
        {
            _snapshots = snapshots;
            _disposeException = disposeException;
            _shouldFailOnReload = shouldFailOnReload;
            Snapshot = new MockSnapshot(_snapshots[0]);
        }

        public int ReloadCount { get; private set; }
        public bool DisposeCalled { get; private set; }
        public ICfgSnapshot Snapshot { get; private set; }

        public ValueTask<bool> ReloadAsync(CancellationToken ct = default)
        {
            if (_shouldFailOnReload)
                return ValueTask.FromException<bool>(
                    new InvalidOperationException("Simulated reload failure")
                );

            ReloadCount++;
            if (_index < _snapshots.Count - 1)
            {
                var nextValues = _snapshots[_index + 1];
                if (Snapshot is not MockSnapshot currentSnapshot)
                    throw new InvalidOperationException("Unexpected snapshot implementation.");

                _index++;
                if (!ConfigDataComparer.Equals(currentSnapshot.Values, nextValues))
                {
                    var oldSignal = _changeSignal;
                    Snapshot = new MockSnapshot(nextValues);
                    _changeSignal = new ControllableMockChangeSignal();
                    oldSignal.NotifyChanged();
                    return ValueTask.FromResult(true);
                }
            }

            return ValueTask.FromResult(false);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalled = true;

            if (_disposeException is not null)
                return ValueTask.FromException(_disposeException);

            return ValueTask.CompletedTask;
        }

        public void NotifyChanged()
        {
            _changeSignal.NotifyChanged();
        }
    }

    private sealed class FailingReloadProvider : ICfgProvider
    {
        public ICfgSnapshot Snapshot { get; } = new MockSnapshot(new Dictionary<string, string>());

        public ValueTask<bool> ReloadAsync(CancellationToken ct = default)
        {
            throw new InvalidOperationException("Reload failed.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ConcurrentReloadProvider(
        string key,
        string value,
        ParallelReloadCoordination coordination
    ) : ICfgProvider
    {
        private int _activeReloads;

        public int MaxConcurrentReloads { get; private set; }
        public ICfgSnapshot Snapshot { get; private set; } =
            new MockSnapshot(new Dictionary<string, string>());

        public async ValueTask<bool> ReloadAsync(CancellationToken ct = default)
        {
            var active = Interlocked.Increment(ref _activeReloads);
            var globalActive = coordination.Enter();
            UpdateMax(active);

            try
            {
                await coordination.WaitForAllEnteredAsync(ct);
                Snapshot = new MockSnapshot(new Dictionary<string, string> { [key] = value });
                return true;
            }
            finally
            {
                Interlocked.Decrement(ref _activeReloads);
                coordination.Exit();
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void UpdateMax(int active)
        {
            if (active > MaxConcurrentReloads)
                MaxConcurrentReloads = active;
            coordination.RecordObserved();
        }
    }

    private sealed class ParallelReloadCoordination(int expectedConcurrentReloads)
    {
        private readonly TaskCompletionSource _allEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _activeReloads;
        private int _enteredReloads;

        public int MaxObservedConcurrentReloads { get; private set; }

        public int Enter()
        {
            var active = Interlocked.Increment(ref _activeReloads);
            if (Interlocked.Increment(ref _enteredReloads) == expectedConcurrentReloads)
                _allEntered.TrySetResult();

            return active;
        }

        public Task WaitForAllEnteredAsync(CancellationToken ct) => _allEntered.Task.WaitAsync(ct);

        public void Exit() => Interlocked.Decrement(ref _activeReloads);

        public void RecordObserved()
        {
            var current = _activeReloads;
            if (current > MaxObservedConcurrentReloads)
                MaxObservedConcurrentReloads = current;
        }
    }

    private sealed class CancelingReloadProvider(CancellationTokenSource cancellationSource)
        : ICfgProvider
    {
        public ICfgSnapshot Snapshot { get; } = new MockSnapshot(new Dictionary<string, string>());

        public ValueTask<bool> ReloadAsync(CancellationToken ct = default)
        {
            cancellationSource.Cancel();
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(false);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CountingDisposeProvider : ICfgProvider
    {
        public int DisposeCount { get; private set; }
        public ICfgSnapshot Snapshot { get; } = new MockSnapshot(new Dictionary<string, string>());

        public ValueTask<bool> ReloadAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(false);

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DeferredPublishingProvider(string key, string before, string after)
        : ICfgProvider
    {
        private readonly TaskCompletionSource _entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private bool _reloaded;

        public ICfgSnapshot Snapshot { get; private set; } =
            new MockSnapshot(new Dictionary<string, string> { [key] = before });

        public async ValueTask<bool> ReloadAsync(CancellationToken ct = default)
        {
            if (_reloaded)
                return false;

            _entered.TrySetResult();
            await _release.Task.WaitAsync(ct);

            _reloaded = true;
            Snapshot = new MockSnapshot(new Dictionary<string, string> { [key] = after });
            return true;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task WaitForReloadStartedAsync() => _entered.Task;

        public void CompleteReload() => _release.TrySetResult();
    }

    private sealed class DeferredFailingProvider(Exception failure) : ICfgProvider
    {
        private readonly TaskCompletionSource _entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public ICfgSnapshot Snapshot { get; } = new MockSnapshot(new Dictionary<string, string>());

        public async ValueTask<bool> ReloadAsync(CancellationToken ct = default)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(ct);
            return await ValueTask.FromException<bool>(failure);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task WaitForReloadStartedAsync() => _entered.Task;

        public void FailReload() => _release.TrySetResult();
    }

    private sealed class ThrowingReloadProvider(Exception failure) : ICfgProvider
    {
        public ICfgSnapshot Snapshot { get; } = new MockSnapshot(new Dictionary<string, string>());

        public ValueTask<bool> ReloadAsync(CancellationToken ct = default) => throw failure;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StaticProvider(ICfgSnapshot snapshot) : ICfgProvider
    {
        public ICfgSnapshot Snapshot { get; } = snapshot;

        public ValueTask<bool> ReloadAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(false);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

    private sealed class GatedReloadProvider : ICfgProvider
    {
        private int _activeReloads;
        private readonly TaskCompletionSource _entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public int MaxConcurrentReloads { get; private set; }
        public ICfgSnapshot Snapshot { get; } = new MockSnapshot(new Dictionary<string, string>());

        public async ValueTask<bool> ReloadAsync(CancellationToken ct = default)
        {
            var active = Interlocked.Increment(ref _activeReloads);
            if (active > MaxConcurrentReloads)
                MaxConcurrentReloads = active;

            _entered.TrySetResult();

            try
            {
                await _release.Task.WaitAsync(ct);
                return false;
            }
            finally
            {
                Interlocked.Decrement(ref _activeReloads);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task WaitForFirstEntryAsync() => _entered.Task;

        public ValueTask AllowFirstReloadToComplete()
        {
            _release.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NonCooperativeReloadProvider : ICfgProvider
    {
        private readonly TaskCompletionSource _gateAcquired = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public ICfgSnapshot Snapshot { get; } = new MockSnapshot(new Dictionary<string, string>());

        public async ValueTask<bool> ReloadAsync(CancellationToken ct = default)
        {
            // Signal that we've acquired the reload gate, then block forever
            // without checking the cancellation token (simulating a truly
            // stuck/non-cooperative provider).
            _gateAcquired.TrySetResult();

            // Use a long delay without passing the cancellation token —
            // this simulates a provider that ignores cancellation.
            await Task.Delay(TimeSpan.FromMinutes(5), CancellationToken.None);
            return false;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task WaitForGateAcquiredAsync() => _gateAcquired.Task;
    }

    private sealed class MockSnapshot(IReadOnlyDictionary<string, string> values) : ICfgSnapshot
    {
        public IReadOnlyDictionary<string, string> Values { get; } = values;

        public bool TryGetValue(string path, out string? value)
        {
            if (Values.TryGetValue(path, out var existingValue))
            {
                value = existingValue;
                return true;
            }

            value = null;
            return false;
        }

        public IReadOnlyDictionary<string, string> GetAllValues() => Values;
    }

    private sealed class ControllableMockChangeSignal
    {
        private CancellationTokenSource _cts = new();

        public bool HasChanged { get; private set; }

        public void NotifyChanged()
        {
            HasChanged = true;
            _cts.Cancel();
        }

        public ValueTask WaitForChangeAsync(CancellationToken ct = default)
        {
            return new ValueTask(WaitInternalAsync(ct));
        }

        private async Task WaitInternalAsync(CancellationToken ct)
        {
            if (HasChanged)
                return;

            var signalTask = _cts.Token.AwaitCancellationAsync(throwOnCancellation: false);
            if (!ct.CanBeCanceled)
            {
                await signalTask;
                return;
            }

            var cancellationTask = ct.AwaitCancellationAsync();
            var completedTask = await Task.WhenAny(signalTask, cancellationTask);
            await completedTask;
        }
    }

    [Test]
    public async Task GetValue_ConcurrentAccess_ReturnsConsistentResults()
    {
        var provider = new MockProvider([
            new Dictionary<string, string> { ["shared"] = "value42", ["other"] = "hello" },
        ]);
        var root = TestCfgFactory.CreateRoot([provider]);

        const int threadCount = 50;
        var tasks = new Task<string?>[threadCount];
        for (var i = 0; i < threadCount; i++)
        {
            var key = i % 2 == 0 ? "shared" : "other";
            tasks[i] = Task.Run(() => root.GetValue(key));
        }

        var results = await Task.WhenAll(tasks);

        for (var i = 0; i < threadCount; i++)
        {
            var expected = i % 2 == 0 ? "value42" : "hello";
            await Assert.That(results[i]).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task DisposeAsync_WhenReloadBlocksGate_CompletesWithinTimeout()
    {
        // Bug: CfgRoot.DisposeCoreAsync uses CancellationToken.None for the fallback
        // wait on _reloadGate, which blocks indefinitely when a non-cooperative
        // ReloadAsync holds the gate past the initial 10-second timeout.
        var provider = new NonCooperativeReloadProvider();
        var root = TestCfgFactory.CreateRoot([provider]);

        // Start a reload that acquires the gate but never completes and ignores
        // cancellation (simulating a truly stuck provider).
        var reloadTask = root.ReloadAsync().AsTask();
        await provider.WaitForGateAcquiredAsync();

        // Dispose must complete within a bounded time even when the reload
        // is non-cooperative. The current code would block indefinitely on
        // CancellationToken.None in the fallback branch of DisposeCoreAsync.
        using var disposeCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var disposeTask = root.DisposeAsync().AsTask();

        // The dispose should eventually complete (forced by a secondary timeout).
        await disposeTask.WaitAsync(disposeCts.Token);

        await Assert.That(disposeTask.IsCompletedSuccessfully).IsTrue();
    }

    [Test]
    public async Task ReloadAsync_TwoProvidersThirdFails_PublishesPartialThenRethrows()
    {
        var provider1 = new MockProvider(
            [
                new Dictionary<string, string> { ["p1"] = "v1" },
                new Dictionary<string, string> { ["p1"] = "v1b" },
            ],
            shouldFailOnReload: false
        );
        var provider2 = new MockProvider(
            [
                new Dictionary<string, string> { ["p2"] = "v2" },
                new Dictionary<string, string> { ["p2"] = "v2b" },
            ],
            shouldFailOnReload: false
        );
        var failingProvider = new MockProvider(
            [new Dictionary<string, string> { ["p3"] = "v3" }],
            shouldFailOnReload: true
        );
        var root = TestCfgFactory.CreateRoot([provider1, provider2, failingProvider]);

        await Assert.That(root.GetValue("p1")).IsEqualTo("v1");
        var originalSnapshot = SnapshotOf(root);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await root.ReloadAsync()
        );

        await Assert.That(ex!.Message).Contains("Simulated reload failure");
        await Assert.That(root.GetValue("p1")).IsEqualTo("v1b");
        await Assert.That(root.GetValue("p2")).IsEqualTo("v2b");

        var afterFailureSnapshot = SnapshotOf(root);
        await Assert.That(afterFailureSnapshot).IsNotSameReferenceAs(originalSnapshot);
    }
}
