namespace PicoSchedule.Tests;

public sealed class AotContractTests
{
    [Test]
    public async Task Assembly_HasNoDynamicCodeDependencies()
    {
        // AOT contract (spec §11): zero reflection / zero expression trees /
        // zero runtime code generation — no IL2xxx exceptions allowed.
        var assembly = typeof(PicoSchedule.Scheduler).Assembly;
        var names = assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToHashSet(StringComparer.Ordinal);

        await Assert.That(names).DoesNotContain("System.Linq.Expressions");
        await Assert.That(names).DoesNotContain("System.Reflection.Emit");
        await Assert.That(names).DoesNotContain("System.Reflection");
    }

    [Test]
    public async Task PublicApi_NoStringTypeActivation()
    {
        // Contract: no public API accepts a type name for activation (the
        // Quartz-style Job-by-string hole is designed out).
        var types = typeof(PicoSchedule.Scheduler).Assembly.GetExportedTypes();
        foreach (var t in types)
        {
            foreach (var m in t.GetMethods())
            {
                if (m.Name.Contains("Job") || m.Name.Contains("Activate"))
                    await Assert
                        .That(m.GetParameters().Any(p => p.ParameterType == typeof(Type)))
                        .IsFalse();
            }
        }
    }
}
