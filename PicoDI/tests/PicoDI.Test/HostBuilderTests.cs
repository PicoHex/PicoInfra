namespace PicoDI.Test;

public class HostBuilderTests
{
    private sealed class TestHostedSvc : IHostedSvc
    {
        public bool Started { get; private set; }
        public bool Stopped { get; private set; }

        public Task StartAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            Stopped = true;
            return Task.CompletedTask;
        }
    }

    [Test]
    public async Task Build_ReturnsStartedContainer()
    {
        var builder = new SvcHostBuilder();
        builder.ConfigureServices(c =>
        {
            c.RegisterHostedSvc<TestHostedSvc>(_ => new TestHostedSvc());
            c.RegisterTransient<ISimpleService>(_ => new SimpleService());
        });

        await using var host = await builder.BuildAsync();

        await Assert.That(host).IsNotNull();

        await using var scope = host.Services.CreateScope();
        var hosted = scope.GetService<TestHostedSvc>();
        await Assert.That(hosted).IsNotNull();
        await Assert.That(hosted!.Started).IsTrue();
    }

    [Test]
    public async Task ConfigureServices_MultipleCallsAccumulate()
    {
        var builder = new SvcHostBuilder();
        builder.ConfigureServices(c =>
        {
            c.RegisterSingleton<ISimpleService>(_ => new SimpleService());
        });
        builder.ConfigureServices(c =>
        {
            c.RegisterSingleton<IServiceWithDependency>(sp =>
            {
                var dep = sp.GetService<ISimpleService>();
                return new ServiceWithDependency(dep!);
            });
        });

        await using var host = await builder.BuildAsync();

        await using var scope = host.Services.CreateScope();

        var svc1 = scope.GetService<ISimpleService>();
        var svc2 = scope.GetService<IServiceWithDependency>();

        await Assert.That(svc1).IsNotNull();
        await Assert.That(svc2).IsNotNull();
        await Assert.That(svc2!.Dependency).IsNotNull();
    }

    [Test]
    public async Task BuildAsync_WithCancellationToken()
    {
        var builder = new SvcHostBuilder();
        builder.ConfigureServices(c =>
        {
            c.RegisterHostedSvc<TestHostedSvc>(_ => new TestHostedSvc());
        });

        using var cts = new CancellationTokenSource();
        await using var host = await builder.BuildAsync(cts.Token);

        await Assert.That(host).IsNotNull();

        await using var scope = host.Services.CreateScope();
        var hosted = scope.GetService<TestHostedSvc>();
        await Assert.That(hosted).IsNotNull();
        await Assert.That(hosted!.Started).IsTrue();
    }

    /// <summary>
    /// Honours the token and identifies itself: the cancellation exception carries a message
    /// only this service can produce, so an assertion on it pins what the BUILDER does with the
    /// service's exception (a builder-level pre-check could not satisfy it).
    /// </summary>
    private sealed class CancellingHostedSvc : IHostedSvc
    {
        public bool Started { get; private set; }

        public Task StartAsync(CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException("hosted service observed cancellation");
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Ignores the token and records whether it was already cancelled on entry.</summary>
    private sealed class TokenIgnoringHostedSvc : IHostedSvc
    {
        public bool Started { get; private set; }
        public bool SawCancelledToken { get; private set; }

        public Task StartAsync(CancellationToken ct)
        {
            SawCancelledToken = ct.IsCancellationRequested;
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    [Test]
    public async Task BuildAsync_HostedSvcHonouringTheCancelledToken_PropagatesItsCancellation()
    {
        // review-2026-11-02 defect L3. The predecessor of this test was called
        // BuildAsync_WithCancelledToken and asserted only Throws<OperationCanceledException>(),
        // but the exception came from the sample service's own ct.ThrowIfCancellationRequested()
        // — nothing in it observed the builder (probe P3: with a token-ignoring service,
        // BuildAsync(cancelledToken) still handed back a STARTED host).
        //
        // What is asserted now is the builder's half of the contract: the service's exception is
        // not swallowed, BuildAsync faults with that same exception (its own message), and the
        // host is never handed back. review-2026-11-02 finding 2's guarantee stays pinned.
        var svc = new CancellingHostedSvc();
        var builder = new SvcHostBuilder();
        builder.ConfigureServices(c => c.RegisterHostedSvc<CancellingHostedSvc>(_ => svc));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert
            .That(async () =>
            {
                _ = await builder.BuildAsync(cts.Token);
            })
            .Throws<OperationCanceledException>()
            .WithMessage("hosted service observed cancellation");

        // The failed start was not a start: the service never reported itself up.
        await Assert.That(svc.Started).IsFalse();
    }

    [Test]
    public async Task BuildAsync_CancelledToken_ReachesTheServices_ButTheBuilderDoesNotCheckIt()
    {
        // review-2026-11-02 defect L3, stated as the contract it actually is. SvcHostBuilder
        // forwards the token to every hosted service and does NOT abort a cancelled build on its
        // own: a service that ignores the token still starts, and BuildAsync still returns a
        // STARTED host. Same division as Microsoft.Extensions.Hosting, where Host.StartAsync
        // hands the token to the hosted services and HostedServiceExecutor does not pre-check it.
        // This test exists so the pair cannot drift back into one over-promising name: changing
        // the builder to pre-check the token turns this red, and removal of the forwarding turns
        // SawCancelledToken false.
        var svc = new TokenIgnoringHostedSvc();
        var builder = new SvcHostBuilder();
        builder.ConfigureServices(c => c.RegisterHostedSvc<TokenIgnoringHostedSvc>(_ => svc));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await using var host = await builder.BuildAsync(cts.Token);

        await Assert.That(host).IsNotNull();
        await Assert.That(svc.Started).IsTrue();
        // The token really did reach the service: the builder forwards it, it does not drop it.
        await Assert.That(svc.SawCancelledToken).IsTrue();
    }

    [Test]
    public async Task Build_CalledTwice_Throws()
    {
        var builder = new SvcHostBuilder();
        builder.ConfigureServices(c =>
        {
            c.RegisterHostedSvc<TestHostedSvc>(_ => new TestHostedSvc());
        });

        await builder.BuildAsync();

        await Assert
            .That(async () => await builder.BuildAsync())
            .Throws<InvalidOperationException>()
            .WithMessage("Build has already been called.");
    }

    [Test]
    public async Task Dispose_DisposesContainer()
    {
        var builder = new SvcHostBuilder();
        builder.ConfigureServices(c =>
        {
            c.RegisterHostedSvc<TestHostedSvc>(_ => new TestHostedSvc());
        });

        await using var host = await builder.BuildAsync();
        await builder.DisposeAsync();

        await Assert.That(() => host.Services.CreateScope()).Throws<ObjectDisposedException>();
    }

    private sealed class FailsInFactorySvc : IHostedSvc
    {
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    [Test]
    public async Task BuildAsync_HostedServiceFactoryThrows_DisposesStartedServices()
    {
        var builder = new SvcHostBuilder();
        var startedSvc = new TestHostedSvc();

        builder.ConfigureServices(c =>
        {
            c.RegisterHostedSvc<TestHostedSvc>(_ => startedSvc);
            c.RegisterHostedSvc<FailsInFactorySvc>(_ =>
                throw new InvalidOperationException("factory boom")
            );
        });

        await Assert
            .That(async () => await builder.BuildAsync())
            .Throws<InvalidOperationException>();

        // Bug 1: without fix, the container is leaked and StopAsync is never called.
        // The first hosted service was started but never stopped.
        await Assert.That(startedSvc.Started).IsTrue();
        await Assert.That(startedSvc.Stopped).IsTrue();
    }
}
