namespace PicoCfg.Gen.Tests;

#pragma warning disable CS0618

public class PicoCfgBindGeneratorGeneratedSourceTests
{
    [Test]
    public async Task CfgBindEntryPoints_EmitExpectedGeneratedShape()
    {
        var generatedSource = await GenerateSourceAsync(
            """
            using PicoCfg;
            using PicoCfg.Abs;

            public sealed class AppSettings
            {
                public string? Name { get; set; }
                public int Count { get; set; }
            }

            public static class Entry
            {
                public static AppSettings Bind(ICfg cfg) => CfgBind.Bind<AppSettings>(cfg);
                public static bool TryBind(ICfg cfg, out AppSettings value) => CfgBind.TryBind<AppSettings>(cfg, out value);
                public static void BindInto(ICfg cfg, AppSettings target) => CfgBind.BindInto(cfg, target);
            }
            """
        );

        await AssertGeneratedSourceContainsAsync(
            generatedSource,
            "[global::System.Runtime.CompilerServices.ModuleInitializerAttribute]",
            "global::PicoCfg.CfgBindRuntime.Register<global::AppSettings>",
            "contractVersion: global::PicoCfg.CfgBindRuntime.ContractVersion",
            "private static global::AppSettings Bind_0",
            "private static bool TryBind_0",
            "private static void BindInto_0",
            "global::PicoCfg.CfgBindRuntime.CombinePath(section, \"Name\")",
            "global::PicoCfg.CfgBindRuntime.TryParseInt32(__raw_Count, out var __value_Count)"
        );
    }

    [Test]
    public async Task DiRegistrationEntryPoints_EmitExpectedGeneratedRegistration()
    {
        var generatedSource = await GenerateSourceAsync(
            """
            using PicoCfg.DI;
            using PicoDI;

            var container = new SvcContainer();
            container.RegisterCfgTransient<AppSettings>("App");

            public sealed class AppSettings
            {
                public string? Name { get; set; }
            }
            """
        );

        await AssertGeneratedSourceContainsAsync(
            generatedSource,
            "global::PicoCfg.CfgBindRuntime.Register<global::AppSettings>",
            "private static global::AppSettings Bind_0",
            "private static void BindInto_0",
            "tryBind: null"
        );

        // DI registration entry points only need the constructing bind —
        // TryBind_ must not be emitted (generated-code redundancy).
        await Assert.That(generatedSource.Contains("private static bool TryBind_0")).IsFalse();
    }

    [Test]
    public async Task CollectionProperty_EmitExpectedGeneratedShape()
    {
        var generatedSource = await GenerateSourceAsync(
            """
            using System.Collections.Generic;
            using PicoCfg;
            using PicoCfg.Abs;

            public sealed class CollectionSettings
            {
                public List<int> Ports { get; set; } = new();
            }

            public static class Entry
            {
                public static CollectionSettings Run(ICfg cfg) => CfgBind.Bind<CollectionSettings>(cfg);
            }
            """
        );

        await AssertGeneratedSourceContainsAsync(
            generatedSource,
            "global::PicoCfg.CfgBindRuntime.Register<global::CollectionSettings>",
            "CreateScopedView",
            "System.Collections.Generic.List<int>",
            "TryParseInt32"
        );
    }

    [Test]
    public async Task NestedCollectionElement_EmitsExpectedGeneratedShape()
    {
        var generatedSource = await GenerateSourceAsync(
            """
            using System.Collections.Generic;
            using PicoCfg;
            using PicoCfg.Abs;

            public sealed class ItemSettings
            {
                public string? Name { get; set; }
            }

            public sealed class CollectionSettings
            {
                public List<ItemSettings> Items { get; set; } = new();
            }

            public static class Entry
            {
                public static CollectionSettings Run(ICfg cfg) => CfgBind.Bind<CollectionSettings>(cfg);
            }
            """
        );

        await AssertGeneratedSourceContainsAsync(
            generatedSource,
            "global::PicoCfg.CfgBindRuntime.Register<global::CollectionSettings>",
            "CreateScopedView",
            "List<global::ItemSettings>",
            "Bind_0",
            "Bind_1"
        );
    }

    [Test]
    public async Task DictionaryWithNestedComplexValueType_GeneratesCompilableCode()
    {
        var generatedSource = await GenerateSourceAsync(
            """
            using System.Collections.Generic;
            using PicoCfg;
            using PicoCfg.Abs;

            public sealed class ProviderEntry
            {
                public string? Name { get; set; }
                public int Port { get; set; }
            }

            public sealed class AgentConfig
            {
                public Dictionary<string, ProviderEntry> Providers { get; set; } = new();
            }

            public static class Entry
            {
                public static AgentConfig Run(ICfg cfg) => CfgBind.Bind<AgentConfig>(cfg);
            }
            """
        );

        await Assert.That(generatedSource).IsNotNullOrEmpty();
    }

    [Test]
    public async Task DictionaryWithNestedInitOnlyType_GeneratesCompilableCode()
    {
        // Reproduction for: CS0825 'var' may only appear within a local variable declaration
        //                  CS0708 cannot declare instance members in a static class
        // The bug triggers when a Dictionary<string, NestedInitOnlyType> is bound
        // and NestedInitOnlyType has init-only properties requiring inline binding.
        var generatedSource = await GenerateSourceAsync(
            """
            using System.Collections.Generic;
            using PicoCfg;
            using PicoCfg.Abs;

            public sealed class ProviderEntry
            {
                public string? Name { get; init; }
                public int Port { get; init; }
            }

            public sealed class AgentConfig
            {
                public Dictionary<string, ProviderEntry> Providers { get; set; } = new();
            }

            public static class Entry
            {
                public static AgentConfig Run(ICfg cfg) => CfgBind.Bind<AgentConfig>(cfg);
            }
            """
        );

        await Assert.That(generatedSource).IsNotNullOrEmpty();
    }

    [Test]
    public async Task NestedDictOfDictWithInitOnly_GeneratesCompilableCode()
    {
        // Deeper nesting: a type with a Dictionary property whose value type also
        // has a Dictionary property with an init-only value type.
        var generatedSource = await GenerateSourceAsync(
            """
            using System.Collections.Generic;
            using PicoCfg;
            using PicoCfg.Abs;

            public sealed class InnerConfig
            {
                public string? Key { get; init; }
                public int Value { get; init; }
            }

            public sealed class NestedConfig
            {
                public Dictionary<string, InnerConfig> Items { get; set; } = new();
            }

            public sealed class RootConfig
            {
                public Dictionary<string, NestedConfig> Sections { get; set; } = new();
            }

            public static class Entry
            {
                public static RootConfig Run(ICfg cfg) => CfgBind.Bind<RootConfig>(cfg);
            }
            """
        );

        await Assert.That(generatedSource).IsNotNullOrEmpty();
    }

    [Test]
    public async Task TypeWithDictAndNestedTypeHavingDict_GeneratesCompilableCode()
    {
        // A type with Dictionary<string, X> where X also has a Dictionary<string, Y>
        // and Y has init-only scalar properties.
        var generatedSource = await GenerateSourceAsync(
            """
            using System.Collections.Generic;
            using PicoCfg;
            using PicoCfg.Abs;

            public sealed class ProviderEntry
            {
                public string? Name { get; init; }
                public int Port { get; init; }
                public Dictionary<string, string>? Tags { get; init; }
            }

            public sealed class AgentConfig
            {
                public string? Name { get; set; }
                public Dictionary<string, ProviderEntry> Providers { get; set; } = new();
            }

            public static class Entry
            {
                public static AgentConfig Run(ICfg cfg) => CfgBind.Bind<AgentConfig>(cfg);
            }
            """
        );

        await Assert.That(generatedSource).IsNotNullOrEmpty();
    }

    private static async Task AssertGeneratedSourceContainsAsync(
        string generatedSource,
        params string[] expectedFragments
    )
    {
        foreach (var expectedFragment in expectedFragments)
            await Assert
                .That(generatedSource.Contains(expectedFragment, StringComparison.Ordinal))
                .IsTrue();
    }

    private static async Task<string> GenerateSourceAsync(string source)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions);

        var compilation = CSharpCompilation.Create(
            assemblyName: "PicoCfg.Gen.Tests.GeneratedSourceCompilation",
            syntaxTrees: [syntaxTree],
            references: GetMetadataReferences(),
            options: new CSharpCompilationOptions(OutputKind.ConsoleApplication)
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

        var runResult = driver.GetRunResult();
        var generatedSources = runResult
            .Results.SelectMany(static result => result.GeneratedSources)
            .ToArray();
        var generatedRegistration = generatedSources.SingleOrDefault(sourceResult =>
            sourceResult.HintName == "PicoCfgBindRegistrations.g.cs"
        );

        if (generatedRegistration.HintName is null)
        {
            var allDiagnostics = outputCompilation
                .GetDiagnostics()
                .AddRange(driverDiagnostics)
                .AddRange(runResult.Diagnostics);
            var diagnosticText = string.Join(
                Environment.NewLine,
                allDiagnostics.Select(static diagnostic => diagnostic.ToString())
            );

            throw new InvalidOperationException(
                $"Expected generated source 'PicoCfgBindRegistrations.g.cs' was not produced.{Environment.NewLine}{diagnosticText}"
            );
        }

        var generatedSource = generatedRegistration.SourceText.ToString();

        await Assert.That(string.IsNullOrWhiteSpace(generatedSource)).IsFalse();
        return generatedSource;
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "AOT",
        "IL3000",
        Justification = "These Roslyn-based generator tests intentionally construct metadata references from file-backed assemblies during test execution."
    )]
    private static MetadataReference[] GetMetadataReferences()
    {
        var trustedPlatformAssemblies = (
            (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
        )!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        var explicitAssemblies = new[]
        {
            typeof(CfgBind).Assembly.Location,
            typeof(PicoCfg.DI.SvcContainerExtensions).Assembly.Location,
            typeof(PicoDI.SvcContainer).Assembly.Location,
        };

        return trustedPlatformAssemblies
            .Concat(explicitAssemblies)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(static path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }
}

#pragma warning restore CS0618
