namespace PicoDI.Test;

public class LifecycleSvcTests
{
    private sealed class LifecycleTestSvc : IHostedLifecycleSvc
    {
        private readonly List<string> _calls = new();
        public IReadOnlyList<string> Calls => _calls;

        public Task StartingAsync(CancellationToken ct)
        {
            _calls.Add("Starting");
            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken ct)
        {
            _calls.Add("Start");
            return Task.CompletedTask;
        }

        public Task StartedAsync(CancellationToken ct)
        {
            _calls.Add("Started");
            return Task.CompletedTask;
        }

        public Task StoppingAsync(CancellationToken ct)
        {
            _calls.Add("Stopping");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            _calls.Add("Stop");
            return Task.CompletedTask;
        }

        public Task StoppedAsync(CancellationToken ct)
        {
            _calls.Add("Stopped");
            return Task.CompletedTask;
        }
    }

    private sealed class FaultyStartingLifecycleSvc : IHostedLifecycleSvc
    {
        private readonly List<string> _calls = new();
        public IReadOnlyList<string> Calls => _calls;

        public Task StartingAsync(CancellationToken ct)
        {
            _calls.Add("Starting");
            throw new InvalidOperationException("StartingAsync failed on purpose");
        }

        public Task StartAsync(CancellationToken ct)
        {
            _calls.Add("Start");
            return Task.CompletedTask;
        }

        public Task StartedAsync(CancellationToken ct)
        {
            _calls.Add("Started");
            return Task.CompletedTask;
        }

        public Task StoppingAsync(CancellationToken ct)
        {
            _calls.Add("Stopping");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            _calls.Add("Stop");
            return Task.CompletedTask;
        }

        public Task StoppedAsync(CancellationToken ct)
        {
            _calls.Add("Stopped");
            return Task.CompletedTask;
        }
    }

    private sealed class PlainHostedSvc : IHostedSvc
    {
        private readonly List<string> _calls = new();
        public IReadOnlyList<string> Calls => _calls;

        public Task StartAsync(CancellationToken ct)
        {
            _calls.Add("Start");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            _calls.Add("Stop");
            return Task.CompletedTask;
        }
    }

    private sealed class LifecycleTestSvcB : IHostedLifecycleSvc
    {
        private readonly List<string> _calls = new();
        public IReadOnlyList<string> Calls => _calls;

        public Task StartingAsync(CancellationToken ct)
        {
            _calls.Add("Starting");
            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken ct)
        {
            _calls.Add("Start");
            return Task.CompletedTask;
        }

        public Task StartedAsync(CancellationToken ct)
        {
            _calls.Add("Started");
            return Task.CompletedTask;
        }

        public Task StoppingAsync(CancellationToken ct)
        {
            _calls.Add("Stopping");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            _calls.Add("Stop");
            return Task.CompletedTask;
        }

        public Task StoppedAsync(CancellationToken ct)
        {
            _calls.Add("Stopped");
            return Task.CompletedTask;
        }
    }

    [Test]
    public async Task Lifecycle_StartAsync_CallsAllPhasesInOrder()
    {
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        var svc = new LifecycleTestSvc();
        container.RegisterHostedSvc<LifecycleTestSvc>(_ => svc);
        container.Build();
        var host = new SvcHost(container);

        await host.StartAsync();

        await using var scope = container.CreateScope();
        var resolved = scope.GetService<LifecycleTestSvc>();
        // Sequence, not membership: the default comparison is a set, which cannot tell
        // Starting → Start → Started from Starting → Started → Start — the start chain could be
        // reordered with this test green (review-2026-11-02 test-validity finding 3).
        await Assert
            .That(resolved!.Calls)
            .IsEquivalentTo(new[] { "Starting", "Start", "Started" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Lifecycle_StopAsync_CallsAllPhasesInOrder()
    {
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        var svc = new LifecycleTestSvc();
        container.RegisterHostedSvc<LifecycleTestSvc>(_ => svc);
        container.Build();
        var host = new SvcHost(container);

        await host.StartAsync();
        await host.StopAsync();

        await using var scope = container.CreateScope();
        var resolved = scope.GetService<LifecycleTestSvc>();
        // Both chains are pinned as sequences: the start chain Starting → Start → Started and
        // the stop chain Stopping → Stop → Stopped (review-2026-11-02 test-validity finding 3).
        await Assert
            .That(resolved!.Calls)
            .IsEquivalentTo(
                new[] { "Starting", "Start", "Started", "Stopping", "Stop", "Stopped" },
                CollectionOrdering.Matching
            );
    }

    [Test]
    public async Task Lifecycle_ExceptionInStarting_AbortsTheStartSequence()
    {
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        var svc = new FaultyStartingLifecycleSvc();
        container.RegisterHostedSvc<FaultyStartingLifecycleSvc>(_ => svc);
        container.Build();
        var host = new SvcHost(container);

        // review-2026-11-02 finding 2: the failure faults the caller (it used to be swallowed
        // into OnError, default null) and the phase chain stops at the failing phase.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        await Assert.That(ex.Message).IsEqualTo("StartingAsync failed on purpose");

        await using var scope = container.CreateScope();
        var resolved = scope.GetService<FaultyStartingLifecycleSvc>();

        await Assert.That(resolved!.Calls).IsEquivalentTo(new[] { "Starting" });
    }

    [Test]
    public async Task Lifecycle_PlainHostedSvc_DoesNotCallPhases()
    {
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        var svc = new PlainHostedSvc();
        container.RegisterHostedSvc<PlainHostedSvc>(_ => svc);
        container.Build();
        var host = new SvcHost(container);

        await host.StartAsync();

        await using var scope = container.CreateScope();
        var resolved = scope.GetService<PlainHostedSvc>();

        await Assert.That(resolved!.Calls).IsEquivalentTo(new[] { "Start" });
    }

    [Test]
    public async Task Lifecycle_MultipleServices_CorrectOrder()
    {
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        var svcA = new LifecycleTestSvc();
        var svcB = new LifecycleTestSvcB();
        container.RegisterHostedSvc<LifecycleTestSvc>(_ => svcA);
        container.RegisterHostedSvc<LifecycleTestSvcB>(_ => svcB);
        container.Build();
        var host = new SvcHost(container);

        await host.StartAsync();

        // Each service's phase chain is pinned as a sequence. The cross-service half of the
        // name (A finishes before B starts) is pinned in HostingIntegrationTests by
        // MultipleLifecycleServices_StartInRegistrationOrder, whose markers carry the service
        // name — two per-service lists cannot see which of them went first.
        await Assert
            .That(svcA.Calls)
            .IsEquivalentTo(new[] { "Starting", "Start", "Started" }, CollectionOrdering.Matching);
        await Assert
            .That(svcB.Calls)
            .IsEquivalentTo(new[] { "Starting", "Start", "Started" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Lifecycle_EmptyContainer_NoPhases()
    {
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.Build();
        var host = new SvcHost(container);

        await host.StartAsync();
        await host.StopAsync();
    }
}
