namespace PicoDI.Test;

public sealed class InterceptorLifetimeTests
{
    private static readonly string? s_picoAopAbsPath;

    static InterceptorLifetimeTests()
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
                "PicoAop/src/PicoAop.DI/bin/Debug/net10.0/PicoAop.Abs.dll",
                "PicoAop/src/PicoAop.DI/bin/Release/net10.0/PicoAop.Abs.dll",
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
        if (s_picoAopAbsPath is null)
            return null;

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

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);

        // Use existing test pattern to access generated sources
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
        if (interceptedText is null)
            return null;

        var m = Regex.Match(interceptedText, @"SvcLifetime\.(\w+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    [Test]
    public async Task InterceptedSingleton_UsesSingletonLifetime()
    {
        if (s_picoAopAbsPath is null)
        {
            TestContext.Current!.OutputWriter.WriteLine("Skipped");
            return;
        }

        // Use SvcContainer + using PicoDI — same pattern as existing passing tests
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
                    c.RegisterSingleton<ISvc, Impl>().InterceptBy<MyInterceptor>();
                }
            }
            """;

        var lifetime = RunGeneratorAndGetIntercepted(source);
        await Assert.That(lifetime).IsNotNull();
        await Assert.That(lifetime).IsEqualTo("Singleton");
    }

    [Test]
    public async Task InterceptedTransient_UsesTransientLifetime()
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
                    c.RegisterTransient<ISvc, Impl>().InterceptBy<MyInterceptor>();
                }
            }
            """;

        var lifetime = RunGeneratorAndGetIntercepted(source);
        await Assert.That(lifetime).IsNotNull();
        await Assert.That(lifetime).IsEqualTo("Transient");
    }

    [Test]
    public async Task InterceptedScoped_UsesScopedLifetime()
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

        var lifetime = RunGeneratorAndGetIntercepted(source);
        await Assert.That(lifetime).IsNotNull();
        await Assert.That(lifetime).IsEqualTo("Scoped");
    }

    [Test]
    public async Task InterceptedNotHardcodedScoped_WhenSingleton()
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
                    c.RegisterSingleton<ISvc, Impl>().InterceptBy<MyInterceptor>();
                }
            }
            """;

        var lifetime = RunGeneratorAndGetIntercepted(source);
        await Assert.That(lifetime).IsNotNull();
        await Assert.That(lifetime).IsNotEqualTo("Scoped");
    }

    [Test]
    public async Task InterceptedLifetimeArgument_UsesArgumentLifetime()
    {
        if (s_picoAopAbsPath is null)
        {
            TestContext.Current!.OutputWriter.WriteLine("Skipped");
            return;
        }

        // Register<TService, TImplementation>(SvcLifetime) passes the lifetime
        // as an ARGUMENT — the interception override must honor the argument
        // instead of defaulting to Singleton via method-name inference.
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
                    c.Register<ISvc, Impl>(SvcLifetime.Transient).InterceptBy<MyInterceptor>();
                }
            }
            """;

        var lifetime = RunGeneratorAndGetIntercepted(source);
        await Assert.That(lifetime).IsNotNull();
        await Assert.That(lifetime).IsEqualTo("Transient");
    }

    [Test]
    public async Task InterceptedLifetimeArgumentScoped_UsesArgumentLifetime()
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
                    c.Register<ISvc, Impl>(SvcLifetime.Scoped).InterceptBy<MyInterceptor>();
                }
            }
            """;

        var lifetime = RunGeneratorAndGetIntercepted(source);
        await Assert.That(lifetime).IsNotNull();
        await Assert.That(lifetime).IsEqualTo("Scoped");
    }
}
