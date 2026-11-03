namespace PicoDI.Test;

/// <summary>
/// A hosted service registered more than once under the <b>same</b> service type must be tracked
/// per <i>registration</i>, not per type. Collecting one registration per type dropped the earlier
/// ones: one instance never started and never stopped, and the container reported success.
/// </summary>
public class HostedSvcDuplicateRegistrationTests
{
    private sealed class RecordingHostedSvc(string name, List<string> log) : IHostedSvc
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
