namespace PicoAop.Gen;

internal static class PicoAopDiag
{
    private const string Category = "PicoAop";

    public static readonly DiagnosticDescriptor SealedType = new(
        "PICO101",
        "Sealed type cannot be intercepted",
        "Type '{0}' is sealed; extract an interface for interception",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor ValueType = new(
        "PICO102",
        "Value type cannot be intercepted",
        "Type '{0}' is a value type; extract an interface for interception",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor NotAnInterceptor = new(
        "PICO105",
        "Type does not implement IInterceptor",
        "Type '{0}' does not implement IInterceptor",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor RefOutMethod = new(
        "PICO110",
        "Method with ref/out parameters delegated without interception",
        "Method '{0}' on '{1}' has ref/out parameters and will not be intercepted (direct delegation)",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    /// <summary>
    /// A member shape the proxy can implement but cannot intercept (indexer,
    /// event, ref-returning method, ref struct or pointer parameter). The member
    /// is emitted as a direct pass-through so the generated proxy still
    /// compiles.
    /// </summary>
    public static readonly DiagnosticDescriptor MemberDelegated = new(
        "PICO111",
        "Member cannot be intercepted and is delegated",
        "Member '{0}' on '{1}' cannot be intercepted ({2}) and is delegated directly to the inner instance",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    /// <summary>
    /// A shape the proxy cannot implement at all: an <c>init</c> accessor
    /// writes only through an object initializer, so there is no target to
    /// route it to. Emitting the proxy would produce CS0535 in generated code
    /// the consumer cannot edit, so interception for the whole service is
    /// skipped and the plain registration is kept.
    /// </summary>
    public static readonly DiagnosticDescriptor InitOnlyProperty = new(
        "PICO112",
        "Property with an init accessor cannot be intercepted",
        "Property '{0}' on '{1}' has an init accessor that a generated proxy cannot implement; interception for '{1}' is skipped",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    /// <summary>
    /// Two members map to one generated invocation name — the signature is
    /// rendered from <c>p.Type.Name</c>, which drops generic type arguments, so
    /// <c>M(List&lt;int&gt;)</c> and <c>M(List&lt;string&gt;)</c> collide. Those
    /// members are delegated instead of emitting CS0101/CS0102/CS0111.
    /// </summary>
    public static readonly DiagnosticDescriptor MemberShapeCollision = new(
        "PICO113",
        "Members share one generated invocation name",
        "Members '{0}' of '{1}' map to the same generated invocation name '{2}' and are delegated without interception; overloads that differ only by generic type arguments are not supported",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );
}
