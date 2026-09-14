namespace PicoCfg.DI;

/// <summary>
/// Shared internal helpers for resolving configuration services from PicoDI scopes.
/// </summary>
internal static class CfgServiceHelper
{
    /// <summary>
    /// Attempts to resolve all registered services of type <typeparamref name="T"/>.
    /// Returns an empty sequence when the service type is not registered, rather than throwing.
    /// </summary>
    internal static IEnumerable<T> TryGetServices<T>(ISvcScope scope)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(scope);

        try
        {
            return scope.GetServices<T>();
        }
        catch (PicoDiException)
        {
            // PicoDI.Abs does not expose an IsRegistered<T>() query API.
            // Catching PicoDiException from GetServices<T>() is the only non-throwing
            // path to detect unregistered service types at this layer.
            return [];
        }
    }

    /// <summary>
    /// Resolves the most recently registered <see cref="ICfgRoot"/> or <see cref="ICfg"/> from the scope.
    /// Throws <see cref="InvalidOperationException"/> when neither is registered.
    /// </summary>
    internal static ICfg ResolveCfg(ISvcScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var root = TryGetServices<ICfgRoot>(scope).LastOrDefault();
        if (root is not null)
            return root;

        var cfg = TryGetServices<ICfg>(scope).LastOrDefault();
        if (cfg is not null)
            return cfg;

        throw new InvalidOperationException(
            "No PicoCfg configuration source is registered. Call RegisterCfgRoot(...) before registering configuration options."
        );
    }

    /// <summary>
    /// Resolves an <see cref="ICfg"/> from the scope (convenience shorthand for extension method bodies).
    /// </summary>
    internal static ICfg Resolve(ISvcScope scope) => ResolveCfg(scope);

    /// <summary>
    /// Binds a POCO of type <typeparamref name="T"/> from configuration resolved through the scope.
    /// This is a forwarding helper — the actual binding is deferred to runtime via the
    /// source-generated or reflection-based binder for the closed type.
    /// </summary>
#pragma warning disable PCFGGEN001 // Direct closed named target type required
    internal static T Bind<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors
                | DynamicallyAccessedMemberTypes.PublicProperties
        )]
            T
    >(ISvcScope scope, string? section = null)
        where T : class => CfgBind.Bind<T>(ResolveCfg(scope), section);
#pragma warning restore PCFGGEN001
}
