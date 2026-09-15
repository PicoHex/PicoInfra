namespace PicoDI;

public sealed partial class SvcContainer
{
    /// <inheritdoc />
    public ISvcContainer Register(SvcDescriptor descriptor)
    {
        DisposalGuards.ThrowIfDisposed(ref _disposed, nameof(SvcContainer));

        var runtimeRegistration = SvcRuntimeRegistration.Create(descriptor);

        lock (_registrationLock)
        {
            if (_frozenCache != null)
                throw new InvalidOperationException(
                    "Cannot register services after Build() has been called. "
                        + "Register all services before calling Build()."
                );

            var cache =
                _registrationCache
                ?? throw new InvalidOperationException(
                    "Cannot register services after the container has been finalized. "
                        + "Register all services before calling Build()."
                );

            if (cache.TryGetValue(runtimeRegistration.ServiceType, out var existing))
            {
                existing.Add(runtimeRegistration);
            }
            else
            {
                cache[runtimeRegistration.ServiceType] = [runtimeRegistration];
            }
        }

        return this;
    }

    /// <summary>
    /// Builds and optimizes the container for maximum performance.
    /// </summary>
    public void Build()
    {
        DisposalGuards.ThrowIfDisposed(ref _disposed, nameof(SvcContainer));

        // Apply DEFERRED source-generated configurators HERE (immediate ones ran in
        // the constructor): manual registrations made before Build must win over
        // generated ones (the configurators dedup per service type). An explicit
        // apply such as AddPicoMediator() may already have marked the deferred set;
        // TryApplyDeferred is once-per-container, so this is a no-op in that case.
        ApplyDeferredAutoConfigurationIfEnabled();

        lock (_registrationLock)
        {
            if (_frozenCache != null)
                return;

            var cache =
                _registrationCache
                ?? throw new InvalidOperationException(
                    "Cannot build after the container has been finalized. "
                        + "Register all services before calling Build()."
                );

            // Validate before freezing so a failed Build can be retried.
            TrackHostedServicesFromRegistrations(cache);

            // Convert lists to arrays for frozen storage.
            var frozenCache = cache.ToFrozenDictionary(
                static kvp => kvp.Key,
                static kvp => kvp.Value.ToArray()
            );
            Volatile.Write(ref _frozenCache, frozenCache);
            Volatile.Write(ref _registrationCache, null);
        }
    }
}
