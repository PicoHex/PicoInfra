namespace PicoAop.Tests;

/// <summary>
/// The signature key has to follow the identity the compiler uses, not the text
/// a type happens to be spelled with. <c>dynamic</c> and <c>object</c> are one
/// and the same type to the compiler: a member declared <c>dynamic M()</c> in one
/// base interface and <c>object M()</c> in another is a single implementation
/// (<c>C</c> satisfies both with one member, no CS0535/CS0738), so the proxy must
/// emit it once and route it once.
/// <para>
/// The key rendered <c>PositionalType</c> through <c>ToDisplayParts</c>, so the
/// twin keyed as <c>dynamic</c> vs <c>object</c>, took the collision branch and
/// delegated both members: a false <c>PICO113</c> plus interception silently
/// lost, with zero compiler diagnostics to show it (review-2026-11-02/rev-aop-4.md,
/// defect 1; probe P8).
/// </para>
/// </summary>
public class SignatureKeyIdentityTests : GeneratorTestBase
{
    private const string Preamble = """
        #nullable enable
        using System;
        using System.Collections.Generic;
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
    /// probe P8 of the review: <c>IA { dynamic M(int x); }</c> and
    /// <c>IB { object M(int x); }</c> — one signature, one implementation.
    /// </summary>
    [Test]
    public async Task DynamicAndObjectReturnTwin_IsOneInterceptedMember()
    {
        var result = CompileGenerated(
            Preamble
                + """

                public interface IA
                {
                    dynamic M(int x);
                }

                public interface IB
                {
                    object M(int x);
                }

                public interface IC : IA, IB { }

                public class C : IC
                {
                    public dynamic M(int x) => x;
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
        Console.WriteLine($"DIAGS: {string.Join("|", result.DiagnosticText)}");

        var declarations = result
            .GeneratedOutput.Split('\n')
            .Where(line => line.Contains(" M(int x)", StringComparison.Ordinal))
            .ToList();
        Console.WriteLine($"DECLARATIONS: {string.Join(" || ", declarations)}");
        await Assert.That(declarations.Count).IsEqualTo(1);

        // One member, implemented implicitly: the colliding branch would spell the
        // second one `global::IA.M`, and the choice of which one wins is arbitrary.
        await Assert
            .That(
                result.GeneratedOutput.Contains("global::IA.M(", StringComparison.Ordinal)
                    || result.GeneratedOutput.Contains("global::IB.M(", StringComparison.Ordinal)
            )
            .IsFalse();

        // The member is routed: the collision branch delegates it, which the
        // consumer cannot see in the build at all.
        await Assert
            .That(
                result.GeneratedOutput.Contains(
                    "new Invocation_IC_M_Int32",
                    StringComparison.Ordinal
                )
            )
            .IsTrue();
        await Assert.That(result.DiagnosticIds.Contains("PICO113")).IsFalse();
        await Assert
            .That(result.GeneratedOutput.Contains("!0", StringComparison.Ordinal))
            .IsFalse();

        await Assert.That(result.WarningText).IsEqualTo(string.Empty);
        await Assert.That(result.ErrorText).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task DynamicAndObjectParameterTwin_IsOneInterceptedMember()
    {
        var result = CompileGenerated(
            Preamble
                + """

                public interface IA
                {
                    int M(dynamic x);
                }

                public interface IB
                {
                    int M(object x);
                }

                public interface IC : IA, IB { }

                public class C : IC
                {
                    public int M(object x) => 1;
                }
                """
                + Registration
        );
        Console.WriteLine("----- generated -----");
        Console.WriteLine(result.GeneratedOutput);
        Console.WriteLine("----- errors -----");
        Console.WriteLine(result.ErrorText);
        Console.WriteLine($"DIAGS: {string.Join("|", result.DiagnosticText)}");

        var declarations = result
            .GeneratedOutput.Split('\n')
            .Where(line => line.Contains("int M(", StringComparison.Ordinal))
            .ToList();
        Console.WriteLine($"DECLARATIONS: {string.Join(" || ", declarations)}");
        await Assert.That(declarations.Count).IsEqualTo(1);
        await Assert
            .That(
                result.GeneratedOutput.Contains("global::IA.M(", StringComparison.Ordinal)
                    || result.GeneratedOutput.Contains("global::IB.M(", StringComparison.Ordinal)
            )
            .IsFalse();
        await Assert.That(result.DiagnosticIds.Contains("PICO113")).IsFalse();
        await Assert
            .That(result.GeneratedOutput.Contains("new Invocation_IC_M", StringComparison.Ordinal))
            .IsTrue();
        await Assert.That(result.WarningText).IsEqualTo(string.Empty);
        await Assert.That(result.ErrorText).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// The same identity, spelled differently: <c>nint</c> and
    /// <c>System.IntPtr</c> are one type (CS0111 refuses to overload them), so a
    /// twin declared with one in each base interface is one intercepted member
    /// too.
    /// </summary>
    [Test]
    public async Task NativeIntAndIntPtrTwin_IsOneInterceptedMember()
    {
        var result = CompileGenerated(
            Preamble
                + """

                public interface IA
                {
                    int M(nint x);
                }

                public interface IB
                {
                    int M(IntPtr x);
                }

                public interface IC : IA, IB { }

                public class C : IC
                {
                    public int M(nint x) => 1;
                }
                """
                + Registration
        );
        Console.WriteLine("----- generated -----");
        Console.WriteLine(result.GeneratedOutput);
        Console.WriteLine("----- errors -----");
        Console.WriteLine(result.ErrorText);
        Console.WriteLine($"DIAGS: {string.Join("|", result.DiagnosticText)}");

        var declarations = result
            .GeneratedOutput.Split('\n')
            .Where(line => line.Contains("int M(", StringComparison.Ordinal))
            .ToList();
        Console.WriteLine($"DECLARATIONS: {string.Join(" || ", declarations)}");
        await Assert.That(declarations.Count).IsEqualTo(1);
        await Assert.That(result.DiagnosticIds.Contains("PICO113")).IsFalse();
        await Assert
            .That(result.GeneratedOutput.Contains("new Invocation_IC_M", StringComparison.Ordinal))
            .IsTrue();
        await Assert.That(result.WarningText).IsEqualTo(string.Empty);
        await Assert.That(result.ErrorText).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// The same identity, one level down: <c>List&lt;dynamic&gt;</c> and
    /// <c>List&lt;object&gt;</c> are the same type too, so a normalisation that
    /// only looked at the top-level type symbol would still split this pair.
    /// </summary>
    [Test]
    public async Task NestedDynamicAndObjectTwin_IsOneInterceptedMember()
    {
        var result = CompileGenerated(
            Preamble
                + """

                public interface IA
                {
                    int M<T>(List<dynamic> x);
                }

                public interface IB
                {
                    int M<U>(List<object> y);
                }

                public interface IC : IA, IB { }

                public class C : IC
                {
                    public int M<T>(List<object> x) => 1;
                }
                """
                + Registration
        );
        Console.WriteLine("----- generated -----");
        Console.WriteLine(result.GeneratedOutput);
        Console.WriteLine("----- errors -----");
        Console.WriteLine(result.ErrorText);
        Console.WriteLine($"DIAGS: {string.Join("|", result.DiagnosticText)}");

        var declarations = result
            .GeneratedOutput.Split('\n')
            .Where(line => line.Contains("int M<", StringComparison.Ordinal))
            .ToList();
        Console.WriteLine($"DECLARATIONS: {string.Join(" || ", declarations)}");
        await Assert.That(declarations.Count).IsEqualTo(1);
        await Assert.That(result.DiagnosticIds.Contains("PICO113")).IsFalse();
        await Assert
            .That(result.GeneratedOutput.Contains("new Invocation_IC_M", StringComparison.Ordinal))
            .IsTrue();
        await Assert.That(result.WarningText).IsEqualTo(string.Empty);
        await Assert.That(result.ErrorText).IsEqualTo(string.Empty);
    }
}
