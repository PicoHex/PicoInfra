namespace PicoDI.Test;

/// <summary>
/// Pins the owner-decided disposal contract: <b>PicoDI is async-only for disposal</b>.
/// <para>
/// <see cref="ISvcContainer"/>, <see cref="ISvcScope"/> and <see cref="IHost"/> implement
/// <see cref="IAsyncDisposable"/> and nothing in <c>PicoDI/src</c> implements
/// <see cref="IDisposable"/>. That is a contract, not an omission. A synchronous <c>Dispose</c>
/// facade would either block on async work (<c>Wait()</c>/<c>GetAwaiter().GetResult()</c> — a classic
/// thread-pool starvation and sync-context deadlock) or silently skip it, and hosted services stop
/// through an asynchronous <see cref="IHost.StopAsync"/> whose dependencies are themselves
/// <see cref="IAsyncDisposable"/>, so their shutdown cannot be honoured synchronously without
/// blocking or dropping work.
/// </para>
/// <para>
/// This test is the tripwire for the "helpful" change: if it is red, the fix is to remove
/// <see cref="IDisposable"/>, never to relax the assertion.
/// </para>
/// </summary>
public sealed class AsyncOnlyDisposalContractTests
{
    [Test]
    public async Task DisposalContract_IsAsyncOnly_NoContractTypeImplementsIDisposable()
    {
        // The three public contract types plus their shipping implementations: the decision covers
        // the whole surface, so a synchronous Dispose added to either half turns this red.
        (string Name, Type Type)[] asyncOnlyTypes =
        [
            (nameof(ISvcContainer), typeof(ISvcContainer)),
            (nameof(ISvcScope), typeof(ISvcScope)),
            (nameof(IHost), typeof(IHost)),
            (nameof(SvcContainer), typeof(SvcContainer)),
            (nameof(SvcScope), typeof(SvcScope)),
            (nameof(SvcHost), typeof(SvcHost)),
        ];

        foreach (var (name, type) in asyncOnlyTypes)
        {
            // Positive control: without it a type that dropped out of IAsyncDisposable altogether
            // would satisfy the negative assertion below while breaking the contract differently.
            await Assert
                .That(typeof(IAsyncDisposable).IsAssignableFrom(type))
                .IsTrue()
                .Because($"{name} must stay disposable through IAsyncDisposable");

            // The pin. Adding IDisposable to any of these is the defect; a synchronous Dispose
            // would block on async shutdown or drop it, so it is refused by design.
            await Assert
                .That(typeof(IDisposable).IsAssignableFrom(type))
                .IsFalse()
                .Because(
                    $"{name} must not implement IDisposable: PicoDI is async-only for disposal by "
                        + "design. If you are reading this because you added a synchronous Dispose, "
                        + "remove it — this assertion is deliberate and will not be relaxed."
                );
        }
    }
}
