namespace PicoDI.Test;

public class ContractAssemblyTests
{
    [Test]
    public async Task SourceGeneratorRequiredException_LivesInTheAbsContractAssembly()
    {
        // It is part of the PicoDI.Abs contract package: consumers that reference
        // only the abstractions must be able to catch it without taking the
        // runtime assembly into their dependency closure.
        var assemblyName = typeof(SourceGeneratorRequiredException).Assembly.GetName().Name;
        await Assert.That(assemblyName).IsEqualTo("PicoDI.Abs");
    }

    [Test]
    public async Task RuntimeAssembly_ForwardsTheExceptionType_ForBinaryCompat()
    {
        // The type moved from PicoDI.dll to PicoDI.Abs.dll. A loader lookup that
        // targets the runtime assembly must still resolve it through the forwarder
        // — the same path a pre-compiled consumer's reference takes.
        var resolved = Type.GetType(
            "PicoDI.Abs.SourceGeneratorRequiredException, PicoDI",
            throwOnError: false
        );
        await Assert.That(resolved).IsEqualTo(typeof(SourceGeneratorRequiredException));
    }
}
