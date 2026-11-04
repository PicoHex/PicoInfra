namespace PicoDI.Test;

/// <summary>
/// Pins behaviour 2 of the threading/lifetime pair: <b>a captive dependency is silently promoted</b>.
/// <para>
/// A singleton factory runs against the container-internal root scope, not the scope that asked for the
/// singleton, so a <c>Scoped</c> dependency the factory resolves is built there: it is not the
/// requesting scope's own instance, it survives that scope's <c>DisposeAsync</c>, every scope that
/// resolves the singleton shares it, and it lives until container disposal — released <i>after</i> the
/// singleton that captured it, so the singleton may still use it from its own <c>Dispose</c>. A
/// <i>transient</i> the same factory resolves is captive in exactly the same way, because the root
/// scope is what tracks and disposes it. Measured on this tree: the captured instances were created in
/// the implicit root scope (never in the requesting scope), survived the disposal of the two sibling
/// scopes, and were each disposed exactly once, at container disposal, while each sibling scope's own
/// scoped instance died with its scope.
/// </para>
/// <para>
/// Nothing warns, and the reason is structural rather than an oversight: a singleton has no channel to a
/// caller's scope — it is created once, possibly from a different scope than the one that later uses it,
/// so the only scope it can safely be resolved against is the container's own. The contract for a
/// caller is therefore "use <c>Scoped</c> for anything scope-bound": a <c>Singleton -&gt; Scoped</c> edge
/// is a defect whose symptom is a request-scoped unit of work shared by every caller and outliving the
/// request that triggered it. <c>PicoDI/README.md</c> (Lifetimes) states both.
/// </para>
/// </summary>
public class CaptiveDependencyPromotionTests
{
    /// <summary>Records the scope that created it and counts its own disposals.</summary>
    private sealed class ScopedUnitOfWork(ISvcScope createdIn, ContainerProbe owner) : IDisposable
    {
        public int DisposeCount;

        public ISvcScope CreatedIn { get; } = createdIn;

        public void Dispose()
        {
            Interlocked.Increment(ref DisposeCount);
            Interlocked.Increment(ref owner.ScopedDisposals);
        }
    }

    /// <summary>A transient captured by the same singleton — the other lifetime that goes captive.</summary>
    private sealed class TransientUnitOfWork(ISvcScope createdIn, ContainerProbe owner)
        : IDisposable
    {
        public int DisposeCount;

        public ISvcScope CreatedIn { get; } = createdIn;

        public void Dispose()
        {
            Interlocked.Increment(ref DisposeCount);
            Interlocked.Increment(ref owner.TransientDisposals);
        }
    }

    private sealed class ContainerProbe
    {
        public int ScopedDisposals;
        public int TransientDisposals;
    }

    /// <summary>The singleton that captures a scoped dependency — the captive edge itself.</summary>
    private sealed class CaptiveSingleton(
        ScopedUnitOfWork dependency,
        TransientUnitOfWork transient
    ) : IDisposable
    {
        public ScopedUnitOfWork Dependency { get; } = dependency;

        public TransientUnitOfWork Transient { get; } = transient;

        public int DisposeCount;

        /// <summary>Was the captured dependency still alive when this singleton was disposed?</summary>
        public bool DependencyAliveAtOwnDisposal { get; private set; }

        /// <summary>Was the captured transient still alive when this singleton was disposed?</summary>
        public bool TransientAliveAtOwnDisposal { get; private set; }

        public void Dispose()
        {
            Interlocked.Increment(ref DisposeCount);
            DependencyAliveAtOwnDisposal = Volatile.Read(ref Dependency.DisposeCount) == 0;
            TransientAliveAtOwnDisposal = Volatile.Read(ref Transient.DisposeCount) == 0;
        }
    }

    [Test]
    public async Task ScopedAndTransientDepCapturedBySingleton_ArePromotedToRootScope_OutliveRequestingScopes_DieWithContainer()
    {
        var probe = new ContainerProbe();
        var container = new SvcContainer(autoConfigureFromGenerator: false);
        ISvcScope? singletonFactoryScope = null;
        var singletonFactoryCalls = 0;
        CaptiveSingleton? singleton = null;

        container.RegisterScoped<ScopedUnitOfWork>(scope => new ScopedUnitOfWork(scope, probe));
        container.RegisterTransient<TransientUnitOfWork>(scope => new TransientUnitOfWork(
            scope,
            probe
        ));
        container.RegisterSingleton<CaptiveSingleton>(scope =>
        {
            singletonFactoryScope = scope;
            Interlocked.Increment(ref singletonFactoryCalls);
            return singleton = new CaptiveSingleton(
                scope.GetService<ScopedUnitOfWork>(),
                scope.GetService<TransientUnitOfWork>()
            );
        });
        container.Build();

        var scopeA = container.CreateScope();
        var scopeB = container.CreateScope();

        // The singleton is built while scope A is the requesting scope ...
        var fromA = scopeA.GetService<CaptiveSingleton>();
        // ... and a properly scoped resolve in the same scope A, for the contrast.
        var scopeAOwned = scopeA.GetService<ScopedUnitOfWork>();
        // A sibling scope shares the singleton (no second factory call) ...
        var fromB = scopeB.GetService<CaptiveSingleton>();
        // ... but gets its own scoped instance, not the captured one.
        var scopeBOwned = scopeB.GetService<ScopedUnitOfWork>();

        var captured = singleton!.Dependency;
        var capturedTransient = singleton.Transient;

        await Assert
            .That(ReferenceEquals(fromA, fromB))
            .IsTrue()
            .Because("the captured dependency is captured by a singleton, so it is shared");
        await Assert
            .That(Volatile.Read(ref singletonFactoryCalls))
            .IsEqualTo(1)
            .Because("a second scope must reuse the published singleton, not rebuild it");

        // The contrast: a properly scoped resolution belongs to the scope that asked for it.
        await Assert
            .That(ReferenceEquals(scopeAOwned.CreatedIn, scopeA))
            .IsTrue()
            .Because("a scoped resolve belongs to the scope that asked for it");

        // Disposing every requesting scope releases their own instances and nothing else.
        await scopeA.DisposeAsync();
        await scopeB.DisposeAsync();

        await Assert
            .That(Volatile.Read(ref scopeAOwned.DisposeCount))
            .IsEqualTo(1)
            .Because("a properly scoped instance dies with its scope");
        await Assert
            .That(Volatile.Read(ref scopeBOwned.DisposeCount))
            .IsEqualTo(1)
            .Because("a properly scoped instance dies with its scope");

        // The pin: the promoted instance is anchored to the container, silently, with no warning.
        await Assert
            .That(Volatile.Read(ref captured.DisposeCount))
            .IsEqualTo(0)
            .Because(
                "the promoted instance must survive the requesting scopes' disposal — the captive "
                    + "dependency is not disposed with the scope that triggered it"
            );

        // The transient the factory resolved went the same way: the root scope, not scope A, is what
        // tracks it, so the scope that triggered the singleton's construction never sees it.
        await Assert
            .That(Volatile.Read(ref capturedTransient.DisposeCount))
            .IsEqualTo(0)
            .Because(
                "a transient the singleton factory resolved is tracked by the root scope, not by the "
                    + "requesting scope, so disposing that scope must not release it"
            );

        // ... and it is not either scope's own instance, however long the scopes live.
        await Assert
            .That(ReferenceEquals(scopeAOwned, captured))
            .IsFalse()
            .Because(
                "scope A's own scoped instance must not be the instance the singleton captured"
            );
        await Assert
            .That(ReferenceEquals(scopeBOwned, captured))
            .IsFalse()
            .Because(
                "scope B's own scoped instance must not be the instance the singleton captured"
            );

        // Where it lives, and why: the factory has no channel to a caller's scope, so it is handed the
        // container's own root scope — and that is where the promoted scoped instance is created.
        var implicitRoot = container.GetImplicitRootScope();
        await Assert
            .That(ReferenceEquals(singletonFactoryScope, implicitRoot))
            .IsTrue()
            .Because("a singleton factory runs against the container-internal root scope");
        await Assert
            .That(ReferenceEquals(singletonFactoryScope, scopeA))
            .IsFalse()
            .Because("the promoting scope must not be the requesting scope");
        await Assert
            .That(ReferenceEquals(captured.CreatedIn, implicitRoot))
            .IsTrue()
            .Because(
                "the captured scoped dependency must be created in the root scope, not in scope A"
            );
        await Assert
            .That(ReferenceEquals(capturedTransient.CreatedIn, implicitRoot))
            .IsTrue()
            .Because(
                "the captured transient must be created in the root scope too — that is what makes "
                    + "its disposal the container's, not the requesting scope's"
            );

        // ... and container disposal is what finally releases it, after the singleton that captured it.
        await container.DisposeAsync();

        await Assert
            .That(Volatile.Read(ref captured.DisposeCount))
            .IsEqualTo(1)
            .Because("the promoted instance lives until container disposal");
        await Assert
            .That(Volatile.Read(ref capturedTransient.DisposeCount))
            .IsEqualTo(1)
            .Because(
                "the captured transient lives until container disposal too, and is released once"
            );
        await Assert
            .That(Volatile.Read(ref singleton!.DisposeCount))
            .IsEqualTo(1)
            .Because("the capturing singleton is disposed once, by the container");
        await Assert
            .That(singleton.DependencyAliveAtOwnDisposal)
            .IsTrue()
            .Because(
                "singletons are released before the root scope, so a Dispose may still use them"
            );
        await Assert
            .That(singleton.TransientAliveAtOwnDisposal)
            .IsTrue()
            .Because(
                "singletons are released before the root scope, so a Dispose may still use the "
                    + "transient it captured"
            );
        await Assert
            .That(Volatile.Read(ref probe.ScopedDisposals))
            .IsEqualTo(3)
            .Because("three scoped instances were built and each was disposed exactly once");
        await Assert
            .That(Volatile.Read(ref probe.TransientDisposals))
            .IsEqualTo(1)
            .Because("one transient was built and it was disposed exactly once");
    }
}
