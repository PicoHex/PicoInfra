namespace PicoDI.Abs;

/// <summary>
/// Base exception for all PicoDI errors. Not sealed by design —
/// serves as the base class for specialized exception types such as
/// <see cref="HostedSvcRegistrationException"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Circular dependencies — exception type change:</b> a singleton or scoped factory that resolves
/// the service it is constructing now throws this type, naming the cycle (<c>A -&gt; B -&gt; A</c>).
/// Before that diagnostic existed a <i>scoped</i> cycle surfaced as
/// <see cref="InvalidOperationException"/> (<c>ValueFactory attempted to access the Value property of
/// this instance.</c> — the scoped instance is a <c>Lazy&lt;T&gt;</c>), and a singleton cycle aborted the
/// process with a stack overflow. This type derives from <see cref="Exception"/>, not from
/// <see cref="InvalidOperationException"/>, so a <c>catch (InvalidOperationException)</c> around a
/// scoped resolve no longer catches a cycle: catch <see cref="PicoDiException"/> (or
/// <see cref="Exception"/>) instead. Singleton cycles went from a process abort to the same type.
/// </para>
/// </remarks>
public class PicoDiException : Exception
{
    public PicoDiException() { }

    public PicoDiException(string message)
        : base(message) { }

    public PicoDiException(string message, Exception innerException)
        : base(message, innerException) { }
}
