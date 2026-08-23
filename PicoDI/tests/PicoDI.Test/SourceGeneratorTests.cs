namespace PicoDI.Test;

public sealed class PicoDISourceGeneratorTests
{
    [Test]
    public async Task ServiceRegistrationGenerator_ProducesValidOutput()
    {
        var inputSource = """
            using PicoDI;
            using PicoDI.Abs;

            public interface IGoldenService
            {
                string Name { get; }
            }

            public class GoldenService : IGoldenService
            {
                public string Name => "Golden";
            }

            public static class GoldenSetup
            {
                public static void Configure(SvcContainer container)
                {
                    container.RegisterSingleton<IGoldenService, GoldenService>();
                }
            }
            """;

        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var inputTree = CSharpSyntaxTree.ParseText(inputSource, parseOptions);
        var references = GetMetadataReferences();

        var compilation = CSharpCompilation.Create(
            assemblyName: "GeneratorInput",
            syntaxTrees: [inputTree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        var generator = new ServiceRegistrationGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [generator.AsSourceGenerator()],
            parseOptions: parseOptions
        );

        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var outputCompilation,
            out var diagnostics
        );

        using var ms = new MemoryStream();
        var result = outputCompilation.Emit(ms);
        await Assert.That(result.Success).IsTrue();

        var runResult = driver.GetRunResult();
        var generatedSources = runResult
            .Results.SelectMany(static r => r.GeneratedSources)
            .ToImmutableArray();

        var registrationSource = generatedSources.Single(s =>
            s.HintName.Contains("ServiceRegistrations", StringComparison.Ordinal)
        );

        var generatedTree = CSharpSyntaxTree.ParseText(registrationSource.SourceText, parseOptions);

        var root = await generatedTree.GetRootAsync();
        var generatedClass = root.DescendantNodes().OfType<ClassDeclarationSyntax>().First();

        var configureMethod = generatedClass
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == "ConfigureGeneratedServices");
        await Assert
            .That(
                configureMethod
                    .ParameterList.Parameters[0]
                    .Modifiers.Any(m => m.IsKind(SyntaxKind.ThisKeyword))
            )
            .IsTrue();

        // PrebuiltCache was removed — it was generated but never consumed by the
        // runtime, wasting a FrozenDictionary allocation at assembly load time.
        var hasPrebuiltCache = generatedClass
            .DescendantNodes()
            .OfType<FieldDeclarationSyntax>()
            .Any(f => f.Declaration.Variables.Any(v => v.Identifier.Text == "PrebuiltCache"));
        await Assert.That(hasPrebuiltCache).IsFalse();

        // GoldenService has no factory dependencies — no Resolve methods are
        // referenced, so the Resolve class is not emitted at all (generated-code
        // pruning: only dependencies referenced by factory chains get resolvers).
        var resolveClass = generatedClass
            .DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.Text == "Resolve");
        await Assert.That(resolveClass).IsNull();
    }

    [Test]
    public async Task OpenGenericOnly_DoesNotGenerateServiceRegistrations()
    {
        // PicoLog.DI scenario: only RegisterSingleton(typeof(ILogger<>), typeof(Logger<>))
        // No concrete (closed-generic) registrations. The Registration file should NOT be emitted.
        var inputSource = """
            using PicoDI;
            using PicoDI.Abs;

            public interface ILogger<T> { void Log(string msg); }
            public sealed class Logger<T> : ILogger<T> { public void Log(string msg) { } }

            public static class Setup
            {
                public static void Configure(SvcContainer container)
                {
                    container.RegisterSingleton(typeof(ILogger<>), typeof(Logger<>));
                }
            }
            """;

        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var inputTree = CSharpSyntaxTree.ParseText(inputSource, parseOptions);
        var references = GetMetadataReferences();

        var compilation = CSharpCompilation.Create(
            assemblyName: "OpenGenericOnly",
            syntaxTrees: [inputTree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        var generator = new ServiceRegistrationGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [generator.AsSourceGenerator()],
            parseOptions: parseOptions
        );

        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var outputCompilation,
            out var diags
        );

        var runResult = driver.GetRunResult();
        var generatedSources = runResult
            .Results.SelectMany(static r => r.GeneratedSources)
            .ToImmutableArray();

        // Collect hint names for diagnostics
        var hintNames = string.Join(", ", generatedSources.Select(s => s.HintName));
        await Assert.That(hintNames).IsNotEmpty(); // ensure we have at least the metadata file

        var hasRegistration = hintNames.Contains("ServiceRegistrations", StringComparison.Ordinal);
        await Assert.That(hasRegistration).IsFalse();

        var hasMetadata = hintNames.Contains("OpenGenericMetadata", StringComparison.Ordinal);
        await Assert.That(hasMetadata).IsTrue();
    }

    [UnconditionalSuppressMessage(
        "AOT",
        "IL3000",
        Justification = "Roslyn-based generator tests construct metadata references from file-backed assemblies during test execution."
    )]
    private static MetadataReference[] GetMetadataReferences()
    {
        var trustedPlatformAssemblies = (
            (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
        )!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        var explicitAssemblies = new[]
        {
            typeof(SvcContainer).Assembly.Location,
            typeof(ISvcContainer).Assembly.Location,
        };

        return trustedPlatformAssemblies
            .Concat(explicitAssemblies)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(static path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }

    [Test]
    public async Task ResolveMethods_OnlyGeneratedForReferencedDependencies()
    {
        // Generated-code redundancy: the Resolve class must only contain
        // methods for service types actually referenced by other factories'
        // dependency-injection chains — not one method per registration.
        var inputSource = """
            using PicoDI;
            using PicoDI.Abs;

            public interface IDep { }
            public sealed class Dep : IDep { }

            public interface IConsumer { }
            public sealed class Consumer : IConsumer
            {
                public Consumer(IDep dep) { }
            }

            public interface IStandalone { }
            public sealed class Standalone : IStandalone { }

            public static class Setup
            {
                public static void Configure(SvcContainer container)
                {
                    container.RegisterSingleton<IDep, Dep>();
                    container.RegisterSingleton<IConsumer, Consumer>();
                    container.RegisterSingleton<IStandalone, Standalone>();
                }
            }
            """;

        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var inputTree = CSharpSyntaxTree.ParseText(inputSource, parseOptions);

        var compilation = CSharpCompilation.Create(
            assemblyName: "GeneratorInput",
            syntaxTrees: [inputTree],
            references: GetMetadataReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        var generator = new ServiceRegistrationGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [generator.AsSourceGenerator()],
            parseOptions: parseOptions
        );

        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var outputCompilation,
            out var diagnostics
        );

        using var ms = new MemoryStream();
        var result = outputCompilation.Emit(ms);
        await Assert.That(result.Success).IsTrue();

        var runResult = driver.GetRunResult();
        var registrationSource = runResult
            .Results.SelectMany(static r => r.GeneratedSources)
            .Single(s => s.HintName.Contains("ServiceRegistrations", StringComparison.Ordinal))
            .SourceText.ToString();

        var generatedTree = CSharpSyntaxTree.ParseText(registrationSource, parseOptions);
        var root = await generatedTree.GetRootAsync();
        var generatedClass = root.DescendantNodes().OfType<ClassDeclarationSyntax>().First();

        var resolveClass = generatedClass
            .DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.Text == "Resolve");

        // Resolve must be an internal implementation detail, not public API.
        if (resolveClass is not null)
        {
            await Assert
                .That(resolveClass.Modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword)))
                .IsTrue();
        }

        // The consumer's dependency (IDep) is referenced by Consumer's factory
        // chain — its Resolve method must exist.
        await Assert
            .That(
                resolveClass is not null
                    && resolveClass
                        .DescendantNodes()
                        .OfType<MethodDeclarationSyntax>()
                        .Any(m => m.Identifier.Text == "IDep")
            )
            .IsTrue();

        // Standalone is never referenced by any factory chain — no Resolve method.
        await Assert
            .That(
                resolveClass is null
                    || !resolveClass
                        .DescendantNodes()
                        .OfType<MethodDeclarationSyntax>()
                        .Any(m => m.Identifier.Text == "IStandalone")
            )
            .IsTrue();
    }
}
