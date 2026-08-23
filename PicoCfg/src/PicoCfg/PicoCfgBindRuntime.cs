namespace PicoCfg;

/// <summary>
/// Runtime infrastructure for source-generated PicoCfg.Gen binders.
/// Register, path composition, conversion errors, and registration storage
/// used by compile-time generated binding delegates.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static partial class CfgBindRuntime
{
    /// <summary>Version number used by the source generator to ensure generated code matches this runtime.</summary>
    public const int ContractVersion = 3;

    /// <summary>Registers source-generated binding delegates for <typeparamref name="T"/>.</summary>
    public static void Register<T>(
        int contractVersion,
        Func<ICfg, string?, T>? bind,
        PicoCfgGeneratedTryBindDelegate<T>? tryBind,
        PicoCfgGeneratedBindIntoDelegate<T>? bindInto
    )
    {
        PicoCfgBindRegistrationStore<T>.Registration = new PicoCfgBindRegistration<T>(
            contractVersion,
            bind,
            tryBind,
            bindInto
        );
    }

    /// <summary>Combines an optional section prefix with a property name into a configuration path.</summary>
    public static string CombinePath(string? section, string propertyName)
    {
        ArgumentException.ThrowIfNullOrEmpty(propertyName);
        return string.IsNullOrEmpty(section)
            ? propertyName
            : string.Concat(section, ":", propertyName);
    }

    /// <summary>
    /// Tries to get a configuration value by section + property name,
    /// with case-insensitive fallback.
    /// </summary>
    /// <remarks>
    /// Two-layer lookup:
    /// <list type="number">
    /// <item>
    /// <b>Fast path</b> — exact match via <see cref="ICfg.TryGetValue"/>.
    /// When <paramref name="cfg"/> is a snapshot or composed snapshot,
    /// this also triggers the built-in case-insensitive fallback in
    /// <see cref="CfgSnapshot"/> / <see cref="CfgSnapshotComposer"/>,
    /// so the lookup succeeds even for camelCase / PascalCase mismatches
    /// without reaching step 2.
    /// </item>
    /// <item>
    /// <b>Slow path</b> — case-insensitive scan of the section-scoped key
    /// set.  Only reached when <paramref name="cfg"/> is a non-snapshot
    /// <see cref="ICfg"/> implementation (e.g. inline dictionaries) that
    /// lacks the built-in fallback.  <see cref="PicoCfg.Extensions.CfgEnumerationExtensions.GetAll(ICfg)"/>
    /// uses <see cref="StringComparison.OrdinalIgnoreCase"/> to return
    /// matching keys regardless of casing.
    /// </item>
    /// </list>
    /// </remarks>
    public static bool TryGetValueIgnoreCase(
        ICfg cfg,
        string? section,
        string propertyName,
        out string? value
    )
    {
        // Layer 1: exact match + snapshot built-in case-insensitive fallback
        var path = CombinePath(section, propertyName);
        if (cfg.TryGetValue(path, out value))
            return true;

        // Layer 2: fallback for non-snapshot ICfg implementations
        var scope = string.IsNullOrEmpty(section) ? cfg : cfg.GetSection(section);
        foreach (var (key, val) in scope.GetAll())
        {
            if (string.Equals(key, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = val;
                return true;
            }
        }

        value = null;
        return false;
    }

    /// <summary>Creates a <see cref="FormatException"/> describing a configuration value conversion failure.</summary>
    public static FormatException CreateConversionException(
        string path,
        string targetTypeDisplayName,
        string memberDisplayName
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(targetTypeDisplayName);
        ArgumentException.ThrowIfNullOrEmpty(memberDisplayName);

        return new FormatException(
            $"Configuration value at '{path}' could not be converted to '{targetTypeDisplayName}' for '{memberDisplayName}'."
        );
    }

    internal static PicoCfgBindRegistration<T> GetRequiredRegistration<T>(string operationName)
    {
        var registration = PicoCfgBindRegistrationStore<T>.Registration;
        if (registration is null)
            throw PicoCfgBindRegistrationException.CreateMissing(typeof(T), operationName);

        if (registration.ContractVersion != ContractVersion)
        {
            throw PicoCfgBindRegistrationException.CreateIncompatible(
                typeof(T),
                operationName,
                ContractVersion,
                registration.ContractVersion
            );
        }

        return registration;
    }

    private static class PicoCfgBindRegistrationStore<T>
    {
        public static volatile PicoCfgBindRegistration<T>? Registration;
    }
}
