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
        //
        // Type.GetType with an assembly-qualified name is the string-literal-safe form (the trim
        // analyzer points Assembly.GetType callers at it), and it returns null both for "type not
        // in that assembly" and for "assembly not resolvable" — so the positive control below
        // proves the lookup really reached PicoLog.
        await Assert.That(Type.GetType("PicoLog.SeqSink, PicoLog")).IsNull();
        await Assert.That(Type.GetType("PicoLog.FileSink, PicoLog")).IsNotNull();
    }

    [Test]
    public async Task PicoLog_ReferencesNoSerializer()
    {
        // The core package's entire dependency list is PicoLog.Abs plus in-framework assemblies.
        // Serialization is the sink package's job (PicoLog.Json references PicoJetson), and this
        // test is the tripwire for a "just reference the writer" shortcut inside core.
        //
        // The assertion is an ALLOW-LIST of every assembly PicoLog.dll may reference — not a
        // deny-list of Pico-named ones. A `name.StartsWith("Pico")` filter can only ever see a
        // Pico-named serializer, so the first person who reaches for the obvious framework
        // serializer walks straight through it: with `typeof(System.Text.Json.JsonDocument)`
        // compiled into this assembly the dll carries a real System.Text.Json AssemblyRef and that
        // filter still passed. An exact list has no such blind spot — a new reference fails here
        // whether it is Pico-named, framework-named, or a transitive package.
        //
        // What this cannot catch: an UNUSED <PackageReference> in PicoLog.csproj. Roslyn emits an
        // AssemblyRef only for an assembly whose types are used, so a bare
        // `<PackageReference Include="PicoJetson" />` leaves this assembly's metadata unchanged and
        // the pin green (measured) — even though the reference still installs the package for
        // every consumer of PicoLog. Catching that needs a csproj-level pin, which is deliberately
        // not done here: it would have to find and parse the project file from inside a test
        // binary, and this pin has to hold for the shipped assembly, not for the source tree.
        //
        // Keeping the list exact is the point: adding a dependency means editing it here, which is
        // the review moment the rule needs.
#pragma warning disable IL2026
        // Test-only reflection: this assembly is never trimmed or published.
        var references = typeof(LoggerFactory)
            .Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
#pragma warning restore IL2026

        string[] allowedReferences =
        [
            "PicoLog.Abs",
            "System.Collections",
            "System.Collections.Concurrent",
            "System.Console",
            "System.Diagnostics.DiagnosticSource",
            "System.Linq",
            "System.Runtime",
            "System.Runtime.InteropServices",
            // Not a serializer: this is the reference assembly that declares UTF8Encoding, and it
            // is the only way to construct a UTF-8 encoding that emits no preamble. Encoding.UTF8
            // (which needs nothing from here) is exactly what put a BOM in front of the first log
            // line; the alternative, `new StreamWriter(stream)`, silently picks the internal
            // UTF8NoBOM whose throwOnInvalidBytes is true, so a message carrying a sliced
            // surrogate would throw EncoderFallbackException inside the sink instead of being
            // replaced. The serializer assertion below still holds — this name matches none of
            // its fragments.
            "System.Text.Encoding.Extensions",
            "System.Threading",
            "System.Threading.Channels",
            "System.Threading.Thread",
        ];

        await Assert
            .That(string.Join(",", references))
            .IsEqualTo(string.Join(",", allowedReferences));

        // A second, targeted assertion, because the cheap fix for a red allow-list is to append the
        // offending name to it: the rule is "core carries no serializer", so a serializer-named
        // reference is a red test even after someone has added it to the list above. The fragment
        // match is a heuristic on purpose — it is the belt, the exact list is the braces.
        string[] serializerNameFragments = ["json", "serde", "jetson", "serializer", "newtonsoft"];
        var serializerReferences = references
            .Where(name =>
                serializerNameFragments.Any(fragment =>
                    name.Contains(fragment, StringComparison.OrdinalIgnoreCase)
                )
            )
            .ToArray();

        await Assert
            .That(string.Join(",", serializerReferences))
            .IsEqualTo(string.Empty)
            .Because("PicoLog must carry no serializer; the JSON sinks live in PicoLog.Json");
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
