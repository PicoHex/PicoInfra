namespace PicoDI.Test;

/// <summary>
/// A hosted service registered more than once must be tracked per <i>registration</i>, not per type —
/// two instances, two factories, or one instance shared by two registrations. Collecting one
/// registration per type dropped the earlier ones: they never started and never stopped, and the
/// container still reported a successful start.
/// </summary>
public class HostedSvcDuplicateRegistrationTests
{
    private interface IOtherHostedSvc : IHostedSvc;

    private sealed class RecordingHostedSvc(string name, List<string> log)
        : IHostedSvc,
            IOtherHostedSvc
    {
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }

        public Task StartAsync(CancellationToken ct)
        {
            StartCount++;
            log.Add($"start:{name}");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            StopCount++;
            log.Add($"stop:{name}");
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A second hosted type, so a shared instance can sit at tracked positions 0 and 2 with a
    /// different service at position 1. Implements <see cref="IOtherHostedSvc"/> so this test adds
    /// no further type to the process-global <c>SvcHostedServiceRegistry</c>.
    /// </summary>
    private sealed class LaterHostedSvc(string name, List<string> log) : IOtherHostedSvc
    {
        public Task StartAsync(CancellationToken ct)
        {
            log.Add($"start:{name}");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            log.Add($"stop:{name}");
            return Task.CompletedTask;
        }
    }

    [Test]
    public async Task TwoInstances_OfTheSameHostedType_BothStartInRegistrationOrder_AndBothStopInReverse()
    {
        var log = new List<string>();
        var first = new RecordingHostedSvc("first", log);
        var second = new RecordingHostedSvc("second", log);

        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterHostedSvc<RecordingHostedSvc>(_ => first);
        container.RegisterHostedSvc<RecordingHostedSvc>(_ => second);
        container.Build();
        var host = new SvcHost(container);

        await host.StartAsync();

        // Both registrations are live services: forward order on start.
        await Assert.That(string.Join(", ", log)).IsEqualTo("start:first, start:second");

        await host.StopAsync();

        // ... and reverse order on stop.
        await Assert
            .That(string.Join(", ", log))
            .IsEqualTo("start:first, start:second, stop:second, stop:first");

        await Assert.That(first.StartCount).IsEqualTo(1);
        await Assert.That(first.StopCount).IsEqualTo(1);
        await Assert.That(second.StartCount).IsEqualTo(1);
        await Assert.That(second.StopCount).IsEqualTo(1);
    }

    [Test]
    public async Task OneInstance_RegisteredUnderTwoHostedServiceTypes_StartsAndStopsOnce()
    {
        // Two registrations, two service types, ONE object. The start pass reaches that object
        // through both registrations; running its start twice would be a second start on a
        // service that is already running, while stops run at most once per instance — start and
        // stop stay symmetric at one each.
        var log = new List<string>();
        var shared = new RecordingHostedSvc("shared", log);

        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterHostedSvc<RecordingHostedSvc>(_ => shared);
        container.RegisterHostedSvc<IOtherHostedSvc>(_ => shared);
        container.Build();
        var host = new SvcHost(container);

        await host.StartAsync();
        await host.StopAsync();

        await Assert.That(string.Join(", ", log)).IsEqualTo("start:shared, stop:shared");
        await Assert.That(shared.StartCount).IsEqualTo(1);
        await Assert.That(shared.StopCount).IsEqualTo(1);
    }

    [Test]
    public async Task SharedInstance_AtFirstAndLastRegistration_StopsAtItsStartPosition_NotFirst()
    {
        // Three tracked registrations, one object at positions 0 and 2, a different service at 1:
        //
        //   0: RecordingHostedSvc -> shared      starts first
        //   1: LaterHostedSvc     -> later
        //   2: IOtherHostedSvc    -> shared      same object, already started -> skipped
        //
        // The object started at position 0, so LIFO must stop it LAST. Walking the *registration*
        // list backwards stopped it at position 2 — while "later", a later-started service, was
        // still running (rev-hosted-dedup finding 1). The stop pass must walk start order.
        var log = new List<string>();
        var shared = new RecordingHostedSvc("shared", log);

        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterHostedSvc<RecordingHostedSvc>(_ => shared);
        container.RegisterHostedSvc<LaterHostedSvc>(_ => new LaterHostedSvc("later", log));
        container.RegisterHostedSvc<IOtherHostedSvc>(_ => shared);
        container.Build();
        var host = new SvcHost(container);

        await host.StartAsync();
        await host.StopAsync();

        await Assert
            .That(string.Join(", ", log))
            .IsEqualTo("start:shared, start:later, stop:later, stop:shared");
        await Assert.That(shared.StartCount).IsEqualTo(1);
        await Assert.That(shared.StopCount).IsEqualTo(1);
    }

    /// <summary>
    /// A hosted service whose stop phase always throws, so the diagnostic message can be checked
    /// for two registrations that share a service type.
    /// </summary>
    private sealed class FailingStopHostedSvc(string name, List<string> log) : IOtherHostedSvc
    {
        public Task StartAsync(CancellationToken ct)
        {
            log.Add($"start:{name}");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct) => throw new InvalidOperationException(name);
    }

    [Test]
    public async Task StartOrder_IsGroupedByServiceType_ThenRegistrationOrderWithinTheType()
    {
        // What the docs now say, measured: the tracked list is built by walking `cache`, a
        // Dictionary<Type, List<...>>, so A1, B, A2 starts a1, a2, b — not a1, b, a2
        // (rev-hosted-dedup finding 3). Shutdown is still the exact reverse of start.
        var log = new List<string>();
        var a1 = new RecordingHostedSvc("a1", log);
        var a2 = new RecordingHostedSvc("a2", log);

        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterHostedSvc<RecordingHostedSvc>(_ => a1);
        container.RegisterHostedSvc<IOtherHostedSvc>(_ => new LaterHostedSvc("b", log));
        container.RegisterHostedSvc<RecordingHostedSvc>(_ => a2);
        container.Build();
        var host = new SvcHost(container);

        await host.StartAsync();
        await host.StopAsync();

        await Assert
            .That(string.Join(", ", log))
            .IsEqualTo("start:a1, start:a2, start:b, stop:b, stop:a2, stop:a1");
    }

    [Test]
    public async Task TwoRegistrations_OfOneType_SayWhichRegistrationFailed()
    {
        // Both registrations report the same service type, so without the registration's position
        // the two messages are byte-identical (rev-hosted-dedup finding 4).
        var log = new List<string>();
        var contexts = new List<string>();

        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.OnError = (_, context) =>
        {
            lock (contexts)
                contexts.Add(context);
        };
        container.RegisterHostedSvc<IOtherHostedSvc>(_ => new FailingStopHostedSvc("one", log));
        container.RegisterHostedSvc<IOtherHostedSvc>(_ => new FailingStopHostedSvc("two", log));
        container.Build();
        var host = new SvcHost(container);

        await host.StartAsync();
        // Both stops throw: shutdown stays fault-tolerant and reports every failure.
        await Assert.ThrowsAsync<AggregateException>(() => host.StopAsync());

        await Assert.That(contexts.Count).IsEqualTo(2);
        await Assert
            .That(contexts.Any(c => c.Contains("(registration 1 of 2)", StringComparison.Ordinal)))
            .IsTrue();
        await Assert
            .That(contexts.Any(c => c.Contains("(registration 2 of 2)", StringComparison.Ordinal)))
            .IsTrue();
    }

    [Test]
    public async Task TwoFactories_OfTheSameHostedType_EachCreateAndStartTheirOwnInstance()
    {
        // Two registrations, two factories, none pre-built: the instance must be resolved
        // through *its own* registration. Resolving by type hands the last registration's
        // singleton to every entry, which is the same defect one layer down.
        var log = new List<string>();

        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterHostedSvc<RecordingHostedSvc>(_ => new RecordingHostedSvc("first", log));
        container.RegisterHostedSvc<RecordingHostedSvc>(_ => new RecordingHostedSvc("second", log));
        container.Build();
        var host = new SvcHost(container);

        await host.StartAsync();
        await host.StopAsync();

        await Assert
            .That(string.Join(", ", log))
            .IsEqualTo("start:first, start:second, stop:second, stop:first");
    }
}
