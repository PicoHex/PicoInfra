namespace PicoAop.Gen.Emission;

/// <summary>How one member of an intercepted service is emitted.</summary>
internal enum MemberEmission
{
    /// <summary>Invocation struct plus the interceptor chain.</summary>
    Intercepted,

    /// <summary>Direct pass-through to the inner instance (a diagnostic says why).</summary>
    Delegated,
}

internal enum PlannedMemberKind
{
    Method,
    Property,
    Indexer,
    Event,
}

/// <summary>One member of the intercepted service and how it will be emitted.</summary>
internal sealed record PlannedMember(
    PlannedMemberKind Kind,
    IMethodSymbol? Method,
    IPropertySymbol? Property,
    IEventSymbol? Event,
    MemberEmission Emission,
    string? DelegationReason = null
)
{
    public bool IsIntercepted => Emission == MemberEmission.Intercepted;

    public string DisplayName =>
        Kind switch
        {
            PlannedMemberKind.Method =>
                $"{Method!.ContainingType.Name}.{Method.Name}{InvocationEmitter.TypeParameterList(Method)}({string.Join(", ", Method.Parameters.Select(p => p.Type.ToDisplayString()))})",
            PlannedMemberKind.Indexer => $"{Property!.ContainingType.Name}.this[]",
            PlannedMemberKind.Property => $"{Property!.ContainingType.Name}.{Property.Name}",
            _ => $"{Event!.ContainingType.Name}.{Event.Name}",
        };

    public PlannedMember DelegateTo(string reason) =>
        this with
        {
            Emission = MemberEmission.Delegated,
            DelegationReason = reason,
        };
}

/// <summary>
/// The full emission plan for one intercepted service: every member the proxy
/// has to declare, and whether it routes through the interceptor chain or is
/// passed straight to the inner instance.
/// </summary>
internal sealed class ServicePlan(
    INamedTypeSymbol service,
    string safeName,
    IReadOnlyList<PlannedMember> members
)
{
    public INamedTypeSymbol Service { get; } = service;
    public string SafeName { get; } = safeName;
    public IReadOnlyList<PlannedMember> Members { get; } = members;
}

/// <summary>
/// Decides, once, which members the proxy can intercept and which it can only
/// delegate — and reports a diagnostic for every member it cannot emit at all.
/// The invocation-struct emitter and the proxy emitter both read this plan, so
/// they can no longer disagree about a member (that disagreement is what
/// produced CS1729/CS0103 for properties: the proxy routed through delegates
/// the struct side never emitted).
/// </summary>
internal static class ServicePlanner
{
    public static ServicePlan? Build(INamedTypeSymbol service, List<Diagnostic> diagnostics)
    {
        var safeName = InvocationEmitter.BuildSafeServiceName(service);
        var planned = new List<PlannedMember>();
        var skipService = false;

        foreach (var method in EnumerateMethods(service))
        {
            var member = PlanMethod(service, method, diagnostics);
            if (member is null)
            {
                skipService = true;
                continue;
            }
            planned.Add(member);
        }

        foreach (var property in EnumerateProperties(service))
        {
            if (property.SetMethod is { IsInitOnly: true })
            {
                // An init accessor can only be written from an object
                // initializer, so there is nothing to route it to. A proxy
                // without the accessor fails to implement the interface
                // (CS0535), so the whole service is left unintercepted.
                diagnostics.Add(
                    Diagnostic.Create(
                        PicoAopDiag.InitOnlyProperty,
                        Location.None,
                        property.Name,
                        service.Name
                    )
                );
                skipService = true;
                continue;
            }

            planned.Add(PlanProperty(service, property, diagnostics));
        }

        foreach (var @event in EnumerateEvents(service))
        {
            diagnostics.Add(
                Diagnostic.Create(
                    PicoAopDiag.MemberDelegated,
                    Location.None,
                    @event.Name,
                    service.Name,
                    "an event"
                )
            );
            planned.Add(
                new PlannedMember(
                    PlannedMemberKind.Event,
                    null,
                    null,
                    @event,
                    MemberEmission.Delegated,
                    "event"
                )
            );
        }

        if (skipService)
            return null;

        return new ServicePlan(
            service,
            safeName,
            DegradeCollisions(service, safeName, planned, diagnostics)
        );
    }

    /// <summary>
    /// Method/accessor shapes the proxy cannot route, in the order the checks
    /// matter. Returns <c>null</c> only for a member that cannot be emitted at
    /// all (currently: none — everything un-interceptable is delegated).
    /// </summary>
    private static PlannedMember? PlanMethod(
        INamedTypeSymbol service,
        IMethodSymbol method,
        List<Diagnostic> diagnostics
    )
    {
        PlannedMember Intercepted() =>
            new(PlannedMemberKind.Method, method, null, null, MemberEmission.Intercepted);

        if (method.Parameters.Any(p => p.RefKind != RefKind.None))
        {
            diagnostics.Add(
                Diagnostic.Create(
                    PicoAopDiag.RefOutMethod,
                    Location.None,
                    method.Name,
                    service.Name
                )
            );
            return Intercepted().DelegateTo("ref/out/in parameter");
        }

        if (method.ReturnsByRef || method.ReturnsByRefReadonly)
            return Delegated(service, method, diagnostics, "a ref return");

        if (method.ReturnType.IsRefLikeType || method.Parameters.Any(p => p.Type.IsRefLikeType))
            return Delegated(
                service,
                method,
                diagnostics,
                "a ref struct parameter or return value"
            );

        if (IsUnsafeType(method.ReturnType) || method.Parameters.Any(p => IsUnsafeType(p.Type)))
            return Delegated(service, method, diagnostics, "a pointer parameter or return value");

        if (method.TypeParameters.Any(tp => tp.AllowsRefLikeType))
            return Delegated(
                service,
                method,
                diagnostics,
                "a type parameter that allows ref struct"
            );

        return Intercepted();
    }

    private static PlannedMember PlanProperty(
        INamedTypeSymbol service,
        IPropertySymbol property,
        List<Diagnostic> diagnostics
    )
    {
        var kind = property.IsIndexer ? PlannedMemberKind.Indexer : PlannedMemberKind.Property;
        var intercepted = new PlannedMember(kind, null, property, null, MemberEmission.Intercepted);

        if (property.IsIndexer)
        {
            diagnostics.Add(
                Diagnostic.Create(
                    PicoAopDiag.MemberDelegated,
                    Location.None,
                    "this[]",
                    service.Name,
                    "an indexer"
                )
            );
            return intercepted.DelegateTo("indexer");
        }

        if (property.Type.IsRefLikeType || IsUnsafeType(property.Type))
        {
            diagnostics.Add(
                Diagnostic.Create(
                    PicoAopDiag.MemberDelegated,
                    Location.None,
                    property.Name,
                    service.Name,
                    "a ref struct or pointer property type"
                )
            );
            return intercepted.DelegateTo("ref struct property type");
        }

        return intercepted;
    }

    private static PlannedMember Delegated(
        INamedTypeSymbol service,
        IMethodSymbol method,
        List<Diagnostic> diagnostics,
        string reason
    )
    {
        diagnostics.Add(
            Diagnostic.Create(
                PicoAopDiag.MemberDelegated,
                Location.None,
                method.Name,
                service.Name,
                reason
            )
        );
        return new PlannedMember(
            PlannedMemberKind.Method,
            method,
            null,
            null,
            MemberEmission.Delegated,
            reason
        );
    }

    /// <summary>
    /// Two members whose generated invocation names are identical would emit
    /// CS0101 (struct), CS0102 (delegate field) or CS0111 (override). The names
    /// are built from <c>p.Type.Name</c>, which drops type arguments, so
    /// <c>Take(List&lt;int&gt;)</c> and <c>Take(List&lt;string&gt;)</c> collide.
    /// A correct naming scheme (arity plus <c>ToDisplayString</c>) is a larger
    /// change; until then the colliding members are delegated and reported, so
    /// the generated file still compiles.
    /// </summary>
    private static List<PlannedMember> DegradeCollisions(
        INamedTypeSymbol service,
        string safeName,
        List<PlannedMember> planned,
        List<Diagnostic> diagnostics
    )
    {
        var names = new Dictionary<string, List<PlannedMember>>(StringComparer.Ordinal);
        foreach (var member in planned.Where(m => m.IsIntercepted))
        {
            foreach (var name in InvocationNames(safeName, member))
            {
                if (!names.TryGetValue(name, out var owners))
                    names[name] = owners = new List<PlannedMember>();
                if (!owners.Any(owner => ReferenceEquals(owner, member)))
                    owners.Add(member);
            }
        }

        // Members are records, so the set compares by value; the instances
        // added here are the same ones the plan holds, which is what matters.
        var colliding = new HashSet<PlannedMember>();
        foreach (var pair in names.Where(kvp => kvp.Value.Count > 1))
        {
            var name = pair.Key;
            var owners = pair.Value;
            diagnostics.Add(
                Diagnostic.Create(
                    PicoAopDiag.MemberShapeCollision,
                    Location.None,
                    string.Join("', '", owners.Select(o => o.DisplayName)),
                    service.Name,
                    name
                )
            );
            foreach (var owner in owners)
                colliding.Add(owner);
        }

        if (colliding.Count == 0)
            return planned;

        return
        [
            .. planned.Select(member =>
                colliding.Contains(member)
                    ? member.DelegateTo(
                        "its generated invocation name collides with another member"
                    )
                    : member
            ),
        ];
    }

    private static IEnumerable<string> InvocationNames(string safeName, PlannedMember member)
    {
        if (member.Method is { } method)
        {
            yield return InvocationEmitter.BuildStructName(safeName, method);
            yield break;
        }

        var property = member.Property!;
        if (property.GetMethod?.DeclaredAccessibility == Accessibility.Public)
            yield return $"Invocation_{safeName}_{property.Name}_Getter";
        if (property.SetMethod?.DeclaredAccessibility == Accessibility.Public)
            yield return $"Invocation_{safeName}_{property.Name}_Setter";
    }

    /// <summary>
    /// The service's members as the proxy must see them. For an interface this
    /// includes members inherited from base interfaces: the proxy implements
    /// the interface, so leaving them out is CS0535.
    /// </summary>
    private static IEnumerable<IMethodSymbol> EnumerateMethods(INamedTypeSymbol service) =>
        Distinct(
            EnumerateMemberSources(service)
                .SelectMany(source => source.GetMembers())
                .OfType<IMethodSymbol>()
                .Where(m =>
                    m.MethodKind == MethodKind.Ordinary
                    && m.DeclaredAccessibility == Accessibility.Public
                    && !m.IsStatic
                )
        );

    private static IEnumerable<IPropertySymbol> EnumerateProperties(INamedTypeSymbol service) =>
        Distinct(
            EnumerateMemberSources(service)
                .SelectMany(source => source.GetMembers())
                .OfType<IPropertySymbol>()
                .Where(p => p.DeclaredAccessibility == Accessibility.Public && !p.IsStatic)
        );

    private static IEnumerable<IEventSymbol> EnumerateEvents(INamedTypeSymbol service) =>
        Distinct(
            EnumerateMemberSources(service)
                .SelectMany(source => source.GetMembers())
                .OfType<IEventSymbol>()
                .Where(e => e.DeclaredAccessibility == Accessibility.Public && !e.IsStatic)
        );

    /// <summary>
    /// netstandard2.0 has no <c>Enumerable.DistinctBy</c>, and the same member
    /// can arrive twice through a diamond of base interfaces.
    /// </summary>
    private static IEnumerable<T> Distinct<T>(IEnumerable<T> members)
        where T : ISymbol
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in members)
            if (seen.Add(SignatureKey(member)))
                yield return member;
    }

    /// <summary>
    /// The service itself, plus — for interfaces — every base interface. A
    /// class proxy derives from the class, so inherited class members need no
    /// re-declaration.
    /// </summary>
    private static IEnumerable<INamedTypeSymbol> EnumerateMemberSources(INamedTypeSymbol service)
    {
        yield return service;
        if (service.TypeKind != TypeKind.Interface)
            yield break;

        foreach (var baseInterface in service.AllInterfaces)
            yield return baseInterface;
    }

    private static string SignatureKey(ISymbol member) =>
        member switch
        {
            IMethodSymbol method =>
                $"M:{method.Name}:{method.TypeParameters.Length}:{string.Join(",", method.Parameters.Select(p => $"{p.RefKind}:{p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}"))}",
            IPropertySymbol property =>
                $"P:{property.Name}:{property.Parameters.Length}:{string.Join(",", property.Parameters.Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)))}",
            IEventSymbol @event => $"E:{@event.Name}",
            _ => $"{member.Kind}:{member.Name}",
        };

    private static bool IsUnsafeType(ITypeSymbol type) =>
        type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer;
}
