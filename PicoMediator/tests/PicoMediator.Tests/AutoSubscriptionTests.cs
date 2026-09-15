namespace PicoMediator.Tests;

public class AutoSubscriptionTests
{
    public interface ILog
    {
        void Write(string message);
    }

    public sealed class ListLog : ILog
    {
        public List<string> Entries { get; } = [];

        public void Write(string message) => Entries.Add(message);
    }

    // Dedicated event/handler types for THIS test class: the generator scans
    // the whole test assembly, so using a type no other file declares keeps
    // subscriber counts deterministic across test files.
    public record OrderShipped(int Id) : IEvent;

    public sealed class ShipNotifier(ILog log) : ISubscriber<OrderShipped>
    {
        public ValueTask Handle(OrderShipped e, CancellationToken ct)
        {
            log.Write($"ship:{e.Id}");
            return ValueTask.CompletedTask;
        }
    }

    public record Ping(string Msg) : ICommand<string>;

    public sealed class PingHandler : ICommandHandler<Ping, string>
    {
        public ValueTask<string> Handle(Ping c, CancellationToken ct) => new($"pong:{c.Msg}");
    }

    [Test]
    public async Task AddPicoMediator_AutoRegistersSubscribers()
    {
        var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterSingle<ILog>(new ListLog());
        container.AddPicoMediator();
        container.Build();
        await using var scope = container.CreateScope();

        var subscribers = scope.GetServices<ISubscriber<OrderShipped>>();

        await Assert.That(subscribers).IsNotEmpty();
        await Assert.That(subscribers.Count).IsEqualTo(1);
    }

    [Test]
    public async Task AddPicoMediator_AutoRegistersCommandHandlers()
    {
        var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.AddPicoMediator();
        container.Build();
        await using var scope = container.CreateScope();
        var mediator = scope.GetService<IMediator>();

        var result = await mediator.Send<Ping, string>(new Ping("auto"));

        await Assert.That(result).IsEqualTo("pong:auto");
    }

    [Test]
    public async Task ManualRegistration_BeforeAddPicoMediator_Wins()
    {
        var log = new ListLog();
        var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterSingle<ISubscriber<OrderShipped>>(new ShipNotifier(log));
        container.AddPicoMediator();
        container.Build();
        await using var scope = container.CreateScope();

        var subscribers = scope.GetServices<ISubscriber<OrderShipped>>();

        // Manual wins: the generator skipped ShipNotifier for this type,
        // so exactly the one manual instance is registered.
        await Assert.That(subscribers.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ManualRegistration_BeforeAddPicoMediator_Wins_WithDefaultConstruction()
    {
        // Regression: PicoMediator's generated auto-subscriptions used to apply when
        // AddPicoMediator() was called, so manual registrations before it won. After
        // the generator-configurator registries were unified, they started applying in
        // the SvcContainer constructor — before any manual registration — which made
        // GetServices (fan-out) resolve the generated handler on top of the manual one.
        var log = new ListLog();
        var container = new SvcContainer(); // default: autoConfigureFromGenerator = true
        container.RegisterSingle<ILog>(log);
        container.RegisterSingle<ISubscriber<OrderShipped>>(new ShipNotifier(log));
        container.AddPicoMediator();
        container.Build();
        await using var scope = container.CreateScope();

        var subscribers = scope.GetServices<ISubscriber<OrderShipped>>();

        // Manual wins: exactly the one manual instance is registered.
        await Assert.That(subscribers.Count).IsEqualTo(1);
    }

    [Test]
    public async Task AddPicoMediator_NoAutoRegisterHandlers_DoesNotRegister()
    {
        var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.AddPicoMediator(autoRegisterHandlers: false);
        container.Build();
        await using var scope = container.CreateScope();

        // GetServices throws for unregistered types in PicoDI — use TryGetServices.
        var found = scope.TryGetServices(typeof(ISubscriber<OrderShipped>), out var subscribers);

        await Assert.That(found).IsFalse();
        await Assert.That(subscribers).IsNull();
    }

    [Test]
    public async Task AddPicoMediator_TwiceOnSameContainer_AppliesOnce()
    {
        var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterSingle<ILog>(new ListLog());
        container.AddPicoMediator();
        container.AddPicoMediator();
        container.Build();
        await using var scope = container.CreateScope();

        var subscribers = scope.GetServices<ISubscriber<OrderShipped>>();

        await Assert.That(subscribers.Count).IsEqualTo(1);
    }

    [Test]
    public async Task AutoRegisteredHandler_ConstructorDependenciesResolved()
    {
        var log = new ListLog();
        var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterSingle<ILog>(log);
        container.AddPicoMediator();
        container.Build();
        await using var scope = container.CreateScope();
        var mediator = scope.GetService<IMediator>();

        await mediator.Publish(new OrderShipped(5));

        await Assert.That(log.Entries).IsEquivalentTo(["ship:5"]);
    }
}
