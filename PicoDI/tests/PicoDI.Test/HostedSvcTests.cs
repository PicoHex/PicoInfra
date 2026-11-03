namespace PicoDI.Test;

/// <summary>
/// Tests for IHostedSvc and BackgroundSvc hosted service contracts.
/// </summary>
public class HostedSvcTests
{
    #region Test Service Classes

    internal sealed class TestHostedSvc : IHostedSvc
    {
        public bool Started { get; private set; }
        public bool Stopped { get; private set; }

        public Task StartAsync(CancellationToken ct)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            Stopped = true;
            return Task.CompletedTask;
        }
    }

    private sealed class SlowStartSvc : IHostedSvc
    {
        public bool Started { get; private set; }

        public async Task StartAsync(CancellationToken ct)
        {
            await Task.Delay(50, ct);
            Started = true;
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class TestBackgroundSvc : BackgroundSvc
    {
        public bool Executing { get; private set; }
        public bool Completed { get; private set; }
        public bool TokenCancelled { get; private set; }

        private readonly TaskCompletionSource _tcs = new();

        public void Complete() => _tcs.TrySetResult();

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Executing = true;
            try
            {
                await _tcs.Task.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                TokenCancelled = true;
            }
            Completed = true;
        }
    }

    #endregion

    #region IHostedSvc Lifecycle Tests

    [Test]
    public async Task SgGeneratedRegistry_includes_RegisterHostedSvc_types()
    {
        // The SG should have generated a ModuleInitializer that populates
        // SvcHostedServiceRegistry with all types registered via
        // RegisterHostedSvc<T>() in this assembly.
        // TestHostedSvc is registered in this test class, so it should be in the registry.
        await Assert.That(SvcHostedServiceRegistry.Contains(typeof(TestHostedSvc))).IsTrue();
    }

    [Test]
    public async Task Runtime_RegisterHostedSvc_adds_to_registry()
    {
        // Use non-generic Type overload — SG cannot pre-populate this
        // (no typeof(T) expression in source for SG to extract type from)
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterHostedSvc(typeof(SimpleHostedSvc), _ => new SimpleHostedSvc());

        await Assert.That(SvcHostedServiceRegistry.Contains(typeof(SimpleHostedSvc))).IsTrue();
    }

    // Test service
    private class SimpleHostedSvc : IHostedSvc
    {
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    [Test]
    public async Task RegisterHostedSvc_StartAsync_IsCalled()
    {
        // Arrange
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        var svc = new TestHostedSvc();
        container.RegisterHostedSvc<TestHostedSvc>(_ => svc);
        container.Build();

        // Act
        await new SvcHost(container).StartAsync();

        // Assert
        await Assert.That(svc.Started).IsTrue();
    }

    [Test]
    public async Task RegisterHostedSvc_StopAsync_IsCalled()
    {
        // Arrange
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        var svc = new TestHostedSvc();
        container.RegisterHostedSvc<TestHostedSvc>(_ => svc);
        container.Build();
        var host = new SvcHost(container);
        await host.StartAsync();

        // Act
        await host.StopAsync();

        // Assert
        await Assert.That(svc.Stopped).IsTrue();
    }

    [Test]
    public async Task StartAsync_Idempotent_NoException()
    {
        // Arrange
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterHostedSvc<TestHostedSvc>(_ => new TestHostedSvc());
        container.Build();

        var host = new SvcHost(container);
        await host.StartAsync();
        await host.StartAsync();
    }

    [Test]
    public async Task StopAsync_Idempotent_NoException()
    {
        // Arrange
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        var svc = new TestHostedSvc();
        container.RegisterHostedSvc<TestHostedSvc>(_ => svc);
        container.Build();
        var host = new SvcHost(container);
        await host.StartAsync();

        await host.StopAsync();
        await host.StopAsync();
    }

    #endregion

    #region BackgroundSvc Tests

    [Test]
    public async Task BackgroundSvc_ExecuteAsync_RunsAfterStart()
    {
        // Arrange
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        var svc = new TestBackgroundSvc();
        container.RegisterHostedSvc<TestBackgroundSvc>(_ => svc);
        container.Build();

        // Act
        await new SvcHost(container).StartAsync();

        await Assert.That(svc.Executing).IsTrue();
    }

    [Test]
    public async Task BackgroundSvc_StopAsync_CancelsExecution()
    {
        // Arrange
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        var svc = new TestBackgroundSvc();
        container.RegisterHostedSvc<TestBackgroundSvc>(_ => svc);
        container.Build();
        var host = new SvcHost(container);
        await host.StartAsync();

        svc.Complete();
        await host.StopAsync();

        await Assert.That(svc.Completed).IsTrue();
    }

    [Test]
    public async Task BackgroundSvc_CancellationTokenPropagated()
    {
        // Arrange
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        var svc = new TestBackgroundSvc();
        container.RegisterHostedSvc<TestBackgroundSvc>(_ => svc);
        container.Build();
        var host = new SvcHost(container);
        await host.StartAsync();

        await host.StopAsync();

        await Assert.That(svc.TokenCancelled).IsTrue();
        await Assert.That(svc.Completed).IsTrue();
    }

    #endregion

    #region Edge Cases

    [Test]
    public async Task NoHostedServices_StartStop_DoesNotThrow()
    {
        // Arrange
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.Build();

        var host = new SvcHost(container);
        await host.StartAsync();
        await host.StopAsync();
    }

    #endregion

    #region Deduplication Tests

    private sealed class CountingHostedSvc : IHostedSvc
    {
        public int StartCount;
        public int StopCount;

        public Task StartAsync(CancellationToken ct)
        {
            StartCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            StopCount++;
            return Task.CompletedTask;
        }
    }

    [Test]
    public async Task StartAsync_DuplicateRegistration_SameServiceType_CallsStartExactlyOnce()
    {
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        var svc = new CountingHostedSvc();

        // Register the SAME INSTANCE twice. Both registrations are tracked (identity is the
        // registration, not the type), so the start pass reaches the object twice — the
        // one-start-per-object guard is what keeps it to a single StartAsync, and the
        // one-stop-per-instance guard keeps it to a single StopAsync.
        container.RegisterHostedSvc<CountingHostedSvc>(_ => svc);
        container.RegisterHostedSvc<CountingHostedSvc>(_ => svc);
        container.Build();

        var host = new SvcHost(container);
        await host.StartAsync();

        await Assert.That(svc.StartCount).IsEqualTo(1);

        await host.StopAsync();
        await Assert.That(svc.StopCount).IsEqualTo(1);
    }

    #endregion

    #region Captive Dependency Tests (Bug 12)

    private sealed class ScopedDependentHostedSvc(IDisposableService scopedDep) : IHostedSvc
    {
        public IDisposableService ScopedDep => scopedDep;

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    [Test]
    public async Task HostedSingleton_DependsOnScoped_ScopedDependencyNotDisposedAfterStart()
    {
        var scopedSvc = new DisposableService();

        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterScoped<IDisposableService>(_ => scopedSvc);
        container.RegisterHostedSvc<ScopedDependentHostedSvc>(sp => new ScopedDependentHostedSvc(
            sp.GetService<IDisposableService>()!
        ));
        container.Build();

        await new SvcHost(container).StartAsync();

        // The scoped dependency was resolved from the temporary scope in
        // StartHostedServicesAsync. Without the fix, that scope is immediately
        // disposed, taking the scoped dependency with it.
        await Assert.That(scopedSvc.IsDisposed).IsFalse();
    }

    #endregion
}
