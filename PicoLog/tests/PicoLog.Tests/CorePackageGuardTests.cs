namespace PicoLog.Tests;

/// <summary>
/// The csproj half of the "PicoLog carries no serializer" pin. The
/// <c>PicoLogCoreDeclaresNoSerializer</c> target in PicoLog.csproj fails the build when that file
/// declares a reference other than PicoLog.Abs, because a reference declared there becomes a nuspec
/// dependency of the package — ProjectReference as much as PackageReference — and every consumer of
/// PicoLog inherits it.
/// </summary>
/// <remarks>
/// What this covers: an MSBuild target inside a project file has no test of its own, so deleting it
/// leaves every other pin green. These tests read PicoLog.csproj as XML — no MSBuild run, no
/// restore, no network — and fail if the target is gone, is no longer hooked to both Build and
/// GenerateNuspec, or if the references the file declares leave the allow-list the target enforces.
/// What it does not cover: it does not run the guard, and it does not inspect a packed .nupkg. A
/// pack-and-assert test was considered and rejected — it would need a nested <c>dotnet pack</c> from
/// inside <c>dotnet test</c>, which is slow and needs a restore the moment the build machine's
/// package cache is cold.
/// </remarks>
public sealed class CorePackageGuardTests
{
    private const string SolutionFileName = "PicoInfra.slnx";
    private const string CoreProjectPath = "PicoLog/src/PicoLog/PicoLog.csproj";
    private const string GuardTargetName = "PicoLogCoreDeclaresNoSerializer";
    private const string AllowedId = "PicoLog.Abs";

    [Test]
    public async Task CoreProject_StillDeclaresTheNoSerializerGuardBeforeBuildAndPack()
    {
        var target = GuardTarget();
        var beforeTargets = ((string?)target.Attribute("BeforeTargets") ?? string.Empty).Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );

        await Assert
            .That(beforeTargets.Contains("Build"))
            .IsTrue()
            .Because(
                "the guard must fail a plain `dotnet build`, not only a pack: without Build the"
                    + $" offender is caught late or not at all. {GuardTargetName} BeforeTargets="
                    + string.Join(';', beforeTargets)
            );
        await Assert
            .That(beforeTargets.Contains("GenerateNuspec"))
            .IsTrue()
            .Because(
                "GenerateNuspec is the last target before the .nupkg is written, so failing there"
                    + " leaves no package on disk for a pipeline that collects bin/**/*.nupkg"
                    + $" regardless of exit status. {GuardTargetName} BeforeTargets="
                    + string.Join(';', beforeTargets)
            );
    }

    [Test]
    public async Task CoreProject_DeclaresNoReferenceOtherThanPicoLogAbs()
    {
        var declared = DeclaredReferences();

        // Positive control: a walk that found nothing (wrong root, a renamed element, a parse that
        // silently ate the file) must fail instead of passing vacuously.
        await Assert
            .That(declared.Count)
            .IsGreaterThanOrEqualTo(2)
            .Because(
                "PicoLog.csproj declares one ProjectReference (the UseProjectReferences branch)"
                    + " and one PackageReference (the other), so fewer means this test is reading"
                    + " something other than that file:"
                    + Environment.NewLine
                    + Describe(declared)
            );

        var violations = new List<string>();
        foreach (var reference in declared)
        {
            if (!string.Equals(reference.Id, AllowedId, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add(
                    $"{reference.Kind} '{reference.Id}': the only allowed id is {AllowedId}. It"
                        + " becomes a nuspec dependency of PicoLog that every consumer of PicoLog"
                        + " inherits; serialization belongs to PicoLog.Json."
                );
            }
            else if (string.IsNullOrWhiteSpace(reference.PrivateAssets))
            {
                violations.Add(
                    $"{reference.Kind} '{reference.Id}': PrivateAssets is missing. Without it the"
                        + " reference flows its build assets to every consumer project instead of"
                        + " shipping as the one trimmed dependency."
                );
            }
            else if (reference.PrivateAssets.Contains("all", StringComparison.OrdinalIgnoreCase))
            {
                violations.Add(
                    $"{reference.Kind} '{reference.Id}': PrivateAssets=\"{reference.PrivateAssets}\""
                        + " removes the dependency from the nuspec altogether, and PicoLog.dll needs"
                        + " PicoLog.Abs at run time — consumers would get an assembly they cannot load."
                );
            }
            else if (
                reference.Kind == "PackageReference"
                && !string.Equals(
                    reference.PrivateAssets,
                    "Build",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                // The guard pins the package-mode dependency to exactly "Build" and only forbids
                // "all" on the ProjectReference; this test checks the same two rules.
                violations.Add(
                    $"{reference.Kind} '{reference.Id}': PrivateAssets=\"{reference.PrivateAssets}\""
                        + " — the guard pins the package-mode dependency to exactly \"Build\"."
                );
            }
        }

        await Assert
            .That(violations.Count)
            .IsEqualTo(0)
            .Because(
                "PicoLog (core)'s dependency list is PicoLog.Abs plus in-framework assemblies only:"
                    + string.Join(Environment.NewLine, violations)
            );
    }

    /// <summary>
    /// The referenced project's file name without its extension. Both separators are stripped by
    /// hand: the includes are written Windows-style (<c>..\PicoLog.Abs\PicoLog.Abs.csproj</c>), and
    /// <see cref="Path.GetFileNameWithoutExtension(string)"/> honours only the host's separator, so
    /// on the Linux and macOS CI legs it would hand back the whole path and fail this test for the
    /// wrong reason.
    /// </summary>
    private static string ProjectFileStem(string include)
    {
        var separator = include.LastIndexOfAny(['/', '\\']);
        var fileName = separator >= 0 ? include[(separator + 1)..] : include;
        return Path.GetFileNameWithoutExtension(fileName);
    }

    private static string Describe(IReadOnlyList<DeclaredReference> references) =>
        references.Count == 0
            ? "  (no PackageReference or ProjectReference element was found at all)"
            : string.Join(
                Environment.NewLine,
                references.Select(reference =>
                    $"  {reference.Kind} '{reference.Id}' PrivateAssets='{reference.PrivateAssets}'"
                )
            );

    /// <summary>
    /// The guard target, located by name. A missing target throws: this test file exists because a
    /// guard that was quietly deleted looks exactly like a guard that never fired.
    /// </summary>
    private static XElement GuardTarget() =>
        LoadCoreProject()
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Target"
                && string.Equals(
                    (string?)element.Attribute("Name"),
                    GuardTargetName,
                    StringComparison.Ordinal
                )
            )
        ?? throw new InvalidOperationException(
            $"{CoreProjectPath} no longer declares the <Target Name=\"{GuardTargetName}\"> guard."
                + " That target is the only thing that fails a build which would ship a serializer"
                + " (or any other dependency) to every consumer of PicoLog — restore it rather than"
                + " deleting this test."
        );

    /// <summary>
    /// Every <c>PackageReference</c> / <c>ProjectReference</c> the project file declares, in both
    /// UseProjectReferences branches (only one of which is active per build). Each item carries its
    /// own <c>PrivateAssets</c>, with an empty string standing for "not set".
    /// </summary>
    private static List<DeclaredReference> DeclaredReferences()
    {
        var references = new List<DeclaredReference>();
        foreach (var element in LoadCoreProject().Descendants())
        {
            var kind = element.Name.LocalName;
            if (kind is not ("PackageReference" or "ProjectReference"))
                continue;

            var include = (string?)element.Attribute("Include");
            if (string.IsNullOrWhiteSpace(include))
                continue;

            // A ProjectReference's Include is a path, a PackageReference's Include is the package
            // id; the guard compares the referenced project's file name, and so does this.
            var id = kind == "PackageReference" ? include.Trim() : ProjectFileStem(include.Trim());

            references.Add(
                new DeclaredReference(
                    kind,
                    id,
                    (string?)element.Attribute("PrivateAssets") ?? string.Empty
                )
            );
        }

        return references;
    }

    private static XDocument LoadCoreProject() =>
        XDocument.Load(
            Path.Combine(
                RepositoryRoot(),
                CoreProjectPath.Replace('/', Path.DirectorySeparatorChar)
            )
        );

    /// <summary>
    /// The repository root, located from two independent anchors: the compiled-in source path, and
    /// the test binary's output directory — the second one survives the SDK PathMap rewrite of
    /// <see cref="System.Runtime.CompilerServices.CallerFilePathAttribute"/> paths that
    /// <c>ContinuousIntegrationBuild</c> (CI=true) applies, which turns the first into <c>/_/…</c>.
    /// A failed walk throws with what each anchor saw; it never returns a plausible-looking path,
    /// because a test that quietly checks nothing is the failure this file exists to prevent.
    /// </summary>
    private static string RepositoryRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string thisFile = ""
    )
    {
        var attempts = new List<string>();
        foreach (
            var (start, anchor) in new[]
            {
                (
                    Path.GetDirectoryName(thisFile) ?? string.Empty,
                    $"the compiled-in source path ('{thisFile}')"
                ),
                (
                    AppContext.BaseDirectory,
                    $"the test output directory ('{AppContext.BaseDirectory}')"
                ),
            }
        )
        {
            var directory = start;
            while (!string.IsNullOrEmpty(directory))
            {
                if (
                    File.Exists(Path.Combine(directory, SolutionFileName))
                    && File.Exists(
                        Path.Combine(
                            directory,
                            CoreProjectPath.Replace('/', Path.DirectorySeparatorChar)
                        )
                    )
                )
                    return directory;

                directory = Path.GetDirectoryName(directory);
            }

            attempts.Add(
                $"{anchor}: no directory above it holds both {SolutionFileName} and {CoreProjectPath}."
            );
        }

        throw new InvalidOperationException(
            "CorePackageGuardTests cannot locate the repository root, so it cannot check anything:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, attempts.Select(attempt => "  - " + attempt))
        );
    }

    private readonly record struct DeclaredReference(string Kind, string Id, string PrivateAssets);
}
