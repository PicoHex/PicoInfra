namespace PicoDI.Test;

using System.Collections.Concurrent;

/// <summary>
/// Regression coverage for the lazy <see cref="SvcContainer.Build"/> vs. deferred
/// auto-configuration race.
///
/// <para>
/// <see cref="SvcContainer"/> (with <c>autoConfigureFromGenerator: true</c>, the
/// default) never forces a build; the first <see cref="ISvcContainer.CreateScope"/>
/// does it lazily. Deferred configurators (PicoMediator/PicoActor generated
/// auto-subscriptions) run inside <c>Build()</c> and call
/// <see cref="ISvcContainer.Register"/> while they run.
/// </para>
/// <para>
/// Before the fix <c>Build()</c> ran the configurators outside any build gate and
/// only serialized the freeze step. A second concurrent first-scope caller saw the
/// deferred group already marked "applied" (the registry marks before running),
/// skipped the configurators and froze the container while the first caller was
/// still registering — the next <c>Register</c> call then threw
/// <see cref="InvalidOperationException"/> ("Cannot register services after
/// Build() has been called"), surfacing as a bare HTTP 500 in hosts that created
/// the first scope outside their exception guard.
/// </para>
/// </summary>
[NotInParallel]
public class DeferredAutoConfigurationRaceTests
{
    [Test]
    public async Task DeferredAutoConfiguration_ConcurrentFirstScopeCreation_MustNotThrow()
    {
        GeneratorConfiguratorRegistry.Clear();
        try
        {
            await using var container = new SvcContainer(); // autoConfigureFromGenerator: true

            using var configuratorStarted = new ManualResetEventSlim(false);
            using var releaseConfigurator = new ManualResetEventSlim(false);

            GeneratorConfiguratorRegistry.RegisterDeferred(
                "race::deferred",
                c =>
                {
                    // The registry is process-global: only drive the container under
                    // test (another parallel test's container must be unaffected).
                    if (!ReferenceEquals(c, container))
                        return;

                    c.RegisterScoped<ISimpleService>(static _ => new SimpleService());
                    configuratorStarted.Set();

                    // Hold the configurator open so the concurrent first-scope
                    // callers get a wide window to race the freeze.
                    releaseConfigurator.Wait(TimeSpan.FromSeconds(10));

                    // Under the bug this call hits a container that was frozen by a
                    // concurrent Build() and throws.
                    c.RegisterScoped<ILevelOneService>(static _ => new LevelOneService());
                }
            );

            var failures = new ConcurrentBag<Exception>();

            // First first-scope caller: enters Build() and runs the deferred configurator.
            var firstScope = Task.Run(async () =>
            {
                try
                {
                    await using var scope = container.CreateScope();
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            });

            await Assert.That(configuratorStarted.Wait(TimeSpan.FromSeconds(10))).IsTrue();

            // Concurrent first-scope callers must be blocked by (or at least not
            // corrupt) the in-flight build instead of freezing the container.
            var concurrentScopes = Enumerable
                .Range(0, 32)
                .Select(_ =>
                    Task.Run(async () =>
                    {
                        try
                        {
                            await using var scope = container.CreateScope();
                        }
                        catch (Exception ex)
                        {
                            failures.Add(ex);
                        }
                    })
                )
                .ToArray();

            // Buggy code: these callers skip the configurators and freeze the
            // container, so they complete while the configurator is still open.
            // Fixed code: they wait on the build gate, so this window expires and
            // the configurator completes afterwards.
            await Task.WhenAny(
                Task.WhenAll(concurrentScopes),
                Task.Delay(TimeSpan.FromMilliseconds(500))
            );

            releaseConfigurator.Set();

            await Task.WhenAll(concurrentScopes.Append(firstScope));

            await Assert.That(failures).IsEmpty();
            await Assert
                .That(SvcContainerAutoConfiguration.HasAppliedDeferredConfiguration(container))
                .IsTrue();

            await using var scope = container.CreateScope();
            await Assert.That(scope.GetService<ISimpleService>()).IsNotNull();
            await Assert.That(scope.GetService<ILevelOneService>()).IsNotNull();
        }
        finally
        {
            GeneratorConfiguratorRegistry.Clear();
        }
    }

    [Test]
    public async Task DeferredAutoConfiguration_ConcurrentGetImplicitRootScope_MustNotThrow()
    {
        GeneratorConfiguratorRegistry.Clear();
        try
        {
            await using var container = new SvcContainer();

            using var configuratorStarted = new ManualResetEventSlim(false);
            using var releaseConfigurator = new ManualResetEventSlim(false);

            GeneratorConfiguratorRegistry.RegisterDeferred(
                "race::deferred-root-scope",
                c =>
                {
                    if (!ReferenceEquals(c, container))
                        return;

                    c.RegisterScoped<ISimpleService>(static _ => new SimpleService());
                    configuratorStarted.Set();
                    releaseConfigurator.Wait(TimeSpan.FromSeconds(10));
                    c.RegisterScoped<ILevelOneService>(static _ => new LevelOneService());
                }
            );

            var failures = new ConcurrentBag<Exception>();

            // GetImplicitRootScope() shares the same lazy-Build pattern as
            // CreateScope() (singleton factories are invoked from it).
            var firstResolution = Task.Run(() =>
            {
                try
                {
                    container.GetImplicitRootScope();
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            });

            await Assert.That(configuratorStarted.Wait(TimeSpan.FromSeconds(10))).IsTrue();

            var concurrentResolutions = Enumerable
                .Range(0, 32)
                .Select(_ =>
                    Task.Run(() =>
                    {
                        try
                        {
                            container.GetImplicitRootScope();
                        }
                        catch (Exception ex)
                        {
                            failures.Add(ex);
                        }
                    })
                )
                .ToArray();

            await Task.WhenAny(
                Task.WhenAll(concurrentResolutions),
                Task.Delay(TimeSpan.FromMilliseconds(500))
            );

            releaseConfigurator.Set();

            await Task.WhenAll(concurrentResolutions.Append(firstResolution));

            await Assert.That(failures).IsEmpty();
        }
        finally
        {
            GeneratorConfiguratorRegistry.Clear();
        }
    }
}
