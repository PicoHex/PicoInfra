namespace PicoDI.Test;

/// <summary>
/// review-2026-11-02 finding 4: <c>SvcHost.StartAsync</c> on a container that was never
/// <c>Build()</c>-ed read a null hosted-registration list, started nothing, and still flipped
/// the one-shot state to Started — so even after a later <c>Build()</c> a second
/// <c>StartAsync</c> could never start anything.
/// </summary>
/// <remarks>
/// The fix chosen: <c>StartAsync</c> builds the container first (the same lazy <c>Build()</c>
/// that <c>CreateScope()</c> and <c>GetImplicitRootScope()</c> already perform), instead of
/// throwing. Rationale: the container's whole public surface treats "not built yet" as
/// "build it now" — a host over a fresh <c>new SvcContainer()</c> is the natural first thing
/// user code writes, and throwing there would make <c>StartAsync</c> the only member that
/// cannot be called without an explicit <c>Build()</c>. The one-shot state is consumed only
/// after the build succeeds: a disposed container faults every <c>StartAsync</c> with
/// <see cref="ObjectDisposedException"/> instead of quietly eating the single start.
/// </remarks>
public class SvcHostUnbuiltContainerTests
{
    private sealed class CountingHostedSvc : IHostedSvc
    {
        private int _starts;
        private int _stops;

        public int Starts => Volatile.Read(ref _starts);
        public int Stops => Volatile.Read(ref _stops);

        public Task StartAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _starts);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _stops);
            return Task.CompletedTask;
        }
    }

    [Test]
    public async Task StartAsync_OnUnbuiltContainer_StartsTheHostedServices()
    {
        var svc = new CountingHostedSvc();
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterHostedSvc<CountingHostedSvc>(_ => svc);

        // Deliberately NOT calling container.Build() — this is the documented SvcHost entry
        // point: `new SvcHost(container)`.
        var host = new SvcHost(container);
        await host.StartAsync();

        await Assert.That(svc.Starts).IsEqualTo(1);
    }

    [Test]
    public async Task StartAsync_OnUnbuiltContainer_DoesNotBurnTheOneShot()
    {
        var svc = new CountingHostedSvc();
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterHostedSvc<CountingHostedSvc>(_ => svc);

        // The reviewer's exact sequence: start on an unbuilt container, then Build(), then
        // start again. Before the fix the first call started nothing and consumed the
        // one-shot, so the second call could never start anything either (starts == 0).
        var host = new SvcHost(container);
        await host.StartAsync();
        container.Build();
        await host.StartAsync();

        await Assert.That(svc.Starts).IsEqualTo(1);
        await Assert.That(container.IsRegistered(typeof(CountingHostedSvc))).IsTrue();
    }

    [Test]
    public async Task StartAsync_AfterTheContainerWasDisposed_ThrowsEveryTime()
    {
        var svc = new CountingHostedSvc();
        var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterHostedSvc<CountingHostedSvc>(_ => svc);
        var host = new SvcHost(container);

        await container.DisposeAsync();

        // The build-first path fails before the one-shot transition, so the state is not
        // consumed: both calls report the real problem instead of the second one silently
        // returning as if the host had started.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.StartAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.StartAsync());
        await Assert.That(svc.Starts).IsEqualTo(0);

        await host.DisposeAsync();
    }

    [Test]
    public async Task SvcHostBuilder_StillBuildsBeforeStarting()
    {
        // The documented path must stay unaffected (it already calls container.Build()).
        var svc = new CountingHostedSvc();
        var builder = new SvcHostBuilder(autoConfigureFromGenerator: false);
        builder.ConfigureServices(container =>
            container.RegisterHostedSvc<CountingHostedSvc>(_ => svc)
        );

        await using var host = await builder.BuildAsync();

        await Assert.That(svc.Starts).IsEqualTo(1);
    }
}
