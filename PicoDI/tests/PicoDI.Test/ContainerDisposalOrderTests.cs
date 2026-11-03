namespace PicoDI.Test;

/// <summary>
/// review-2026-11-02 finding 3: <c>SvcContainer.DisposeAsync</c> disposed the container-owned
/// singletons *before* the user root scopes, so a scoped <c>Dispose</c> ran against singletons
/// that were already gone (measured there as
/// <c>hosted.Stop → singleton.Dispose → scoped.Dispose</c>).
/// </summary>
/// <remarks>
/// <para>
/// Order asserted here — a disposer may use what it depends on, so a dependency is always
/// released after its dependents:
/// </para>
/// <list type="number">
/// <item>hosted services stop first (LIFO) — their <c>StopAsync</c> may still use anything;</item>
/// <item>user root scopes, children before parents, LIFO within each scope;</item>
/// <item>container-owned singletons, LIFO by construction order;</item>
/// <item>the implicit root scope last: it owns the scoped/transient instances that singleton
/// factories captured, so it must outlive the singletons it created them for.</item>
/// </list>
/// </remarks>
public class ContainerDisposalOrderTests
{
    private sealed class OrderLog
    {
        private readonly List<string> _entries = [];
        private readonly Lock _gate = new();

        public void Record(string entry)
        {
            lock (_gate)
                _entries.Add(entry);
        }

        public IReadOnlyList<string> Entries
        {
            get
            {
                lock (_gate)
                    return _entries.ToArray();
            }
        }
    }

    private sealed class OrderHostedSvc(OrderLog log) : IHostedSvc
    {
        public Task StartAsync(CancellationToken ct)
        {
            log.Record("hosted.Start");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            log.Record("hosted.Stop");
            return Task.CompletedTask;
        }
    }

    private sealed class OrderSingleton(OrderLog log) : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
            log.Record("singleton.Dispose");
        }
    }

    private sealed class OrderScoped(OrderLog log, OrderSingleton singleton) : IDisposable
    {
        public void Dispose() =>
            log.Record($"scoped.Dispose(singletonAlive={!singleton.Disposed})");
    }

    // The captive-dependency shape: a singleton captures a scoped service, which the
    // container resolves for it from the implicit root scope.
    private sealed class CapturedScopedDep(OrderLog log) : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
            log.Record($"scopedDep.Dispose(depAlive={!Disposed})");
        }
    }

    private sealed class CaptiveSingleton(OrderLog log, CapturedScopedDep dep) : IDisposable
    {
        public CapturedScopedDep Dep { get; } = dep;

        public void Dispose()
        {
            // A singleton may use its captured dependency inside Dispose — the dependency
            // must therefore still be alive at this point.
            log.Record($"singleton.Dispose(depAlive={!Dep.Disposed})");
        }
    }

    [Test]
    public async Task ContainerDisposal_StopsHostedSvc_ThenUserScope_ThenSingleton()
    {
        var log = new OrderLog();
        var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterHostedSvc<OrderHostedSvc>(_ => new OrderHostedSvc(log));
        container.RegisterSingleton<OrderSingleton>(_ => new OrderSingleton(log));
        container.RegisterScoped<OrderScoped>(scope => new OrderScoped(
            log,
            scope.GetService<OrderSingleton>()
        ));
        container.Build();

        var host = new SvcHost(container);
        await host.StartAsync();

        // A user root scope holding a disposable that depends on a singleton.
        var scope = container.CreateScope();
        _ = scope.GetService<OrderScoped>();

        await container.DisposeAsync();

        // hosted.Stop first, then the scope's disposables, then the singleton they depend on.
        await Assert
            .That(log.Entries)
            .IsEquivalentTo([
                "hosted.Start",
                "hosted.Stop",
                "scoped.Dispose(singletonAlive=True)",
                "singleton.Dispose",
            ]);
    }

    [Test]
    public async Task ContainerDisposal_SingletonDisposedBeforeTheScopedDependencyItCaptured()
    {
        var log = new OrderLog();
        var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterScoped<CapturedScopedDep>(_ => new CapturedScopedDep(log));
        container.RegisterSingleton<CaptiveSingleton>(scope => new CaptiveSingleton(
            log,
            scope.GetService<CapturedScopedDep>()
        ));
        container.Build();

        _ = container.CreateScope().GetService<CaptiveSingleton>();

        await container.DisposeAsync();

        // The singleton's Dispose may use the scoped dependency it captured; that dependency
        // lives in the container-internal root scope and must therefore be released last.
        await Assert
            .That(log.Entries)
            .IsEquivalentTo([
                "singleton.Dispose(depAlive=True)",
                "scopedDep.Dispose(depAlive=False)",
            ]);
    }
}
