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
    /// A member shape the proxy cannot intercept. Whether the member is still
    /// emitted as a pass-through, and where its calls then run, differs by cause
    /// and is stated by the reason clause: an interface member is emitted and
    /// forwarded to the inner instance, while a member of a class service that a
    /// derived proxy cannot override is reached on the inherited implementation
    /// in the proxy instance — the emitted <c>new</c> pass-through is unreachable
    /// for a consumer, because <c>Wrap_*</c> returns the service type.
    /// </summary>
    public static readonly DiagnosticDescriptor MemberDelegated = new(
        "PICO111",
        "Member cannot be intercepted",
        "Member '{0}' on '{1}' cannot be intercepted ({2})",
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
    /// Two members map to one generated invocation name, so at most one of them
    /// can be routed. Two causes: the invocation name is rendered from
    /// <c>p.Type.Name</c>, which drops generic type arguments
    /// (<c>M(List&lt;int&gt;)</c> / <c>M(List&lt;string&gt;)</c>), or two base
    /// interfaces expose one member name with different signatures
    /// (<c>IA:int V</c> / <c>IB:string V</c>). Either way those members are
    /// delegated instead of emitting CS0101/CS0102/CS0111/CS0229.
    /// </summary>
    public static readonly DiagnosticDescriptor MemberShapeCollision = new(
        "PICO113",
        "Members share one generated invocation name",
        "Members '{0}' of '{1}' map to the same generated invocation name '{2}' and are delegated without interception; a proxy can route only one member per generated name",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );
}
