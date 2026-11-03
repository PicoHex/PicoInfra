namespace PicoAop.Tests;

/// <summary>
/// The proxy re-declares every member of the service, so the declarations it
/// emits have to carry the member's nullable annotations. They were rendered with
/// the plain fully-qualified format, which drops <c>?</c>: a parameter
/// <c>string?</c> became <c>string</c>, and the consumer's build reported
/// <c>CS8767</c>/<c>CS8766</c> in <c>PicoAop.Invocations.g.cs</c> — a warning in a
/// file nobody can edit (review-2026-11-02/rev-aop-4.md, defect 2; probe P2).
/// </summary>
public class ProxyNullabilityTests : GeneratorTestBase
{
    private const string Preamble = """
        #nullable enable
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;
        using PicoAop.Abs;

        public class CountingInterceptor : InterceptorBase
        {
            public override TResult Invoke<TInvocation, TResult>(
                TInvocation inv,
                Func<TInvocation, TResult> next
            ) => next(inv);
        }

        interface IDummyContainer
        {
            IDummyContainer Register<T, TImpl>() where T : class where TImpl : class;
        }

        static class Ext
        {
            internal static IDummyContainer InterceptBy<T>(this IDummyContainer c)
                where T : class => c;
        }
        """;

    private const string Registration = """

        static class Registration
        {
            static void Do(IDummyContainer c)
            {
                c.Register<IC, C>().InterceptBy<CountingInterceptor>();
            }
        }
        """;

    /// <summary>
    /// probe P2 of the review, both halves at once: a nullable parameter
    /// (<c>CS8767</c>) and a nullable return type (<c>CS8766</c>).
    /// </summary>
    [Test]
    public async Task NullableParameterAndReturnType_CompileWithoutNullableWarnings()
    {
        var result = CompileGenerated(
            Preamble
                + """

                public interface IA
                {
                    string? Find(string? key);
                }

                public interface IB
                {
                    string? Find(string? key);
                }

                public interface IC : IA, IB { }

                public class C : IC
                {
                    public string? Find(string? key) => key;
                }
                """
                + Registration
        );
        Console.WriteLine("----- generated -----");
        Console.WriteLine(result.GeneratedOutput);
        Console.WriteLine("----- errors -----");
        Console.WriteLine(result.ErrorText);
        Console.WriteLine("----- warnings -----");
        Console.WriteLine(result.WarningText);

        var declaration = result
            .GeneratedOutput.Split('\n')
            .FirstOrDefault(line => line.Contains(" Find(string", StringComparison.Ordinal));
        Console.WriteLine($"DECLARATION: {declaration}");
        await Assert.That(declaration).IsNotNull();
        await Assert.That(declaration!.Contains("string? Find", StringComparison.Ordinal)).IsTrue();
        await Assert.That(declaration.Contains("string? key", StringComparison.Ordinal)).IsTrue();

        await Assert.That(result.WarningText).IsEqualTo(string.Empty);
        await Assert.That(result.ErrorText).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// The same annotation on the other declaration shapes: a property, an
    /// indexer, an event, and a value parameter of a generic method
    /// (<c>T?</c>, which needs the <c>where T : class</c> the proxy repeats).
    /// </summary>
    [Test]
    public async Task NullablePropertyIndexerEventAndTypeParameter_CompileWithoutNullableWarnings()
    {
        var result = CompileGenerated(
            Preamble
                + """

                public interface IA
                {
                    string? Name { get; }

                    string? Note { get; set; }

                    string? this[int index] { get; }

                    event EventHandler? Changed;

                    T? Echo<T>(T? value)
                        where T : class;
                }

                public interface IB
                {
                    string? Name { get; }

                    string? Note { get; set; }

                    string? this[int index] { get; }

                    event EventHandler? Changed;

                    T? Echo<T>(T? value)
                        where T : class;
                }

                public interface IC : IA, IB { }

                public class C : IC
                {
                    public string? Name => null;

                    public string? Note { get; set; }

                    public string? this[int index] => null;

                    public event EventHandler? Changed;

                    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);

                    public T? Echo<T>(T? value)
                        where T : class => value;
                }
                """
                + Registration
        );
        Console.WriteLine("----- generated -----");
        Console.WriteLine(result.GeneratedOutput);
        Console.WriteLine("----- errors -----");
        Console.WriteLine(result.ErrorText);
        Console.WriteLine("----- warnings -----");
        Console.WriteLine(result.WarningText);

        await Assert.That(result.WarningText).IsEqualTo(string.Empty);
        await Assert.That(result.ErrorText).IsEqualTo(string.Empty);

        // The declaration shapes, all of them carrying the annotation.
        await Assert
            .That(result.GeneratedOutput.Contains("string? Name", StringComparison.Ordinal))
            .IsTrue();
        await Assert
            .That(
                result.GeneratedOutput.Contains("string? this[int index]", StringComparison.Ordinal)
            )
            .IsTrue();
        await Assert
            .That(
                result.GeneratedOutput.Contains(
                    "System.EventHandler? Changed",
                    StringComparison.Ordinal
                )
            )
            .IsTrue();
        await Assert
            .That(result.GeneratedOutput.Contains("T? Echo<T>(T? value)", StringComparison.Ordinal))
            .IsTrue();
    }

    /// <summary>
    /// A nullable result behind a <c>Task</c>: the invocation struct's
    /// <c>IInvocation&lt;TResult&gt;</c>, its <c>Result</c> property and the
    /// interceptor chain's delegate all carry the result type, so all of them
    /// have to agree with the member's annotation.
    /// </summary>
    [Test]
    public async Task NullableAsyncResult_CompilesWithoutNullableWarnings()
    {
        var result = CompileGenerated(
            Preamble
                + """

                public interface IA
                {
                    Task<string?> LoadAsync(string? key);
                }

                public interface IB
                {
                    Task<string?> LoadAsync(string? key);
                }

                public interface IC : IA, IB { }

                public class C : IC
                {
                    public Task<string?> LoadAsync(string? key) => Task.FromResult(key);
                }
                """
                + Registration
        );
        Console.WriteLine("----- generated -----");
        Console.WriteLine(result.GeneratedOutput);
        Console.WriteLine("----- errors -----");
        Console.WriteLine(result.ErrorText);
        Console.WriteLine("----- warnings -----");
        Console.WriteLine(result.WarningText);

        await Assert.That(result.WarningText).IsEqualTo(string.Empty);
        await Assert.That(result.ErrorText).IsEqualTo(string.Empty);

        // The result type reaches the invocation struct as well: `IInvocation<T>`
        // and its `Result` member would otherwise say `string`, and every step of
        // the chain would disagree with the member's own annotation.
        await Assert
            .That(result.GeneratedOutput.Contains("IInvocation<string?>", StringComparison.Ordinal))
            .IsTrue();
        await Assert
            .That(
                result
                    .GeneratedOutput.Split('\n')
                    .Any(line =>
                        line.Contains(
                            "Task<string?> LoadAsync(string? key)",
                            StringComparison.Ordinal
                        )
                    )
            )
            .IsTrue();
    }
}
