namespace PicoDI.Abs;

/// <summary>
/// Thrown when compile-time generated registrations are required but not available.
/// Enforces that PicoDI.Gen must run and ConfigureGeneratedServices() must be called.
/// </summary>
public sealed class SourceGeneratorRequiredException : InvalidOperationException
{
    public SourceGeneratorRequiredException()
        : base(
            "Compile-time generated registrations are required. Ensure PicoDI.Gen runs and call ConfigureGeneratedServices()."
        ) { }

    public SourceGeneratorRequiredException(string? message)
        : base(message) { }

    public SourceGeneratorRequiredException(string? message, Exception? inner)
        : base(message, inner) { }
}
