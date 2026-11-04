namespace PicoMediator.DI;

/// <summary>
/// DI registration extensions for PicoMediator.
/// </summary>
public static class SvcContainerMediatorExtensions
{
    extension(ISvcContainer container)
    {
        /// <summary>
        /// Registers <see cref="Mediator"/> as <see cref="IMediator"/> with the
        /// specified lifetime. Default is <see cref="SvcLifetime.Scoped"/> for
        /// request-scoped isolation. Manual registrations made before this call
        /// win over generated ones.
        /// </summary>
        /// <param name="lifetime">
        /// Lifetime of the mediator. It also decides the scope handlers are
        /// created in, because the mediator resolves them through the scope it was
        /// constructed with. <see cref="SvcLifetime.Singleton"/> therefore makes the
        /// mediator container-wide and, since PicoDI builds singleton factories
        /// against the container-internal root scope (its documented singleton
        /// rule), makes handlers and their scoped dependencies live for the
        /// container's lifetime: a <see cref="SvcLifetime.Scoped"/> handler
        /// effectively becomes one instance per container instead of one per caller
        /// scope, and disposable transient handlers are retained until container
        /// disposal. That follows from PicoDI's singleton rule, not from a mediator
        /// bug (<c>Send</c>/<c>Publish</c> take no scope, so a singleton has no
        /// channel to a caller's scope). Keep <see cref="SvcLifetime.Scoped"/> unless
        /// the whole handler graph is safe to live that long.
        /// </param>
        /// <param name="autoRegisterHandlers">
        /// When true (default), all handler implementations scanned by
        /// PicoMediator.Gen are registered automatically (declare-and-subscribe).
        /// </param>
        public ISvcContainer AddPicoMediator(
            SvcLifetime lifetime = SvcLifetime.Scoped,
            bool autoRegisterHandlers = true
        )
        {
            if (autoRegisterHandlers)
                MediatorAutoSubscriptionRegistry.TryApplyConfiguration(container);

            container.Register(
                SvcDescriptor.Create(typeof(IMediator), scope => new Mediator(scope), lifetime)
            );
            return container;
        }
    }
}
