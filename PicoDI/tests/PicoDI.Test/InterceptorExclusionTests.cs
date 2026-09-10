namespace PicoDI.Test;

/// <summary>
/// Tests for WithoutInterceptor&lt;T&gt;() / WithoutInterceptors() exclusion
/// markers in interception chains.
///
/// Bug: the markers were declared in PicoAop.DI but consumed by neither
/// generator — writing them silently had no effect on the emitted
/// interception override.
/// </summary>
public sealed class InterceptorExclusionTests
{
    private static readonly string? s_picoAopAbsPath;

    static InterceptorExclusionTests()
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

    private static string? RunGeneratorAndGetIntercepted(string sourceCode)
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
        foreach (var r in runResult.Results)
        {
            if (r.GeneratedSources.IsDefault)
                continue;
            foreach (var s in r.GeneratedSources)
            {
                if (s.HintName.Contains("InterceptedRegistrations"))
                    return s.SourceText.ToString();
            }
        }

        return null;
    }

    private const string ChainSkeleton = """
        using PicoDI;
        using PicoDI.Abs;

        interface ISvc { void Do(); }
        class Impl : ISvc { public void Do() {} }
        class IntA {}
        class IntB {}

        static class Ext
        {
            internal static ISvcContainer InterceptBy<T>(this ISvcContainer c) where T : class => c;
            internal static ISvcContainer WithoutInterceptor<T>(this ISvcContainer c) where T : class => c;
            internal static ISvcContainer WithoutInterceptors(this ISvcContainer c) => c;
        }

        static class Setup
        {
            static void X(SvcContainer c)
            {
                c.RegisterScoped<ISvc, Impl>()__CHAIN__;
            }
        }
        """;

    [Test]
    public async Task WithoutInterceptor_RemovesThatInterceptorFromWrapper()
    {
        if (s_picoAopAbsPath is null)
        {
            TestContext.Current!.OutputWriter.WriteLine("Skipped");
            return;
        }

        var source = ChainSkeleton.Replace(
            "__CHAIN__",
            ".InterceptBy<IntA>().InterceptBy<IntB>().WithoutInterceptor<IntA>()"
        );

        var intercepted = RunGeneratorAndGetIntercepted(source);

        await Assert.That(intercepted).IsNotNull();
        await Assert.That(intercepted!.Contains("GetService<global::IntB>")).IsTrue();
        await Assert.That(intercepted.Contains("GetService<global::IntA>")).IsFalse();
    }

    [Test]
    public async Task WithoutInterceptors_SuppressesTheOverrideEntirely()
    {
        if (s_picoAopAbsPath is null)
        {
            TestContext.Current!.OutputWriter.WriteLine("Skipped");
            return;
        }

        var source = ChainSkeleton.Replace(
            "__CHAIN__",
            ".InterceptBy<IntA>().WithoutInterceptors()"
        );

        var intercepted = RunGeneratorAndGetIntercepted(source);

        // No interceptors remain — the original (non-intercepted) registration
        // stands, so no override file may be emitted.
        await Assert.That(intercepted).IsNull();
    }

    [Test]
    public async Task AllInterceptorsRemoved_SuppressesTheOverrideEntirely()
    {
        if (s_picoAopAbsPath is null)
        {
            TestContext.Current!.OutputWriter.WriteLine("Skipped");
            return;
        }

        var source = ChainSkeleton.Replace(
            "__CHAIN__",
            ".InterceptBy<IntA>().WithoutInterceptor<IntA>()"
        );

        var intercepted = RunGeneratorAndGetIntercepted(source);

        await Assert.That(intercepted).IsNull();
    }
}
