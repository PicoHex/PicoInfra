namespace PicoMediator.Tests;

public class MediatorLifetimeTests
{
    public record Ping : ICommand<string>;

    public sealed class PingHandler : ICommandHandler<Ping, string>
    {
        public ValueTask<string> Handle(Ping r, CancellationToken ct) =>
            ValueTask.FromResult("pong");
    }

    [Test]
    public async Task AddPicoMediator_Default_IsScoped()
    {
        // Default behavior should remain Scoped for backward compatibility. "IsNotNull" was the
        // whole assertion here, and every lifetime resolves — flipping the default parameter to
        // Singleton left this test green (review-2026-11-02 test-validity finding 4). The
        // contract is identity: one mediator per scope, so two sibling scopes must NOT share it.
        var container = new SvcContainer();
        container.RegisterScoped<ICommandHandler<Ping, string>>(_ => new PingHandler());
        container.AddPicoMediator();
        container.Build();

        await using var scope1 = container.CreateScope();
        var mediator1 = scope1.GetService<IMediator>();
        await Assert.That(mediator1).IsNotNull().Because("the default registration must resolve");

        await using var scope2 = container.CreateScope();
        var mediator2 = scope2.GetService<IMediator>();

        await Assert
            .That(ReferenceEquals(mediator1, mediator2))
            .IsFalse()
            .Because(
                "the default lifetime is Scoped: a second scope must get its own mediator, "
                    + "not the first scope's instance (Singleton passes IsNotNull)"
            );

        // ...and within one scope the scoped instance is stable.
        await Assert.That(ReferenceEquals(mediator1, scope1.GetService<IMediator>())).IsTrue();
    }

    [Test]
    public async Task AddPicoMediator_Singleton_Works()
    {
        var container = new SvcContainer();
        container.RegisterScoped<ICommandHandler<Ping, string>>(_ => new PingHandler());
        container.AddPicoMediator(SvcLifetime.Singleton);
        container.Build();

        // Resolve the mediator once (creates the singleton)
        await using var scope1 = container.CreateScope();
        var mediator1 = scope1.GetService<IMediator>();

        // Resolve again in a different scope — should be the same instance
        await using var scope2 = container.CreateScope();
        var mediator2 = scope2.GetService<IMediator>();

        await Assert.That(ReferenceEquals(mediator1, mediator2)).IsTrue();
    }

    [Test]
    public async Task AddPicoMediator_ScopedExplicit_BehavesAsScoped()
    {
        var container = new SvcContainer();
        container.RegisterScoped<ICommandHandler<Ping, string>>(_ => new PingHandler());
        container.AddPicoMediator(SvcLifetime.Scoped);
        container.Build();

        await using var scope1 = container.CreateScope();
        var mediator1 = scope1.GetService<IMediator>();

        await using var scope2 = container.CreateScope();
        var mediator2 = scope2.GetService<IMediator>();

        await Assert.That(ReferenceEquals(mediator1, mediator2)).IsFalse();
    }
}
