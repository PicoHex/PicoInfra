namespace PicoDI.Test;

public class SingletonRootScopeTests
{
    public interface IScopedDep { }

    public sealed class ScopedDep : IScopedDep, IDisposable
    {
        public bool Disposed;

        public void Dispose() => Disposed = true;
    }

    public interface ISingletonWithDep
    {
        IScopedDep Dep { get; }
    }

    public sealed class SingletonWithDep(IScopedDep dep) : ISingletonWithDep
    {
        public IScopedDep Dep { get; } = dep;
    }

    [Test]
    public async Task SingletonFactory_ResolvesScopedDepFromContainerRoot_NotRequestScope()
    {
        var container = new SvcContainer();
        container.RegisterScoped<IScopedDep>(_ => new ScopedDep());
        container.RegisterSingleton<ISingletonWithDep>(scope => new SingletonWithDep(
            scope.GetService<IScopedDep>()
        ));
        container.Build();

        // Trigger creation from a short-lived scope (the E1 scenario)
        ISingletonWithDep singleton;
        {
            await using var requestScope = container.CreateScope();
            singleton = requestScope.GetService<ISingletonWithDep>();
        } // request scope disposed — scoped dep must NOT die with it

        await Assert.That(singleton).IsNotNull();
        await Assert.That(singleton.Dep).IsNotNull();
        // E1 regression: under the bug the dep was anchored to the disposed
        // request scope and is dead — assert it is still alive.
        await Assert.That(((ScopedDep)singleton.Dep).Disposed).IsFalse();

        await container.DisposeAsync();
    }

    [Test]
    public async Task SingletonFactory_ScopedDep_DisposedAtContainerDisposal_NotBefore()
    {
        // Instance-based assertions (no static counters): safe under parallel
        // execution — each test tracks the exact ScopedDep instance it created.
        ScopedDep? dep = null;
        var container = new SvcContainer();
        container.RegisterScoped<IScopedDep>(_ => new ScopedDep());
        container.RegisterSingleton<ISingletonWithDep>(scope =>
        {
            dep = (ScopedDep)scope.GetService<IScopedDep>();
            return new SingletonWithDep(dep);
        });
        container.Build();

        await using (var requestScope = container.CreateScope())
        {
            _ = requestScope.GetService<ISingletonWithDep>();
        }

        // The dep must outlive the request scope (lives in the container root).
        await Assert.That(dep!.Disposed).IsFalse();

        await container.DisposeAsync();
        await Assert.That(dep.Disposed).IsTrue();
    }

    [Test]
    public async Task SingletonFactory_FromChildScope_ReachesContainerRoot()
    {
        var container = new SvcContainer();
        container.RegisterScoped<IScopedDep>(_ => new ScopedDep());
        container.RegisterSingleton<ISingletonWithDep>(scope => new SingletonWithDep(
            scope.GetService<IScopedDep>()
        ));
        container.Build();

        ISingletonWithDep singleton;
        {
            await using var root = container.CreateScope();
            await using var child = root.CreateScope();
            singleton = child.GetService<ISingletonWithDep>(); // creation from a child scope
        } // child + root disposed — dep must NOT die with them

        await Assert.That(singleton.Dep).IsNotNull();
        await Assert.That(((ScopedDep)singleton.Dep).Disposed).IsFalse();

        await container.DisposeAsync();
    }

    [Test]
    public async Task SingletonFactory_ReceivesContainerRootScope_NotRequestScope()
    {
        SvcScope? seen = null;
        var container = new SvcContainer();
        container.RegisterSingleton<object>(scope =>
        {
            seen = (SvcScope)scope;
            return new object();
        });
        container.Build();

        await using var root = container.CreateScope();
        _ = root.GetService<object>();

        await Assert.That(seen).IsNotNull();
        await Assert.That(ReferenceEquals(seen, root)).IsFalse();

        // "Not the triggering scope" is not the contract: it accepts ANY other scope, including
        // a fresh throwaway one handed to every resolution — the shape that leaks every scoped
        // dependency a singleton factory captures until process exit (the E1 defect). Measured on
        // this branch: that injection leaves this test green and 348/350 of PicoDI.Test green.
        // The factory must receive the container-internal root scope — the one the container
        // keeps and releases last — so pin the identity, not the inequality.
        await Assert
            .That(ReferenceEquals(seen, container.GetImplicitRootScope()))
            .IsTrue()
            .Because("the singleton factory runs against the container-internal root scope");
    }
}
