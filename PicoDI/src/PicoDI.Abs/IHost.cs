namespace PicoDI.Abs;

/// <summary>
/// Represents a host that manages the lifecycle of hosted services within a dependency injection container.
/// </summary>
/// <remarks>
/// Asynchronous disposal is the only disposal: release the host with <c>await using</c> or <c>await DisposeAsync()</c>, because this contract implements <see cref="IAsyncDisposable"/> and there is no <see cref="IDisposable"/>. Shutdown stops hosted services through <see cref="IHostedSvc.StopAsync"/>, so a synchronous form could only block on it or drop it.
/// </remarks>
public interface IHost : IAsyncDisposable
{
    /// <summary>
    /// Gets the underlying service container for service resolution.
    /// </summary>
    public ISvcContainer Services { get; }

    /// <summary>
    /// Starts all registered hosted services.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops all started hosted services in the exact reverse of the order they started (LIFO).
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken = default);
}
