namespace PicoMediator.Tests;

/// <summary>
/// Pins the one measured scope trap of <c>AddPicoMediator</c>:
/// <c>AddPicoMediator(SvcLifetime.Singleton)</c> captures the <b>container-internal root scope</b>.
/// </summary>
/// <remarks>
/// <para>
/// The registration is <c>scope =&gt; new Mediator(scope)</c>; for a Singleton, PicoDI deliberately
/// invokes the factory with <c>Owner.GetImplicitRootScope()</c>
/// (<c>PicoDI/src/PicoDI/SvcScope.Resolution.cs:270</c>, documented at <c>PicoDI/README.md:130</c>:
/// "Singleton factories run against the container root scope"). <see cref="Mediator"/> keeps that
/// scope for its own lifetime and resolves every handler through it, so a <c>Scoped</c> handler
/// becomes <b>one instance per container</b>, materialised in the root scope, surviving every
/// caller scope's <c>DisposeAsync</c> and disposed only when the container disposes the root scope
/// last.
/// </para>
/// <para>
/// This is <b>not a mediator defect</b>: <c>Send</c>/<c>Publish</c> carry no scope, so a singleton
/// mediator has no channel through which a later caller's scope could arrive, and PicoDI exposes
/// no container-level resolution (<c>ISvcContainer</c> is not <c>ISvcScope</c>). It is an
/// undocumented consequence of a documented rule — measured in
/// <c>review-2026-11-02/mediator-scope-observation.md</c> §2.2 / §3.1 (<c>scoped-H#8</c> for both
/// sibling scopes). The test below is the contract that keeps it from changing silently.
/// </para>
/// </remarks>
public class MediatorScopeCaptureTests
{
    public sealed record ScopedEvent : IEvent;

    /// <summary>
    /// Counts handles and disposals so the test can tell "one instance per container" from "one
    /// instance per scope", and "died with the scope" from "died with the container".
    /// </summary>
    public sealed class DisposalTrackedSubscriber : ISubscriber<ScopedEvent>, IAsyncDisposable
    {
        private int _disposeCount;

        public int HandleCount { get; private set; }

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public ValueTask Handle(ScopedEvent @event, CancellationToken ct = default)
        {
            HandleCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Records the <see cref="ISvcScope"/> its factory was handed. Registered as a Singleton, it
    /// receives PicoDI's container-internal root scope — the public-API handle on that scope.
    /// <c>SvcContainer.GetImplicitRootScope()</c> is <c>internal</c> and PicoDI grants
    /// <c>InternalsVisibleTo</c> only to <c>PicoDI.Test</c>, not to this project, so the scope a
    /// singleton factory receives is the reachable form of the same object
    /// (<c>PicoDI/README.md:130</c>).
    /// </summary>
    public sealed class SingletonScopeProbe(ISvcScope capturedScope)
    {
        public ISvcScope CapturedScope { get; } = capturedScope;
    }

    [Test]
    public async Task AddPicoMediator_Singleton_PromotesScopedHandlerToContainerRootScope()
    {
        var created = new List<DisposalTrackedSubscriber>();

        var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterSingleton<SingletonScopeProbe>(scope => new SingletonScopeProbe(scope));
        container.RegisterScoped<ISubscriber<ScopedEvent>>(_ =>
        {
            var subscriber = new DisposalTrackedSubscriber();
            created.Add(subscriber);
            return subscriber;
        });
        container.AddPicoMediator(SvcLifetime.Singleton, autoRegisterHandlers: false);
        container.Build();

        var scope1 = container.CreateScope();
        var scope2 = container.CreateScope();

        // Name the container-internal root scope through the public API: it is the scope PicoDI
        // hands every Singleton factory, and it is neither sibling scope.
        var implicitRoot = scope1.GetService<SingletonScopeProbe>().CapturedScope;
        await Assert.That(ReferenceEquals(implicitRoot, scope1)).IsFalse();
        await Assert.That(ReferenceEquals(implicitRoot, scope2)).IsFalse();

        var mediator1 = scope1.GetService<IMediator>();
        var mediator2 = scope2.GetService<IMediator>();
        await Assert
            .That(ReferenceEquals(mediator1, mediator2))
            .IsTrue()
            .Because("precondition: a Singleton mediator is one instance per container");

        await mediator1.Publish(new ScopedEvent());
        await mediator2.Publish(new ScopedEvent());
        await mediator1.Publish(new ScopedEvent());

        await Assert
            .That(created.Count)
            .IsEqualTo(1)
            .Because(
                "a Scoped handler resolved through a Singleton mediator is one instance per "
                    + "container: both sibling scopes must have been served by the same object"
            );
        var promoted = created[0];
        await Assert.That(promoted.HandleCount).IsEqualTo(3);

        // Root-scope identity, without reflection: the promoted handler IS the scoped instance
        // that the container's implicit root scope holds.
        await Assert
            .That(ReferenceEquals(promoted, implicitRoot.GetService<ISubscriber<ScopedEvent>>()))
            .IsTrue()
            .Because(
                "the handler must be created in the container-internal root scope (the scope "
                    + "Singleton factories receive), not in a caller scope"
            );

        // Two coexisting truths: direct DI resolution in the same container is still per scope.
        var direct1 = (DisposalTrackedSubscriber)scope1.GetService<ISubscriber<ScopedEvent>>();
        var direct2 = (DisposalTrackedSubscriber)scope2.GetService<ISubscriber<ScopedEvent>>();
        await Assert.That(ReferenceEquals(direct1, direct2)).IsFalse();
        await Assert.That(ReferenceEquals(direct1, promoted)).IsFalse();
        await Assert.That(ReferenceEquals(direct2, promoted)).IsFalse();
        await Assert.That(created.Count).IsEqualTo(3);

        // It outlives a sibling scope's DisposeAsync — and its creator's…
        await scope1.DisposeAsync();
        await Assert
            .That(promoted.DisposeCount)
            .IsEqualTo(0)
            .Because(
                "the promoted handler lives on the root scope, not on the disposed sibling scope"
            );
        await Assert.That(direct1.DisposeCount).IsEqualTo(1);

        await mediator2.Publish(new ScopedEvent());
        await Assert.That(promoted.HandleCount).IsEqualTo(4);
        await Assert.That(created.Count).IsEqualTo(3);

        await scope2.DisposeAsync();
        await Assert.That(promoted.DisposeCount).IsEqualTo(0);
        await Assert.That(direct2.DisposeCount).IsEqualTo(1);

        // …and dies only when container disposal disposes the implicit root scope last.
        await container.DisposeAsync();
        await Assert.That(promoted.DisposeCount).IsEqualTo(1);

        // ── Source-scope baseline: the SAME handler under AddPicoMediator(Scoped) is per scope ──
        var scopedCreated = new List<DisposalTrackedSubscriber>();
        var scopedContainer = new SvcContainer(autoConfigureFromGenerator: false);
        scopedContainer.RegisterScoped<ISubscriber<ScopedEvent>>(_ =>
        {
            var subscriber = new DisposalTrackedSubscriber();
            scopedCreated.Add(subscriber);
            return subscriber;
        });
        scopedContainer.AddPicoMediator(SvcLifetime.Scoped, autoRegisterHandlers: false);
        scopedContainer.Build();

        var scopedScope1 = scopedContainer.CreateScope();
        var scopedScope2 = scopedContainer.CreateScope();
        await scopedScope1.GetService<IMediator>().Publish(new ScopedEvent());
        await scopedScope2.GetService<IMediator>().Publish(new ScopedEvent());

        await Assert
            .That(scopedCreated.Count)
            .IsEqualTo(2)
            .Because(
                "with the default Scoped mediator the handler follows the caller's scope — the "
                    + "per-container promotion is a property of Singleton, not of the registration"
            );
        await Assert.That(ReferenceEquals(scopedCreated[0], scopedCreated[1])).IsFalse();

        await scopedScope1.DisposeAsync();
        await scopedScope2.DisposeAsync();
        await Assert.That(scopedCreated[0].DisposeCount).IsEqualTo(1);
        await Assert.That(scopedCreated[1].DisposeCount).IsEqualTo(1);

        await scopedContainer.DisposeAsync();
    }
}
