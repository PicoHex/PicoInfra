namespace PicoAop.Tests;

/// <summary>
/// Two base interfaces can legally expose the same member name with incompatible
/// signatures — <c>IA { int V {get;} }</c> and <c>IB { string V {get;} }</c> —
/// and a class has to implement both explicitly. The generator used to throw the
/// second member away (the property part of <c>SignatureKey</c> had no return
/// type), so the proxy compiled to CS0738/CS0229 in
/// <c>obj/Generated/PicoAop.Invocations.g.cs</c> with no diagnostic at all
/// (review-2026-11-02/rev-aop-d603729.md, defect 2).
/// </summary>
public class ConflictingInterfaceMemberTests : GeneratorTestBase
{
    private const string Preamble = """
        #nullable enable
        using System;
        using System.Collections.Generic;
        using PicoAop.Abs;

        public class CountingInterceptor : InterceptorBase
        {
            public static readonly List<string> Calls = new();

            public override TResult Invoke<TInvocation, TResult>(
                TInvocation inv,
                Func<TInvocation, TResult> next
            )
            {
                Calls.Add("sync:" + inv.MethodName);
                return next(inv);
            }

            public override void InvokeVoid<TInvocation>(
                TInvocation inv,
                Func<TInvocation, object?> next
            )
            {
                Calls.Add("void:" + inv.MethodName);
                next(inv);
            }
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

    private const string PropertyShape = """

        public interface IA
        {
            int V { get; }
        }

        public interface IB
        {
            string V { get; }
        }

        public interface IC : IA, IB { }

        public class C : IC
        {
            int IA.V => 1;
            string IB.V => "s";
        }

        static class Registration
        {
            static void Do(IDummyContainer c)
            {
                c.Register<IC, C>().InterceptBy<CountingInterceptor>();
            }
        }
        """;

    [Test]
    public async Task SameNamedPropertiesWithDifferentTypes_CompileAndBothAreImplemented()
    {
        // RED (before the fix, measured on d603729): the property key was
        // P:{Name}:{paramCount}:{param types} — no return type — so Distinct
        // dropped IB.V and the proxy implemented only IA.V:
        //   CS0738: 'Intercepted_IC_CountingInterceptor' does not implement
        //           interface member 'IB.V' ...
        //   CS0229: Ambiguity between 'IA.V' and 'IB.V'
        //   diags=[]
        var result = CompileGenerated(Preamble + PropertyShape);
        Console.WriteLine("----- generated -----");
        Console.WriteLine(result.GeneratedOutput);
        Console.WriteLine("----- errors -----");
        Console.WriteLine(result.ErrorText);
        Console.WriteLine("----- warnings -----");
        Console.WriteLine(result.WarningText);
        Console.WriteLine($"DIAGS: {string.Join("|", result.DiagnosticText)}");

        // Both members exist and each reads the inner instance through its own
        // interface: a class cannot declare two properties with one name and no
        // qualifier, so one of them is an explicit implementation and the plain
        // `_inner.V` (CS0229) is never used.
        await Assert.That(result.GeneratedOutput.Contains("((global::IA)_inner).V")).IsTrue();
        await Assert.That(result.GeneratedOutput.Contains("((global::IB)_inner).V")).IsTrue();
        await Assert
            .That(
                result.GeneratedOutput.Contains("global::IB.V")
                    || result.GeneratedOutput.Contains("global::IA.V")
            )
            .IsTrue();

        // A warning names the collision instead of failing silently.
        await Assert.That(result.DiagnosticIds.Count(id => id == "PICO113")).IsEqualTo(1);
        await Assert.That(result.DiagnosticText.Contains("IA.V")).IsTrue();
        await Assert.That(result.DiagnosticText.Contains("IB.V")).IsTrue();

        await Assert.That(result.WarningText).IsEqualTo(string.Empty);
        await Assert.That(result.ErrorText).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task SameNamedPropertiesWithDifferentTypes_BothReachTheInnerInstance()
    {
        var assembly = CompileAndLoad(Preamble + PropertyShape, out _);
        var proxyType = assembly.GetType("PicoAop.Generated.Intercepted_IC_CountingInterceptor")!;
        var proxy = Activator.CreateInstance(
            proxyType,
            Activator.CreateInstance(assembly.GetType("C")!),
            Activator.CreateInstance(assembly.GetType("CountingInterceptor")!)
        )!;

        var ia = assembly.GetType("IA")!;
        var ib = assembly.GetType("IB")!;
        await Assert.That(ia.GetProperty("V")!.GetValue(proxy)).IsEqualTo(1);
        await Assert.That(ib.GetProperty("V")!.GetValue(proxy)).IsEqualTo("s");

        var calls = ReadCalls(assembly);
        Console.WriteLine($"CALLS: {string.Join(",", calls)}");
        // Colliding members cannot be routed: they are delegated, not falsely
        // advertised as intercepted.
        await Assert.That(calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SameNamedMethodsWithDifferentReturnTypes_CompileAndBothAreImplemented()
    {
        // Same defect, method side: the method part of SignatureKey had no
        // return type either, so IB.M was dropped and the proxy implemented only
        // IA.M -> CS0535.
        var result = CompileGenerated(
            Preamble
                + """

                public interface IA
                {
                    int M();
                }

                public interface IB
                {
                    string M();
                }

                public interface IC : IA, IB { }

                public class C : IC
                {
                    int IA.M() => 1;
                    string IB.M() => "s";
                }

                static class Registration
                {
                    static void Do(IDummyContainer c)
                    {
                        c.Register<IC, C>().InterceptBy<CountingInterceptor>();
                    }
                }
                """
        );
        Console.WriteLine("----- generated -----");
        Console.WriteLine(result.GeneratedOutput);
        Console.WriteLine(result.ErrorText);
        Console.WriteLine($"DIAGS: {string.Join("|", result.DiagnosticText)}");

        await Assert.That(result.GeneratedOutput.Contains("((global::IA)_inner).M()")).IsTrue();
        await Assert.That(result.GeneratedOutput.Contains("((global::IB)_inner).M()")).IsTrue();
        await Assert
            .That(
                result.GeneratedOutput.Contains("global::IB.M")
                    || result.GeneratedOutput.Contains("global::IA.M")
            )
            .IsTrue();
        await Assert.That(result.DiagnosticIds.Count(id => id == "PICO113")).IsEqualTo(1);
        await Assert.That(result.WarningText).IsEqualTo(string.Empty);
        await Assert.That(result.ErrorText).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task SameNamedEventsAndIndexers_CompileAndBothAreImplemented()
    {
        // The same collision, event and indexer side: this shape crashed the
        // generator outright (CS8785, NullReferenceException in InvocationNames
        // for an event member) before the fix.
        var result = CompileGenerated(
            Preamble
                + """

                public interface IA
                {
                    event EventHandler Changed;
                    int this[int i] { get; }
                }

                public interface IB
                {
                    event Action Changed;
                    string this[int i] { get; }
                }

                public interface IC : IA, IB { }

                public class C : IC
                {
                    event EventHandler IA.Changed { add { } remove { } }
                    event Action IB.Changed { add { } remove { } }
                    int IA.this[int i] => 1;
                    string IB.this[int i] => "s";
                }

                static class Registration
                {
                    static void Do(IDummyContainer c)
                    {
                        c.Register<IC, C>().InterceptBy<CountingInterceptor>();
                    }
                }
                """
        );
        Console.WriteLine("----- generated -----");
        Console.WriteLine(result.GeneratedOutput);
        Console.WriteLine(result.ErrorText);
        Console.WriteLine($"DIAGS: {string.Join("|", result.DiagnosticText)}");

        await Assert.That(result.DiagnosticIds.Contains("CS8785")).IsFalse();
        await Assert.That(result.GeneratedOutput.Contains("global::IB.this[int i]")).IsTrue();
        await Assert.That(result.GeneratedOutput.Contains("global::IB.Changed")).IsTrue();
        // One warning per colliding slot: the event, and the indexer.
        await Assert.That(result.DiagnosticIds.Count(id => id == "PICO113")).IsEqualTo(2);
        await Assert.That(result.WarningText).IsEqualTo(string.Empty);
        await Assert.That(result.ErrorText).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task IdenticalMemberFromTwoInterfaces_IsEmittedOnce_Implicitly()
    {
        // Regression guard for the dedupe: the two members are the *same*
        // signature, so one implicit member implements both interfaces — it must
        // not be duplicated (CS0102) and must not be reported as a collision.
        var result = CompileGenerated(
            Preamble
                + """

                public interface IA
                {
                    int V { get; }
                    void Do();
                }

                public interface IB
                {
                    int V { get; }
                    void Do();
                }

                public interface IC : IA, IB { }

                public class C : IC
                {
                    public int V => 1;
                    public void Do() { }
                }

                static class Registration
                {
                    static void Do(IDummyContainer c)
                    {
                        c.Register<IC, C>().InterceptBy<CountingInterceptor>();
                    }
                }
                """
        );
        Console.WriteLine("----- generated -----");
        Console.WriteLine(result.GeneratedOutput);
        Console.WriteLine(result.ErrorText);
        Console.WriteLine($"DIAGS: {string.Join("|", result.DiagnosticText)}");

        await Assert.That(result.DiagnosticIds.Contains("PICO113")).IsFalse();
        await Assert.That(result.WarningText).IsEqualTo(string.Empty);
        await Assert.That(result.ErrorText).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// An explicit interface implementation inherits its constraints from the
    /// interface member, so a <c>where</c> clause on it is CS0460. The colliding
    /// member is therefore emitted without one — <c>where T : class</c>/<c>struct</c>
    /// is legal there, which is why the plainer shapes below never caught it
    /// (review-2026-11-02/rev-aop-3c.md, D2).
    /// </summary>
    [Test]
    public async Task SameNamedGenericMethodsWithConstraints_ExplicitImplementationCarriesNoWhereClause()
    {
        var result = CompileGenerated(
            Preamble
                + """

                public interface IA
                {
                    int M<T>()
                        where T : IComparable<T>;
                }

                public interface IB
                {
                    string M<T>()
                        where T : IComparable<T>;
                }

                public interface IC : IA, IB { }

                public class C : IC
                {
                    int IA.M<T>() => 1;
                    string IB.M<T>() => "s";
                }

                static class Registration
                {
                    static void Do(IDummyContainer c)
                    {
                        c.Register<IC, C>().InterceptBy<CountingInterceptor>();
                    }
                }
                """
        );
        Console.WriteLine("----- generated -----");
        Console.WriteLine(result.GeneratedOutput);
        Console.WriteLine("----- errors -----");
        Console.WriteLine(result.ErrorText);
        Console.WriteLine("----- warnings -----");
        Console.WriteLine(result.WarningText);
        Console.WriteLine($"DIAGS: {string.Join("|", result.DiagnosticText)}");

        // Both members are emitted, one of them explicitly, and the collision is
        // reported instead of the member being dropped.
        await Assert.That(result.DiagnosticIds.Count(id => id == "PICO113")).IsEqualTo(1);
        await Assert
            .That(
                result.GeneratedOutput.Contains("global::IB.M<T>")
                    || result.GeneratedOutput.Contains("global::IA.M<T>")
            )
            .IsTrue();

        // The explicitly-implemented member repeats no `where` clause: constraints
        // for an explicit implementation are inherited (CS0460).
        var explicitLines = result
            .GeneratedOutput.Split('\n')
            .Where(line =>
                line.Contains("global::IB.M<T>(", StringComparison.Ordinal)
                || line.Contains("global::IA.M<T>(", StringComparison.Ordinal)
            )
            .ToList();
        Console.WriteLine($"EXPLICIT LINES: {string.Join(" || ", explicitLines)}");
        await Assert.That(explicitLines.Count).IsGreaterThan(0);
        await Assert
            .That(explicitLines.Any(line => line.Contains("where", StringComparison.Ordinal)))
            .IsFalse();

        await Assert.That(result.WarningText).IsEqualTo(string.Empty);
        await Assert.That(result.ErrorText).IsEqualTo(string.Empty);
    }

    private static List<string> ReadCalls(Assembly assembly) =>
        (List<string>)assembly.GetType("CountingInterceptor")!.GetField("Calls")!.GetValue(null)!;
}
