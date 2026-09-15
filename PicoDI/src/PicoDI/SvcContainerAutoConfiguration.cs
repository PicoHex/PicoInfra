namespace PicoDI;

/// <summary>
/// Coordinates source-generated container configuration.
/// Thin public wrapper over the shared <see cref="PicoDI.Abs.GeneratorConfiguratorRegistry"/>
/// (also used by PicoMediator); generated code registers configurators by stable id
/// and <see cref="TryApplyConfiguration"/> applies them to a container exactly once.
/// </summary>
public static class SvcContainerAutoConfiguration
{
    /// <summary>
    /// Registers a configurator action using a stable identifier so repeated module
    /// initializers replace the existing registration instead of appending a duplicate.
    /// Configurators are applied in deterministic <see cref="StringComparer.Ordinal"/> order of this identifier.
    /// </summary>
    /// <param name="configuratorId">A stable identifier for the configurator.</param>
    /// <param name="configurator">The configuration action that registers services with the container.</param>
    public static void RegisterConfigurator(
        string configuratorId,
        Action<ISvcContainer> configurator
    ) => PicoDI.Abs.GeneratorConfiguratorRegistry.Register(configuratorId, configurator);

    /// <summary>
    /// Applies all registered configurations to the given container exactly once.
    /// If any configurator throws, the container is NOT marked as configured,
    /// allowing retries on subsequent containers.
    /// </summary>
    /// <param name="container">The container to configure.</param>
    /// <returns>True if any configurators were registered and applied; otherwise, false.</returns>
    public static bool TryApplyConfiguration(ISvcContainer container) =>
        PicoDI.Abs.GeneratorConfiguratorRegistry.TryApply(container);

    /// <summary>
    /// Applies all deferred configurators (e.g. PicoMediator auto-subscriptions) to the
    /// given container exactly once. Deferred configurators run on
    /// <c>Build()</c> — or earlier via an explicit apply such as
    /// <c>AddPicoMediator()</c> — so manual registrations made before Build win over
    /// generated ones.
    /// </summary>
    /// <param name="container">The container to configure.</param>
    /// <returns>True if any deferred configurators were registered and applied; otherwise, false.</returns>
    public static bool TryApplyDeferredConfiguration(ISvcContainer container) =>
        PicoDI.Abs.GeneratorConfiguratorRegistry.TryApplyDeferred(container);

    /// <summary>True when the container has been marked as deferred-configured.</summary>
    public static bool HasAppliedDeferredConfiguration(ISvcContainer container) =>
        PicoDI.Abs.GeneratorConfiguratorRegistry.HasAppliedDeferred(container);

    /// <summary>True when any deferred configurator has been registered.</summary>
    public static bool HasDeferredConfigurator =>
        PicoDI.Abs.GeneratorConfiguratorRegistry.HasAnyDeferred;

    /// <summary>
    /// Marks the per-container generated-registration state as applied.
    /// This does not inspect the registry or apply any configurators.
    /// </summary>
    /// <param name="container">The configured container.</param>
    public static void MarkGeneratedConfigurationApplied(ISvcContainer container) =>
        PicoDI.Abs.GeneratorConfiguratorRegistry.MarkApplied(container);

    /// <summary>True when the container has already been marked as configured.</summary>
    public static bool HasAppliedGeneratedConfiguration(ISvcContainer container) =>
        PicoDI.Abs.GeneratorConfiguratorRegistry.HasApplied(container);

    /// <summary>
    /// Gets a value indicating whether any configurators have been registered.
    /// </summary>
    public static bool HasConfigurator => PicoDI.Abs.GeneratorConfiguratorRegistry.HasAny;

    /// <summary>
    /// Clears all registered configurators to enable isolated test execution.
    /// Only use in test scenarios — production code should never call this.
    /// </summary>
    internal static void ClearForTesting() => PicoDI.Abs.GeneratorConfiguratorRegistry.Clear();
}
