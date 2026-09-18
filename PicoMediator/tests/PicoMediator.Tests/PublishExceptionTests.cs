namespace PicoMediator.Tests;

public class PublishExceptionTests
{
    public record Boom : IEvent;

    public sealed class BoomHandler(string label, List<string> log) : ISubscriber<Boom>
    {
        public ValueTask Handle(Boom n, CancellationToken ct)
        {
            log.Add($"{label}:Handle");
            throw new InvalidOperationException($"boom from {label}");
        }
    }

    [Test]
    public async Task Publish_MultipleHandlers_AllExecutedDespiteFailures()
    {
        var log = new List<string>();
        var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterSingle<ISubscriber<Boom>>(new BoomHandler("A", log));
        container.RegisterSingle<ISubscriber<Boom>>(new BoomHandler("B", log));
        container.RegisterSingle<ISubscriber<Boom>>(new BoomHandler("C", log));
        container.Build();
        await using var scope = container.CreateScope();

        // Verify all 3 handlers are registered
        var handlers = scope.GetServices<ISubscriber<Boom>>();
        await Assert.That(handlers.Count).IsEqualTo(3);

        var mediator = new Mediator(scope);
        await Assert.ThrowsAsync(async () => await mediator.Publish(new Boom()));

        await Assert.That(log).Contains("A:Handle");
        await Assert.That(log).Contains("B:Handle");
        await Assert.That(log).Contains("C:Handle");
    }

    [Test]
    public async Task Publish_MultipleHandlers_AggregatesExceptions()
    {
        var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterSingle<ISubscriber<Boom>>(new BoomHandler("X", []));
        container.RegisterSingle<ISubscriber<Boom>>(new BoomHandler("Y", []));
        container.Build();
        await using var scope = container.CreateScope();
        var mediator = new Mediator(scope);

        var ex = await Assert.ThrowsAsync(async () => await mediator.Publish(new Boom()));

        await Assert.That(ex).IsTypeOf<AggregateException>();
        await Assert.That(((AggregateException)ex!).InnerExceptions.Count).IsEqualTo(2);
    }

    public sealed class CancellationProbe : ISubscriber<Boom>
    {
        public bool Invoked;

        public ValueTask Handle(Boom n, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Invoked = true;
            return ValueTask.CompletedTask;
        }
    }

    [Test]
    public async Task Publish_PreCancelledToken_PropagatesOperationCanceled()
    {
        // Cancellation is not a handler failure: callers must observe
        // OperationCanceledException, not an AggregateException wrapping it.
        var container = new SvcContainer(autoConfigureFromGenerator: false);
        var probe = new CancellationProbe();
        container.RegisterSingle<ISubscriber<Boom>>(probe);
        container.Build();
        await using var scope = container.CreateScope();
        var mediator = new Mediator(scope);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Assert.ThrowsAsync(async () =>
            await mediator.Publish(new Boom(), cts.Token)
        );

        await Assert.That(ex).IsTypeOf<OperationCanceledException>();
        await Assert.That(probe.Invoked).IsFalse();
    }

    [Test]
    public async Task PublishParallel_PreCancelledToken_PropagatesOperationCanceled()
    {
        var container = new SvcContainer(autoConfigureFromGenerator: false);
        var probe = new CancellationProbe();
        container.RegisterSingle<ISubscriber<Boom>>(probe);
        container.Build();
        await using var scope = container.CreateScope();
        var mediator = new Mediator(scope);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Assert.ThrowsAsync(async () =>
            await mediator.PublishParallel(new Boom(), cts.Token)
        );

        await Assert.That(ex).IsTypeOf<OperationCanceledException>();
        await Assert.That(probe.Invoked).IsFalse();
    }
}
