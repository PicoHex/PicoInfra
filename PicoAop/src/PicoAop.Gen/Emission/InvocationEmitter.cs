namespace PicoAop.Gen.Emission;

internal static class InvocationEmitter
{
    private static readonly SymbolDisplayFormat Fq = SymbolDisplayFormat.FullyQualifiedFormat;

    /// <summary>
    /// Display format that keeps nullable reference type annotations, so a
    /// constraint such as <c>where T : class?</c> survives the round trip.
    /// </summary>
    private static readonly SymbolDisplayFormat FqWithNullability =
        SymbolDisplayFormat.FullyQualifiedFormat.AddMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
        );

    public static string BuildStructName(string safeSvcName, IMethodSymbol method) =>
        $"Invocation_{safeSvcName}_{method.Name}{SignatureSuffix(method)}";

    /// <summary>
    /// Parameter type names as a name fragment. Type arguments are deliberately
    /// dropped here (<c>List&lt;int&gt;</c> and <c>List&lt;string&gt;</c> both
    /// become <c>_List</c>); overloads that would collide are detected by
    /// <see cref="ServicePlanner"/> and reported as PICO113 instead of emitting
    /// CS0101.
    /// </summary>
    public static string SignatureSuffix(IMethodSymbol method) =>
        string.Concat(method.Parameters.Select(p => $"_{Sanitize(p.Type.Name)}"));

    /// <summary>
    /// Base name of the per-method static delegate chain. It carries the
    /// parameter types, so overloads get distinct fields
    /// (<c>s_Get_Int32_Step0</c> / <c>s_Get_String_Step0</c>) instead of
    /// colliding on the method name alone.
    /// </summary>
    public static string BuildDelegateBase(IMethodSymbol method) =>
        $"{method.Name}{SignatureSuffix(method)}";

    /// <summary><c>&lt;T, TOut&gt;</c>, or an empty string for a non-generic method.</summary>
    public static string TypeParameterList(IMethodSymbol method) =>
        method.TypeParameters.Length == 0
            ? string.Empty
            : $"<{string.Join(", ", method.TypeParameters.Select(tp => tp.Name))}>";

    /// <summary>
    /// <c> where T : class where TOut : class, new()</c> (leading space, empty
    /// when the method is not generic). A generated member must repeat the
    /// exact constraints of the interface member or the override does not
    /// implement it.
    /// </summary>
    public static string ConstraintClauses(IMethodSymbol method)
    {
        if (method.TypeParameters.Length == 0)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var typeParameter in method.TypeParameters)
        {
            var clauses = new List<string>();
            if (typeParameter.HasUnmanagedTypeConstraint)
                clauses.Add("unmanaged");
            else if (typeParameter.HasValueTypeConstraint)
                clauses.Add("struct");
            else if (typeParameter.HasReferenceTypeConstraint)
                clauses.Add(
                    typeParameter.ReferenceTypeConstraintNullableAnnotation
                    == NullableAnnotation.Annotated
                        ? "class?"
                        : "class"
                );
            else if (typeParameter.HasNotNullConstraint)
                clauses.Add("notnull");

            foreach (var constraintType in typeParameter.ConstraintTypes)
                clauses.Add(constraintType.ToDisplayString(FqWithNullability));

            if (typeParameter.HasConstructorConstraint)
                clauses.Add("new()");

            if (clauses.Count > 0)
                sb.Append($" where {typeParameter.Name} : {string.Join(", ", clauses)}");
        }
        return sb.ToString();
    }

    public static string Sanitize(string name) =>
        name.Replace("global::", "")
            .Replace("::", "_")
            .Replace('.', '_')
            .Replace('<', '_')
            .Replace('>', '_')
            .Replace(',', '_')
            .Replace(' ', '_')
            .Replace('+', '_');

    public static string BuildSafeServiceName(ITypeSymbol serviceType) =>
        Sanitize(serviceType.ToDisplayString(Fq));
}
