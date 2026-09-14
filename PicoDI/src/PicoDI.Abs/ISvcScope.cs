namespace PicoDI.Abs;

/// <summary>
/// Represents a service scope that manages the lifetime of scoped services and provides service resolution.
/// </summary>
public interface ISvcScope : IAsyncDisposable
{
    /// <summary>
    /// Creates a new child service scope.
    /// </summary>
    /// <returns>A new service scope instance.</returns>
    public ISvcScope CreateScope();

    /// <summary>
    /// Resolves a service of the specified type.
    /// </summary>
    /// <param name="serviceType">The type of service to resolve.</param>
    /// <returns>The resolved service instance.</returns>
    public object GetService(Type serviceType);

    /// <summary>
    /// Resolves all services of the specified type.
    /// </summary>
    /// <param name="serviceType">The type of services to resolve.</param>
    /// <returns>A read-only list of all registered service instances of the specified type.</returns>
    /// <exception cref="PicoDiException">
    /// Thrown when <paramref name="serviceType"/> is not registered.
    /// Unlike some DI containers that return an empty collection for unregistered types,
    /// PicoDI throws to fail fast on misconfiguration.
    /// </exception>
    public IReadOnlyList<object> GetServices(Type serviceType);

    /// <summary>
    /// Attempts to resolve a service of the specified type without throwing.
    /// </summary>
    /// <param name="serviceType">The type of service to resolve.</param>
    /// <param name="service">The resolved service instance, or null if not found.</param>
    /// <returns><see langword="true"/> if the service was resolved; otherwise, <see langword="false"/>.</returns>
    public bool TryGetService(
        Type serviceType,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out object? service
    );

    /// <summary>
    /// Attempts to resolve all services of the specified type without throwing.
    /// </summary>
    /// <param name="serviceType">The type of services to resolve.</param>
    /// <param name="services">The resolved service instances, or null if the type is not registered.</param>
    /// <returns><see langword="true"/> if the service type is registered; otherwise, <see langword="false"/>.</returns>
    public bool TryGetServices(
        Type serviceType,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IReadOnlyList<object>? services
    );
}

/// <summary>
/// Provides extension methods for <see cref="ISvcScope"/> to simplify service resolution.
/// </summary>
public static class SvcProviderExtensions
{
    extension(ISvcScope provider)
    {
        /// <summary>
        /// Resolves a service of the specified type.
        /// Uses static generic class caching to avoid typeof(T) overhead on repeated calls.
        /// </summary>
        /// <typeparam name="T">The type of service to resolve.</typeparam>
        /// <returns>The resolved service instance.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T GetService<T>() => (T)provider.GetService(typeof(T));

        /// <summary>
        /// Resolves all services of the specified type.
        /// Uses direct iteration instead of <c>Cast&lt;T&gt;()</c> for AOT compatibility.
        /// </summary>
        /// <typeparam name="T">The type of services to resolve.</typeparam>
        /// <returns>A read-only list of all registered service instances of the specified type.</returns>
        public IReadOnlyList<T> GetServices<T>()
        {
            var source = provider.GetServices(typeof(T));
            var result = new T[source.Count];
            for (int i = 0; i < source.Count; i++)
                result[i] = (T)source[i];
            return result;
        }
    }
}
