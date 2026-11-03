namespace PicoDI;

/// <summary>
/// One service whose factory is currently running on this thread.
/// </summary>
/// <remarks>
/// <para>
/// <c>OwnerScope</c> is <see langword="null"/> for singletons. A singleton is container-wide, so
/// any nested resolve of the same registration — from any scope of that container — is the same
/// construction in progress and therefore a genuine cycle.
/// </para>
/// <para>
/// For scoped services it is the scope that owns the instance: the same service type resolved from
/// a <i>different</i> scope is a different instance, so nesting a child scope stays legal and must
/// not be reported as a cycle.
/// </para>
/// <para>
/// The <see cref="SvcRuntimeRegistration"/> (not the service <see cref="Type"/>) is the identity,
/// because a container may hold several registrations for one service type; only re-entering the
/// same registration is a cycle.
/// </para>
/// </remarks>
internal readonly struct SvcConstructionFrame(
    SvcContainer container,
    SvcScope? ownerScope,
    SvcRuntimeRegistration registration
)
{
    internal readonly SvcContainer Container = container;
    internal readonly SvcScope? OwnerScope = ownerScope;
    internal readonly SvcRuntimeRegistration Registration = registration;
}

/// <summary>
/// Per-thread record of the service constructions currently in progress, used to turn a
/// re-entrant factory into a named <see cref="PicoDiException"/> instead of an uncatchable
/// stack overflow (review-2026-11-02/di-aop-mediator.md, finding 1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why <c>[ThreadStatic]</c> and not <see cref="AsyncLocal{T}"/>:</b> the marker answers "is this
/// registration already on the stack of the construction I am running right now?", and a factory is
/// a synchronous <c>Func&lt;ISvcScope, object&gt;</c>, so the construction chain — and the
/// <c>finally</c> that pops the frame — lives on exactly one thread. A <c>[ThreadStatic]</c> list is
/// therefore the exact scope of the fact being recorded, costs one field read on the slow path and
/// nothing at all on the singleton fast path, needs no locking (one thread touches it), and cannot
/// be inherited by unrelated work.
/// </para>
/// <para>
/// The alternative, <see cref="AsyncLocal{T}"/>, would flow the marker into every continuation the
/// factory starts, which means a factory that blocks on async work (<c>GetAwaiter().GetResult()</c>,
/// <c>Task.Wait</c>) and whose continuation hops to a thread-pool thread would still be reported —
/// and also that two resolutions running concurrently as siblings of one flowed context would append
/// to the same object, so the marker would have to be an immutable/persistent list instead of a
/// stack, and sibling work that is not nested inside ours could be misreported. That is a larger,
/// costlier mechanism for a false-negative case that is not a stack overflow either: a construction
/// that hops threads and re-enters itself on the new thread is not re-entering the same stack — the
/// original frame is blocked, the new one starts a fresh stack — so it cannot blow the stack the way
/// this defect does; it can only deadlock (scoped <c>Lazy</c>) or storm (singleton), which is a
/// different failure that no per-thread marker should claim to explain.
/// </para>
/// <para>
/// Consequence of the choice, stated plainly: if a singleton factory blocks on async work and the
/// continuation (on another thread, with the flowed execution context) resolves the same singleton
/// again, this guard does not see the in-progress frame on that thread and does not report a cycle.
/// The recursion it protects against — the one that kills the process — is exactly the same-thread
/// kind, because only a same-thread re-entry consumes stack.
/// </para>
/// </remarks>
internal static class SvcConstructionGuard
{
    [ThreadStatic]
    private static List<SvcConstructionFrame>? _frames;

    /// <summary>
    /// Number of constructions in progress on the current thread. Zero outside resolution, and zero
    /// again after any factory throws — asserted by the tests so a rejected cycle cannot leave the
    /// marker set.
    /// </summary>
    internal static int DepthForCurrentThread => _frames?.Count ?? 0;

    /// <summary>
    /// True when <paramref name="registration"/> is already being constructed by the current thread
    /// for this container (and, for scoped services, for this scope).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsUnderConstruction(
        SvcContainer container,
        SvcScope? ownerScope,
        SvcRuntimeRegistration registration
    )
    {
        var frames = _frames;
        if (frames is null)
            return false;

        var count = frames.Count;
        for (var i = 0; i < count; i++)
        {
            var frame = frames[i];
            if (
                ReferenceEquals(frame.Registration, registration)
                && ReferenceEquals(frame.Container, container)
                && ReferenceEquals(frame.OwnerScope, ownerScope)
            )
                return true;
        }

        return false;
    }

    /// <summary>
    /// Builds the diagnostic for a cycle: the construction path from the first occurrence of
    /// <paramref name="registration"/> to the re-entry, e.g. <c>A -> B -> A</c>. Called only when
    /// <see cref="IsUnderConstruction"/> is true.
    /// </summary>
    internal static PicoDiException CreateCycleException(
        SvcContainer container,
        SvcScope? ownerScope,
        SvcRuntimeRegistration registration,
        SvcLifetime lifetime
    )
    {
        var frames = _frames;

        var cycleStart = 0;
        if (frames is not null)
        {
            for (var i = 0; i < frames.Count; i++)
            {
                var frame = frames[i];
                if (
                    ReferenceEquals(frame.Registration, registration)
                    && ReferenceEquals(frame.Container, container)
                    && ReferenceEquals(frame.OwnerScope, ownerScope)
                )
                {
                    cycleStart = i;
                    break;
                }
            }
        }

        var path = BuildPath(frames, cycleStart, registration.ServiceType);

        return new PicoDiException(
            $"Circular dependency detected: {path}. Service '{registration.ServiceType.FullName}' "
                + $"is already under construction on this thread, so its {FormatLifetime(lifetime)} factory "
                + "cannot resolve it. Break the cycle (inject a factory, a lazy wrapper, or a "
                + "separate service) — the container cannot construct it."
        );
    }

    /// <summary>
    /// Records <paramref name="registration"/> as under construction on the current thread.
    /// Must be paired with <see cref="Pop"/> in a <c>finally</c>.
    /// </summary>
    internal static void Push(
        SvcContainer container,
        SvcScope? ownerScope,
        SvcRuntimeRegistration registration
    )
    {
        var frames = _frames;
        if (frames is null)
        {
            frames = new List<SvcConstructionFrame>(4);
            _frames = frames;
        }

        frames.Add(new SvcConstructionFrame(container, ownerScope, registration));
    }

    /// <summary>
    /// Clears the innermost construction frame. The list itself is dropped when it empties so a
    /// thread never retains a container or a scope after the outermost resolve returns.
    /// </summary>
    internal static void Pop()
    {
        var frames = _frames;
        if (frames is null || frames.Count == 0)
            return;

        frames.RemoveAt(frames.Count - 1);
        if (frames.Count == 0)
            _frames = null;
    }

    /// <summary>
    /// Lifetime names are literals, not <c>Enum.ToString()</c> (reflection-based, trim-hostile).
    /// </summary>
    private static string FormatLifetime(SvcLifetime lifetime) =>
        lifetime switch
        {
            SvcLifetime.Singleton => "singleton",
            SvcLifetime.Scoped => "scoped",
            SvcLifetime.Transient => "transient",
            _ => "unknown-lifetime",
        };

    /// <summary>
    /// Joins the construction path with <c>" -> "</c> using simple type names — the same convention
    /// the compile-time cycle diagnostic (PICO002) uses, so a runtime cycle and a generated one read
    /// the same. Culture-invariant: only <see cref="string"/> values are concatenated.
    /// </summary>
    private static string BuildPath(
        List<SvcConstructionFrame>? frames,
        int cycleStart,
        Type reenteredServiceType
    )
    {
        var names = new List<string>(4);
        if (frames is not null)
        {
            for (var i = cycleStart; i < frames.Count; i++)
                names.Add(SimpleName(frames[i].Registration.ServiceType));
        }

        names.Add(SimpleName(reenteredServiceType));
        return string.Join(" -> ", names);
    }

    /// <summary>
    /// <c>Type.Name</c> without the generic arity suffix (<c>Repository`1</c> -> <c>Repository</c>).
    /// Ordinal only; no formatting, no reflection.
    /// </summary>
    private static string SimpleName(Type serviceType)
    {
        var name = serviceType.Name;
        var arityMarker = name.IndexOf('`');
        return arityMarker < 0 ? name : name[..arityMarker];
    }
}
