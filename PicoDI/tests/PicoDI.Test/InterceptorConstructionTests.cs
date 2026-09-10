namespace PicoDI.Test;

/// <summary>
/// Tests for how the PicoDI.Gen interception override constructs the inner
/// (wrapped) implementation.
///
/// Bug: the generated wrapper factory resolved the implementation via
/// <c>scope.GetService&lt;Impl&gt;()</c>, which only works when the
/// implementation is ALSO registered under its own type — a hidden
/// requirement. For factory-based registrations the emitted code resolved
/// the SERVICE type from inside its own factory, producing infinite
/// recursion at runtime.
/// </summary>
public sealed class InterceptorConstructionTests
{
    private static readonly string? s_picoAopAbsPath;

    static InterceptorConstructionTests()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PicoInfra.slnx")))
            dir = dir.Parent;
        if (dir is null)
            return;

        foreach (
            var rel in new[]
            {
                "PicoAop/src/PicoAop.Abs/bin/Debug/net10.0/PicoAop.Abs.dll",
                "PicoAop/src/PicoAop.Abs/bin/Release/net10.0/PicoAop.Abs.dll",
            }
        )
        {
            var dll = Path.Combine(dir.FullName, rel);
            if (File.Exists(dll))
            {
                s_picoAopAbsPath = dll;
                break;
            }
        }
    }

    [UnconditionalSuppressMessage("AOT", "IL3000", Justification = "Roslyn-based generator tests")]
    private static MetadataReference[] GetMetadataReferences()
    {
        var trusted =
            ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
            ?? throw new InvalidOperationException("TRUSTED_PLATFORM_ASSEMBLIES not set");
        var refs = trusted
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => MetadataReference.CreateFromFile(p))
            .ToList();
        refs.Add(MetadataReference.CreateFromFile(typeof(SvcContainer).Assembly.Location));
        refs.Add(MetadataReference.CreateFromFile(typeof(ISvcContainer).Assembly.Location));
        if (s_picoAopAbsPath is not null)
            refs.Add(MetadataReference.CreateFromFile(s_picoAopAbsPath));
        return [.. refs];
    }

    private static (string? InterceptedText, ImmutableArray<Diagnostic> Diagnostics) RunGenerator(
        string sourceCode
    )
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode, parseOptions);
        var compilation = CSharpCompilation.Create(
            "test",
            [syntaxTree],
            GetMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        var generator = new ServiceRegistrationGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [generator.AsSourceGenerator()],
            parseOptions: parseOptions
        );

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);

        var runResult = driver.GetRunResult();

        string? interceptedText = null;
        foreach (var r in runResult.Results)
        {
            if (r.GeneratedSources.IsDefault)
                continue;
            foreach (var s in r.GeneratedSources)
            {
                if (s.HintName.Contains("InterceptedRegistrations"))
                    interceptedText = s.SourceText.ToString();
            }
        }

        return (interceptedText, runResult.Diagnostics);
    }

    [Test]
    public async Task TypeBasedRegistration_ConstructsImplementationInline()
    {
        if (s_picoAopAbsPath is null)
        {
            TestContext.Current!.OutputWriter.WriteLine("Skipped");
            return;
        }

        var source = """
            using PicoDI;
            using PicoDI.Abs;

            interface ISvc { void Do(); }
            class Impl : ISvc { public void Do() {} }
            class MyInterceptor {}

            static class Ext
            {
                internal static ISvcContainer InterceptBy<T>(this ISvcContainer c) where T : class => c;
            }

            static class Setup
            {
                static void X(SvcContainer c)
                {
                    c.RegisterScoped<ISvc, Impl>().InterceptBy<MyInterceptor>();
                }
            }
            """;

        var (intercepted, _) = RunGenerator(source);

        await Assert.That(intercepted).IsNotNull();
        // The wrapper must construct the implementation directly — it must
        // NOT depend on the implementation being registered under its own
        // type via GetService<Impl>().
        await Assert.That(intercepted!.Contains("new global::Impl()")).IsTrue();
        await Assert.That(intercepted.Contains("GetService<global::Impl>")).IsFalse();
    }

    [Test]
    public async Task FactoryBasedRegistration_ReportsPICO006()
    {
        if (s_picoAopAbsPath is null)
        {
            TestContext.Current!.OutputWriter.WriteLine("Skipped");
            return;
        }

        var source = """
            using PicoDI;
            using PicoDI.Abs;

            interface ISvc { void Do(); }
            class Impl : ISvc { public void Do() {} }
            class MyInterceptor {}

            static class Ext
            {
                internal static ISvcContainer InterceptBy<T>(this ISvcContainer c) where T : class => c;
            }

            static class Setup
            {
                static void X(SvcContainer c)
                {
                    c.RegisterTransient<ISvc>(_ => new Impl()).InterceptBy<MyInterceptor>();
                }
            }
            """;

        var (intercepted, diagnostics) = RunGenerator(source);

        var pico006 = diagnostics.FirstOrDefault(d => d.Id == "PICO006");
        await Assert.That(pico006).IsNotNull();
        await Assert.That(pico006!.Severity).IsEqualTo(DiagnosticSeverity.Error);
        // No interception override may be emitted for the factory-based case.
        await Assert.That(intercepted).IsNull();
    }
}
