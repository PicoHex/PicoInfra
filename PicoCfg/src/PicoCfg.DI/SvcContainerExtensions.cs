using static PicoCfg.DI.CfgServiceHelper;

namespace PicoCfg.DI;

/// <summary>
/// PicoDI registration helpers for PicoCfg configuration roots and source-generated bound POCOs.
/// Call <see cref="RegisterCfgRoot"/> first, then use <c>RegisterCfg*</c> to expose typed configuration views.
/// </summary>
public static class SvcContainerExtensions
{
    extension(ISvcContainer container)
    {
        /// <summary>
        /// Registers the configuration root as <see cref="ICfgRoot"/> and <see cref="ICfg"/>.
        /// Must be called before <c>RegisterCfg*</c> methods that consume configuration.
        /// </summary>
        public ISvcContainer RegisterCfgRoot(ICfgRoot root)
        {
            ArgumentNullException.ThrowIfNull(root);
            return container
                .RegisterSingleton<ICfgRoot>(_ => root)
                .RegisterSingleton<ICfg>(_ => root);
        }

        /// <summary>
        /// Registers a transient bound POCO of type <typeparamref name="T"/> resolved from the configuration root.
        /// Requires <c>RegisterCfgRoot</c> to have been called first.
        /// </summary>
        public ISvcContainer RegisterCfgTransient<
            [DynamicallyAccessedMembers(
                DynamicallyAccessedMemberTypes.PublicConstructors
                    | DynamicallyAccessedMemberTypes.PublicProperties
            )]
                T
        >(string? section = null)
            where T : class => container.RegisterTransient<T>(scope => Bind<T>(scope, section));

        /// <summary>
        /// Registers a scoped bound POCO of type <typeparamref name="T"/> resolved from the configuration root.
        /// Requires <c>RegisterCfgRoot</c> to have been called first.
        /// </summary>
        public ISvcContainer RegisterCfgScoped<
            [DynamicallyAccessedMembers(
                DynamicallyAccessedMemberTypes.PublicConstructors
                    | DynamicallyAccessedMemberTypes.PublicProperties
            )]
                T
        >(string? section = null)
            where T : class => container.RegisterScoped<T>(scope => Bind<T>(scope, section));

        /// <summary>
        /// Registers a singleton bound POCO of type <typeparamref name="T"/> resolved from the configuration root.
        /// Requires <c>RegisterCfgRoot</c> to have been called first.
        /// </summary>
        public ISvcContainer RegisterCfgSingleton<
            [DynamicallyAccessedMembers(
                DynamicallyAccessedMemberTypes.PublicConstructors
                    | DynamicallyAccessedMemberTypes.PublicProperties
            )]
                T
        >(string? section = null)
            where T : class => container.RegisterSingleton<T>(scope => Bind<T>(scope, section));
    }
}
