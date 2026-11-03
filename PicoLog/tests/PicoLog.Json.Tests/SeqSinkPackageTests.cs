namespace PicoLog.Json.Tests;

/// <summary>
/// Pins where <see cref="SeqSink"/> lives and what that placement promises a consumer.
/// </summary>
/// <remarks>
/// The type moved from the core <c>PicoLog</c> package into <c>PicoLog.Json</c> because it is a
/// sink <em>implementation</em> that serializes JSON, and the core package is not allowed to carry
/// a serializer at all. The namespace did not move with it: <c>using PicoLog;</c> and
/// <c>new SeqSink(...)</c> still compile, so the only thing a consumer adds is a package reference.
/// </remarks>
public sealed class SeqSinkPackageTests
{
    [Test]
    public async Task SeqSink_ShipsInPicoLogJson_AndKeepsTheCoreNamespace()
    {
        var assembly = typeof(SeqSink).Assembly.GetName().Name;

        await Assert
            .That(assembly)
            .IsEqualTo("PicoLog.Json")
            .Because("a sink implementation belongs to the sink package, not to core");
        await Assert
            .That(typeof(SeqSink).Namespace)
            .IsEqualTo("PicoLog")
            .Because("source compatibility: `using PicoLog;` must still find SeqSink");
    }

    [Test]
    public async Task SeqSink_PackageReferencesTheJsonWriterItSerializesWith()
    {
        // The whole point of the move: the sink writes through PicoJetson's JsonWriter, which is
        // only reachable from the package that references it. If this assertion fails because the
        // reference disappeared, SeqSink no longer compiles — so the test documents the contract
        // rather than guarding a live risk.
        // Test-only reflection: this assembly is never trimmed or published, so the IL2026 the
        // analyzer raises for GetReferencedAssemblies() cannot bite here.
#pragma warning disable IL2026
        var references = typeof(SeqSink).Assembly.GetReferencedAssemblies().Select(a => a.Name);
#pragma warning restore IL2026

        await Assert.That(references).Contains("PicoJetson");
        await Assert.That(references).Contains("PicoLog");
    }
}
