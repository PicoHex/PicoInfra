namespace PicoAop.Gen.Emission;

internal static class ProxyEmitter
{
    private static readonly SymbolDisplayFormat Fq = SymbolDisplayFormat.FullyQualifiedFormat;

    /// <summary>
    /// Emits the decorator class for one service and one interceptor chain.
    /// Everything it declares comes from <paramref name="plan"/>, so the set of
    /// members routed through the chain is exactly the set the invocation
    /// structs were emitted for.
    /// </summary>
    public static string EmitInterceptedClass(ServicePlan plan, List<ITypeSymbol> interceptorTypes)
    {
        if (interceptorTypes.Count == 0)
            return string.Empty;

        var service = plan.Service;
        var safeName = plan.SafeName;
        var svcFullName = service.ToDisplayString(Fq);
        var intSuffix = InterceptorSuffix(interceptorTypes);
        var className = $"{PicoAopNames.InterceptedPrefix}{safeName}_{intSuffix}";
        var isClassType = service.TypeKind == TypeKind.Class;
        var lastStep = interceptorTypes.Count - 1;

        var sb = new StringBuilder();
        sb.AppendLine($"sealed class {className} : {svcFullName}");
        sb.AppendLine("{");
        sb.AppendLine($"    private readonly {svcFullName} _inner;");
        for (var i = 0; i < interceptorTypes.Count; i++)
            sb.AppendLine($"    private readonly {interceptorTypes[i].ToDisplayString(Fq)} _i{i};");
        sb.AppendLine();

        var ctorParams =
            $"({svcFullName} inner"
            + string.Concat(
                interceptorTypes.Select(
                    (t, i) =>
                        $", {t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} i{i}"
                )
            )
            + ")";
        sb.AppendLine($"    public {className}{ctorParams}");
        sb.AppendLine("    {");
        sb.AppendLine("        _inner = inner;");
        for (var i = 0; i < interceptorTypes.Count; i++)
            sb.AppendLine($"        _i{i} = i{i};");
        sb.AppendLine("    }");
        sb.AppendLine();

        // Static delegate chains. Generic members keep theirs inside the
        // invocation struct instead: their delegates mention the method's type
        // parameters, which do not exist at class scope.
        var emittedChains = false;
        foreach (var member in plan.Members.Where(m => m.IsIntercepted))
        {
            if (member.Method is { } method)
            {
                if (method.TypeParameters.Length > 0)
                    continue;
                var invocationType =
                    $"{InvocationEmitter.BuildStructName(safeName, method)}_{intSuffix}";
                foreach (
                    var line in EmitMethodChainFields(
                        method,
                        invocationType,
                        $"s_{InvocationEmitter.BuildDelegateBase(method)}",
                        interceptorTypes.Count,
                        "private"
                    )
                )
                    sb.AppendLine(line);
                emittedChains = true;
            }
            else if (member.Property is { } property)
            {
                foreach (
                    var line in EmitPropertyChainFields(
                        safeName,
                        property,
                        intSuffix,
                        interceptorTypes
                    )
                )
                    sb.AppendLine(line);
                emittedChains = true;
            }
        }
        if (emittedChains)
            sb.AppendLine();

        foreach (var member in plan.Members)
        {
            switch (member.Kind)
            {
                case PlannedMemberKind.Method:
                    EmitMethod(sb, plan, member, interceptorTypes, intSuffix, isClassType);
                    break;
                case PlannedMemberKind.Property:
                    EmitProperty(sb, plan, member, interceptorTypes, intSuffix, isClassType);
                    break;
                case PlannedMemberKind.Indexer:
                    EmitIndexer(sb, member, isClassType);
                    break;
                case PlannedMemberKind.Event:
                    EmitEvent(sb, member, isClassType);
                    break;
            }
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// The interceptor chain for one method, as static <c>Func</c> fields:
    /// <c>Step0</c> calls the target, every further step wraps the previous one
    /// in one interceptor, outer-most last.
    /// </summary>
    public static IEnumerable<string> EmitMethodChainFields(
        IMethodSymbol method,
        string invocationType,
        string fieldNamePrefix,
        int interceptorCount,
        string accessibility
    )
    {
        var shape = ShapeFor(method);
        var target =
            $"    {accessibility} static readonly Func<{invocationType}, {shape.DelegateResultType}>";

        yield return $"{target} {fieldNamePrefix}_Step0 = static inv => {StepBody(shape, $"inv.{shape.TargetCall}()")};";

        for (var step = 1; step < interceptorCount; step++)
        {
            var interceptorIndex = interceptorCount - step;
            yield return $"{target} {fieldNamePrefix}_Step{step} = static inv => {StepBody(shape, $"inv._i{interceptorIndex}.{shape.InterceptorMethod}(inv, {fieldNamePrefix}_Step{step - 1})")};";
        }
    }

    private static IEnumerable<string> EmitPropertyChainFields(
        string safeName,
        IPropertySymbol property,
        string intSuffix,
        List<ITypeSymbol> interceptorTypes
    )
    {
        var propType = property.Type.ToDisplayString(Fq);
        var lastStep = interceptorTypes.Count - 1;

        if (property.GetMethod?.DeclaredAccessibility == Accessibility.Public)
        {
            var getter = $"Invocation_{safeName}_{property.Name}_Getter_{intSuffix}";
            yield return $"    private static readonly Func<{getter}, {propType}> s_get_{property.Name}_Step0 = static inv => inv.InvokeTarget();";
            for (var step = 1; step <= lastStep; step++)
            {
                var interceptorIndex = interceptorTypes.Count - step;
                yield return $"    private static readonly Func<{getter}, {propType}> s_get_{property.Name}_Step{step} = static inv => inv._i{interceptorIndex}.Invoke(inv, s_get_{property.Name}_Step{step - 1});";
            }
        }

        if (IsWritable(property))
        {
            var setter = $"Invocation_{safeName}_{property.Name}_Setter_{intSuffix}";
            yield return $"    private static readonly Func<{setter}, object?> s_set_{property.Name}_Step0 = static inv => {{ inv.InvokeTarget(); return null; }};";
            for (var step = 1; step <= lastStep; step++)
            {
                var interceptorIndex = interceptorTypes.Count - step;
                yield return $"    private static readonly Func<{setter}, object?> s_set_{property.Name}_Step{step} = static inv => {{ inv._i{interceptorIndex}.InvokeVoid(inv, s_set_{property.Name}_Step{step - 1}); return null; }};";
            }
        }
    }

    private static void EmitMethod(
        StringBuilder sb,
        ServicePlan plan,
        PlannedMember member,
        List<ITypeSymbol> interceptorTypes,
        string intSuffix,
        bool isClassType
    )
    {
        var method = member.Method!;
        var typeParameters = InvocationEmitter.TypeParameterList(method);
        // An explicit interface implementation inherits the constraints of the
        // interface member: repeating them is CS0460. A constraint-free method
        // is unaffected, which is why the plainer collision shapes missed this.
        var constraints = member.ExplicitInterface is null
            ? InvocationEmitter.ConstraintClauses(method)
            : string.Empty;
        var refPrefix =
            method.ReturnsByRef ? "ref "
            : method.ReturnsByRefReadonly ? "ref readonly "
            : string.Empty;
        var retType = method.ReturnType.ToDisplayString(Fq);
        var paramDecl = string.Join(", ", method.Parameters.Select(ParameterDeclaration));
        var paramArgs = string.Join(", ", method.Parameters.Select(ParameterArgument));
        var modifier = Modifier(member, isClassType);

        sb.AppendLine(
            $"    {modifier}{refPrefix}{retType} {MemberName(member, method.Name)}{typeParameters}({paramDecl}){constraints}"
        );
        sb.AppendLine("    {");

        if (!member.IsIntercepted)
        {
            var call = $"{Inner(member)}.{method.Name}{typeParameters}({paramArgs})";
            if (refPrefix.Length > 0)
                sb.AppendLine($"        return ref {call};");
            else if (method.ReturnType.SpecialType == SpecialType.System_Void)
                sb.AppendLine($"        {call};");
            else
                sb.AppendLine($"        return {call};");
        }
        else
        {
            var structName =
                $"{InvocationEmitter.BuildStructName(plan.SafeName, method)}_{intSuffix}";
            var structType = $"{structName}{typeParameters}";
            var ctorArgs =
                $"_inner{string.Concat(interceptorTypes.Select((_, i) => $", _i{i}"))}"
                + (method.Parameters.Length > 0 ? ", " + paramArgs : string.Empty);
            var next =
                method.TypeParameters.Length > 0
                    ? $"{structType}.s_Step{interceptorTypes.Count - 1}"
                    : $"s_{InvocationEmitter.BuildDelegateBase(method)}_Step{interceptorTypes.Count - 1}";
            var shape = ShapeFor(method);

            sb.AppendLine($"        var inv = new {structType}({ctorArgs});");
            switch (shape.InterceptorMethod)
            {
                case "Invoke":
                    sb.AppendLine(
                        $"        return _i0.Invoke<{structType}, {shape.ExplicitResultType}>(inv, {next});"
                    );
                    break;
                case "InvokeAsync":
                    sb.AppendLine(
                        $"        return _i0.InvokeAsync<{structType}, {shape.ExplicitResultType}>(inv, {next}){(shape.AsTask ? ".AsTask()" : string.Empty)};"
                    );
                    break;
                case "InvokeAsyncVoid":
                    sb.AppendLine(
                        $"        return _i0.InvokeAsyncVoid<{structType}>(inv, {next}){(shape.AsTask ? ".AsTask()" : string.Empty)};"
                    );
                    break;
                default:
                    sb.AppendLine($"        _i0.InvokeVoid(inv, {next});");
                    break;
            }
        }

        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private static void EmitProperty(
        StringBuilder sb,
        ServicePlan plan,
        PlannedMember member,
        List<ITypeSymbol> interceptorTypes,
        string intSuffix,
        bool isClassType
    )
    {
        var property = member.Property!;
        var propType = property.Type.ToDisplayString(Fq);
        var modifier = Modifier(member, isClassType);
        var lastStep = interceptorTypes.Count - 1;
        var interceptorArgs = string.Join(", ", interceptorTypes.Select((_, i) => $"_i{i}"));

        sb.AppendLine($"    {modifier}{propType} {MemberName(member, property.Name)}");
        sb.AppendLine("    {");

        if (property.GetMethod?.DeclaredAccessibility == Accessibility.Public)
        {
            sb.AppendLine("        get");
            sb.AppendLine("        {");
            if (member.IsIntercepted)
            {
                sb.AppendLine(
                    $"            var inv = new Invocation_{plan.SafeName}_{property.Name}_Getter_{intSuffix}(_inner, {interceptorArgs});"
                );
                sb.AppendLine(
                    $"            return _i0.Invoke(inv, s_get_{property.Name}_Step{lastStep});"
                );
            }
            else
            {
                sb.AppendLine($"            return {Inner(member)}.{property.Name};");
            }
            sb.AppendLine("        }");
        }

        if (IsWritable(property))
        {
            sb.AppendLine("        set");
            sb.AppendLine("        {");
            if (member.IsIntercepted)
            {
                sb.AppendLine(
                    $"            var inv = new Invocation_{plan.SafeName}_{property.Name}_Setter_{intSuffix}(_inner, {interceptorArgs}, value);"
                );
                sb.AppendLine(
                    $"            _i0.InvokeVoid(inv, s_set_{property.Name}_Step{lastStep});"
                );
            }
            else
            {
                sb.AppendLine($"            {Inner(member)}.{property.Name} = value;");
            }
            sb.AppendLine("        }");
        }

        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private static void EmitIndexer(StringBuilder sb, PlannedMember member, bool isClassType)
    {
        var indexer = member.Property!;
        var propType = indexer.Type.ToDisplayString(Fq);
        var modifier = Modifier(member, isClassType);
        var paramDecl = string.Join(", ", indexer.Parameters.Select(ParameterDeclaration));
        var paramArgs = string.Join(", ", indexer.Parameters.Select(ParameterArgument));
        var target = Inner(member);

        sb.AppendLine($"    {modifier}{propType} {MemberName(member, "this")}[{paramDecl}]");
        sb.AppendLine("    {");
        if (indexer.GetMethod?.DeclaredAccessibility == Accessibility.Public)
            sb.AppendLine($"        get => {target}[{paramArgs}];");
        if (IsWritable(indexer))
            sb.AppendLine($"        set => {target}[{paramArgs}] = value;");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private static void EmitEvent(StringBuilder sb, PlannedMember member, bool isClassType)
    {
        var @event = member.Event!;
        var eventType = @event.Type.ToDisplayString(Fq);
        var modifier = Modifier(member, isClassType);
        var target = Inner(member);

        sb.AppendLine($"    {modifier}event {eventType} {MemberName(member, @event.Name)}");
        sb.AppendLine("    {");
        sb.AppendLine($"        add => {target}.{@event.Name} += value;");
        sb.AppendLine($"        remove => {target}.{@event.Name} -= value;");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    /// <summary>
    /// How a generated member is declared, including its trailing space. On a
    /// class service an overridable member is <c>override</c>n (that is what
    /// routes the call); a member the proxy cannot override is emitted with
    /// <c>new</c>, so the hiding is deliberate — no CS0108 warning lands in a
    /// file the consumer cannot edit. That pass-through is unreachable in
    /// practice: <c>Wrap_*</c> returns the service type and the proxy type is
    /// internal, so a caller binds to the inherited implementation and the call
    /// runs on the proxy instance, never on <c>_inner</c> (PICO111 says so). An
    /// explicit interface implementation takes no access modifier at all.
    /// </summary>
    private static string Modifier(PlannedMember member, bool isClassType)
    {
        if (member.ExplicitInterface is not null)
            return string.Empty;

        return !isClassType ? "public "
            : ServicePlanner.IsOverridable(SymbolOf(member)) ? "public override "
            : "public new ";
    }

    /// <summary>
    /// The member name as it is declared. A member that collides with another
    /// one from a different base interface is qualified, so the compiler reads it
    /// as an explicit interface implementation.
    /// </summary>
    private static string MemberName(PlannedMember member, string name) =>
        member.ExplicitInterface is null ? name : $"{member.ExplicitInterface}.{name}";

    /// <summary>
    /// The expression a pass-through body reads the inner instance through. On a
    /// service that inherits one member name from several interfaces the plain
    /// <c>_inner.X</c> is ambiguous (CS0229), so the plan records the interface
    /// each member has to be seen through.
    /// </summary>
    private static string Inner(PlannedMember member) =>
        member.ReceiverCast is null ? "_inner" : $"(({member.ReceiverCast})_inner)";

    private static ISymbol SymbolOf(PlannedMember member) =>
        member.Method ?? (ISymbol?)member.Property ?? member.Event!;

    private static bool IsWritable(IPropertySymbol property) =>
        property.SetMethod is { DeclaredAccessibility: Accessibility.Public, IsInitOnly: false };

    private static string ParameterDeclaration(IParameterSymbol parameter) =>
        $"{RefPrefix(parameter.RefKind)}{parameter.Type.ToDisplayString(Fq)} {parameter.Name}";

    private static string ParameterArgument(IParameterSymbol parameter) =>
        $"{RefPrefix(parameter.RefKind)}{parameter.Name}";

    private static string RefPrefix(RefKind refKind) =>
        refKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.Out => "out ",
            RefKind.In => "in ",
            RefKind.RefReadOnlyParameter => "ref readonly ",
            _ => string.Empty,
        };

    private static string InterceptorSuffix(List<ITypeSymbol> interceptorTypes) =>
        string.Join(
            "_",
            interceptorTypes.Select(t => InvocationEmitter.Sanitize(t.ToDisplayString(Fq)))
        );

    private static string StepBody(ChainShape shape, string body) =>
        shape.WrapsVoid ? $"{{ {body}; return null; }}" : body;

    /// <summary>
    /// How one method's return type maps onto the interceptor API: which
    /// <c>IInterceptor</c> method carries the chain, what the cached
    /// <c>Func</c> returns, and whether a <c>Task</c> has to be rebuilt on the
    /// way out.
    /// </summary>
    private static ChainShape ShapeFor(IMethodSymbol method)
    {
        var returnType = method.ReturnType as INamedTypeSymbol;
        var metadataName = returnType?.MetadataName;

        if (metadataName is "Task`1" or "ValueTask`1")
        {
            var inner = returnType!
                .TypeArguments[0]
                .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            return new ChainShape(
                $"ValueTask<{inner}>",
                inner,
                "InvokeAsync",
                metadataName is "Task`1",
                "InvokeTargetAsync"
            );
        }

        if (metadataName is "Task" or "ValueTask")
            return new ChainShape(
                "ValueTask",
                null,
                "InvokeAsyncVoid",
                metadataName is "Task",
                "InvokeTargetAsync"
            );

        if (method.ReturnType.SpecialType == SpecialType.System_Void)
            return new ChainShape("object?", null, "InvokeVoid", false, "InvokeTarget");

        var sync = method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return new ChainShape(sync, sync, "Invoke", false, "InvokeTarget");
    }

    private readonly struct ChainShape(
        string delegateResultType,
        string? explicitResultType,
        string interceptorMethod,
        bool asTask,
        string targetCall
    )
    {
        public string DelegateResultType { get; } = delegateResultType;
        public string? ExplicitResultType { get; } = explicitResultType;
        public string InterceptorMethod { get; } = interceptorMethod;
        public bool AsTask { get; } = asTask;
        public string TargetCall { get; } = targetCall;
        public bool WrapsVoid => InterceptorMethod == "InvokeVoid";
    }
}
