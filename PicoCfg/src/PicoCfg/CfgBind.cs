namespace PicoCfg;

/// <summary>
/// Source-generated configuration binding API.
/// The static methods on this type are recognized by the PicoCfg.Gen source generator,
/// which emits typed binding delegates for the specified target types at compile time.
/// </summary>
public static class CfgBind
{
    /// <summary>
    /// Binds a new instance of <typeparamref name="T"/> from the configuration root,
    /// optionally scoped to a configuration <paramref name="section"/> prefix.
    /// </summary>
    public static T Bind<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors
                | DynamicallyAccessedMemberTypes.PublicProperties
        )]
            T
    >(ICfgRoot root, string? section = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        return Bind<T>((ICfg)root, section);
    }

    /// <summary>
    /// Attempts to bind a new instance of <typeparamref name="T"/> from the configuration root,
    /// optionally scoped to a configuration <paramref name="section"/> prefix.
    /// Returns <see langword="false"/> when binding fails without throwing.
    /// </summary>
    public static bool TryBind<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors
                | DynamicallyAccessedMemberTypes.PublicProperties
        )]
            T
    >(ICfgRoot root, [MaybeNullWhen(false)] out T value, string? section = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        return TryBind((ICfg)root, out value, section);
    }

    /// <summary>
    /// Binds configuration values into an existing <paramref name="instance"/> of <typeparamref name="T"/>
    /// from the configuration root, optionally scoped to a configuration <paramref name="section"/> prefix.
    /// </summary>
    public static void BindInto<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T
    >(ICfgRoot root, T instance, string? section = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        BindInto((ICfg)root, instance, section);
    }

    /// <summary>
    /// Binds a new instance of <typeparamref name="T"/> from a configuration view,
    /// optionally scoped to a configuration <paramref name="section"/> prefix.
    /// </summary>
    public static T Bind<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors
                | DynamicallyAccessedMemberTypes.PublicProperties
        )]
            T
    >(ICfg cfg, string? section = null)
    {
        ArgumentNullException.ThrowIfNull(cfg);

        var registration = CfgBindRuntime.GetRequiredRegistration<T>(nameof(Bind));
        if (registration.Bind is null)
            throw PicoCfgBindRegistrationException.CreateMissing(typeof(T), nameof(Bind));

        return registration.Bind(cfg, section);
    }

    /// <summary>
    /// Attempts to bind a new instance of <typeparamref name="T"/> from a configuration view,
    /// optionally scoped to a configuration <paramref name="section"/> prefix.
    /// Returns <see langword="false"/> when binding fails without throwing.
    /// </summary>
    public static bool TryBind<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors
                | DynamicallyAccessedMemberTypes.PublicProperties
        )]
            T
    >(ICfg cfg, [MaybeNullWhen(false)] out T value, string? section = null)
    {
        ArgumentNullException.ThrowIfNull(cfg);

        var registration = CfgBindRuntime.GetRequiredRegistration<T>(nameof(TryBind));
        if (registration.TryBind is null)
            throw PicoCfgBindRegistrationException.CreateMissing(typeof(T), nameof(TryBind));

        return registration.TryBind(cfg, section, out value);
    }

    /// <summary>
    /// Binds configuration values into an existing <paramref name="instance"/> of <typeparamref name="T"/>
    /// from a configuration view, optionally scoped to a configuration <paramref name="section"/> prefix.
    /// </summary>
    public static void BindInto<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T
    >(ICfg cfg, T instance, string? section = null)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(instance);

        var registration = CfgBindRuntime.GetRequiredRegistration<T>(nameof(BindInto));
        if (registration.BindInto is null)
            throw PicoCfgBindRegistrationException.CreateMissing(typeof(T), nameof(BindInto));

        registration.BindInto(cfg, section, instance);
    }
}
