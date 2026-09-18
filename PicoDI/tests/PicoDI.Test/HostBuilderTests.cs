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

    [Test]
    public async Task BuildAsync_WithCancelledToken()
    {
        var builder = new SvcHostBuilder();
        builder.ConfigureServices(c =>
        {
            c.RegisterHostedSvc<TestHostedSvc>(_ => new TestHostedSvc());
        });

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await using var host = await builder.BuildAsync(cts.Token);

        await Assert.That(host).IsNotNull();

        await using var scope = host.Services.CreateScope();
        var hosted = scope.GetService<TestHostedSvc>();
        await Assert.That(hosted).IsNotNull();
        await Assert.That(hosted!.Started).IsFalse();
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
