namespace PicoCfg.Gen;

// Orchestrates the incremental generator pipeline and target aggregation.
[Generator(LanguageNames.CSharp)]
public sealed partial class PicoCfgBindGenerator : IIncrementalGenerator
{
    /// <summary>
    /// Maximum number of nested type levels the generator expands. Dictionary value
    /// types and collection element types count toward the limit (each nested class
    /// level adds one). Types at the boundary are truncated with PCFGGEN009.
    /// </summary>
    private const int NestingDepthLimit = 8;

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var calls = context
            .SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => PicoCfgBindGenerator.IsCandidateInvocation(node),
                static (ctx, _) => PicoCfgBindGenerator.TransformInvocation(ctx)
            )
            .Where(static call => call is not null)
            .Select(static (call, _) => call!);

        context.RegisterSourceOutput(
            calls.Collect(),
            static (spc, collectedCalls) => Execute(spc, collectedCalls)
        );
    }

    private static void Execute(SourceProductionContext context, ImmutableArray<BindCall> calls)
    {
        if (calls.IsDefaultOrEmpty)
            return;

        var targets = new Dictionary<ITypeSymbol, TargetRegistration>(
            SymbolEqualityComparer.Default
        );

        // Phase 1: Collect explicit targets from Bind<T>/TryBind<T>/BindInto<T> calls
        foreach (var call in calls)
        {
            if (!targets.TryGetValue(call.TargetType, out var registration))
            {
                registration = new TargetRegistration(call.TargetType);
                targets.Add(call.TargetType, registration);
            }

            registration.Operations |= call.Operation;
            registration.Locations.Add(call.Location);
        }

        // Phase 2: Recursively discover nested bindable targets from property types
        var truncatedTypes = DiscoverNestedTargets(targets, context);

        // Phase 3: Analyze all targets
        var validTargets = new List<TargetModel>(targets.Count);
        foreach (var registration in targets.Values)
        {
            if (!TryAnalyzeTarget(context, registration, targets, truncatedTypes, out var model))
                continue;

            validTargets.Add(model);
        }

        if (validTargets.Count == 0)
            return;

        // Phase 4: Topological sort — nested types before their dependents
        validTargets = TopologicalSort(validTargets, context);
        if (validTargets.Count == 0)
            return; // cycle detected

        // Fill in NestedModelIndex for each nested property
        var sortedTypeToIndex = new Dictionary<ITypeSymbol, int>(SymbolEqualityComparer.Default);
        for (var i = 0; i < validTargets.Count; i++)
            sortedTypeToIndex[validTargets[i].TargetType] = i;

        // Types referenced from nested properties or collection elements — their
        // Bind_ method must be emitted even when the caller never binds them
        // directly (the parent's generated code calls Bind_&lt;idx&gt;).
        var referencedNestedTypes = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);

        foreach (var target in validTargets)
        {
            foreach (var prop in target.Properties)
            {
                if (
                    prop.ScalarKind == ScalarKind.Nested
                    && prop.NestedType is not null
                    && sortedTypeToIndex.TryGetValue(prop.NestedType, out var nestedIdx)
                )
                {
                    // A nested type without any usable construction path (no public
                    // parameterless constructor and no detectable primary
                    // constructor) cannot produce a Bind_N method — referencing it
                    // would emit CS0103. Skip the property and report instead.
                    if (!CanConstruct(validTargets[nestedIdx]))
                    {
                        ReportAll(
                            context,
                            NestedTypeLocations(prop.NestedType),
                            Diagnostics.MissingPublicParameterlessConstructor,
                            prop.NestedType.ToDisplayString()
                        );
                        continue;
                    }

                    prop.NestedModelIndex = nestedIdx;
                    referencedNestedTypes.Add(prop.NestedType);
                }
            }
        }

        // Also fill collection element nested indices (recursively — nested classes
        // can appear at any collection depth, e.g. Dictionary<string, List<SubConfig>>)
        foreach (var target in validTargets)
        {
            foreach (var prop in target.Properties)
            {
                CollectReferencedNestedElementTypes(
                    prop.ElementBinding,
                    sortedTypeToIndex,
                    validTargets,
                    referencedNestedTypes,
                    context
                );
            }
        }

        // Phase 5: Render
        context.AddSource(
            "PicoCfgBindRegistrations.g.cs",
            SourceText.From(Render(validTargets, referencedNestedTypes), Encoding.UTF8)
        );
    }

    /// <summary>
    /// Discovers nested bindable targets from property types, breadth-first, down
    /// to <see cref="NestingDepthLimit"/> levels. Types at the boundary are added
    /// to <paramref name="targets"/> but their members are not expanded; they are
    /// returned in the truncated set so analysis can skip their unknown property
    /// graph instead of generating references to never-emitted bind methods.
    /// </summary>
    private static ISet<ITypeSymbol> DiscoverNestedTargets(
        Dictionary<ITypeSymbol, TargetRegistration> targets,
        SourceProductionContext context
    )
    {
        var queue = new Queue<ITypeSymbol>(targets.Keys);
        var depth = new Dictionary<ITypeSymbol, int>(SymbolEqualityComparer.Default);
        var truncated = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var reportedTruncated = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);

        foreach (var key in targets.Keys)
            depth[key] = 0;

        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            var currentDepth = depth.TryGetValue(type, out var d) ? d : 0;

            if (currentDepth >= NestingDepthLimit)
            {
                truncated.Add(type);
                if (reportedTruncated.Add(type))
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            Diagnostics.NestingTruncated,
                            Location.None,
                            NestingDepthLimit,
                            type.ToDisplayString()
                        )
                    );
                }
                continue;
            }

            if (type is not INamedTypeSymbol namedType)
                continue;

            foreach (var member in namedType.GetMembers())
            {
                if (member is not IPropertySymbol prop)
                    continue;

                // Skip compiler-synthesized members (e.g., record EqualityContract)
                // to avoid descending into framework types.
                if (prop.IsImplicitlyDeclared)
                    continue;

                if (
                    IsNestedBindableType(prop.Type)
                    && !targets.ContainsKey(prop.Type)
                    && !IsSilentlySkippablePropertyType(prop.Type)
                )
                {
                    var nestedType = (INamedTypeSymbol)prop.Type;
                    targets.Add(nestedType, new TargetRegistration(nestedType));
                    depth[nestedType] = currentDepth + 1;
                    queue.Enqueue(nestedType);
                }

                if (
                    TryGetCollectionKind(prop.Type, out _, out var elementType)
                    && elementType is not null
                )
                {
                    DiscoverNestedElementTargets(elementType, targets, depth, queue, currentDepth);
                }
            }
        }

        return truncated;
    }

    private static bool CanConstruct(TargetModel target) =>
        target.HasPublicParameterlessConstructor || target.HasPrimaryConstructor;

    private static IEnumerable<Location> NestedTypeLocations(ITypeSymbol type) =>
        type.Locations.Length > 0 ? [type.Locations[0]] : [Location.None];

    private static void CollectReferencedNestedElementTypes(
        ElementBindingModel? element,
        Dictionary<ITypeSymbol, int> sortedTypeToIndex,
        IReadOnlyList<TargetModel> validTargets,
        ISet<ITypeSymbol> referencedNestedTypes,
        SourceProductionContext context
    )
    {
        if (element is null)
            return;

        if (
            element.Kind == ScalarKind.Nested
            && element.Type is INamedTypeSymbol nestedType
            && sortedTypeToIndex.TryGetValue(nestedType, out var nestedIdx)
        )
        {
            if (!CanConstruct(validTargets[nestedIdx]))
            {
                // Unconstructible element type — skip the element binding instead
                // of emitting a reference to a never-emitted Bind_N (CS0103).
                ReportAll(
                    context,
                    NestedTypeLocations(element.Type),
                    Diagnostics.MissingPublicParameterlessConstructor,
                    nestedType.ToDisplayString()
                );
                return;
            }

            element.NestedModelIndex = nestedIdx;
            referencedNestedTypes.Add(nestedType);
        }

        CollectReferencedNestedElementTypes(
            element.Element,
            sortedTypeToIndex,
            validTargets,
            referencedNestedTypes,
            context
        );
    }

    /// <summary>
    /// Recursively discovers nested-class targets inside collection element types
    /// (e.g. the <c>SubConfig</c> in <c>Dictionary&lt;string, List&lt;SubConfig&gt;&gt;</c>).
    /// </summary>
    private static void DiscoverNestedElementTargets(
        ITypeSymbol type,
        Dictionary<ITypeSymbol, TargetRegistration> targets,
        Dictionary<ITypeSymbol, int> depth,
        Queue<ITypeSymbol> queue,
        int parentDepth
    )
    {
        if (IsNestedBindableType(type) && !targets.ContainsKey(type))
        {
            targets.Add(type, new TargetRegistration(type));
            depth[type] = parentDepth + 1;
            queue.Enqueue(type);
            return;
        }

        if (TryGetCollectionKind(type, out _, out var elementType) && elementType is not null)
        {
            DiscoverNestedElementTargets(elementType, targets, depth, queue, parentDepth);
        }
    }

    private static List<TargetModel> TopologicalSort(
        List<TargetModel> targets,
        SourceProductionContext context
    )
    {
        if (targets.Count <= 1)
            return targets;

        // Map type symbols to their positions
        var typeToIndex = new Dictionary<ITypeSymbol, int>(SymbolEqualityComparer.Default);
        for (var i = 0; i < targets.Count; i++)
            typeToIndex[targets[i].TargetType] = i;

        // Build adjacency: adj[dep] = [dependent, ...]  (nested types first!)
        var inDegree = new int[targets.Count];
        var adj = new List<int>[targets.Count];
        for (var i = 0; i < targets.Count; i++)
            adj[i] = new List<int>();

        for (var parentIdx = 0; parentIdx < targets.Count; parentIdx++)
        {
            var target = targets[parentIdx];
            foreach (var prop in target.Properties)
            {
                if (
                    typeToIndex.TryGetValue(prop.Type, out var nestedTypeIdx)
                    && nestedTypeIdx != parentIdx
                )
                {
                    // parentIdx depends on nestedTypeIdx being rendered first
                    adj[nestedTypeIdx].Add(parentIdx);
                    inDegree[parentIdx]++;
                }

                AddCollectionElementDependencies(
                    prop.ElementBinding,
                    parentIdx,
                    typeToIndex,
                    adj,
                    inDegree
                );
            }
        }

        // Kahn's algorithm
        var queue = new Queue<int>();
        for (var i = 0; i < targets.Count; i++)
        {
            if (inDegree[i] == 0)
                queue.Enqueue(i);
        }

        var sorted = new List<TargetModel>(targets.Count);
        while (queue.Count > 0)
        {
            var idx = queue.Dequeue();
            sorted.Add(targets[idx]);
            foreach (var dependent in adj[idx])
            {
                inDegree[dependent]--;
                if (inDegree[dependent] == 0)
                    queue.Enqueue(dependent);
            }
        }

        if (sorted.Count != targets.Count)
        {
            var cycleIndices = Enumerable
                .Range(0, targets.Count)
                .Where(i => inDegree[i] > 0)
                .ToArray();
            if (cycleIndices.Length > 0)
            {
                var cyclePath = FormatCyclePath(targets, adj, cycleIndices);
                context.ReportDiagnostic(
                    Diagnostic.Create(Diagnostics.CycleInNestedTypes, Location.None, cyclePath)
                );
            }
            return new List<TargetModel>(0); // Empty — don't generate
        }

        return sorted;
    }

    /// <summary>
    /// Adds topological dependency edges for nested-class elements inside collections
    /// (recursively — a nested class at any collection depth must render before its
    /// owning type).
    /// </summary>
    private static void AddCollectionElementDependencies(
        ElementBindingModel? element,
        int parentIdx,
        Dictionary<ITypeSymbol, int> typeToIndex,
        List<int>[] adj,
        int[] inDegree
    )
    {
        if (element is null)
            return;

        if (
            element.Kind == ScalarKind.Nested
            && typeToIndex.TryGetValue(element.Type, out var nestedIdx)
            && nestedIdx != parentIdx
        )
        {
            adj[nestedIdx].Add(parentIdx);
            inDegree[parentIdx]++;
        }

        AddCollectionElementDependencies(element.Element, parentIdx, typeToIndex, adj, inDegree);
    }

    private static string FormatCyclePath(
        IReadOnlyList<TargetModel> targets,
        List<int>[] adj,
        int[] cycleIndices
    )
    {
        var dependsOn = new List<int>[adj.Length];
        for (var i = 0; i < adj.Length; i++)
            dependsOn[i] = new List<int>();
        for (var dep = 0; dep < adj.Length; dep++)
            foreach (var dependent in adj[dep])
                dependsOn[dependent].Add(dep);

        var cycleSet = new HashSet<int>(cycleIndices);
        var start = cycleIndices[0];
        var path = new List<int>();
        var inPath = new HashSet<int>();

        var current = start;
        while (!inPath.Contains(current))
        {
            inPath.Add(current);
            path.Add(current);

            var next = dependsOn[current].Find(n => cycleSet.Contains(n) && !inPath.Contains(n));

            if (next < 0)
            {
                if (dependsOn[current].Contains(start))
                    path.Add(start);
                break;
            }
            current = next;
        }

        return string.Join(" → ", path.Select(i => targets[i].TargetType.Name));
    }
}
