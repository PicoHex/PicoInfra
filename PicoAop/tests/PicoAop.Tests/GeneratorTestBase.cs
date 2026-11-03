namespace PicoAop.Tests;

public abstract class GeneratorTestBase
{
    private static int s_assemblyCounter;

    /// <summary>
    /// The full framework reference set (trusted platform assemblies) plus the
    /// PicoAop.Abs assembly. The two-reference set the older tests used cannot
    /// type-check generated code that mentions <c>Task</c>, <c>ValueTask</c>,
    /// <c>List&lt;T&gt;</c> or <c>Type</c>, so the compile assertions below would
    /// report phantom errors.
    /// </summary>
    private static readonly Lazy<ImmutableArray<MetadataReference>> FrameworkReferences = new(() =>
    {
        var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;
        // The TPA list can carry the same assembly twice (a local copy plus the
        // shared-framework one). Two metadata images of System.Private.CoreLib
        // give every core type two identities, so keep one image per file name.
        var references = tpa.Split(
                System.IO.Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries
            )
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .GroupBy(path => System.IO.Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Select(group => (MetadataReference)MetadataReference.CreateFromFile(group.First()))
            .ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(IInterceptor).Assembly.Location));
        return [.. references];
    });

    /// <summary>
    /// Outcome of a generator run: the driver result, the generator's own
    /// diagnostics, and — crucially — the compiler diagnostics of the updated
    /// compilation. <see cref="Errors"/> is what proves the emitted code
    /// compiles; a generator that silently emits garbage reports no diagnostics
    /// of its own.
    /// </summary>
    protected sealed record CompilationResult(
        GeneratorDriverRunResult RunResult,
        ImmutableArray<Diagnostic> GeneratorDiagnostics,
        ImmutableArray<Diagnostic> Errors,
        ImmutableArray<Diagnostic> Warnings,
        Compilation UpdatedCompilation,
        string GeneratedOutput
    )
    {
        public string ErrorText =>
            string.Join(
                Environment.NewLine,
                Errors.Select(e =>
                    $"{e.Id}: {e.GetMessage()} [{e.Location.SourceTree?.FilePath ?? "<none>"}]"
                )
            );

        /// <summary>
        /// The compiler warnings of the updated compilation. A generated proxy
        /// that hides a base member (CS0108) reports here, and that warning
        /// lands in a file the consumer cannot edit — so a test can assert the
        /// emitted code is not merely error-free but warning-free.
        /// </summary>
        public string WarningText =>
            string.Join(
                Environment.NewLine,
                Warnings.Select(e =>
                    $"{e.Id}: {e.GetMessage()} [{e.Location.SourceTree?.FilePath ?? "<none>"}]"
                )
            );

        public ImmutableArray<string> DiagnosticIds => [.. GeneratorDiagnostics.Select(d => d.Id)];

        /// <summary>Messages of the generator's own diagnostics, one per line.</summary>
        public string DiagnosticText =>
            string.Join(Environment.NewLine, GeneratorDiagnostics.Select(d => d.GetMessage()));
    }

    protected static async Task RunGenerator(
        string source,
        Func<GeneratorDriverRunResult, Task>? assert = null
    )
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "test",
            [syntaxTree],
            references:
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(IInterceptor).Assembly.Location),
            ],
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        var generator = new InterceptorGenerator();
        var csharpDriver = CSharpGeneratorDriver.Create(generator);
        var driver = csharpDriver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var updatedComp,
            out var compileDiags
        );
        foreach (var d in compileDiags.Where(d => d.Severity == DiagnosticSeverity.Error))
            Console.Error.WriteLine($"COMPILE: {d.Id} {d.GetMessage()}");
        Console.Error.WriteLine($"Trees: {driver.GetRunResult().GeneratedTrees.Length}");
        Console.Error.WriteLine($"Results: {driver.GetRunResult().Results.Length}");
        if (assert is not null)
            await assert(driver.GetRunResult());
        else
        {
            var errors = compileDiags.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            foreach (var e in errors)
                Console.WriteLine($"DIAG: {e.Id} {e.GetMessage()}");
        }
    }

    /// <summary>
    /// Runs the generator over <paramref name="source"/> and returns the driver
    /// result plus every compiler error of the resulting compilation — i.e. the
    /// errors the consumer's build hits, including the ones that land in the
    /// generated <c>PicoAop.Invocations.g.cs</c>.
    /// </summary>
    protected static CompilationResult CompileGenerated(string source)
    {
        var assemblyName = $"PicoAop.GeneratedProbe.{Interlocked.Increment(ref s_assemblyCounter)}";
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [syntaxTree],
            FrameworkReferences.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        var driver = CSharpGeneratorDriver.Create(new InterceptorGenerator());
        // RunGeneratorsAndUpdateCompilation RETURNS the updated driver; reading
        // GetRunResult() off the original one returns zero trees and hides
        // every emitted file.
        var updatedDriver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var updated,
            out var genDiags
        );
        var runResult = updatedDriver.GetRunResult();
        var all = updated.GetDiagnostics().ToImmutableArray();
        var errors = all.Where(d => d.Severity == DiagnosticSeverity.Error).ToImmutableArray();
        var warnings = all.Where(d => d.Severity == DiagnosticSeverity.Warning).ToImmutableArray();
        var output = string.Join("\n", runResult.GeneratedTrees.Select(t => t.ToString()));
        return new CompilationResult(runResult, genDiags, errors, warnings, updated, output);
    }

    /// <summary>
    /// Compiles the source plus the generated trees into an in-memory assembly
    /// so a test can instantiate the generated proxy and observe whether the
    /// call actually went through the interceptor.
    /// </summary>
    protected static Assembly CompileAndLoad(string source, out CompilationResult result)
    {
        result = CompileGenerated(source);
        if (!result.Errors.IsEmpty)
            throw new InvalidOperationException(
                "generated code does not compile:" + Environment.NewLine + result.ErrorText
            );
        using var stream = new System.IO.MemoryStream();
        var emit = result.UpdatedCompilation.Emit(stream);
        if (!emit.Success)
            throw new InvalidOperationException(
                "emit failed:"
                    + Environment.NewLine
                    + string.Join(
                        Environment.NewLine,
                        emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
                    )
            );
        return Assembly.Load(stream.ToArray());
    }

    protected static string GetGeneratedOutput(GeneratorDriverRunResult result)
    {
        return string.Join("\n", result.GeneratedTrees.Select(t => t.ToString()));
    }

    protected static string RunGeneratorAndGetOutput(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "test",
            new[] { syntaxTree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(IInterceptor).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        var driver = CSharpGeneratorDriver.Create(new InterceptorGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);
        var result = driver.GetRunResult();
        return string.Join("\n", result.GeneratedTrees.Select(t => t.ToString()));
    }

    protected static List<Diagnostic> RunAndGetDiagnostics(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "test",
            new[] { syntaxTree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(IInterceptor).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        var driver = CSharpGeneratorDriver.Create(new InterceptorGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);
        return [.. diagnostics];
    }
}
