namespace PicoDI.Gen;

internal static partial class ServiceRegistrationSourceEmitter
{
    /// <summary>
    /// Generates typed resolver extension methods that bypass dictionary lookup.
    /// Only methods for service types referenced by other factories' dependency
    /// chains are emitted; the class is internal (an implementation detail of
    /// the generated registrations, not public API).
    /// </summary>
    private static void GenerateTypedResolvers(
        StringBuilder sb,
        ImmutableArray<ServiceRegistration> registrations,
        Dictionary<string, ServiceRegistration> registrationLookup,
        ISet<string> referencedResolvers
    )
    {
        if (referencedResolvers.Count == 0)
            return;

        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// High-performance typed resolvers that bypass dictionary lookup.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    internal static class Resolve");
        sb.AppendLine("    {");

        var generatedMethods = new HashSet<string>();

        foreach (var reg in registrations)
        {
            var methodName = GetResolverMethodName(reg.ServiceTypeFullName);
            if (!generatedMethods.Add(methodName))
                continue;
            if (!referencedResolvers.Contains(methodName))
                continue;

            var effectiveRegistration = registrationLookup[reg.ServiceTypeFullName];
            var serviceType = effectiveRegistration.ServiceTypeFullName;
            sb.AppendLine(
                $"        /// <summary>Resolves {effectiveRegistration.ServiceTypeName} with direct factory call.</summary>"
            );
            sb.AppendLine(
                "        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]"
            );
            sb.AppendLine(
                $"        public static {serviceType} {methodName}(global::PicoDI.Abs.ISvcScope scope)"
            );
            sb.AppendLine("        {");

            switch (effectiveRegistration.Lifetime)
            {
                case PicoDiNames.Transient:
                    var transientFactory = GenerateInlinedFactory(
                        effectiveRegistration,
                        registrationLookup,
                        [],
                        0,
                        "scope",
                        (HashSet<string>?)referencedResolvers
                    );
                    sb.AppendLine($"            return {transientFactory};");
                    break;
                default:
                    sb.AppendLine(
                        $"            return ({serviceType})scope.GetService(typeof({serviceType}));"
                    );
                    break;
            }

            sb.AppendLine("        }");
            sb.AppendLine();
        }

        sb.AppendLine("    }");
    }

    /// <summary>
    /// Produces a legal C# method name from a fully qualified type name by replacing
    /// special characters with underscores. Two distinct types may produce the same
    /// method name (e.g. <c>Dictionary&lt;A_B, C&gt;</c> and <c>Dictionary&lt;A, B_C&gt;</c>).
    /// The caller guards against this via <c>generatedMethods</c> — the second type
    /// silently falls back to dictionary-based resolution with no diagnostic.
    /// In practice this requires user type names with underscores adjacent to generic
    /// parameter boundaries and is expected to be extremely rare.
    /// </summary>
    private static string GetResolverMethodName(string serviceTypeFullName)
    {
        var name = serviceTypeFullName.Replace("global::", "");
        name = name.Replace("<", "_").Replace(">", "").Replace(",", "_").Replace(" ", "");
        return name.Replace(".", "_");
    }
}
