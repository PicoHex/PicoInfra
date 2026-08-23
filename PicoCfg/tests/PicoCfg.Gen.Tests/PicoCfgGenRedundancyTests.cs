using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PicoCfg;
using PicoCfg.Abs;
using PicoCfg.Gen;

namespace PicoCfg.Gen.Tests;

#pragma warning disable CS0618

// Generated-code redundancy: the generator must only emit the binding methods
// the call sites actually need (Bind_ / TryBind_ / BindInto_ / TryBindInto_),
// instead of unconditionally generating all four per target type.
public sealed class PicoCfgGenRedundancyTests
{
    [Test]
    public async Task BindOnly_DoesNotGenerateTryBindMethods()
    {
        // Only CfgBind.Bind<T> is used: TryBind_ / TryBindInto_ must not be
        // emitted; Bind_ + BindInto_ (called by the parameterless Bind_ path)
        // must be emitted and the tryBind registration must be null.
        var result = await CompileAndGetErrorsAsync(
            """
            using PicoCfg;
            using PicoCfg.Abs;

            public sealed class AppSettings
            {
                public string? Name { get; set; }
                public int Port { get; set; }
            }

            public static class Entry
            {
                public static AppSettings Run(ICfg cfg) => CfgBind.Bind<AppSettings>(cfg);
            }
            """
        );

        await Assert.That(result.Errors.Length).IsEqualTo(0);
        var source = result.GeneratedSource;
        await Assert.That(source.Contains("Bind_0(")).IsTrue();
        await Assert.That(source.Contains("BindInto_0(")).IsTrue();
        await Assert.That(source.Contains("TryBind_0(")).IsFalse();
        await Assert.That(source.Contains("TryBindInto_0(")).IsFalse();
        await Assert.That(source.Contains("tryBind: null")).IsTrue();
    }

    [Test]
    public async Task TryBindOnly_GeneratesTryBindAndTryBindInto()
    {
        var result = await CompileAndGetErrorsAsync(
            """
            using PicoCfg;
            using PicoCfg.Abs;

            public sealed class AppSettings
            {
                public string? Name { get; set; }
            }

            public static class Entry
            {
                public static bool Run(ICfg cfg, out AppSettings? s) =>
                    CfgBind.TryBind<AppSettings>(cfg, out s);
            }
            """
        );

        if (result.Errors.Length > 0)
            Console.WriteLine(string.Join("\n", result.Errors));
        await Assert.That(result.Errors.Length).IsEqualTo(0);
        var source = result.GeneratedSource;
        await Assert.That(source.Contains("bool TryBind_0(")).IsTrue();
        await Assert.That(source.Contains("bool TryBindInto_0(")).IsTrue();
        await Assert.That(source.Contains("AppSettings Bind_0(")).IsFalse();
        await Assert.That(source.Contains("void BindInto_0(")).IsFalse();
        await Assert.That(source.Contains("bind: null")).IsTrue();
    }

    [Test]
    public async Task BindIntoOnly_GeneratesOnlyBindInto()
    {
        var result = await CompileAndGetErrorsAsync(
            """
            using PicoCfg;
            using PicoCfg.Abs;

            public sealed class AppSettings
            {
                public string? Name { get; set; }
            }

            public static class Entry
            {
                public static void Run(ICfg cfg, AppSettings s) => CfgBind.BindInto(cfg, s);
            }
            """
        );

        await Assert.That(result.Errors.Length).IsEqualTo(0);
        var source = result.GeneratedSource;
        await Assert.That(source.Contains("BindInto_0(")).IsTrue();
        await Assert.That(source.Contains("Bind_0(")).IsFalse();
        await Assert.That(source.Contains("TryBind_0(")).IsFalse();
        await Assert.That(source.Contains("TryBindInto_0(")).IsFalse();
        await Assert.That(source.Contains("bind: null")).IsTrue();
        await Assert.That(source.Contains("tryBind: null")).IsTrue();
    }

    [Test]
    public async Task NestedReferencedType_KeepsBindMethod()
    {
        // SubConfig is only referenced as a dictionary value — its Bind_ method
        // is required (the parent's element binding calls it) but TryBind_ and
        // TryBindInto_ must not be emitted for it.
        var result = await CompileAndGetErrorsAsync(
            """
            using System.Collections.Generic;
            using PicoCfg;
            using PicoCfg.Abs;

            public sealed class SubConfig
            {
                public string? Name { get; set; }
            }

            public sealed class ParentConfig
            {
                public Dictionary<string, SubConfig> Items { get; set; } = new();
            }

            public static class Entry
            {
                public static ParentConfig Run(ICfg cfg) => CfgBind.Bind<ParentConfig>(cfg);
            }
            """
        );

        await Assert.That(result.Errors.Length).IsEqualTo(0);
        var source = result.GeneratedSource;
        // ParentConfig's own bind methods.
        await Assert.That(source.Contains("Bind_0(")).IsTrue();
        // SubConfig's Bind_ must exist (nested reference), but no TryBind*.
        await Assert.That(source.Contains("Bind_1(")).IsTrue();
        await Assert.That(source.Contains("TryBind_1(")).IsFalse();
        await Assert.That(source.Contains("TryBindInto_1(")).IsFalse();
    }

    [Test]
    public async Task BindOnlyPositionalRecord_DoesNotGenerateBindInto()
    {
        // A positional record's Bind_ constructs inline via its primary
        // constructor — it never calls BindInto_, so BindInto_ must not be
        // emitted unless the caller uses CfgBind.BindInto.
        var result = await CompileAndGetErrorsAsync(
            """
            using PicoCfg;
            using PicoCfg.Abs;

            public sealed record ModelCost(decimal Input, decimal Output);

            public static class Entry
            {
                public static ModelCost Run(ICfg cfg) => CfgBind.Bind<ModelCost>(cfg);
            }
            """
        );

        await Assert.That(result.Errors.Length).IsEqualTo(0);
        var source = result.GeneratedSource;
        await Assert.That(source.Contains("Bind_0(")).IsTrue();
        await Assert.That(source.Contains("BindInto_0(")).IsFalse();
        await Assert.That(source.Contains("TryBind_0(")).IsFalse();
        await Assert.That(source.Contains("TryBindInto_0(")).IsFalse();
    }

    private static async Task<CompilationResult> CompileAndGetErrorsAsync(string source)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions);

        var compilation = CSharpCompilation.Create(
            assemblyName: "CompilationTest",
            syntaxTrees: [syntaxTree],
            references: RoslynTestHelpers.GetMetadataReferences(),
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable,
                generalDiagnosticOption: ReportDiagnostic.Error
            )
        );

        var generator = new PicoCfgBindGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [generator.AsSourceGenerator()],
            parseOptions: parseOptions
        );

        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var outputCompilation,
            out var driverDiagnostics
        );

        using var ms = new MemoryStream();
        var emitResult = outputCompilation.Emit(ms);

        var allDiagnostics = ImmutableArray<Diagnostic>
            .Empty.AddRange(outputCompilation.GetDiagnostics())
            .AddRange(driverDiagnostics)
            .AddRange(emitResult.Diagnostics);

        var errors = allDiagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToArray();

        var runResult = driver.GetRunResult();
        var generatedSource = runResult
            .Results.SelectMany(r => r.GeneratedSources)
            .FirstOrDefault(s => s.HintName == "PicoCfgBindRegistrations.g.cs");

        return new CompilationResult(
            errors,
            generatedSource.HintName is not null ? generatedSource.SourceText.ToString() : ""
        );
    }

    private sealed record CompilationResult(string[] Errors, string GeneratedSource);
}

#pragma warning restore CS0618
