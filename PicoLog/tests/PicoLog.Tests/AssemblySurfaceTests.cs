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
        var picoLogAssembly = typeof(LoggerFactory).Assembly;
        var references = picoLogAssembly
            .GetReferencedAssemblies()
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
            "System.Threading",
            "System.Threading.Channels",
            "System.Threading.Thread",
        ];

        // The one exception, and it is not a dependency of ours: the linux-x64 leg of CI runs this
        // project under Microsoft.Testing.Extensions.CodeCoverage, whose instrumenter REWRITES
        // PicoLog.dll on disk before the test host loads it, and the helper types it injects drag
        // in an AssemblyRef the compiler never emits. Measured — this Release build instrumented
        // with dotnet-coverage 18.10.0, the exact engine behind the CodeCoverage package version
        // this project restores:
        //
        //     netstandard
        //
        // That is the entire list. The CI failure proves it: the allow-list below joins to 236
        // characters and the observed string differed at index 236, which is where `,netstandard`
        // was appended. A name may be added here only from such a measurement, and it is stripped
        // only while the instrumenter's own types are present in the module (see below).
        string[] coverageInjectedReferences = ["netstandard"];

        var instrumentationPresent = HasCoverageInstrumentation(picoLogAssembly);
        var strippedReferences = instrumentationPresent
            ? references
                .Where(name => !coverageInjectedReferences.Contains(name, StringComparer.Ordinal))
                .ToArray()
            : references;

        // Strip first, then compare exactly: the allow-list stays 11 entries long, so anything that
        // is not "the allow-list plus the measured instrumentation names" is still red.
        await Assert
            .That(string.Join(",", strippedReferences))
            .IsEqualTo(string.Join(",", allowedReferences));

        // The exemption must not be able to hide a real reference, so it is conditional on the
        // assembly actually being instrumented (detected from the injected types, not from the
        // reference list itself — that would be circular). In a plain build none of those names may
        // appear at all: if one does, the compiler emitted it and this test is red.
        if (!instrumentationPresent)
        {
            var unjustified = references
                .Where(name => coverageInjectedReferences.Contains(name, StringComparer.Ordinal))
                .ToArray();

            await Assert
                .That(string.Join(",", unjustified))
                .IsEqualTo(string.Empty)
                .Because(
                    "the coverage exemption is instrumentation-only; without the instrumenter's types in the assembly, a name on that list is a real reference"
                );
        }

        // A second, targeted assertion, because the cheap fix for a red allow-list is to append the
        // offending name to it: the rule is "core carries no serializer", so a serializer-named
        // reference is a red test even after someone has added it to the list above. The fragment
        // match is a heuristic on purpose — it is the belt, the exact list is the braces. It runs on
        // the ORIGINAL set, so a serializer injected by the coverage tooling still fails here.
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

    // The Cecil instrumenter behind Microsoft.Testing.Extensions.CodeCoverage injects these two
    // types into every module it rewrites (measured: both are present in the instrumented
    // PicoLog.dll, alongside the single reference above). Their presence is the signal that the
    // measured assembly is instrumented; if a future instrumenter renames them this returns false,
    // nothing is stripped, and the exact comparison goes red on the extra references instead of
    // quietly ignoring them. The fix then is to re-measure — never to widen the strip list.
    private static readonly string[] CoverageInstrumentationTypeNames =
    [
        "Microsoft.CodeCoverage.Instrumentation.Static.Tracker.StaticManagedTrackerTemplate_",
        "Microsoft.CodeCoverage.Instrumentation.Static.Tracker.PipeHelper_",
    ];

#pragma warning disable IL2026
    // Test-only reflection: this assembly is never trimmed or published.
    private static bool HasCoverageInstrumentation(System.Reflection.Assembly assembly) =>
        CoverageInstrumentationTypeNames.Any(typeName =>
            assembly.GetType(typeName, throwOnError: false) is not null
        );
#pragma warning restore IL2026

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
