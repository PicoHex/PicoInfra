namespace PicoDI;

/// <summary>
/// Default implementation of <see cref="IHost"/> that wraps an <see cref="SvcContainer"/>.
/// </summary>
public sealed class SvcHost(SvcContainer container) : IHost
{
    /// <inheritdoc />
    public ISvcContainer Services => container;

    /// <inheritdoc />
    /// <remarks>
    /// Builds the container if it has not been built yet — the same lazy build
    /// <c>CreateScope()</c> and <c>GetImplicitRootScope()</c> perform — so a host over a fresh
    /// <see cref="SvcContainer"/> starts its services instead of silently starting nothing.
    /// <c>Build()</c> freezes the registrations: register every service before the first
    /// <c>StartAsync</c> (or build explicitly, as <see cref="SvcHostBuilder"/> does).
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The container has already been disposed.</exception>
    public Task StartAsync(CancellationToken cancellationToken = default) =>
        container.StartHostedServicesAsync(cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Symmetric with <see cref="StartAsync"/>: a disposed container faults
    /// <see cref="ObjectDisposedException"/> here too. Disposal already ran the single stop pass,
    /// so returning normally would look like a shutdown that really happened (review-2026-11-02
    /// defect L4).
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The container has already been disposed.</exception>
    public Task StopAsync(CancellationToken cancellationToken = default) =>
        container.StopHostedServicesAsync(cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await container.DisposeAsync().ConfigureAwait(false);
    }
}
