namespace PicoCfg;

internal interface IInternalCfgRootSnapshotAccessor
{
    ICfgSnapshot CurrentSnapshot { get; }
}

internal sealed class CfgRoot : ICfgRoot, IInternalCfgRootSnapshotAccessor
{
    private readonly Lock _disposeSyncRoot = new();
    private readonly Lock _syncRoot = new();
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Func<IReadOnlyList<ICfgSnapshot>, ICfgSnapshot> _snapshotComposer;
    private readonly Func<CfgChangeSignal> _changeSignalFactory;
    private readonly TimeSpan _reloadDrainTimeout;
    private readonly TimeSpan _reloadDrainRetryTimeout;
    private readonly List<ICfgProvider> _providers;
    private ICfgSnapshot[] _providerSnapshots;
    private ICfgSnapshot _snapshot;
    private CfgChangeSignal _changeSignal;
    private Task? _disposeTask;
    private int _disposed;

    internal CfgRoot(
        IEnumerable<ICfgProvider> providers,
        Func<IReadOnlyList<ICfgSnapshot>, ICfgSnapshot> snapshotComposer,
        Func<CfgChangeSignal> changeSignalFactory,
        TimeSpan? reloadDrainTimeout = null,
        TimeSpan? reloadDrainRetryTimeout = null
    )
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(snapshotComposer);
        ArgumentNullException.ThrowIfNull(changeSignalFactory);
        _snapshotComposer = snapshotComposer;
        _changeSignalFactory = changeSignalFactory;
        _reloadDrainTimeout = reloadDrainTimeout ?? TimeSpan.FromSeconds(10);
        _reloadDrainRetryTimeout = reloadDrainRetryTimeout ?? TimeSpan.FromSeconds(5);
        _providers = [.. providers];
        _providerSnapshots = [.. _providers.Select(static provider => provider.Snapshot)];
        _snapshot = _snapshotComposer(_providerSnapshots);
        _changeSignal = _changeSignalFactory();
    }

    private ICfgSnapshot Snapshot => Volatile.Read(ref _snapshot);

    ICfgSnapshot IInternalCfgRootSnapshotAccessor.CurrentSnapshot => Snapshot;

    public bool TryGetValue(string path, out string? value) =>
        Snapshot.TryGetValue(path, out value);

    public async ValueTask<bool> ReloadAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) == 1)
            throw new ObjectDisposedException(nameof(CfgRoot));

        ct.ThrowIfCancellationRequested();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            _disposeCts.Token
        );
        await _reloadGate.WaitAsync(linkedCts.Token);
        try
        {
            var reloadState = await CreateReloadStateAsync(linkedCts.Token);
            PublishReloadState(reloadState);
            return CompleteReload(reloadState);
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    internal CfgChangeSignal GetChangeSignal()
    {
        lock (_syncRoot)
            return _changeSignal;
    }

    public ValueTask WaitForChangeAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) == 1)
            throw new ObjectDisposedException(nameof(CfgRoot));

        var changeSignal = GetChangeSignal();
        return changeSignal.WaitForChangeAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_disposeSyncRoot)
        {
            _disposeTask ??= DisposeCoreAsync();
            disposeTask = _disposeTask;
        }

        await disposeTask;
    }

    private async Task DisposeCoreAsync()
    {
        Volatile.Write(ref _disposed, 1);
        _disposeCts.Cancel();

        // Wait for any in-flight reload to settle. The cancellation above unblocks
        // cooperative providers; the bounded waits are a safety cap for
        // non-cooperative ones (see the finally block for what happens when even
        // the retry times out).
        var entered = await _reloadGate.WaitAsync(_reloadDrainTimeout, CancellationToken.None);
        try
        {
            List<Exception>? exceptions = null;

            for (var i = _providers.Count - 1; i >= 0; i--)
            {
                try
                {
                    await _providers[i].DisposeAsync();
                }
                catch (Exception ex)
                {
                    exceptions ??= [];
                    exceptions.Add(ex);
                }
            }

            if (exceptions is null)
                return;

            if (exceptions.Count is 1)
                ExceptionDispatchInfo.Throw(exceptions[0]);

            throw new AggregateException(exceptions);
        }
        finally
        {
            var drained = entered;
            if (entered)
            {
                _reloadGate.Release();
            }
            else
            {
                // A non-cooperative reload still owns the gate. One bounded
                // secondary wait reduces the window; if even that fails, the
                // gate stays with its owner (see below).
                try
                {
                    drained = await _reloadGate
                        .WaitAsync(_reloadDrainRetryTimeout, CancellationToken.None)
                        .ConfigureAwait(false);
                    if (drained)
                    {
                        _reloadGate.Release();
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Already disposed by someone else.
                    drained = true;
                }
                catch (OperationCanceledException)
                {
                    // Should not happen with CancellationToken.None, but
                    // defensive — proceed with disposal.
                }
            }

            if (drained)
            {
                _disposeCts.Dispose();
                _reloadGate.Dispose();
            }
            else
            {
                // Not drained: a stuck reload still holds the gate and its token
                // chain. Disposing them would fault that reload with
                // ObjectDisposedException on Release() and mask its real outcome.
                // Leave both to the GC — SemaphoreSlim holds no unmanaged
                // resources when only WaitAsync is used.
            }
        }
    }

    private async Task<ReloadState> CreateReloadStateAsync(CancellationToken ct)
    {
        var reloadRun = StartProviderReloads(ct);
        var reloadFailure = await ObserveReloadCompletionAsync(
            reloadRun.Tasks,
            reloadRun.CreationFailure
        );
        var observedProviderSnapshots = ObserveProviderSnapshots();
        var publishedSnapshot = TryComposePublishedSnapshot(observedProviderSnapshots);
        return new ReloadState(observedProviderSnapshots, publishedSnapshot, reloadFailure);
    }

    private ReloadRun StartProviderReloads(CancellationToken ct)
    {
        var tasks = new List<Task>(_providers.Count);
        ExceptionDispatchInfo? creationFailure = null;

        for (var i = 0; i < _providers.Count; i++)
        {
            try
            {
                tasks.Add(_providers[i].ReloadAsync(ct).AsTask());
            }
            catch (Exception ex)
            {
                creationFailure = ExceptionDispatchInfo.Capture(ex);
                break;
            }
        }

        return new ReloadRun(tasks, creationFailure);
    }

    private ICfgSnapshot[] ObserveProviderSnapshots()
    {
        // Providers publish their own snapshots first. Re-sample the observed provider sequence after
        // all reload tasks settle so a sibling fault/cancellation does not leave the root behind.
        var observedProviderSnapshots = new ICfgSnapshot[_providers.Count];
        for (var i = 0; i < _providers.Count; i++)
            observedProviderSnapshots[i] = _providers[i].Snapshot;

        return observedProviderSnapshots;
    }

    private void PublishReloadState(ReloadState reloadState)
    {
        // Root publication is based on provider snapshot identity rather than just the final visible values.
        // A provider can publish a new snapshot that stays overridden by later providers, and callers should
        // still observe a fresh root snapshot/change signal for that publication.
        if (reloadState.PublishedSnapshot is null)
            return;

        PublishRootSnapshot(reloadState.ObservedProviderSnapshots, reloadState.PublishedSnapshot);
    }

    private static async Task<ExceptionDispatchInfo?> ObserveReloadCompletionAsync(
        IReadOnlyList<Task> reloadTasks,
        ExceptionDispatchInfo? creationFailure
    )
    {
        try
        {
            await Task.WhenAll(reloadTasks);
        }
        catch (OperationCanceledException)
        {
            // Cancellation always wins, even when a synchronous creation failure exists.
            throw;
        }
        catch when (creationFailure is not null)
        {
            // Preserve the original synchronous creation failure after already-started
            // reloads settle. Task exceptions from already-launched providers are
            // logged via Trace so no failure information is silently lost.
            foreach (var task in reloadTasks)
            {
                if (task.IsFaulted && task.Exception is { } aggregateException)
                {
                    foreach (var inner in aggregateException.InnerExceptions)
                        Trace.TraceError(
                            $"[PicoCfg] Provider reload faulted while handling a "
                                + $"synchronous startup failure: {inner}"
                        );
                }
            }
        }
        catch
        {
            // Collect all task exceptions instead of relying on await's
            // single-exception unwrap so no provider failure is silently lost.
            var exceptions = new List<Exception>();
            foreach (var task in reloadTasks)
            {
                if (task.IsFaulted && task.Exception is { } aggregateException)
                    exceptions.AddRange(aggregateException.InnerExceptions);
            }

            if (exceptions.Count == 1)
                return ExceptionDispatchInfo.Capture(exceptions[0]);
            if (exceptions.Count > 1)
                return ExceptionDispatchInfo.Capture(new AggregateException(exceptions));

            throw;
        }

        return creationFailure;
    }

    private static bool CompleteReload(ReloadState reloadState)
    {
        reloadState.ReloadFailure?.Throw();
        return reloadState.PublishedSnapshot is not null;
    }

    private ICfgSnapshot? TryComposePublishedSnapshot(ICfgSnapshot[] observedProviderSnapshots)
    {
        if (!ProviderSnapshotSequenceChanged(_providerSnapshots, observedProviderSnapshots))
            return null;

        // Compose once on the reload path so steady-state reads stay on the current published snapshot.
        return _snapshotComposer(observedProviderSnapshots);
    }

    private void PublishRootSnapshot(ICfgSnapshot[] providerSnapshots, ICfgSnapshot snapshot)
    {
        CfgChangeSignal? changedSignal = null;
        lock (_syncRoot)
        {
            _providerSnapshots = providerSnapshots;
            Volatile.Write(ref _snapshot, snapshot);
            changedSignal = _changeSignal;
            _changeSignal = _changeSignalFactory();
        }

        changedSignal.NotifyChanged();
    }

    private static bool ProviderSnapshotSequenceChanged(
        IReadOnlyList<ICfgSnapshot> currentSnapshots,
        IReadOnlyList<ICfgSnapshot> nextSnapshots
    ) => !CfgSnapshotComposer.SequenceEqual(currentSnapshots, nextSnapshots);

    private sealed record ReloadRun(
        IReadOnlyList<Task> Tasks,
        ExceptionDispatchInfo? CreationFailure
    );

    private sealed record ReloadState(
        ICfgSnapshot[] ObservedProviderSnapshots,
        ICfgSnapshot? PublishedSnapshot,
        ExceptionDispatchInfo? ReloadFailure
    );
}
