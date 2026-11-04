namespace PicoLog.Tests;

/// <summary>
/// Repo-wide documentation guard: a Markdown snippet may not release an async-dispose-only type
/// with <c>using var</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>PicoLog/README.md</c> shipped three snippets that could not compile —
/// <c>using var factory = new LoggerFactory(…)</c> — and nothing noticed, because no test reads
/// the documentation. <see cref="AssemblySurfaceTests"/> pins the contract that makes those
/// snippets wrong (<c>IDisposable.IsAssignableFrom(typeof(LoggerFactory)) == false</c>) but never
/// looks at a README. This guard looks at the READMEs, and it is deliberately narrow: compiling
/// every fence is not viable (most snippets are fragments — a statement with no enclosing type, a
/// piece of a method body), while <c>using var</c> on an async-dispose-only type is a
/// self-contained, always-fatal error (<c>CS8418</c>) that needs no context to detect.
/// </para>
/// <para>
/// Scope: every Markdown file git tracks (<c>git ls-files '*.md'</c>) — the docs the repository
/// ships, not untracked scratch such as <c>.sisyphus/</c> or <c>results/</c>, which must never
/// decide whether the build is green. Fenced <c>csharp</c> blocks and inline <c>`code`</c> spans
/// are scanned; other fence languages are not (the snippets that rot are C#).
/// </para>
/// <para>
/// Async-dispose-only is decided by reflection on the real <c>PicoLog</c>/<c>PicoDI</c> type —
/// <see cref="IAsyncDisposable"/> is assignable and <see cref="IDisposable"/> is not — which is
/// exactly the CS8418 condition. The name→type table only says which types a snippet may name;
/// <see cref="SnippetTypeTable_CoversEveryPublicAsyncDisposeOnlyTypeInPicoLogAndPicoDi"/> fails
/// when a new public async-dispose-only type appears, so the table cannot drift silently.
/// </para>
/// <para>
/// Known limitation: the type is resolved from the snippet text (a <c>new T(…)</c> expression, or
/// <c>container.CreateScope()</c>/<c>logger.BeginScope(…)</c>), so a snippet that declares a
/// variable on one line and releases it on another, or names a user-derived type
/// (<c>BackgroundSvc</c> subclasses), is out of reach. So is a snippet in a four-space-indented
/// code block: that is not a fence, and telling it apart from a list continuation needs the
/// block grammar, so it is not scanned. Widening either means compiling the fragments, which is
/// the guard this one deliberately is not.
/// </para>
/// </remarks>
public sealed class DocSnippetAsyncDisposalTests
{
    /// <summary>
    /// Exemption convention (documented in <c>CONTRIBUTING.md</c>): a block — a fenced code block
    /// or a prose paragraph — that deliberately shows the <em>wrong</em> form carries this marker,
    /// because the prose around it argues that the code does not compile. The marker is block
    /// scoped, so it works for both kinds of block: for a fence it goes on the fence line, in a
    /// code comment inside the fence, or on the line directly above it; for prose, anywhere in the
    /// paragraph.
    /// </summary>
    private const string CounterExampleMarker = "<!-- doc-lint: counter-example -->";

    /// <summary>Root marker used to locate the repository; also present in every worktree.</summary>
    private const string SolutionFileName = "PicoInfra.slnx";

    /// <summary>
    /// Second root marker, paired with <see cref="SolutionFileName"/>: a solution file alone could
    /// be a copy, so the walk also requires the project directory this guard reads.
    /// </summary>
    private const string RootProjectDirectoryName = "PicoLog";

    /// <summary>
    /// Types a documentation snippet may name, mapped to the real type so the verdict is
    /// reflection, not a hardcoded list. Every entry is here because it is disposable — either
    /// async-dispose-only (flagged) or <see cref="IDisposable"/> (legal with <c>using var</c>).
    /// <see cref="SnippetTypeTable_PinsTheDisposalContractOfEveryEntry"/> holds both halves.
    /// </summary>
    private static readonly (string Name, Type Type)[] SnippetTypes =
    [
        ("LoggerFactory", typeof(LoggerFactory)),
        ("ILoggerFactory", typeof(ILoggerFactory)),
        ("IFlushableLoggerFactory", typeof(IFlushableLoggerFactory)),
        ("ILogSink", typeof(ILogSink)),
        ("IFlushableLogSink", typeof(IFlushableLogSink)),
        ("IBatchingLogSink", typeof(IBatchingLogSink)),
        ("IFastLogSink", typeof(IFastLogSink)),
        ("FileSink", typeof(FileSink)),
        ("ConsoleSink", typeof(ConsoleSink)),
        ("ColoredConsoleSink", typeof(ColoredConsoleSink)),
        ("SvcContainer", typeof(SvcContainer)),
        ("ISvcContainer", typeof(ISvcContainer)),
        ("SvcScope", typeof(SvcScope)),
        ("ISvcScope", typeof(ISvcScope)),
        ("SvcHost", typeof(SvcHost)),
        ("IHost", typeof(IHost)),
        ("SvcHostBuilder", typeof(SvcHostBuilder)),
        // Not flagged, on purpose — the two legitimate `using var` forms in today's documentation:
        //   ILogScope               — `logger.BeginScope(…)` returns it and it is IDisposable
        //                             (`using (logger.BeginScope(…))` in PicoLog/README.md).
        //   CancellationTokenSource — BCL type, IDisposable (`using var cts = …` in PicoCfg/README.md).
        ("ILogScope", typeof(ILogScope)),
        ("CancellationTokenSource", typeof(CancellationTokenSource)),
    ];

    /// <summary>
    /// Factory members whose declared return type a snippet releases without a <c>new</c>:
    /// <c>using var scope = container.CreateScope();</c> (SvcScope) and
    /// <c>using var scope = logger.BeginScope(…);</c> (ILogScope, IDisposable → legal).
    /// </summary>
    private static readonly (string Member, Type Type)[] FactoryMembers =
    [
        ("CreateScope", typeof(SvcScope)),
        ("BeginScope", typeof(ILogScope)),
    ];

    /// <summary>
    /// Public async-dispose-only types the table deliberately does not name: <c>BackgroundSvc</c>
    /// is the abstract base users derive from, so a snippet names a user type that no table can
    /// resolve. Adding a type here is a decision, which is the point.
    /// </summary>
    private static readonly string[] TableExemptTypeNames = ["BackgroundSvc"];

    /// <summary>The seven names this guard may never lose (each is async-dispose-only today).</summary>
    private static readonly string[] RequiredTypeNames =
    [
        "LoggerFactory",
        "ILoggerFactory",
        "SvcContainer",
        "SvcScope",
        "SvcHost",
        "ILogSink",
        "SvcHostBuilder",
    ];

    [Test]
    public async Task TrackedMarkdown_NeverDisposesAnAsyncDisposeOnlyTypeWithUsingVar()
    {
        var root = RepositoryRoot();
        var paths = TrackedMarkdownPaths(root);

        // Positive controls: a walk that found nothing (wrong root, broken git, a filter that ate
        // every file) must fail loudly instead of passing.
        await Assert
            .That(paths.Count >= 10)
            .IsTrue()
            .Because($"the guard found only {paths.Count} tracked Markdown files under {root}");
        await Assert
            .That(paths.Contains("PicoLog/README.md") && paths.Contains("PicoDI/README.md"))
            .IsTrue()
            .Because("the guard must see the two READMEs whose snippets rotted");

        var violations = new List<string>();
        foreach (var path in paths)
            violations.AddRange(ScanDocument(path, File.ReadAllLines(Path.Combine(root, path))));

        await Assert
            .That(violations.Count)
            .IsEqualTo(0)
            .Because(
                "a Markdown snippet may not write `using var` on an async-dispose-only type — that"
                    + " is CS8418 and the fix is `await using`; a deliberate counter-example must be"
                    + $" marked with `{CounterExampleMarker}`:"
                    + Environment.NewLine
                    + string.Join(Environment.NewLine, violations)
            );
    }

    [Test]
    public async Task Detector_FlagsEveryUsingVarFormOnAnAsyncDisposeOnlyType()
    {
        string[] document =
        [
            "# Doc", // 1
            string.Empty,
            "```csharp", // 3 — fence line
            "var sink = new ColoredConsoleSink(new ConsoleFormatter());", // 4
            "using var factory = new LoggerFactory([sink],", // 5 — the rot PicoLog/README.md shipped
            "    new LoggerFactoryOptions { MinLevel = LogLevel.Info });",
            "```",
            string.Empty,
            "A caller who writes `using var scope = container.CreateScope();` gets CS8418.", // 9
            string.Empty,
            "```csharp",
            "using (var host = new SvcHost(container))", // 12 — the parenthesized form
            "{",
            "    host.StartAsync();",
            "}",
            "using  var spaced = new LoggerFactory(sinks);", // 16 — doubled space
            "```",
        ];

        var violations = ScanDocument("README.md", document).ToArray();
        var report = string.Join(Environment.NewLine, violations);

        await Assert
            .That(report)
            .Contains("README.md:5")
            .Because("the exact form that shipped broken must be flagged");
        await Assert
            .That(report)
            .Contains("README.md:9")
            .Because("an inline prose snippet claiming `using var scope = …` is wrong too");
        await Assert
            .That(report)
            .Contains("README.md:12")
            .Because("`using (var x = …)` is the same defect in the other syntax");
        await Assert
            .That(report)
            .Contains("README.md:16")
            .Because("a doubled space must not slip past the detector");
        await Assert.That(violations.Length).IsEqualTo(4).Because(report);
    }

    [Test]
    public async Task Detector_AllowsTheDocumentedExceptions()
    {
        string[] document =
        [
            "```csharp", // 1
            "await using var factory = new LoggerFactory([sink]);", // the fix, not the defect
            "```",
            string.Empty,
            "```csharp",
            "using var cts = new CancellationTokenSource();", // IDisposable: legal
            "using var scope = logger.BeginScope(new { RequestId = \"abc-123\" });", // ILogScope: legal
            "```",
            string.Empty,
            CounterExampleMarker, // marks the fence below
            "```csharp",
            "using var factory = new LoggerFactory(sinks);", // a deliberate counter-example
            "```",
            string.Empty,
            "A paragraph that quotes the wrong form: `using var scope = container.CreateScope();`", // 15
            CounterExampleMarker, // the marker may sit anywhere in the paragraph
            string.Empty,
            "```text",
            "using var factory = new LoggerFactory(sinks);", // not C#: out of scope
            "```",
        ];

        var violations = ScanDocument("README.md", document).ToArray();

        await Assert
            .That(string.Join(Environment.NewLine, violations))
            .IsEqualTo(string.Empty)
            .Because(
                "`await using`, IDisposable types, non-C# fences and marked blocks are all legal"
            );
    }

    [Test]
    public async Task Detector_ExemptsOnlyTheMarkedBlock()
    {
        // The marker is block scoped: it must not leak to the next block, or one marked
        // counter-example would silence the whole file.
        string[] document =
        [
            CounterExampleMarker, // 1
            "```csharp",
            "using var factory = new LoggerFactory(sinks);", // 3 — marked
            "```",
            string.Empty,
            "```csharp",
            "using var other = new LoggerFactory(sinks);", // 7 — not marked
            "```",
        ];

        var violations = ScanDocument("README.md", document).ToArray();

        await Assert.That(violations.Length).IsEqualTo(1);
        await Assert.That(violations[0]).Contains("README.md:7");
    }

    [Test]
    public async Task SnippetTypeTable_PinsTheDisposalContractOfEveryEntry()
    {
        foreach (var (name, type) in SnippetTypes)
        {
            await Assert
                .That(type.Name)
                .IsEqualTo(name)
                .Because("a name/type mismatch would stop the table matching the documentation");
            await Assert
                .That(IsAsyncDisposeOnly(type) || typeof(IDisposable).IsAssignableFrom(type))
                .IsTrue()
                .Because($"{name} is not disposable at all, so it does not belong in this table");
        }

        // The seven names the guard may never lose, each async-dispose-only in the current source
        // — i.e. `using var` on it is CS8418. If one of them ever implements IDisposable, the
        // READMEs may use `using var` again and this guard must be told, not left stale.
        foreach (var required in RequiredTypeNames)
            await Assert
                .That(IsAsyncDisposeOnly(TypeNamed(required)!))
                .IsTrue()
                .Because($"{required} must stay async-dispose-only, or `using var` on it compiles");

        // The two legitimate `using var` forms in today's docs, pinned by their contract:
        // ILogScope is `logger.BeginScope(…)`'s return type, CancellationTokenSource is the BCL
        // one, and both implement IDisposable — so the guard must never flag them.
        await Assert.That(typeof(IDisposable).IsAssignableFrom(typeof(ILogScope))).IsTrue();
        await Assert
            .That(typeof(IDisposable).IsAssignableFrom(typeof(CancellationTokenSource)))
            .IsTrue();
    }

    [Test]
    public async Task SnippetTypeTable_CoversEveryPublicAsyncDisposeOnlyTypeInPicoLogAndPicoDi()
    {
        Type[] anchors =
        [
            typeof(LoggerFactory), // PicoLog
            typeof(ILoggerFactory), // PicoLog.Abs
            typeof(SvcContainer), // PicoDI
            typeof(ISvcContainer), // PicoDI.Abs
        ];

        var unmapped = anchors
            .SelectMany(TypesInAssemblyOf)
            .Where(type => type.IsPublic || type.IsNestedPublic)
            .Where(IsAsyncDisposeOnly)
            .Select(type => type.Name)
            .Where(name => TypeNamed(name) is null)
            .Where(name => !TableExemptTypeNames.Contains(name, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        await Assert
            .That(unmapped.Length)
            .IsEqualTo(0)
            .Because(
                "a public async-dispose-only type the table does not name is a documentation defect"
                    + " this guard would miss: add it to SnippetTypes, or to TableExemptTypeNames"
                    + " with the reason it cannot appear in a snippet. Missing: "
                    + string.Join(", ", unmapped)
            );
    }

    // ---------------------------------------------------------------- scanning

    /// <summary>
    /// Returns one line per offending snippet: <c>path:line: detail</c>, with the 1-based line of
    /// the offending statement (inside a fence) or of the span (in prose).
    /// </summary>
    private static IEnumerable<string> ScanDocument(string relativePath, string[] lines)
    {
        foreach (var block in ParseBlocks(lines))
        {
            if (ContainsCounterExampleMarker(lines, block))
                continue;

            if (block.IsFence && !block.IsCSharpFence)
                continue;

            var statements = block.IsFence
                ? FenceStatements(lines, block)
                : InlineStatements(lines, block);

            foreach (var (lineIndex, statement) in statements)
            {
                if (!TryFindAsyncDisposeOnlyUsingVar(statement, out var typeName))
                    continue;

                yield return $"{relativePath}:{lineIndex + 1}: `using var` on async-dispose-only"
                    + $" {typeName} does not compile (CS8418) — use `await using`:"
                    + $" {Compact(statement)}";
            }
        }
    }

    /// <summary>
    /// Splits a document into blocks: fenced code blocks and prose paragraphs. Line ranges are
    /// inclusive and 0-based; a block is the unit the counter-example marker covers.
    /// </summary>
    private static IEnumerable<DocBlock> ParseBlocks(string[] lines)
    {
        var index = 0;
        while (index < lines.Length)
        {
            if (TryReadFenceOpening(lines[index], out var fence, out var info))
            {
                var close = index + 1;
                while (close < lines.Length && !IsFenceClosing(lines[close], fence))
                    close++;

                yield return new DocBlock(index, Math.Min(close, lines.Length - 1), info);
                index = close + 1;
                continue;
            }

            if (string.IsNullOrWhiteSpace(lines[index]))
            {
                index++;
                continue;
            }

            var start = index;
            while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]))
            {
                if (TryReadFenceOpening(lines[index], out _, out _))
                    break;
                index++;
            }

            yield return new DocBlock(start, index - 1, null);
        }
    }

    /// <summary>
    /// Recognises a fence opening: up to three leading spaces, then a run of three or more
    /// backticks or tildes (an inline span opens with a single backtick, so it never matches).
    /// </summary>
    private static bool TryReadFenceOpening(string line, out string fence, out string info)
    {
        fence = string.Empty;
        info = string.Empty;

        var indent = 0;
        while (indent < line.Length && indent < 3 && line[indent] == ' ')
            indent++;

        if (indent >= line.Length || line[indent] is not ('`' or '~'))
            return false;

        var markerChar = line[indent];
        var run = indent;
        while (run < line.Length && line[run] == markerChar)
            run++;

        if (run - indent < 3)
            return false;

        fence = line[indent..run];
        info = line[run..].Trim();
        return true;
    }

    private static bool IsFenceClosing(string line, string fence) => line.Trim() == fence;

    /// <summary>Statements inside a fence, skipping the fence delimiters.</summary>
    private static IEnumerable<(int LineIndex, string Statement)> FenceStatements(
        string[] lines,
        DocBlock block
    )
    {
        for (var index = block.StartIndex + 1; index < block.EndIndex; index++)
        {
            if (!StartsUsingVar(lines[index]))
                continue;

            // A release may wrap (`using var factory = new LoggerFactory(`, options on the next
            // lines); join a bounded number of continuation lines so the type on line one is found.
            var last = index;
            var statement = lines[index];
            while (
                !statement.Contains(';', StringComparison.Ordinal)
                && last + 1 < block.EndIndex
                && last - index < 3
            )
            {
                last++;
                statement = statement + " " + lines[last].Trim();
            }

            yield return (index, statement);
            index = last;
        }
    }

    /// <summary>Inline <c>`code`</c> spans in a prose paragraph.</summary>
    private static IEnumerable<(int LineIndex, string Statement)> InlineStatements(
        string[] lines,
        DocBlock block
    )
    {
        for (var index = block.StartIndex; index <= block.EndIndex; index++)
        {
            var line = lines[index];
            var open = line.IndexOf('`', StringComparison.Ordinal);
            while (open >= 0)
            {
                var close = line.IndexOf('`', open + 1);
                if (close < 0)
                    break;

                var span = line[(open + 1)..close];
                if (StartsUsingVar(span))
                    yield return (index, span);

                open = line.IndexOf('`', close + 1);
            }
        }
    }

    /// <summary>
    /// True when the block carries the counter-example marker. A fenced block may be marked on the
    /// fence line, in a code comment inside it, or on the line directly above it (the natural
    /// place to write the marker); a prose paragraph is marked anywhere inside it.
    /// </summary>
    private static bool ContainsCounterExampleMarker(string[] lines, DocBlock block)
    {
        var first = block.IsFence ? Math.Max(0, block.StartIndex - 1) : block.StartIndex;

        for (var index = first; index <= block.EndIndex && index < lines.Length; index++)
            if (lines[index].Contains(CounterExampleMarker, StringComparison.Ordinal))
                return true;

        return false;
    }

    // ---------------------------------------------------------------- detection

    /// <summary>
    /// True when <paramref name="statement"/> releases a value whose type is async-dispose-only
    /// with <c>using var</c> or <c>using (var …)</c>. <c>await using</c> is the fix, not a hit.
    /// </summary>
    private static bool TryFindAsyncDisposeOnlyUsingVar(string statement, out string typeName)
    {
        typeName = string.Empty;
        // Normalise whitespace runs (a joined continuation, a double space, a tab) so the
        // match does not depend on how the snippet is laid out; type names survive intact.
        var text = Compact(statement);

        if (text.StartsWith("await ", StringComparison.Ordinal))
            return false;

        var body = text switch
        {
            _ when text.StartsWith("using (var ", StringComparison.Ordinal) => text[
                "using (".Length..
            ],
            _ when text.StartsWith("using var ", StringComparison.Ordinal) => text[
                "using ".Length..
            ],
            _ => null,
        };

        if (body is null || !body.StartsWith("var ", StringComparison.Ordinal))
            return false;

        var equals = body.IndexOf('=', StringComparison.Ordinal);
        if (equals < 0 || body["var ".Length..equals].Trim().Length == 0)
            return false;

        var type = ResolveDeclaredType(body[(equals + 1)..]);
        if (type is null || !IsAsyncDisposeOnly(type))
            return false;

        typeName = type.Name;
        return true;
    }

    /// <summary>
    /// Resolves the declared type of a snippet's right-hand side from the snippet text alone:
    /// a <c>new T(…)</c> expression, or one of <see cref="FactoryMembers"/>. Anything else (a
    /// variable, a parameter, a user type) is unresolvable, and the guard stays silent rather than
    /// guess — see the known limitation on this class.
    /// </summary>
    private static Type? ResolveDeclaredType(string expression)
    {
        var text = expression.Trim();

        if (text.StartsWith("new ", StringComparison.Ordinal))
            return TypeNamed(SimpleTypeName(text["new ".Length..]));

        foreach (var (member, type) in FactoryMembers)
            if (
                text.Contains($"{member}(", StringComparison.Ordinal)
                || text.Contains($"{member} (", StringComparison.Ordinal)
            )
                return type;

        return null;
    }

    /// <summary>
    /// Simple name of the type a <c>new</c> expression constructs (namespace and generics dropped);
    /// <c>new { … }</c> yields an empty name, which resolves to nothing.
    /// </summary>
    private static string SimpleTypeName(string afterNew)
    {
        var end = 0;
        while (
            end < afterNew.Length
            && (char.IsLetterOrDigit(afterNew[end]) || afterNew[end] is '_' or '.')
        )
            end++;

        var identifier = afterNew[..end];
        var lastDot = identifier.LastIndexOf('.');
        return lastDot < 0 ? identifier : identifier[(lastDot + 1)..];
    }

    private static bool StartsUsingVar(string text)
    {
        var trimmed = Compact(text);
        return trimmed.StartsWith("using var ", StringComparison.Ordinal)
            || trimmed.StartsWith("using (var ", StringComparison.Ordinal)
            || trimmed.StartsWith("await using var ", StringComparison.Ordinal)
            || trimmed.StartsWith("await using (var ", StringComparison.Ordinal);
    }

    /// <summary>The CS8418 condition: async-dispose-only means <c>using var</c> cannot compile.</summary>
    private static bool IsAsyncDisposeOnly(Type type) =>
        typeof(IAsyncDisposable).IsAssignableFrom(type)
        && !typeof(IDisposable).IsAssignableFrom(type);

    private static Type? TypeNamed(string name) =>
        SnippetTypes
            .FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.Ordinal))
            .Type;

    /// <summary>
    /// Every type declared in the assembly that declares <paramref name="anchor"/>. The anchor is a
    /// type from <see cref="SnippetTypes"/>, so this only enumerates assemblies the guard already
    /// knows about: a new assembly is a deliberate addition, not a silent widening.
    /// </summary>
    /// <remarks>
    /// Suppressed deliberately: the trim analyzer flags
    /// <see cref="System.Reflection.Assembly.GetTypes"/>, but this is document-lint enumeration in
    /// a test project — tests run managed, never AOT, never inside a trimmed consumer, and the
    /// assemblies are referenced by this project rather than loaded by name.
    /// </remarks>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Test-only enumeration of assemblies this project references; tests run managed, never AOT."
    )]
    private static IEnumerable<Type> TypesInAssemblyOf(Type anchor) => anchor.Assembly.GetTypes();

    private static string Compact(string statement)
    {
        var single = new StringBuilder(statement.Length);
        var space = false;
        foreach (var character in statement)
        {
            if (char.IsWhiteSpace(character))
            {
                space = single.Length > 0;
                continue;
            }

            if (space)
                single.Append(' ');
            space = false;
            single.Append(character);
        }

        return single.ToString();
    }

    // ---------------------------------------------------------------- repository access

    /// <summary>
    /// The repository root, located from two independent anchors. The compiled-in source path is
    /// tried first — it is the most precise — but the test binary's own output directory is tried
    /// as well, because the SDK PathMap rewrites <see cref="CallerFilePathAttribute"/> paths to
    /// <c>/_/…</c> under <c>ContinuousIntegrationBuild</c> (set by <c>Directory.Build.props</c>
    /// when <c>CI=true</c>), and then the first anchor points into a directory tree that does not
    /// exist.
    /// </summary>
    /// <remarks>
    /// A failed walk throws, naming both anchors and what each one saw. It must never return
    /// something plausible-looking or let the caller skip: a guard that quietly checks nothing is
    /// the exact failure this file exists to prevent.
    /// </remarks>
    private static string RepositoryRoot([CallerFilePath] string thisFile = "")
    {
        var attempts = new List<string>();

        if (
            TryFindRootAbove(
                Path.GetDirectoryName(thisFile),
                $"the compiled-in source path ('{thisFile}')",
                attempts,
                out var root
            )
        )
            return root;

        if (
            TryFindRootAbove(
                AppContext.BaseDirectory,
                $"the test output directory ('{AppContext.BaseDirectory}')",
                attempts,
                out root
            )
        )
            return root;

        throw new InvalidOperationException(
            "The documentation guard cannot locate the repository root, so it cannot check"
                + $" anything. A repository root is a directory containing both {SolutionFileName}"
                + $" and {RootProjectDirectoryName}/. Tried:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, attempts.Select(attempt => "  - " + attempt))
                + Environment.NewLine
                + "If the first anchor reads '/_/…', the SDK PathMap rewrote the source path"
                + " (ContinuousIntegrationBuild, i.e. CI=true); the output directory survives that"
                + " rewrite, so it failing too means this binary is running with no repository"
                + " above it."
        );
    }

    /// <summary>
    /// Walks up from <paramref name="start"/> to the first directory that is verifiably the
    /// repository root. The outcome is recorded in <paramref name="attempts"/> so a total failure
    /// can say what was tried and why it did not work.
    /// </summary>
    private static bool TryFindRootAbove(
        string? start,
        string anchor,
        List<string> attempts,
        out string root
    )
    {
        root = string.Empty;
        if (string.IsNullOrEmpty(start))
        {
            attempts.Add($"{anchor}: no directory to start from.");
            return false;
        }

        var directory = start;
        var examined = 0;
        while (!string.IsNullOrEmpty(directory))
        {
            examined++;
            if (IsRepositoryRoot(directory))
            {
                root = directory;
                return true;
            }

            directory = Path.GetDirectoryName(directory);
        }

        attempts.Add(
            $"{anchor}: walked up {examined} directories from '{start}' without finding one that"
                + $" contains both {SolutionFileName} and {RootProjectDirectoryName}/."
        );
        return false;
    }

    /// <summary>
    /// Whether <paramref name="directory"/> is the repository root. Both halves are required: a
    /// lone solution file proves nothing (a copy, a fixture, a stale artifact), while a root with
    /// the solution <em>and</em> the project this guard documents is not a coincidence.
    /// </summary>
    private static bool IsRepositoryRoot(string directory) =>
        File.Exists(Path.Combine(directory, SolutionFileName))
        && Directory.Exists(Path.Combine(directory, RootProjectDirectoryName));

    /// <summary>
    /// Every Markdown file git tracks, relative to the root with forward slashes. Tracked files are
    /// the ones the repository ships; untracked scratch (<c>.sisyphus/</c>, <c>results/</c>,
    /// <c>docs/superpowers/</c>) must never decide whether a build is green.
    /// </summary>
    private static IReadOnlyList<string> TrackedMarkdownPaths(string root)
    {
        var output = RunGit(root, ["ls-files", "-z", "--", "*.md"]);

        return output
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(path =>
                !Path.GetFileName(path).Contains("THIRD", StringComparison.OrdinalIgnoreCase)
            )
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static string RunGit(string root, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process =
            Process.Start(startInfo)
            ?? throw new InvalidOperationException("git could not be started.");

        // Output is one path per tracked Markdown file — far below the pipe buffer — so reading
        // stdout before stderr cannot deadlock.
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return process.ExitCode == 0
            ? standardOutput
            : throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed with exit code {process.ExitCode} in"
                    + $" '{root}': {standardError}"
            );
    }

    /// <summary>
    /// A fenced code block or a prose paragraph. <see cref="Info"/> is the fence info string and is
    /// <see langword="null"/> for a prose paragraph, so an empty info string is still a fence.
    /// </summary>
    private readonly record struct DocBlock(int StartIndex, int EndIndex, string? Info)
    {
        public bool IsFence => Info is not null;

        public bool IsCSharpFence
        {
            get
            {
                var language = Info
                    ?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();
                return language is not null
                    && (
                        language.Equals("csharp", StringComparison.OrdinalIgnoreCase)
                        || language.Equals("cs", StringComparison.OrdinalIgnoreCase)
                        || language.Equals("c#", StringComparison.OrdinalIgnoreCase)
                    );
            }
        }
    }
}
