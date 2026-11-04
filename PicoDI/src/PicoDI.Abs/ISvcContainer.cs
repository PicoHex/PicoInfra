namespace PicoDI.Abs;

/// <summary>
/// Represents a dependency injection container that manages service registrations and scope creation.
/// </summary>
/// <remarks>
/// Asynchronous disposal is the only disposal: release the container with <c>await using</c> or <c>await DisposeAsync()</c>, because this contract implements <see cref="IAsyncDisposable"/> and there is no <see cref="IDisposable"/>.
/// </remarks>
public interface ISvcContainer : IAsyncDisposable
{
    /// <summary>
    /// Registers a service descriptor with the container.
    /// </summary>
    /// <param name="descriptor">The service descriptor containing service type, factory, and lifetime information.</param>
    /// <returns>The container instance for method chaining.</returns>
    public ISvcContainer Register(SvcDescriptor descriptor);

    /// <summary>
    /// Creates a new service scope for resolving scoped services.
    /// </summary>
    /// <returns>A new service scope instance.</returns>
    public ISvcScope CreateScope();

    /// <summary>
    /// Determines whether the specified service type has been registered with the container.
    /// </summary>
    /// <param name="serviceType">The service type to check.</param>
    /// <returns><see langword="true"/> if the service type is registered; otherwise, <see langword="false"/>.</returns>
    public bool IsRegistered(Type serviceType);
}
