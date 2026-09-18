namespace PicoMediator.Tests;

public class DiIntegrationTests
{
    public record Ping : ICommand<string>;

    // Deliberately has NO handler class anywhere in the test assembly, so
    // auto-registration can never provide a handler for it.
    public record UnhandledPing : ICommand<string>;

    public sealed class PingHandler : ICommandHandler<Ping, string>
    {
        public ValueTask<string> Handle(Ping r, CancellationToken ct) =>
            ValueTask.FromResult("pong");
    }

    [Test]
    public async Task AddPicoMediator_RegistersMediatorAsScoped()
    {
        var container = new SvcContainer();
        container.RegisterScoped<ICommandHandler<Ping, string>>(_ => new PingHandler());
        container.AddPicoMediator();
        container.Build();

        await using var scope = container.CreateScope();
        var mediator = scope.GetService<IMediator>();

        var result = await mediator.Send<Ping, string>(new Ping());

        await Assert.That(result).IsEqualTo("pong");
    }

    [Test]
    public async Task AddPicoMediator_NoHandler_Throws()
    {
        var container = new SvcContainer();
        container.AddPicoMediator();
        container.Build();

        await using var scope = container.CreateScope();
        var mediator = scope.GetService<IMediator>()!;

        // No handler class exists for this request type anywhere in the test
        // assembly, so auto-registration cannot provide one. The failure must
        // be the descriptive InvalidOperationException, not a raw DI
        // resolution exception.
        var ex = await Assert.ThrowsAsync(async () =>
            await mediator.Send<UnhandledPing, string>(new UnhandledPing())
        );
        await Assert.That(ex).IsTypeOf<InvalidOperationException>();
        await Assert.That(ex!.Message).Contains("No handler registered for");
    }
}
