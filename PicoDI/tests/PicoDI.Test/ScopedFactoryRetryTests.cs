namespace PicoDI.Test;

/// <summary>
/// A scoped instance is stored as a <c>Lazy&lt;object&gt;</c> in
/// <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/>, whose contract is "run the factory
/// once and cache the outcome" — including a failure. For caching an instance that is right; for a
/// construction that failed it is poison: one transient fault (a socket not yet up, a missing file,
/// a cycle that was correctly rejected) made every later resolve of that service in that scope
/// rethrow the first exception without ever running the factory again.
/// <para>
/// The approved behaviour change: a failed construction is evicted and the next resolve retries,
/// while a successful instance is still created exactly once and disposed exactly once. The cost
/// is stated where callers look: a scoped factory with side effects may now run more than once per
/// scope.
/// </para>
/// </summary>
public class ScopedFactoryRetryTests
{
    [Test]
    public async Task ScopedFactoryThrowingOnceThenSucceeding_ReturnsTheInstanceOnTheNextResolve()
    {
        var calls = 0;
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterScoped<FlakyService>(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw new InvalidOperationException("first call fails");

            return new FlakyService();
        });
        await using var scope = container.CreateScope();

        var first = Capture(() => scope.GetService<FlakyService>());

        await Assert.That(first).IsTypeOf<InvalidOperationException>();
        await Assert.That(first!.Message).IsEqualTo("first call fails");

        // RED before the fix: the Lazy rethrew the cached exception, so this resolve threw the
        // *first* exception again and the factory stayed at one call for the life of the scope.
        var instance = scope.GetService<FlakyService>();

        await Assert.That(instance).IsNotNull();
        await Assert.That(calls).IsEqualTo(2);
        // The second attempt succeeded, so it is cached like any other scoped instance.
        await Assert.That(scope.GetService<FlakyService>()).IsSameReferenceAs(instance);
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task ScopedFactoryThatAlwaysThrows_ThrowsOnEveryResolve_WithoutCachingTheFirstFailure()
    {
        var calls = 0;
        var messages = new List<string>();
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterScoped<AlwaysBoomService>(_ =>
            // The message names the call, so a cached failure is visible as a repeated "boom 1".
            throw new InvalidOperationException($"boom {Interlocked.Increment(ref calls)}")
        );
        await using var scope = container.CreateScope();

        for (var i = 0; i < 3; i++)
        {
            var thrown = Capture(() => scope.GetService<AlwaysBoomService>());
            await Assert.That(thrown).IsTypeOf<InvalidOperationException>();
            messages.Add(thrown!.Message);
        }

        // RED before the fix: calls = 1 and every message was "boom 1" (the Lazy cached the failure).
        await Assert.That(calls).IsEqualTo(3);
        await Assert.That(string.Join(" | ", messages)).IsEqualTo("boom 1 | boom 2 | boom 3");
        // Each failed attempt popped its own frame: nothing is left "under construction".
        await Assert.That(SvcConstructionGuard.DepthForCurrentThread).IsEqualTo(0);
    }

    [Test]
    public async Task SuccessfulScopedFactory_IsCreatedOnce_AndDisposedOnce()
    {
        var created = 0;
        var disposed = 0;
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterScoped<TrackedService>(_ =>
        {
            Interlocked.Increment(ref created);
            return new TrackedService(() => Interlocked.Increment(ref disposed));
        });
        var scope = container.CreateScope();

        var first = scope.GetService<TrackedService>();
        var second = scope.GetService<TrackedService>();
        var third = scope.GetService<TrackedService>();

        await Assert.That(second).IsSameReferenceAs(first);
        await Assert.That(third).IsSameReferenceAs(first);
        await Assert.That(created).IsEqualTo(1);
        await Assert.That(disposed).IsEqualTo(0);

        await scope.DisposeAsync();

        await Assert.That(disposed).IsEqualTo(1);
        await Assert.That(created).IsEqualTo(1);
    }

    /// <summary>
    /// The eviction must not weaken the cycle diagnostic: a rejected scoped cycle is reported again
    /// (a fresh detection, naming the cycle) instead of being swallowed, and the *second* attempt
    /// must not be mistaken for the first one still being under construction.
    /// </summary>
    [Test]
    public async Task RejectedScopedCycle_IsReportedAgainOnASecondAttempt()
    {
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterScoped<SelfCycleService>(sp => sp.GetService<SelfCycleService>());
        await using var scope = container.CreateScope();

        var first = Capture(() => scope.GetService<SelfCycleService>());
        var second = Capture(() => scope.GetService<SelfCycleService>());

        await Assert.That(first).IsTypeOf<PicoDiException>();
        await Assert.That(second).IsTypeOf<PicoDiException>();
        await Assert.That(first!.Message).Contains("SelfCycleService -> SelfCycleService");
        await Assert.That(second!.Message).Contains("SelfCycleService -> SelfCycleService");
        await Assert.That(SvcConstructionGuard.DepthForCurrentThread).IsEqualTo(0);
    }

    private static Exception? Capture(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private sealed class FlakyService;

    private sealed class AlwaysBoomService;

    private sealed class SelfCycleService;

    private sealed class TrackedService(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
