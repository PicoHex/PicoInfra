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
        // That is the entire list *as measured on windows-x64*, and it is NOT proven for the
        // linux-x64 leg: CI run 41790525220 was red there while this same measurement reproduces
        // green locally, and the log could not name the extra assembly at all (the renderer
        // truncated both strings to "PicoLog.Abs,…" and only "differs at index 236" survived).
        // Two causes fit that evidence equally — the instrumenter's own types not being detected on
        // Linux, so nothing is stripped, or the linux-path instrumenter appending a name other than
        // this one — so the code below is diagnostic-first: it prints the complete reference list
        // and the detection result before it asserts anything. A name may be added here only from a
        // measurement, never from a guess, and it is still stripped only while the instrumenter's
        // own types are present in the module (see below).
        string[] coverageInjectedReferences = ["netstandard"];

        var instrumentation = ProbeCoverageInstrumentation(picoLogAssembly);
        var instrumentationPresent = instrumentation.IsPresent;
        var strippedReferences = instrumentationPresent
            ? references
                .Where(name => !coverageInjectedReferences.Contains(name, StringComparer.Ordinal))
                .ToArray()
            : references;

        var unexpected = strippedReferences
            .Where(name => !allowedReferences.Contains(name, StringComparer.Ordinal))
            .ToArray();
        var missing = allowedReferences
            .Where(name => !strippedReferences.Contains(name, StringComparer.Ordinal))
            .ToArray();

        // Print before asserting. The red CI log carried "PicoLog.Abs,…" on both sides plus
        // "differs at index 236" and nothing else — no list of assemblies, and no index a human
        // could resolve to a name. stdout survives a failed step, so this is the channel that
        // cannot be truncated by the assertion renderer.
        WriteDiagnostics(
            picoLogAssembly,
            instrumentation,
            references,
            allowedReferences,
            coverageInjectedReferences,
            strippedReferences,
            unexpected,
            missing
        );

        // Name the names BEFORE the exact comparison: one complete list per direction, no index
        // arithmetic, no ellipsis. The exact comparison below stays exactly as it was.
        await Assert
            .That(unexpected)
            .IsEmpty()
            .Because(
                "PicoLog references assemblies outside its allow-list: "
                    + Render(unexpected)
                    + " | received references: "
                    + Render(references)
                    + " | allowed: "
                    + Render(allowedReferences)
                    + " | stripped: "
                    + Render(strippedReferences)
                    + " | instrumentationPresent: "
                    + instrumentationPresent
                    + " | instrumenter types matched: "
                    + Render(instrumentation.MatchedTypeNames)
                    + " | instrumenter scan: "
                    + instrumentation.Scan
            );
        await Assert
            .That(missing)
            .IsEmpty()
            .Because(
                "the allow-list names assemblies this assembly no longer references: "
                    + Render(missing)
                    + " | received references: "
                    + Render(references)
            );

        // The pin, unchanged: strip first, then compare exactly. The allow-list stays 11 entries
        // long, so anything that is not "the allow-list plus the measured instrumentation names" is
        // still red — the two assertions above only make the reason legible.
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
                    "the coverage exemption is instrumentation-only; without the instrumenter's types in the assembly, a name on that list is a real reference. scan: "
                        + instrumentation.Scan
                        + " | detected types: "
                        + Render(instrumentation.MatchedTypeNames)
                        + " | legacy exact-name probe: "
                        + instrumentation.LegacyProbe
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

    // The instrumenter injects its tracker types into every module it rewrites, and their presence
    // is the ONLY positive evidence that this PicoLog.dll is the instrumented copy. Detection is a
    // NAMESPACE scan, not two exact type names: a renamed, added or nested tracker type used to
    // become a silent false negative, and a false negative here means nothing is stripped and the
    // exact comparison goes red on the injected reference instead — exactly the linux-x64 failure
    // this diagnostic was written for.
    //
    // The scan reads the module's METADATA (System.Reflection.Metadata over the file on disk) and
    // never loads a type: Assembly.GetType has to resolve the injected type's dependencies, so it
    // can return null — or throw — for reasons that have nothing to do with instrumentation. The
    // old exact-name probe is still RUN and reported, because "metadata says instrumented, GetType
    // says no" is the difference between the two candidate causes.
    private const string CoverageInstrumentationNamespacePrefix =
        "Microsoft.CodeCoverage.Instrumentation";

    // Measured on windows-x64 with `dotnet-coverage instrument` (18.10.0, the engine behind the
    // Microsoft.Testing.Extensions.CodeCoverage 18.10.0 this project restores) on this Release
    // build: the static instrumenter adds exactly these two types, plus the single `netstandard`
    // AssemblyRef. Reported, never used for the verdict.
    private static readonly string[] MeasuredCoverageInstrumentationTypeNames =
    [
        "Microsoft.CodeCoverage.Instrumentation.Static.Tracker.StaticManagedTrackerTemplate_",
        "Microsoft.CodeCoverage.Instrumentation.Static.Tracker.PipeHelper_",
    ];

    private static InstrumentationProbe ProbeCoverageInstrumentation(
        System.Reflection.Assembly assembly
    )
    {
        var modulePath = ModulePath(assembly);
        var (matched, scan) = modulePath is null
            ? ScanLoadedTypes(assembly)
            : ScanModuleFile(assembly, modulePath);

        return new InstrumentationProbe(
            matched.Length > 0,
            matched,
            scan,
            DescribeLegacyProbe(assembly)
        );
    }

    private static string? ModulePath(System.Reflection.Assembly assembly)
    {
#pragma warning disable IL3000 // Empty only for single-file/in-memory assemblies, handled by the caller.
        var path = assembly.Location;
#pragma warning restore IL3000
        return string.IsNullOrEmpty(path) || !File.Exists(path) ? null : path;
    }

    private static (string[] Matched, string Scan) ScanModuleFile(
        System.Reflection.Assembly assembly,
        string path
    )
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var peReader = new System.Reflection.PortableExecutable.PEReader(stream);
            var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(
                peReader
            );
            var matched = new List<string>();
            var scanned = 0;
            foreach (var handle in metadata.TypeDefinitions)
            {
                scanned++;
                var fullName = TypeDefinitionFullName(metadata, metadata.GetTypeDefinition(handle));
                if (
                    fullName.StartsWith(
                        CoverageInstrumentationNamespacePrefix,
                        StringComparison.Ordinal
                    )
                )
                {
                    matched.Add(fullName);
                }
            }

            matched.Sort(StringComparer.Ordinal);
            return (
                matched.ToArray(),
                $"metadata scan for type names starting '{CoverageInstrumentationNamespacePrefix}' over {scanned} type definition(s) in {path} -> {matched.Count} match(es)"
            );
        }
        catch (Exception exception)
            when (exception
                    is IOException
                        or BadImageFormatException
                        or UnauthorizedAccessException
                        or NotSupportedException
            )
        {
            // A failed scan must never read as "not instrumented by assumption": it is reported as
            // a failure and re-tried against the loaded assembly. The verdict still comes from
            // evidence actually found, never from a default.
            var (matched, scan) = ScanLoadedTypes(assembly);
            return (
                matched,
                $"metadata scan of {path} failed ({exception.GetType().Name}: {exception.Message}); {scan}"
            );
        }
    }

    private static string TypeDefinitionFullName(
        System.Reflection.Metadata.MetadataReader metadata,
        System.Reflection.Metadata.TypeDefinition definition
    )
    {
        var name = metadata.GetString(definition.Name);
        var declaringType = definition.GetDeclaringType();
        if (!declaringType.IsNil)
        {
            return TypeDefinitionFullName(metadata, metadata.GetTypeDefinition(declaringType))
                + "+"
                + name;
        }

        var @namespace = metadata.GetString(definition.Namespace);
        return @namespace.Length == 0 ? name : @namespace + "." + name;
    }

    private static (string[] Matched, string Scan) ScanLoadedTypes(
        System.Reflection.Assembly assembly
    )
    {
        Type?[] loaded;
        string? note = null;
#pragma warning disable IL2026, IL3050
        // Test-only reflection: this assembly is never trimmed or published.
        try
        {
            loaded = assembly.GetTypes();
        }
        catch (System.Reflection.ReflectionTypeLoadException exception)
        {
            loaded = exception.Types;
            note =
                "partially loadable ("
                + exception.LoaderExceptions.Length
                + " loader exception(s))";
        }
        catch (Exception exception)
        {
            loaded = [];
            note = exception.GetType().Name + ": " + exception.Message;
        }
#pragma warning restore IL2026, IL3050

        var matched = loaded
            .Where(type => type is not null)
            .Select(type => type!.FullName ?? string.Empty)
            .Where(name =>
                name.StartsWith(CoverageInstrumentationNamespacePrefix, StringComparison.Ordinal)
            )
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        return (
            matched,
            $"loaded-type scan for names starting '{CoverageInstrumentationNamespacePrefix}' over {loaded.Length} type(s) -> {matched.Length} match(es)"
                + (note is null ? string.Empty : " | " + note)
        );
    }

    // Runs the OLD detection and reports it: it is the hypothesis "HasCoverageInstrumentation
    // returned false on Linux" made observable. A throw here is diagnostic gold — GetType resolving
    // an injected type's dependencies is exactly what the metadata scan removed.
    private static string DescribeLegacyProbe(System.Reflection.Assembly assembly)
    {
        var found = new List<string>();
        var missed = new List<string>();
        string? failure = null;
#pragma warning disable IL2026
        // Test-only reflection: this assembly is never trimmed or published.
        try
        {
            foreach (var typeName in MeasuredCoverageInstrumentationTypeNames)
            {
                if (assembly.GetType(typeName, throwOnError: false) is null)
                {
                    missed.Add(typeName);
                }
                else
                {
                    found.Add(typeName);
                }
            }
        }
        catch (Exception exception)
        {
            failure = exception.GetType().Name + ": " + exception.Message;
        }
#pragma warning restore IL2026

        return "looked for "
            + Render(MeasuredCoverageInstrumentationTypeNames)
            + " | found "
            + Render(found.ToArray())
            + " | missed "
            + Render(missed.ToArray())
            + (failure is null ? string.Empty : " | threw " + failure);
    }

    private static void WriteDiagnostics(
        System.Reflection.Assembly assembly,
        InstrumentationProbe instrumentation,
        string[] references,
        string[] allowedReferences,
        string[] coverageInjectedReferences,
        string[] strippedReferences,
        string[] unexpected,
        string[] missing
    )
    {
        Console.WriteLine("[refs] ---- PicoLog_ReferencesNoSerializer ----");
        Console.WriteLine(
            "[refs] assembly = "
                + assembly.FullName
                + " | location = "
                + (ModulePath(assembly) ?? "unavailable")
                + " | on-disk length = "
                + DescribeModuleLength(assembly)
        );
        Console.WriteLine(
            "[refs] os = "
                + System.Runtime.InteropServices.RuntimeInformation.OSDescription
                + " | architecture = "
                + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
        );
        Console.WriteLine("[refs] instrumentationPresent = " + instrumentation.IsPresent);
        Console.WriteLine("[refs] instrumentation scan: " + instrumentation.Scan);
        foreach (var typeName in instrumentation.MatchedTypeNames)
        {
            Console.WriteLine("[refs] instrumenter type = " + typeName);
        }

        Console.WriteLine("[refs] legacy exact-name probe: " + instrumentation.LegacyProbe);
        WriteList("received", references);
        WriteList("allowed", allowedReferences);
        WriteList("stripped", strippedReferences);
        WriteList("unexpected", unexpected);
        WriteList("missing", missing);
        Console.WriteLine(
            "[refs] stripped-when-instrumented: " + Render(coverageInjectedReferences)
        );
    }

    private static void WriteList(string label, string[] names)
    {
        // "joined length" is the number the red CI log made load-bearing ("differs at index 236"):
        // printed for every list so that arithmetic never has to be guessed again.
        Console.WriteLine(
            $"[refs] {label}: count = {names.Length}, joined length = {string.Join(",", names).Length}"
        );
        for (var index = 0; index < names.Length; index++)
        {
            Console.WriteLine($"[refs] {label}[{index:D2}] = {names[index]}");
        }
    }

    private static string DescribeModuleLength(System.Reflection.Assembly assembly)
    {
        var path = ModulePath(assembly);
        return path is null ? "unavailable" : new FileInfo(path).Length + " bytes";
    }

    // " | "-joined, and never abbreviated: the whole point is a list a human can read in the log.
    private static string Render(string[] names) => string.Join(" | ", names);

    private sealed record InstrumentationProbe(
        bool IsPresent,
        string[] MatchedTypeNames,
        string Scan,
        string LegacyProbe
    );

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
