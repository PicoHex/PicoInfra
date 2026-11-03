namespace PicoLog.Tests;

public sealed class AssemblySurfaceTests
{
    [Test]
    public async Task PicoLogAbs_ContainsPublicContractSurface()
    {
        var absAssembly = typeof(ILogger).Assembly;
        var loggerMethods = typeof(ILogger)
            .GetMethods()
            .Where(method => method.Name == nameof(ILogger.Log))
            .ToArray();
        var logAsyncMethods = typeof(ILogger)
            .GetMethods()
            .Where(method => method.Name == nameof(ILogger.LogAsync))
            .ToArray();
        var pipelineType = typeof(LoggerFactory).Assembly.GetType("PicoLog.CategoryPipeline");

        await Assert.That(absAssembly.GetType("PicoLog.Abs.ILogSink")).IsNotNull();
        await Assert.That(absAssembly.GetType("PicoLog.Abs.IFlushableLogSink")).IsNotNull();
        await Assert.That(absAssembly.GetType("PicoLog.Abs.ILogFormatter")).IsNotNull();
        await Assert.That(absAssembly.GetType("PicoLog.Abs.LogEntry")).IsNotNull();
        await Assert.That(absAssembly.GetType("PicoLog.Abs.IFlushableLoggerFactory")).IsNotNull();
        await Assert.That(absAssembly.GetType("PicoLog.Abs.FlushExtensions")).IsNotNull();
        await Assert.That(absAssembly.GetType("PicoLog.Abs.IPicoLogControl")).IsNull();
        await Assert.That(absAssembly.GetType("PicoLog.Abs.IStructuredLogger")).IsNull();
        await Assert.That(typeof(IDisposable).IsAssignableFrom(typeof(ILogSink))).IsFalse();
        await Assert.That(typeof(IAsyncDisposable).IsAssignableFrom(typeof(ILogSink))).IsTrue();
        await Assert.That(typeof(IDisposable).IsAssignableFrom(typeof(LoggerFactory))).IsFalse();
        await Assert
            .That(typeof(IAsyncDisposable).IsAssignableFrom(typeof(LoggerFactory)))
            .IsTrue();
        await Assert.That(HasPublicParameterlessDispose(typeof(FileSink))).IsFalse();
        await Assert.That(HasThreadField(typeof(FileSink))).IsFalse();
        await Assert.That(pipelineType).IsNotNull();
        await Assert.That(typeof(IDisposable).IsAssignableFrom(pipelineType!)).IsFalse();
        await Assert.That(typeof(IAsyncDisposable).IsAssignableFrom(pipelineType!)).IsTrue();
        await Assert.That(HasThreadField(pipelineType!)).IsFalse();
        await Assert.That(loggerMethods.Any(method => method.GetParameters().Length == 4)).IsTrue();
        await Assert
            .That(logAsyncMethods.Any(method => method.GetParameters().Length == 5))
            .IsTrue();
    }

    [Test]
    public async Task PicoLog_ContainsRuntimeImplementationTypesOnly()
    {
        var picoLogAssembly = typeof(LoggerFactory).Assembly;

        await Assert.That(picoLogAssembly.GetType("PicoLog.ILogSink")).IsNull();
        await Assert.That(picoLogAssembly.GetType("PicoLog.IFlushableLogSink")).IsNull();
        await Assert.That(picoLogAssembly.GetType("PicoLog.ILogFormatter")).IsNull();
        await Assert.That(picoLogAssembly.GetType("PicoLog.LogEntry")).IsNull();
        await Assert.That(picoLogAssembly.GetType("PicoLog.IFlushableLoggerFactory")).IsNull();
        await Assert.That(picoLogAssembly.GetType("PicoLog.FlushExtensions")).IsNull();
        await Assert.That(picoLogAssembly.GetType("PicoLog.Abs.ILogSink")).IsNull();
        await Assert.That(picoLogAssembly.GetType("PicoLog.Abs.IFlushableLogSink")).IsNull();
        await Assert.That(picoLogAssembly.GetType("PicoLog.Abs.ILogFormatter")).IsNull();
        await Assert.That(picoLogAssembly.GetType("PicoLog.Abs.LogEntry")).IsNull();
        await Assert.That(picoLogAssembly.GetType("PicoLog.Abs.IFlushableLoggerFactory")).IsNull();
        await Assert.That(picoLogAssembly.GetType("PicoLog.Abs.FlushExtensions")).IsNull();
        // A sink implementation is not runtime plumbing: SeqSink serializes JSON, so it ships in
        // PicoLog.Json (namespace unchanged) and the core package carries neither it nor a
        // serializer reference. Pinned here because "core has no sink implementations" is the
        // design rule the move implements, and a file added back to this project would restore
        // the hand-rolled escaper rather than fail a build.
        await Assert.That(picoLogAssembly.GetType("PicoLog.SeqSink")).IsNull();
    }

    [Test]
    public async Task PicoLog_ReferencesNoSerializer()
    {
        // The core package's entire dependency list is PicoLog.Abs. Serialization is the sink
        // package's job (PicoLog.Json references PicoJetson), and this test is the tripwire for a
        // "just reference the writer" shortcut inside core.
#pragma warning disable IL2026
        // Test-only reflection: this assembly is never trimmed or published.
        var references = typeof(LoggerFactory)
            .Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => name.StartsWith("Pico", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
#pragma warning restore IL2026

        await Assert.That(references).IsEquivalentTo((string[])["PicoLog.Abs"]);
    }

    private static bool HasPublicParameterlessDispose(Type type) =>
        type.GetMethods(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
            )
            .Any(method =>
                method.Name == nameof(IDisposable.Dispose) && method.GetParameters().Length == 0
            );

    private static bool HasThreadField(Type type) =>
        type.GetFields(
                System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic
            )
            .Any(field => field.FieldType == typeof(Thread));
}
