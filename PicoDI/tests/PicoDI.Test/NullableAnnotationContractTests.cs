namespace PicoDI.Test;

/// <summary>
/// Nullable contract tests for <see cref="ISvcScope"/> TryGet* — the annotation is
/// part of the public API surface: callers must see "success ⇒ non-null" so the
/// documented `if (TryGet…) use(value)` pattern is warning-free (CS8602-free).
/// </summary>
public sealed class NullableAnnotationContractTests
{
    [Test]
    public async Task TryGetService_SuccessPath_IsAnnotatedNotNull()
    {
        var method = typeof(ISvcScope).GetMethod(nameof(ISvcScope.TryGetService))!;
        var attribute = method.GetParameters()[1].GetCustomAttribute<NotNullWhenAttribute>();

        await Assert.That(attribute).IsNotNull();
        await Assert.That(attribute!.ReturnValue).IsTrue();
    }

    [Test]
    public async Task TryGetServices_SuccessPath_IsAnnotatedNotNull()
    {
        var method = typeof(ISvcScope).GetMethod(nameof(ISvcScope.TryGetServices))!;
        var attribute = method.GetParameters()[1].GetCustomAttribute<NotNullWhenAttribute>();

        await Assert.That(attribute).IsNotNull();
        await Assert.That(attribute!.ReturnValue).IsTrue();
    }
}
