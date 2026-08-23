namespace PicoDI.Test;

/// <summary>
/// Tests for open generic registrations with all lifetimes.
/// Open generics: IRepository&lt;&gt; -&gt; Repository&lt;&gt;
/// </summary>
public class OpenGenericTests
{
    public static void RegisterServicesForConstructorSelection(ISvcContainer container)
    {
        container.RegisterTransient<PreferredCtorDependency>();
        container.RegisterTransient<IAlternativeSimpleService, PreferredCtorService>();
    }

    [RequiresAssemblyFiles("Calls System.Reflection.Assembly.Location")]
    private static PortableExecutableReference CreateAotSafeReference(string assemblyName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
        if (File.Exists(path))
            return MetadataReference.CreateFromFile(path);

        // Fallback for non-AOT: use Assembly.Location.
        var asm = System.Reflection.Assembly.Load(assemblyName);
        return MetadataReference.CreateFromFile(asm.Location);
    }

    private static CSharpCompilation CreateTestCompilation(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);

        PortableExecutableReference[] trustedPlatformAssemblies =
            ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
                ?.Split(Path.PathSeparator)
                .Select(path => MetadataReference.CreateFromFile(path))
                .ToArray()
            ?? [];

        var references = new List<PortableExecutableReference>(trustedPlatformAssemblies);
        references.Add(CreateAotSafeReference("PicoDI"));
        references.Add(CreateAotSafeReference("PicoDI.Abs"));

        return CSharpCompilation.Create(
            assemblyName: "SvcConstructorAnalyzerTests",
            syntaxTrees: [syntaxTree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
    }

    private static async Task<ImmutableArray<Diagnostic>> GetAnalyzerDiagnosticsAsync(string source)
    {
        var compilation = CreateTestCompilation(source);
        var analyzer = new ServiceRegistrationAnalyzer();
        return await compilation.WithAnalyzers([analyzer]).GetAnalyzerDiagnosticsAsync();
    }

    private static Task<ImmutableArray<Diagnostic>> GetGeneratorDiagnosticsAsync(string source)
    {
        var compilation = CreateTestCompilation(source);
        var generator = new ServiceRegistrationGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create([generator.AsSourceGenerator()]);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);

        var runResultDiagnostics = driver
            .GetRunResult()
            .Results.SelectMany(result => result.Diagnostics);

        return Task.FromResult(
            diagnostics
                .Concat(runResultDiagnostics)
                .DistinctBy(d =>
                    $"{d.Id}|{d.Location.SourceSpan.Start}|{d.Location.SourceSpan.Length}|{d.GetMessage()}"
                )
                .ToImmutableArray()
        );
    }

    private static GeneratorDriverRunResult RunGenerator(string source)
    {
        var compilation = CreateTestCompilation(source);
        var generator = new ServiceRegistrationGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create([generator.AsSourceGenerator()]);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);
        return driver.GetRunResult();
    }

    private static string GetGeneratedSourceText(
        GeneratorDriverRunResult runResult,
        string hintName
    )
    {
        return runResult
            .Results.SelectMany(result => result.GeneratedSources)
            .Where(source => source.HintName == hintName)
            .Select(source => source.SourceText.ToString())
            .Single();
    }

    private static string GetGeneratedResolverMethodText(
        GeneratorDriverRunResult runResult,
        string hintName,
        string serviceTypeFullName
    )
    {
        var generatedSource = GetGeneratedSourceText(runResult, hintName);
        var methodName = GetResolverMethodName(serviceTypeFullName);
        var signature =
            $"public static {serviceTypeFullName} {methodName}(global::PicoDI.Abs.ISvcScope scope)";
        var methodStart = generatedSource.IndexOf(signature, StringComparison.Ordinal);

        if (methodStart < 0)
            throw new InvalidOperationException(
                $"Resolver method '{methodName}' was not generated."
            );

        var methodEnd = generatedSource.IndexOf("        }", methodStart, StringComparison.Ordinal);
        if (methodEnd < 0)
            throw new InvalidOperationException(
                $"Resolver method '{methodName}' was not terminated."
            );

        return generatedSource.Substring(methodStart, methodEnd - methodStart + "        }".Length);
    }

    private static string GetResolverMethodName(string serviceTypeFullName)
    {
        var name = serviceTypeFullName.Replace("global::", "");
        name = name.Replace("<", "_").Replace(">", "").Replace(",", "_").Replace(" ", "");
        return name.Replace(".", "_");
    }

    private static async Task AssertAnalyzerAndGeneratorReportAsync(
        string source,
        string diagnosticId
    )
    {
        var analyzerDiagnostics = await GetAnalyzerDiagnosticsAsync(source);
        var generatorDiagnostics = await GetGeneratorDiagnosticsAsync(source);

        await Assert.That(analyzerDiagnostics.Any(d => d.Id == diagnosticId)).IsTrue();
        await Assert.That(generatorDiagnostics.Any(d => d.Id == diagnosticId)).IsTrue();
    }

    #region Transient Open Generic

    [Test]
    public async Task OpenGeneric_Transient_ReturnsNewInstanceEachTime()
    {
        // Arrange
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterTransient(typeof(IRepository<>), typeof(Repository<>));

        // Register factory for closed generic (simulating source generator output)
        container.RegisterTransient<IRepository<User>>(static _ => new Repository<User>());
        await using var scope = container.CreateScope();

        // Act
        var repo1 = scope.GetService<IRepository<User>>();
        var repo2 = scope.GetService<IRepository<User>>();

        // Assert
        await Assert.That(repo1).IsNotNull();
        await Assert.That(repo2).IsNotNull();
        await Assert.That(repo1.InstanceId).IsNotEqualTo(repo2.InstanceId);
        await Assert.That(repo1.EntityType).IsEqualTo(typeof(User));
    }

    [Test]
    public async Task OpenGeneric_Transient_DifferentTypeArguments_DifferentInstances()
    {
        // Arrange
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterTransient<IRepository<User>>(static _ => new Repository<User>());
        container.RegisterTransient<IRepository<Order>>(static _ => new Repository<Order>());
        await using var scope = container.CreateScope();

        // Act
        var userRepo = scope.GetService<IRepository<User>>();
        var orderRepo = scope.GetService<IRepository<Order>>();

        // Assert
        await Assert.That(userRepo.EntityType).IsEqualTo(typeof(User));
        await Assert.That(orderRepo.EntityType).IsEqualTo(typeof(Order));
        await Assert.That(userRepo.InstanceId).IsNotEqualTo(orderRepo.InstanceId);
    }

    #endregion

    #region Scoped Open Generic

    [Test]
    public async Task OpenGeneric_Scoped_SameInstanceWithinScope()
    {
        // Arrange
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterScoped(typeof(IRepository<>), typeof(Repository<>));
        container.RegisterScoped<IRepository<User>>(static _ => new Repository<User>());
        await using var scope = container.CreateScope();

        // Act
        var repo1 = scope.GetService<IRepository<User>>();
        var repo2 = scope.GetService<IRepository<User>>();

        // Assert
        await Assert.That(repo1.InstanceId).IsEqualTo(repo2.InstanceId);
    }

    [Test]
    public async Task OpenGeneric_Scoped_DifferentScopes_DifferentInstances()
    {
        // Arrange
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterScoped<IRepository<User>>(static _ => new Repository<User>());
        await using var scope1 = container.CreateScope();
        await using var scope2 = container.CreateScope();

        // Act
        var repo1 = scope1.GetService<IRepository<User>>();
        var repo2 = scope2.GetService<IRepository<User>>();

        // Assert
        await Assert.That(repo1.InstanceId).IsNotEqualTo(repo2.InstanceId);
    }

    [Test]
    public async Task OpenGeneric_Scoped_DifferentTypeArgs_IndependentInstances()
    {
        // Arrange
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterScoped<IRepository<User>>(static _ => new Repository<User>());
        container.RegisterScoped<IRepository<Order>>(static _ => new Repository<Order>());
        await using var scope = container.CreateScope();

        // Act
        var userRepo1 = scope.GetService<IRepository<User>>();
        var userRepo2 = scope.GetService<IRepository<User>>();
        var orderRepo1 = scope.GetService<IRepository<Order>>();
        var orderRepo2 = scope.GetService<IRepository<Order>>();

        // Assert - Each type arg has its own scoped instance
        await Assert.That(userRepo1.InstanceId).IsEqualTo(userRepo2.InstanceId);
        await Assert.That(orderRepo1.InstanceId).IsEqualTo(orderRepo2.InstanceId);
        await Assert.That(userRepo1.InstanceId).IsNotEqualTo(orderRepo1.InstanceId);
    }

    #endregion

    #region Singleton Open Generic

    [Test]
    public async Task OpenGeneric_Singleton_SameInstanceAlways()
    {
        // Arrange
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterSingleton(typeof(IRepository<>), typeof(Repository<>));
        container.RegisterSingleton<IRepository<User>>(static _ => new Repository<User>());
        await using var scope1 = container.CreateScope();
        await using var scope2 = container.CreateScope();

        // Act
        var repo1 = scope1.GetService<IRepository<User>>();
        var repo2 = scope2.GetService<IRepository<User>>();

        // Assert
        await Assert.That(repo1.InstanceId).IsEqualTo(repo2.InstanceId);
    }

    [Test]
    public async Task OpenGeneric_Singleton_DifferentTypeArgs_DifferentSingletons()
    {
        // Arrange
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterSingleton<IRepository<User>>(static _ => new Repository<User>());
        container.RegisterSingleton<IRepository<Order>>(static _ => new Repository<Order>());
        container.RegisterSingleton<IRepository<Product>>(static _ => new Repository<Product>());
        await using var scope = container.CreateScope();

        // Act
        var userRepo = scope.GetService<IRepository<User>>();
        var orderRepo = scope.GetService<IRepository<Order>>();
        var productRepo = scope.GetService<IRepository<Product>>();

        // Assert - Each type argument has its own singleton
        await Assert.That(userRepo.EntityType).IsEqualTo(typeof(User));
        await Assert.That(orderRepo.EntityType).IsEqualTo(typeof(Order));
        await Assert.That(productRepo.EntityType).IsEqualTo(typeof(Product));

        var ids = new[] { userRepo.InstanceId, orderRepo.InstanceId, productRepo.InstanceId };
        await Assert.That(ids.Distinct().Count()).IsEqualTo(3);
    }

    #endregion

    #region Open Generic with Dependencies

    [Test]
    public async Task OpenGeneric_WithSingletonDependency_DependencyShared()
    {
        // Arrange
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterSingleton<ILogger<User>>(static _ => new Logger<User>());
        container.RegisterTransient<IRepository<User>>(static s => new RepositoryWithLogger<User>(
            s.GetService<ILogger<User>>()
        ));
        await using var scope = container.CreateScope();

        // Act
        var repo1 = (RepositoryWithLogger<User>)scope.GetService<IRepository<User>>();
        var repo2 = (RepositoryWithLogger<User>)scope.GetService<IRepository<User>>();

        // Assert - Repos are different (transient), logger is same (singleton)
        await Assert.That(repo1.InstanceId).IsNotEqualTo(repo2.InstanceId);
        await Assert.That(repo1.Logger.InstanceId).IsEqualTo(repo2.Logger.InstanceId);
    }

    #endregion

    #region Multiple Open Generic Registrations

    [Test]
    public async Task MultipleOpenGenerics_GetServices_ReturnsAll()
    {
        // Arrange
        await using var container = new SvcContainer(autoConfigureFromGenerator: false);
        container.RegisterTransient<IRepository<User>>(static _ => new Repository<User>());
        container.RegisterTransient<IRepository<User>>(static _ => new CachedRepository<User>());
        await using var scope = container.CreateScope();

        // Act
        var repos = scope.GetServices<IRepository<User>>().ToList();

        // Assert
        await Assert.That(repos.Count).IsEqualTo(2);
    }

    [Test]
    public async Task SourceGenerator_UsesSvcConstructorAttribute_WhenMultiplePublicConstructorsExist()
    {
        if (!SvcContainerAutoConfiguration.HasConfigurator)
            return;

        await using var container = new SvcContainer(autoConfigureFromGenerator: true);
        await using var scope = container.CreateScope();

        var service = scope.GetService<IAlternativeSimpleService>();

        await Assert.That(service).IsTypeOf<PreferredCtorService>();
        await Assert.That(service.ConstructorUsed).IsEqualTo("preferred");
    }

    [Test]
    public async Task Analyzer_ReportsPico005_WhenMultiplePublicConstructorsAreMarked()
    {
        // Arrange
        const string source = """
            using PicoDI;
            using PicoDI.Abs;

            public interface IMarkedCtorService;
            public sealed class MarkedCtorDependency;

            public sealed class MultipleMarkedCtorService : IMarkedCtorService
            {
                [SvcConstructor]
                public MultipleMarkedCtorService()
                {
                }

                [SvcConstructor]
                public MultipleMarkedCtorService(MarkedCtorDependency dependency)
                {
                }
            }

            public static class RegistrationSite
            {
                public static void Register(ISvcContainer container)
                {
                    container.RegisterTransient<IMarkedCtorService, MultipleMarkedCtorService>();
                }
            }
            """;

        // Act
        var diagnostics = await GetAnalyzerDiagnosticsAsync(source);

        // Assert
        await Assert.That(diagnostics.Any(d => d.Id == "PICO005")).IsTrue();
    }

    [Test]
    public async Task AnalyzerAndGenerator_ReportPico005_WhenMultiplePublicConstructorsAreMarked()
    {
        const string source = """
            using PicoDI;
            using PicoDI.Abs;

            public interface IMarkedCtorService;
            public sealed class MarkedCtorDependency;

            public sealed class MultipleMarkedCtorService : IMarkedCtorService
            {
                [SvcConstructor]
                public MultipleMarkedCtorService()
                {
                }

                [SvcConstructor]
                public MultipleMarkedCtorService(MarkedCtorDependency dependency)
                {
                }
            }

            public static class RegistrationSite
            {
                public static void Register(ISvcContainer container)
                {
                    container.RegisterTransient<IMarkedCtorService, MultipleMarkedCtorService>();
                }
            }
            """;

        await AssertAnalyzerAndGeneratorReportAsync(source, "PICO005");
    }

    [Test]
    public async Task Analyzer_ReportsPico004_WhenImplementationHasNoPublicConstructor()
    {
        // Arrange
        const string source = """
            using PicoDI;
            using PicoDI.Abs;

            public interface IHiddenCtorService;

            public sealed class HiddenCtorService : IHiddenCtorService
            {
                private HiddenCtorService()
                {
                }
            }

            public static class RegistrationSite
            {
                public static void Register(ISvcContainer container)
                {
                    container.RegisterTransient<IHiddenCtorService, HiddenCtorService>();
                }
            }
            """;

        // Act
        var diagnostics = await GetAnalyzerDiagnosticsAsync(source);

        // Assert
        await Assert.That(diagnostics.Any(d => d.Id == "PICO004")).IsTrue();
    }

    [Test]
    public async Task AnalyzerAndGenerator_ReportPico004_WhenImplementationHasNoPublicConstructor()
    {
        const string source = """
            using PicoDI;
            using PicoDI.Abs;

            public interface IHiddenCtorService;

            public sealed class HiddenCtorService : IHiddenCtorService
            {
                private HiddenCtorService()
                {
                }
            }

            public static class RegistrationSite
            {
                public static void Register(ISvcContainer container)
                {
                    container.RegisterTransient<IHiddenCtorService, HiddenCtorService>();
                }
            }
            """;

        await AssertAnalyzerAndGeneratorReportAsync(source, "PICO004");
    }

    [Test]
    public async Task Generator_ReportsPico004Once_WhenImplementationHasNoPublicConstructor()
    {
        const string source = """
            using PicoDI;
            using PicoDI.Abs;

            public interface IHiddenCtorService;

            public sealed class HiddenCtorService : IHiddenCtorService
            {
                private HiddenCtorService()
                {
                }
            }

            public static class RegistrationSite
            {
                public static void Register(ISvcContainer container)
                {
                    container.RegisterTransient<IHiddenCtorService, HiddenCtorService>();
                }
            }
            """;

        var diagnostics = await GetGeneratorDiagnosticsAsync(source);
        var missingConstructorDiagnostics = diagnostics.Where(d => d.Id == "PICO004").ToList();

        await Assert.That(missingConstructorDiagnostics.Count).IsEqualTo(1);
    }

    [Test]
    public async Task AnalyzerAndGenerator_ReportPico003_WhenImplementationIsAbstract()
    {
        const string source = """
            using PicoDI;
            using PicoDI.Abs;

            public interface IAbstractService;

            public abstract class AbstractService : IAbstractService
            {
                protected AbstractService()
                {
                }
            }

            public static class RegistrationSite
            {
                public static void Register(ISvcContainer container)
                {
                    container.RegisterTransient<IAbstractService, AbstractService>();
                }
            }
            """;

        await AssertAnalyzerAndGeneratorReportAsync(source, "PICO003");
    }

    [Test]
    public async Task AnalyzerAndGenerator_ReportPico003_WhenExplicitImplementationTypeIsInterface()
    {
        const string source = """
            using PicoDI;
            using PicoDI.Abs;

            public interface IServiceContract;
            public interface IInterfaceImplementation : IServiceContract;

            public static class RegistrationSite
            {
                public static void Register(ISvcContainer container)
                {
                    container.RegisterTransient<IServiceContract>(typeof(IInterfaceImplementation));
                }
            }
            """;

        await AssertAnalyzerAndGeneratorReportAsync(source, "PICO003");
    }

    [Test]
    public async Task AnalyzerAndGenerator_ReportPico003_WhenOpenGenericImplementationIsAbstract()
    {
        const string source = """
            using PicoDI;
            using PicoDI.Abs;

            public interface IRepository<T>;

            public abstract class AbstractRepository<T> : IRepository<T>
            {
                protected AbstractRepository()
                {
                }
            }

            public static class RegistrationSite
            {
                public static void Register(ISvcContainer container)
                {
                    container.RegisterTransient(typeof(IRepository<>), typeof(AbstractRepository<>));
                }
            }
            """;

        await AssertAnalyzerAndGeneratorReportAsync(source, "PICO003");
    }

    [Test]
    public async Task AnalyzerAndGenerator_ReportPico004_WhenOpenGenericImplementationHasNoPublicConstructor()
    {
        const string source = """
            using PicoDI;
            using PicoDI.Abs;

            public interface IRepository<T>;

            public sealed class HiddenCtorRepository<T> : IRepository<T>
            {
                private HiddenCtorRepository()
                {
                }
            }

            public static class RegistrationSite
            {
                public static void Register(ISvcContainer container)
                {
                    container.RegisterTransient(typeof(IRepository<>), typeof(HiddenCtorRepository<>));
                }
            }
            """;

        await AssertAnalyzerAndGeneratorReportAsync(source, "PICO004");
    }

    [Test]
    public async Task AnalyzerAndGenerator_ReportPico005_WhenOpenGenericImplementationHasMultipleMarkedConstructors()
    {
        const string source = """
            using PicoDI;
            using PicoDI.Abs;

            public interface IRepository<T>;
            public sealed class RepositoryDependency;

            public sealed class MultipleMarkedCtorRepository<T> : IRepository<T>
            {
                [SvcConstructor]
                public MultipleMarkedCtorRepository()
                {
                }

                [SvcConstructor]
                public MultipleMarkedCtorRepository(RepositoryDependency dependency)
                {
                }
            }

            public static class RegistrationSite
            {
                public static void Register(ISvcContainer container)
                {
                    container.RegisterTransient(typeof(IRepository<>), typeof(MultipleMarkedCtorRepository<>));
                }
            }
            """;

        await AssertAnalyzerAndGeneratorReportAsync(source, "PICO005");
    }

    [Test]
    public async Task Generator_SubstitutesGenericConstructorDependencies_ForClosedRegistrations()
    {
        const string source = """
            using PicoDI;
            using PicoDI.Abs;

            public interface IRepository<T>;
            public interface ILogger<T>;

            public sealed class Logger<T> : ILogger<T>
            {
                public Logger()
                {
                }
            }

            public sealed class Repository<T> : IRepository<T>
            {
                public Repository(ILogger<T> logger)
                {
                }
            }

            public sealed class User;

            public static class RegistrationSite
            {
                public static void Register(ISvcContainer container)
                {
                    container.RegisterSingleton(typeof(ILogger<>), typeof(Logger<>));
                    container.RegisterTransient(typeof(IRepository<>), typeof(Repository<>));
                }

                public static IRepository<User> Resolve(ISvcScope scope)
                {
                    return scope.GetService<IRepository<User>>();
                }
            }
            """;

        var runResult = RunGenerator(source);
        var generatedSource = GetGeneratedSourceText(
            runResult,
            "PicoDI.ServiceRegistrations.SvcConstructorAnalyzerTests.g.cs"
        );

        await Assert
            .That(generatedSource)
            .Contains(
                "(global::ILogger<global::User>)scope.GetService(typeof(global::ILogger<global::User>))"
            );
    }

    [Test]
    public async Task Generator_PreservesFirstMatchSemantics_ForDuplicateOpenServiceMappings()
    {
        const string source = """
            using PicoDI;
            using PicoDI.Abs;

            public interface IRepository<T>;

            public sealed class FirstRepository<T> : IRepository<T>
            {
                public FirstRepository()
                {
                }
            }

            public sealed class SecondRepository<T> : IRepository<T>
            {
                public SecondRepository()
                {
                }
            }

            public sealed class User;

            public static class RegistrationSite
            {
                public static void Register(ISvcContainer container)
                {
                    container.RegisterTransient(typeof(IRepository<>), typeof(FirstRepository<>));
                    container.RegisterTransient(typeof(IRepository<>), typeof(SecondRepository<>));
                }

                public static IRepository<User> Resolve(ISvcScope scope)
                {
                    return scope.GetService<IRepository<User>>();
                }
            }
            """;

        var runResult = RunGenerator(source);
        var generatedSource = GetGeneratedSourceText(
            runResult,
            "PicoDI.ServiceRegistrations.SvcConstructorAnalyzerTests.g.cs"
        );

        await Assert.That(generatedSource).Contains("new global::FirstRepository<global::User>()");
        await Assert
            .That(generatedSource)
            .DoesNotContain("new global::SecondRepository<global::User>()");
    }

    [Test]
    public async Task Generator_TypedResolver_UsesLastDuplicateRegistration()
    {
        const string source = """
            using PicoDI;
            using PicoDI.Abs;

            public interface IDuplicateResolverService;

            public sealed class FirstDuplicateResolverService : IDuplicateResolverService
            {
                public FirstDuplicateResolverService()
                {
                }
            }

            public sealed class SecondDuplicateResolverService : IDuplicateResolverService
            {
                public SecondDuplicateResolverService()
                {
                }
            }

            public interface IConsumer
            {
            }

            public sealed class Consumer : IConsumer
            {
                public Consumer(IDuplicateResolverService dep)
                {
                }
            }

            public static class RegistrationSite
            {
                public static void Register(ISvcContainer container)
                {
                    container.RegisterTransient<IDuplicateResolverService, FirstDuplicateResolverService>();
                    container.RegisterScoped<IDuplicateResolverService, SecondDuplicateResolverService>();
                    container.RegisterTransient<IConsumer, Consumer>();
                }
            }
            """;

        var runResult = RunGenerator(source);
        var resolverMethod = GetGeneratedResolverMethodText(
            runResult,
            "PicoDI.ServiceRegistrations.SvcConstructorAnalyzerTests.g.cs",
            "global::IDuplicateResolverService"
        );

        await Assert
            .That(resolverMethod)
            .Contains(
                "return (global::IDuplicateResolverService)scope.GetService(typeof(global::IDuplicateResolverService));"
            );
        await Assert
            .That(resolverMethod)
            .DoesNotContain("new global::FirstDuplicateResolverService()");
    }

    [Test]
    public async Task Generator_CycleDiagnostic_UsesSimpleTypeNames_ForCircularDependencies()
    {
        const string source = """
            using PicoDI;
            using PicoDI.Abs;

            namespace Example.Services;

            public interface IA;
            public interface IB;

            public sealed class A : IA
            {
                public A(IB dependency)
                {
                }
            }

            public sealed class B : IB
            {
                public B(IA dependency)
                {
                }
            }

            public static class RegistrationSite
            {
                public static void Register(ISvcContainer container)
                {
                    container.RegisterTransient<IA, A>();
                    container.RegisterTransient<IB, B>();
                }
            }
            """;

        var diagnostics = await GetGeneratorDiagnosticsAsync(source);
        var cycleDiagnostic = diagnostics.Single(d => d.Id == "PICO002");

        await Assert.That(cycleDiagnostic.GetMessage()).Contains("IA -> IB -> IA");
    }

    #endregion
}

#region Additional Generic Test Services

public class RepositoryWithLogger<T>(ILogger<T> logger) : IRepository<T>
{
    public Guid InstanceId { get; } = Guid.NewGuid();
    public Type EntityType => typeof(T);
    public ILogger<T> Logger { get; } = logger;
}

public class CachedRepository<T> : IRepository<T>
{
    public Guid InstanceId { get; } = Guid.NewGuid();
    public Type EntityType => typeof(T);
}

#endregion
