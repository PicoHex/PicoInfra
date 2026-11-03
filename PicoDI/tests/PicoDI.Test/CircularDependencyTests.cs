namespace PicoDI.Test;

/// <summary>
/// A singleton or scoped service whose factory resolves its own service used to recurse
/// until the process died: <c>GetOrCreateSingletonSlow</c> invokes the factory outside
/// <c>SyncLock</c> (deliberately — a factory that resolves another singleton must not
/// deadlock against it), so nothing noticed that the same service was already being
/// constructed on this thread. Measured before the fix: 2140 nested
/// <c>ResolveServiceCore</c> frames and then <c>Stack overflow.</c> — an uncatchable
/// process abort with no exception, no <c>OnError</c> callback and no log line.
/// <para>
/// The scoped path failed closed (<c>Lazy</c> throws on re-entrance) but named neither the
/// service nor the cycle: <c>InvalidOperationException: ValueFactory attempted to access the
/// Value property of this instance.</c>
/// </para>
/// <para>
/// Asserts the container reports <see cref="PicoDiException"/> naming the cycle instead, and
/// that a rejected cycle leaves no "under construction" marker behind, so the container stays
/// usable.
/// </para>
/// </summary>
public class CircularDependencyTests
{
    /// <summary>
    /// A stack overflow cannot be asserted inside the process that dies from it, so the cycling
    /// factories below stop themselves after <see cref="FactoryRecursionLimit"/> re-entries with a
    /// plain <see cref="InvalidOperationException"/>. Before the fix that exception is what escaped
    /// (proof of unbounded re-entry); after the fix the factory is entered exactly once.
    /// </summary>
    private const int FactoryRecursionLimit = 32;

    [Test]
    public async Task SingletonSelfCycle_ThrowsPicoDiException_NamingTheCycle()
    {
        var guard = new RecursionGuard(FactoryRecursionLimit);
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterSingleton<CycleSelfService>(sp =>
        {
            guard.Enter("CycleSelfService");
            _ = sp.GetService<CycleSelfService>();
            return new CycleSelfService();
        });
        container.RegisterSingleton<HealthyService>(_ => new HealthyService());
        await using var scope = container.CreateScope();

        var thrown = Capture(() => scope.GetService<CycleSelfService>());

        await Assert.That(thrown).IsTypeOf<PicoDiException>();
        await Assert.That(thrown!.Message).Contains("CycleSelfService -> CycleSelfService");
        await Assert.That(guard.MaxDepth).IsEqualTo(1);
        await Assert.That(SvcConstructionGuard.DepthForCurrentThread).IsEqualTo(0);
        // A rejected cycle must not poison the container: an unrelated singleton still resolves.
        await Assert.That(scope.GetService<HealthyService>()).IsNotNull();
    }

    [Test]
    public async Task SingletonMutualCycle_ThrowsPicoDiException_NamingBothServicesInOrder()
    {
        var guard = new RecursionGuard(FactoryRecursionLimit);
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterSingleton<CycleAService>(sp =>
        {
            guard.Enter("CycleAService");
            _ = sp.GetService<CycleBService>();
            return new CycleAService();
        });
        container.RegisterSingleton<CycleBService>(sp =>
        {
            guard.Enter("CycleBService");
            _ = sp.GetService<CycleAService>();
            return new CycleBService();
        });
        container.RegisterSingleton<HealthyService>(_ => new HealthyService());
        await using var scope = container.CreateScope();

        var thrown = Capture(() => scope.GetService<CycleAService>());

        await Assert.That(thrown).IsTypeOf<PicoDiException>();
        await Assert
            .That(thrown!.Message)
            .Contains("CycleAService -> CycleBService -> CycleAService");
        // Each factory is entered once: A, then B, then A is rejected instead of re-entered.
        await Assert.That(guard.MaxDepth).IsEqualTo(2);
        await Assert.That(SvcConstructionGuard.DepthForCurrentThread).IsEqualTo(0);
        await Assert.That(scope.GetService<HealthyService>()).IsNotNull();
    }

    [Test]
    public async Task ScopedMutualCycle_ThrowsPicoDiException_NamingBothServicesInOrder()
    {
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterScoped<CycleAService>(sp =>
        {
            _ = sp.GetService<CycleBService>();
            return new CycleAService();
        });
        container.RegisterScoped<CycleBService>(sp =>
        {
            _ = sp.GetService<CycleAService>();
            return new CycleBService();
        });
        container.RegisterScoped<HealthyService>(_ => new HealthyService());
        await using var scope = container.CreateScope();

        var thrown = Capture(() => scope.GetService<CycleAService>());

        await Assert.That(thrown).IsTypeOf<PicoDiException>();
        await Assert
            .That(thrown!.Message)
            .Contains("CycleAService -> CycleBService -> CycleAService");
        await Assert.That(SvcConstructionGuard.DepthForCurrentThread).IsEqualTo(0);
        // The scope is still usable for services that are not part of the rejected cycle.
        await Assert.That(scope.GetService<HealthyService>()).IsNotNull();
    }

    [Test]
    public async Task ScopedSelfCycle_ThrowsPicoDiException_NamingTheCycle()
    {
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterScoped<CycleSelfService>(sp => sp.GetService<CycleSelfService>());
        await using var scope = container.CreateScope();

        var thrown = Capture(() => scope.GetService<CycleSelfService>());

        await Assert.That(thrown).IsTypeOf<PicoDiException>();
        await Assert.That(thrown!.Message).Contains("CycleSelfService -> CycleSelfService");
        await Assert.That(SvcConstructionGuard.DepthForCurrentThread).IsEqualTo(0);
    }

    [Test]
    public async Task ThrowingSingletonFactory_LeavesNoConstructionMarker_AndNextResolveWorks()
    {
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterSingleton<BoomService>(_ =>
            throw new InvalidOperationException("factory boom")
        );
        container.RegisterSingleton<HealthyService>(_ => new HealthyService());
        await using var scope = container.CreateScope();

        var thrown = Capture(() => scope.GetService<BoomService>());

        // A failed factory keeps propagating as-is — retry-vs-cache is a separate decision.
        await Assert.That(thrown).IsTypeOf<InvalidOperationException>();
        await Assert.That(thrown!.Message).IsEqualTo("factory boom");
        await Assert.That(SvcConstructionGuard.DepthForCurrentThread).IsEqualTo(0);
        // The marker of the failed construction must not survive: a different service resolves.
        await Assert.That(scope.GetService<HealthyService>()).IsNotNull();
    }

    [Test]
    public async Task ThrowingScopedFactory_LeavesNoConstructionMarker_AndNextResolveWorks()
    {
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterScoped<BoomService>(_ =>
            throw new InvalidOperationException("scoped boom")
        );
        container.RegisterScoped<HealthyService>(_ => new HealthyService());
        await using var scope = container.CreateScope();

        var thrown = Capture(() => scope.GetService<BoomService>());

        await Assert.That(thrown).IsTypeOf<InvalidOperationException>();
        await Assert.That(thrown!.Message).IsEqualTo("scoped boom");
        await Assert.That(SvcConstructionGuard.DepthForCurrentThread).IsEqualTo(0);
        await Assert.That(scope.GetService<HealthyService>()).IsNotNull();
    }

    [Test]
    public async Task AcyclicChain_ResolvesWithoutBeingReportedAsACycle()
    {
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterSingleton<CycleAService>(sp => new CycleAService(
            sp.GetService<CycleBService>()
        ));
        container.RegisterSingleton<CycleBService>(_ => new CycleBService(new CycleAService()));
        await using var scope = container.CreateScope();

        var a = scope.GetService<CycleAService>();

        await Assert.That(a).IsNotNull();
        await Assert.That(a.Dependency).IsNotNull();
        await Assert.That(SvcConstructionGuard.DepthForCurrentThread).IsEqualTo(0);
    }

    /// <summary>
    /// The marker is kept per registration, not per immediate parent, so a cycle that passes through
    /// an untracked transient is still caught at the singleton/scoped registration that re-enters.
    /// </summary>
    [Test]
    public async Task SingletonCycleThroughATransient_IsStillReportedAsACycle()
    {
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterSingleton<CycleSelfService>(sp =>
        {
            _ = sp.GetService<CycleTransientService>();
            return new CycleSelfService();
        });
        container.RegisterTransient<CycleTransientService>(sp =>
        {
            _ = sp.GetService<CycleSelfService>();
            return new CycleTransientService();
        });
        await using var scope = container.CreateScope();

        var thrown = Capture(() => scope.GetService<CycleSelfService>());

        await Assert.That(thrown).IsTypeOf<PicoDiException>();
        await Assert.That(thrown!.Message).Contains("CycleSelfService -> CycleSelfService");
        await Assert.That(SvcConstructionGuard.DepthForCurrentThread).IsEqualTo(0);
    }

    /// <summary>
    /// The in-progress marker is keyed by the owning scope for scoped services, so a factory that
    /// builds the same service type in a nested child scope is not misreported as a cycle: those are
    /// distinct instances (Microsoft.Extensions.DependencyInjection allows the same pattern).
    /// <para>
    /// The child scopes the factory creates are disposed here, right after the resolution they exist
    /// for. The factory is a synchronous <c>Func&lt;ISvcScope, object&gt;</c> and <see cref="ISvcScope"/> is
    /// <see cref="IAsyncDisposable"/>-only, so neither <c>using</c> nor <c>await using</c> can be written
    /// inside it; the scopes are collected instead and drained in a <c>finally</c>, so no live scope
    /// survives the test.
    /// </para>
    /// </summary>
    [Test]
    public async Task ScopedFactoryResolvingSameTypeInAChildScope_IsNotReportedAsACycle()
    {
        var depth = 0;
        var childScopes = new List<ISvcScope>();
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterScoped<NestedScopeService>(sp =>
        {
            var level = ++depth;
            if (level < 3)
            {
                var child = sp.CreateScope();
                childScopes.Add(child);
                _ = child.GetService<NestedScopeService>();
            }

            return new NestedScopeService(level);
        });

        await using var scope = container.CreateScope();

        try
        {
            var resolved = scope.GetService<NestedScopeService>();

            await Assert.That(resolved.Level).IsEqualTo(1);
            await Assert.That(depth).IsEqualTo(3);
        }
        finally
        {
            foreach (var child in childScopes)
                await child.DisposeAsync();
        }

        await Assert.That(SvcConstructionGuard.DepthForCurrentThread).IsEqualTo(0);
    }

    /// <summary>
    /// <c>Type.FullName</c> is <see langword="null"/> for a service key that is a generic parameter, so
    /// the cycle message used to interpolate an empty service name where every neighbouring message in
    /// the module guards it (<c>FullName ?? Name</c>). The registration is keyed by the open
    /// definition's type parameter, whose <see cref="Type.Name"/> is <c>TService</c>.
    /// </summary>
    [Test]
    public async Task CycleMessage_NamesAGenericParameterService_WhenFullNameIsNull()
    {
        var thrown = await TriggerGenericParameterCycleAsync();

        await Assert.That(thrown).IsTypeOf<PicoDiException>();
        await Assert.That(thrown!.Message).Contains("TService -> TService");
        await Assert
            .That(thrown.Message)
            .Contains("Service 'TService' is already under construction");
        await Assert.That(SvcConstructionGuard.DepthForCurrentThread).IsEqualTo(0);
    }

    /// <summary>
    /// The per-thread frame store is reused across constructions instead of being allocated and
    /// dropped per outermost resolve: <c>Push</c> used to allocate a fresh <see cref="List{T}"/> and
    /// <c>Pop</c> dropped it as soon as it emptied, so N back-to-back constructions allocated N lists
    /// (and re-grew past the initial capacity of 4). Measured with
    /// <see cref="GC.GetAllocatedBytesForCurrentThread"/> around 1000 push/pop pairs on one thread.
    /// </summary>
    [Test]
    public async Task ConstructionGuard_AllocatesNothingPerPushPopPair()
    {
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        await using var scope = container.CreateScope();
        var ownerScope = (SvcScope)scope;
        var registration = SvcRuntimeRegistration.Create(
            SvcDescriptor.Create(
                typeof(HealthyService),
                static _ => new HealthyService(),
                SvcLifetime.Scoped
            )
        );

        // Warm-up: the first pair may allocate the per-thread store (and its first capacity).
        SvcConstructionGuard.Push(container, ownerScope, registration);
        SvcConstructionGuard.Pop();

        const int pairs = 1000;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < pairs; i++)
        {
            SvcConstructionGuard.Push(container, ownerScope, registration);
            SvcConstructionGuard.Pop();
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        await Assert.That(SvcConstructionGuard.DepthForCurrentThread).IsEqualTo(0);
        await Assert.That(allocated).IsEqualTo(0L);
    }

    private static Exception? Capture(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>
    /// Bounds re-entry so a missing cycle diagnostic fails the test instead of killing the test host.
    /// </summary>
    private sealed class RecursionGuard(int limit)
    {
        private int _depth;

        internal int MaxDepth { get; private set; }

        internal void Enter(string factory)
        {
            var depth = ++_depth;
            if (depth > MaxDepth)
                MaxDepth = depth;
            if (depth > limit)
                throw new InvalidOperationException(
                    $"{factory} factory re-entered {depth} times without a circular-dependency diagnostic."
                );
        }
    }

    private sealed class HealthyService
    {
        public Guid InstanceId { get; } = Guid.NewGuid();
    }

    private sealed class BoomService;

    private sealed class CycleSelfService;

    private sealed class CycleAService(object? dependency = null)
    {
        internal object? Dependency { get; } = dependency;
    }

    private sealed class CycleBService(object? dependency = null)
    {
        internal object? Dependency { get; } = dependency;
    }

    private sealed class CycleTransientService;

    private sealed class NestedScopeService(int level)
    {
        internal int Level { get; } = level;
    }

    private static class GenericParameterCycleProbe<TService>
        where TService : class;

    /// <summary>
    /// Registers and resolves a scoped service keyed by a generic <i>parameter</i> type — the one shape
    /// whose <see cref="Type.FullName"/> is <see langword="null"/> (<c>typeof(T)</c> inside a closed
    /// instantiation substitutes the type argument, so the parameter is read off the open definition).
    /// The factory re-resolves the very service it is building, i.e. a self-cycle.
    /// </summary>
    private static async Task<Exception?> TriggerGenericParameterCycleAsync()
    {
        var serviceType = typeof(GenericParameterCycleProbe<>).GetGenericArguments()[0];
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.Register(
            SvcDescriptor.Create(serviceType, sp => sp.GetService(serviceType), SvcLifetime.Scoped)
        );
        await using var scope = container.CreateScope();

        return Capture(() => scope.GetService(serviceType));
    }
}
