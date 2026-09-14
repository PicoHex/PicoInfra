using static PicoCfg.DI.CfgServiceHelper;

namespace PicoCfg.DI;

/// <summary>
/// PicoDI registration helpers for <see cref="ICfgOptions{T}"/>.
/// Requires <c>RegisterCfgRoot</c> to have been called first.
/// </summary>
public static class CfgOptionsExtensions
{
    extension(ISvcContainer container)
    {
        /// <summary>
        /// Registers a singleton <see cref="ICfgOptions{T}"/> that binds and caches the configuration value once.
        /// Each resolution returns the same cached instance.
        /// Requires <c>RegisterCfgRoot</c> to have been called first.
        /// </summary>
        public ISvcContainer RegisterCfgOptionsSingleton<
            [DynamicallyAccessedMembers(
                DynamicallyAccessedMemberTypes.PublicConstructors
                    | DynamicallyAccessedMemberTypes.PublicProperties
            )]
                T
        >(string? section = null)
            where T : class =>
            container.RegisterSingleton<ICfgOptions<T>>(scope => new CfgOptions<T>(
                Resolve(scope),
                section
            ));

        /// <summary>
        /// Registers a scoped <see cref="ICfgOptions{T}"/> that rebinds the configuration value on every resolution.
        /// Each resolution creates a new instance from the current configuration state.
        /// Requires <c>RegisterCfgRoot</c> to have been called first.
        /// </summary>
        public ISvcContainer RegisterCfgOptionsScoped<
            [DynamicallyAccessedMembers(
                DynamicallyAccessedMemberTypes.PublicConstructors
                    | DynamicallyAccessedMemberTypes.PublicProperties
            )]
                T
        >(string? section = null)
            where T : class =>
            container.RegisterScoped<ICfgOptions<T>>(scope => new CfgOptionsSnapshot<T>(
                Resolve(scope),
                section
            ));
    }
}
