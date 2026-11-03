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

    /// <summary>
    /// Fully-qualified interface this member has to be read through
    /// (<c>((IA)_inner).V</c>). Set when the service inherits one member name
    /// from more than one interface: on such a service the plain
    /// <c>_inner.V</c> is CS0229 (ambiguous), even when one member implements
    /// both.
    /// </summary>
    public string? ReceiverCast { get; init; }

    /// <summary>
    /// Set when the member has to be declared as an explicit interface
    /// implementation (<c>string IB.V</c>), because another member of the same
    /// service occupies the plain name with an incompatible signature.
    /// </summary>
    public string? ExplicitInterface { get; init; }

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
    private static readonly SymbolDisplayFormat Fq = SymbolDisplayFormat.FullyQualifiedFormat;

    /// <summary>
    /// Why a class member cannot be intercepted: the proxy is a subclass, and a
    /// member that is not <c>virtual</c>/<c>abstract</c>/<c>override</c> — or
    /// that is a <c>sealed override</c> — cannot be overridden again, so a
    /// decorated call can never reach the chain.
    /// </summary>
    internal const string NotOverridableReason =
        "a non-virtual or sealed member of a class, which a derived proxy cannot override; calls execute on the inherited implementation, and the emitted pass-through is never reached";

    /// <summary>
    /// Whether a derived proxy can route this member. Only class services care:
    /// an interface member is always implemented by the proxy. A
    /// <c>sealed override</c> is deliberately excluded: it is an override, but
    /// not one a further derived type may override, so emitting
    /// <c>public override</c> for it is CS0239 in the generated file.
    /// </summary>
    internal static bool IsOverridable(ISymbol member) =>
        member switch
        {
            IMethodSymbol method => !method.IsSealed
                && (method.IsVirtual || method.IsOverride || method.IsAbstract),
            IPropertySymbol property => !property.IsSealed
                && (property.IsVirtual || property.IsOverride || property.IsAbstract),
            IEventSymbol @event => !@event.IsSealed
                && (@event.IsVirtual || @event.IsOverride || @event.IsAbstract),
            _ => false,
        };

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
                    "an event; add and remove are forwarded to the inner instance"
                )
            );
            planned.Add(
                new PlannedMember(
                    PlannedMemberKind.Event,
                    null,
                    null,
                    @event,
                    MemberEmission.Delegated,
                    "event; forwarded to the inner instance"
                )
            );
        }

        // A class service inherits its base members instead of re-declaring
        // them, so anything un-interceptable up the chain has to be named.
        if (service.TypeKind == TypeKind.Class)
            ReportUnplannedClassMembers(service, planned, diagnostics);

        if (skipService)
            return null;

        return new ServicePlan(
            service,
            safeName,
            DegradeCollisions(
                service,
                safeName,
                ResolveNameCollisions(service, safeName, planned, diagnostics),
                diagnostics
            )
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

        // A class proxy derives from the service, so only an overridable member
        // can be routed. Emitting the rest as a plain `public` member hides the
        // base one (CS0108) and intercepts nothing a consumer can reach, because
        // `Wrap_*` returns the service type, not the proxy.
        if (service.TypeKind == TypeKind.Class && !IsOverridable(method))
            return Delegated(service, method, diagnostics, NotOverridableReason);

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
            return Delegated(
                service,
                method,
                diagnostics,
                "a ref return; the call is passed straight to the inner instance"
            );

        if (method.ReturnType.IsRefLikeType || method.Parameters.Any(p => p.Type.IsRefLikeType))
            return Delegated(
                service,
                method,
                diagnostics,
                "a ref struct parameter or return value; the call is passed straight to the inner instance"
            );

        if (IsUnsafeType(method.ReturnType) || method.Parameters.Any(p => IsUnsafeType(p.Type)))
            return Delegated(
                service,
                method,
                diagnostics,
                "a pointer parameter or return value; the call is passed straight to the inner instance"
            );

        if (method.TypeParameters.Any(tp => tp.AllowsRefLikeType))
            return Delegated(
                service,
                method,
                diagnostics,
                "a type parameter that allows ref struct; the call is passed straight to the inner instance"
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
                    "an indexer; the access is passed straight to the inner instance"
                )
            );
            return intercepted.DelegateTo("indexer");
        }

        if (service.TypeKind == TypeKind.Class && !IsOverridable(property))
        {
            diagnostics.Add(
                Diagnostic.Create(
                    PicoAopDiag.MemberDelegated,
                    Location.None,
                    property.Name,
                    service.Name,
                    NotOverridableReason
                )
            );
            return intercepted.DelegateTo(NotOverridableReason);
        }

        if (property.Type.IsRefLikeType || IsUnsafeType(property.Type))
        {
            diagnostics.Add(
                Diagnostic.Create(
                    PicoAopDiag.MemberDelegated,
                    Location.None,
                    property.Name,
                    service.Name,
                    "a ref struct or pointer property type; the access is passed straight to the inner instance"
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
    /// <summary>
    /// Two members of one service can occupy the same C# name with incompatible
    /// signatures — <c>IA { int V { get; } }</c> and <c>IB { string V { get; } }</c>
    /// on <c>IC : IA, IB</c> — and a class then has to implement them explicitly.
    /// One signature keeps the plain name (it satisfies every interface that
    /// declares that exact signature), the others are spelled
    /// <c>Interface.Member</c>. Interception cannot tell them apart (they
    /// generate one invocation name), so they are delegated and PICO113 names
    /// them.
    /// <para>
    /// Independently of that, every member the service inherits through more than
    /// one interface is read through the interface that declares it
    /// (<c>((IA)_inner).V</c>): the plain <c>_inner.V</c> is CS0229 on <c>IC</c>,
    /// even when a single member implements both interfaces.
    /// </para>
    /// </summary>
    private static List<PlannedMember> ResolveNameCollisions(
        INamedTypeSymbol service,
        string safeName,
        List<PlannedMember> planned,
        List<Diagnostic> diagnostics
    )
    {
        // A class declares its own members: one name, one slot, no base
        // interface to implement explicitly.
        if (service.TypeKind != TypeKind.Interface)
            return planned;

        var bySlot = planned
            .GroupBy(member => SlotKey(SymbolOf(member)))
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        // Members that must be spelled `Interface.Member` because a different
        // signature owns the plain name, and every member of such a slot (the
        // chain cannot tell them apart, so none of them is routed).
        var explicitMembers = new HashSet<PlannedMember>();
        var delegatedMembers = new HashSet<PlannedMember>();
        foreach (var slot in bySlot.Values.Where(group => group.Count > 1))
        {
            // Members with one signature need one implicit implementation only:
            // it satisfies every interface that declares that signature.
            var distinct = new List<PlannedMember>();
            var signatures = new HashSet<string>(StringComparer.Ordinal);
            foreach (var candidate in slot)
                if (signatures.Add(SignatureKey(SymbolOf(candidate))))
                    distinct.Add(candidate);

            if (distinct.Count < 2)
                continue;

            diagnostics.Add(
                Diagnostic.Create(
                    PicoAopDiag.MemberShapeCollision,
                    Location.None,
                    string.Join("', '", distinct.Select(m => m.DisplayName)),
                    service.Name,
                    InvocationNames(safeName, distinct[0]).First()
                )
            );

            foreach (var candidate in distinct)
                delegatedMembers.Add(candidate);
            foreach (var candidate in distinct.Skip(1))
                explicitMembers.Add(candidate);
        }

        var ambiguousSlots = AmbiguousSlots(service);

        return
        [
            .. planned.Select(member =>
            {
                var declaring = DeclaringTypeName(member);
                var isExplicit = explicitMembers.Contains(member);
                var readThrough = isExplicit || ambiguousSlots.Contains(SlotKey(SymbolOf(member)));

                var mapped = member with
                {
                    ReceiverCast = readThrough ? declaring : null,
                    ExplicitInterface = isExplicit ? declaring : null,
                };

                return delegatedMembers.Contains(member) && mapped.IsIntercepted
                    ? mapped.DelegateTo(
                        "another member of a different base interface owns the same name and parameters; the call is passed straight to the inner instance"
                    )
                    : mapped;
            }),
        ];
    }

    /// <summary>
    /// The signature slots the service inherits from more than one interface.
    /// Reading such a member off the service itself is ambiguous (CS0229), so the
    /// proxy has to go through the declaring interface — including the case where
    /// one member implements both interfaces.
    /// </summary>
    private static HashSet<string> AmbiguousSlots(INamedTypeSymbol service)
    {
        var owners = new Dictionary<string, HashSet<INamedTypeSymbol>>(StringComparer.Ordinal);
        foreach (var source in EnumerateMemberSources(service))
        {
            foreach (var member in source.GetMembers())
            {
                if (!IsPlannedShape(member))
                    continue;

                var key = SlotKey(member);
                if (!owners.TryGetValue(key, out var declaring))
                    owners[key] = declaring = new HashSet<INamedTypeSymbol>(
                        SymbolEqualityComparer.Default
                    );
                declaring.Add(source);
            }
        }

        return [.. owners.Where(pair => pair.Value.Count > 1).Select(pair => pair.Key)];
    }

    /// <summary>
    /// The name the compiler resolves: member name plus, for methods and
    /// indexers, the type parameters and the parameter types. Two members in one
    /// slot cannot both be declared with the plain member name, whatever their
    /// return types are.
    /// </summary>
    private static string SlotKey(ISymbol member) =>
        member switch
        {
            IMethodSymbol method =>
                $"M:{method.Name}:{method.TypeParameters.Length}:{ParameterTypes(method.Parameters, method.TypeParameters)}",
            IPropertySymbol { IsIndexer: true } indexer =>
                $"P:this[]:{ParameterTypes(indexer.Parameters)}",
            IPropertySymbol property => $"P:{property.Name}",
            IEventSymbol @event => $"E:{@event.Name}",
            _ => $"{member.Kind}:{member.Name}",
        };

    /// <summary>Whether a member of the service is one the proxy has to declare.</summary>
    private static bool IsPlannedShape(ISymbol member) =>
        member
            is (
                IMethodSymbol
                {
                    MethodKind: MethodKind.Ordinary,
                    DeclaredAccessibility: Accessibility.Public,
                    IsStatic: false
                }
                or IPropertySymbol { DeclaredAccessibility: Accessibility.Public, IsStatic: false }
                or IEventSymbol { DeclaredAccessibility: Accessibility.Public, IsStatic: false }
            );

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
                        "its generated invocation name collides with another member; the call is not routed"
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

        if (member.Property is { } property)
        {
            if (property.GetMethod?.DeclaredAccessibility == Accessibility.Public)
                yield return $"Invocation_{safeName}_{property.Name}_Getter";
            if (property.SetMethod?.DeclaredAccessibility == Accessibility.Public)
                yield return $"Invocation_{safeName}_{property.Name}_Setter";
            yield break;
        }

        // An event is always delegated, but the collision pass still has to name
        // it instead of dereferencing the property slot (NullReferenceException).
        yield return $"Invocation_{safeName}_{member.Event!.Name}";
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
                .Where(IsPlannedShape)
        );

    private static IEnumerable<IPropertySymbol> EnumerateProperties(INamedTypeSymbol service) =>
        Distinct(
            EnumerateMemberSources(service)
                .SelectMany(source => source.GetMembers())
                .OfType<IPropertySymbol>()
                .Where(IsPlannedShape)
        );

    private static IEnumerable<IEventSymbol> EnumerateEvents(INamedTypeSymbol service) =>
        Distinct(
            EnumerateMemberSources(service)
                .SelectMany(source => source.GetMembers())
                .OfType<IEventSymbol>()
                .Where(IsPlannedShape)
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

    /// <summary>
    /// Reports every member of a class service that the plan does not cover but
    /// that a caller can still reach through the service type. The proxy derives
    /// from the class, so an inherited member needs no re-declaration — which is
    /// also why an inherited <c>virtual</c> member was planned by nobody and
    /// reported by nobody: the call silently ran the base implementation on the
    /// proxy instance. A <c>protected</c> member of the service itself fell into
    /// the same hole (<see cref="IsPlannedShape"/> requires public, and a derived
    /// implementation cannot widen the accessibility — CS0507).
    /// <para>
    /// Walking the base types and emitting overrides for what is found there is a
    /// larger change (the invocation struct, the chain fields and the plan would
    /// all have to accept a member the service does not declare); until then one
    /// PICO111 per skipped member keeps the hole from being silent
    /// (review-2026-11-02/rev-aop-3c.md, D5).
    /// </para>
    /// </summary>
    private static void ReportUnplannedClassMembers(
        INamedTypeSymbol service,
        List<PlannedMember> planned,
        List<Diagnostic> diagnostics
    )
    {
        var plannedSlots = new HashSet<string>(
            planned.Select(member => SlotKey(SymbolOf(member))),
            StringComparer.Ordinal
        );

        // Nearest declaration first: a call binds to the most derived override,
        // so once a type in the chain declares the slot, the declarations behind
        // it are covered by it.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (
            var type = service;
            type is not null && type.SpecialType != SpecialType.System_Object;
            type = type.BaseType
        )
        {
            var isService = SymbolEqualityComparer.Default.Equals(type, service);
            foreach (var member in type.GetMembers())
            {
                if (!IsRoutableCandidate(member))
                    continue;

                var slot = SlotKey(member);
                if (!seen.Add(slot))
                    continue;

                // A member the service declares itself is the plan's business: it
                // is either planned, or it was reported while being planned.
                if (isService && plannedSlots.Contains(slot))
                    continue;

                // Nothing is emitted for either branch below — the plan covers
                // only the service's own public members — so neither may carry
                // the "emitted pass-through is never reached" claim of
                // NotOverridableReason. That claim is true only where a `new`
                // member really exists (D4), which is not the case here.
                diagnostics.Add(
                    Diagnostic.Create(
                        PicoAopDiag.MemberDelegated,
                        Location.None,
                        member.Name,
                        service.Name,
                        isService
                            ? "a non-public member of the service, which a derived proxy cannot re-declare without changing its accessibility; calls execute on the inherited implementation, and nothing is emitted for it"
                            : $"inherited from '{type.Name}', which the proxy does not re-declare; calls execute on the inherited implementation, and nothing is emitted for it"
                    )
                );
            }
        }
    }

    /// <summary>
    /// A member the proxy might have routed had it been planned: an ordinary
    /// instance method, property or event that is <c>virtual</c>, <c>abstract</c>
    /// or an override, and that a derived type can see.
    /// </summary>
    private static bool IsRoutableCandidate(ISymbol member) =>
        member switch
        {
            IMethodSymbol { MethodKind: MethodKind.Ordinary, IsStatic: false } method =>
                IsVisibleToDerived(method.DeclaredAccessibility)
                    && (method.IsVirtual || method.IsAbstract || method.IsOverride),
            IPropertySymbol { IsStatic: false } property => IsVisibleToDerived(
                property.DeclaredAccessibility
            ) && (property.IsVirtual || property.IsAbstract || property.IsOverride),
            IEventSymbol { IsStatic: false } @event => IsVisibleToDerived(
                @event.DeclaredAccessibility
            ) && (@event.IsVirtual || @event.IsAbstract || @event.IsOverride),
            _ => false,
        };

    /// <summary>
    /// Whether a member declared in a base type is reachable from the proxy type
    /// — and therefore worth reporting when it is skipped.
    /// </summary>
    private static bool IsVisibleToDerived(Accessibility accessibility) =>
        accessibility
            is Accessibility.Public
                or Accessibility.Protected
                or Accessibility.ProtectedOrInternal;

    /// <summary>
    /// The full C# signature of a member — return type included, and the
    /// accessors for a property. Without the return type, <c>IA { int V {get;} }</c>
    /// and <c>IB { string V {get;} }</c> look like one member and the second is
    /// silently dropped from the proxy (CS0738/CS0535 in generated code).
    /// <para>
    /// A generic method keys on its type-parameter constraints too. They are not
    /// part of the C# signature, so two members can occupy one name slot while
    /// constraining <c>T</c> differently — but they are different
    /// implementations, and one <c>where T : class</c> cannot satisfy
    /// <c>M&lt;T&gt;() where T : struct</c> (CS0425). Keying on them turns such a
    /// pair into a reported PICO113 collision instead of a dropped member.
    /// </para>
    /// <para>
    /// Every type-parameter reference in the key is positional (see
    /// <see cref="PositionalType"/>): C# keys a signature on the position of a
    /// type parameter, not on the name a declaration chose, so
    /// <c>M&lt;T&gt;(T)</c> and <c>M&lt;U&gt;(U)</c> are one member.
    /// </para>
    /// </summary>
    private static string SignatureKey(ISymbol member) =>
        member switch
        {
            IMethodSymbol method =>
                $"M:{method.Name}:{method.TypeParameters.Length}:{ParameterTypes(method.Parameters, method.TypeParameters)}:{PositionalType(method.ReturnType, method.TypeParameters)}:{ConstraintKey(method)}",
            IPropertySymbol property =>
                $"P:{property.Name}:{ParameterTypes(property.Parameters)}:{PositionalType(property.Type)}:{(property.GetMethod is null ? string.Empty : "g")}{(property.SetMethod is null ? string.Empty : "s")}",
            IEventSymbol @event => $"E:{@event.Name}:{PositionalType(@event.Type)}",
            _ => $"{member.Kind}:{member.Name}",
        };

    /// <summary>
    /// The constraint set of one generic method as a comparable string
    /// (<c>class|global::System.IComparable&lt;!0&gt;+new</c>); empty for a
    /// non-generic method.
    /// <para>
    /// A constraint list is a set, so the constraint types are sorted before
    /// they are joined: <c>where T : A, B</c> and <c>where T : B, A</c>
    /// constrain the type parameter identically. Declaration order used to be
    /// kept, which turned that pair into a PICO113 for a collision that does not
    /// exist and delegated both members — interception lost with no compiler
    /// error to show it.
    /// </para>
    /// </summary>
    private static string ConstraintKey(IMethodSymbol method) =>
        method.TypeParameters.Length == 0
            ? string.Empty
            : string.Join(
                "|",
                method.TypeParameters.Select(typeParameter =>
                    string.Join(
                        "+",
                        new[]
                        {
                            TypeParameterConstraintKind(typeParameter),
                            string.Join(
                                ",",
                                typeParameter
                                    .ConstraintTypes.Select(type =>
                                        PositionalType(type, method.TypeParameters)
                                    )
                                    .OrderBy(text => text, StringComparer.Ordinal)
                            ),
                            typeParameter.HasConstructorConstraint ? "new" : string.Empty,
                            typeParameter.AllowsRefLikeType ? "allows ref struct" : string.Empty,
                        }
                    )
                )
            );

    /// <summary>The primary constraint keyword of one type parameter, if any.</summary>
    private static string TypeParameterConstraintKind(ITypeParameterSymbol typeParameter) =>
        typeParameter.HasUnmanagedTypeConstraint ? "unmanaged"
        : typeParameter.HasValueTypeConstraint ? "struct"
        : typeParameter.HasReferenceTypeConstraint
            ? typeParameter.ReferenceTypeConstraintNullableAnnotation
                == NullableAnnotation.Annotated
                    ? "class?"
                : "class"
        : typeParameter.HasNotNullConstraint ? "notnull"
        : string.Empty;

    /// <summary>
    /// What the compiler considers one name: the member name plus, for methods
    /// and indexers, the type parameters and the parameter types. Two members in
    /// one slot cannot both be declared with the plain member name, whatever
    /// their return types are.
    /// </summary>
    private static string SlotKey(PlannedMember member)
    {
        if (member.Method is { } method)
            return $"M:{method.Name}:{method.TypeParameters.Length}:{ParameterTypes(method.Parameters, method.TypeParameters)}";
        if (member.Property is { IsIndexer: true } indexer)
            return $"P:this[]:{ParameterTypes(indexer.Parameters)}";
        if (member.Property is { } property)
            return $"P:{property.Name}";
        return $"E:{member.Event!.Name}";
    }

    private static string ParameterTypes(
        ImmutableArray<IParameterSymbol> parameters,
        ImmutableArray<ITypeParameterSymbol> typeParameters = default
    ) =>
        string.Join(
            ",",
            parameters.Select(p => $"{p.RefKind}:{PositionalType(p.Type, typeParameters)}")
        );

    /// <summary>
    /// A type as it takes part in a signature slot: a reference to one of the
    /// member's own type parameters is rendered by <em>position</em>
    /// (<c>!0</c>, <c>!1</c>), because position is what C# keys a signature on.
    /// Two base interfaces that declare the same member with different type
    /// parameter names — <c>M&lt;T&gt;(T)</c> and <c>M&lt;U&gt;(U)</c> — are one
    /// member that one implementation satisfies; printing the names split them
    /// into two proxy members (CS0111 + CS0121 in the generated file, zero
    /// diagnostics).
    /// <para>
    /// The rendering walks <see cref="ISymbol.ToDisplayParts"/> and substitutes
    /// the parts that *are* one of the method's own type parameters, so a name
    /// that also happens to be a type in scope cannot be mistaken for one. The
    /// positional form is a key only: nothing emitted into the generated file is
    /// built from this string (the emitters print the symbols' own names).
    /// </para>
    /// </summary>
    private static string PositionalType(
        ITypeSymbol type,
        ImmutableArray<ITypeParameterSymbol> typeParameters = default
    )
    {
        // `default(ImmutableArray<T>)` is the *uninitialized* array, and reading
        // .Length off it throws; properties and events pass no type parameters.
        if (typeParameters.IsDefaultOrEmpty)
            return type.ToDisplayString(Fq);

        var sb = new StringBuilder();
        foreach (var part in type.ToDisplayParts(Fq))
        {
            var index = -1;
            if (part.Symbol is ITypeParameterSymbol typeParameter)
                for (var i = 0; i < typeParameters.Length; i++)
                    if (SymbolEqualityComparer.Default.Equals(typeParameters[i], typeParameter))
                    {
                        index = i;
                        break;
                    }

            if (index < 0)
                sb.Append(part.ToString());
            else
                sb.Append('!').Append(index);
        }
        return sb.ToString();
    }

    private static ISymbol SymbolOf(PlannedMember member) =>
        member.Method ?? (ISymbol?)member.Property ?? member.Event!;

    private static string DeclaringTypeName(PlannedMember member) =>
        SymbolOf(member).ContainingType.ToDisplayString(Fq);

    private static bool IsUnsafeType(ITypeSymbol type) =>
        type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer;
}
