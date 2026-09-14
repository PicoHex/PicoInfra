namespace PicoDI.Abs;

/// <summary>
/// Compile-time registry of hosted service types.
/// Populated by PicoDI.Gen source generator and the <c>RegisterHostedSvc</c>
/// extension methods on <see cref="SvcContainerHostingExtensions"/>.
/// </summary>
/// <remarks>
/// The registry is a process-wide static that is written during registration
/// (including generated <c>[ModuleInitializer]</c>s) and read during
/// <c>Build()</c> on arbitrary threads — all access is synchronized so
/// concurrent <see cref="Register"/> / <see cref="Contains"/> calls cannot
/// corrupt the underlying set.
/// </remarks>
public static class SvcHostedServiceRegistry
{
    private static readonly object SyncRoot = new();
    private static readonly HashSet<Type> Types = new();

    public static void Register(Type hostedType)
    {
        if (hostedType is null)
            throw new ArgumentNullException(nameof(hostedType));
        lock (SyncRoot)
            Types.Add(hostedType);
    }

    public static bool Contains(Type type)
    {
        if (type is null)
            throw new ArgumentNullException(nameof(type));
        lock (SyncRoot)
            return Types.Contains(type);
    }
}
