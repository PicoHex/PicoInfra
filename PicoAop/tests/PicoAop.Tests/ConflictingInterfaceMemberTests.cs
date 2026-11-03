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

    /// <summary>
    /// Constraints do not participate in the C# signature, so two interface
    /// members that differ only by their type-parameter constraints still
    /// occupy one name slot — but they are different implementations, and the
    /// proxy can satisfy only one of them implicitly. The plan used to key a
    /// member on name + arity + parameter types + return type, so the pair
    /// looked like one member: the second was dropped, no PICO113 was reported,
    /// and the single emitted <c>where T : class</c> did not match
    /// <c>IB.M&lt;T&gt;() where T : struct</c> (CS0425, zero diagnostics).
    /// review-2026-11-02/rev-aop-3c.md, D3.
    /// </summary>
    [Test]
    public async Task SameNamedGenericMethodsDifferingOnlyByConstraints_ReportPico113AndCompile()
    {
        var result = CompileGenerated(
            Preamble
                + """

                public interface IA
                {
                    void M<T>()
                        where T : class;
                }

                public interface IB
                {
                    void M<T>()
                        where T : struct;
                }

                public interface IC : IA, IB { }

                public class C : IC
                {
                    void IA.M<T>() { }
                    void IB.M<T>() { }
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

        // The pair must be reported as one generated-name collision instead of
        // one member being silently dropped.
        await Assert.That(result.DiagnosticIds.Count(id => id == "PICO113")).IsEqualTo(1);
        await Assert.That(result.DiagnosticText.Contains("IA.M<T>()")).IsTrue();
        await Assert.That(result.DiagnosticText.Contains("IB.M<T>()")).IsTrue();

        // Both members are emitted: one implicit with its own constraint, one
        // explicit (constraints inherited) — so neither interface member is
        // implemented with the wrong constraint.
        var implicitLines = result
            .GeneratedOutput.Split('\n')
            .Where(line => line.Contains("public void M<T>()", StringComparison.Ordinal))
            .ToList();
        var explicitLines = result
            .GeneratedOutput.Split('\n')
            .Where(line => line.Contains("global::IB.M<T>()", StringComparison.Ordinal))
            .ToList();
        Console.WriteLine($"IMPLICIT: {string.Join(" || ", implicitLines)}");
        Console.WriteLine($"EXPLICIT: {string.Join(" || ", explicitLines)}");
        await Assert.That(implicitLines.Count).IsEqualTo(1);
        await Assert
            .That(implicitLines[0].Contains("where T : class", StringComparison.Ordinal))
            .IsTrue();
        await Assert.That(explicitLines.Count).IsEqualTo(1);
        await Assert.That(explicitLines[0].Contains("where", StringComparison.Ordinal)).IsFalse();

        await Assert.That(result.WarningText).IsEqualTo(string.Empty);
        await Assert.That(result.ErrorText).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// C# keys a generic signature on the *position* of its type parameters, not
    /// on the names the two interfaces happened to choose: <c>IA.M&lt;T&gt;(T)</c>
    /// and <c>IB.M&lt;U&gt;(U)</c> are one signature, which a single method
    /// implements. The planner keyed on the names (<c>ParameterTypes</c> printed
    /// <c>T</c> vs <c>U</c>), so the pair looked like two members and both were
    /// emitted:
    /// <code>
    /// CS0111: 'Intercepted_IC_CountingInterceptor' already defines a member
    ///         called 'M' with the same parameter types
    /// CS0121: The call is ambiguous between 'IA.M&lt;T&gt;(T)' and 'IB.M&lt;U&gt;(U)'
    /// </code>
    /// with zero generator diagnostics — a silently non-compiling proxy.
    /// Pre-existing (it reproduces with no constraints at all).
    /// review-2026-11-02/rev-aop-5c.md, D-a; probe A1b.
    /// </summary>
    [Test]
    public async Task SameNamedGenericMethodsWithRenamedTypeParameters_CompileAsOneMember()
    {
        var result = CompileGenerated(
            Preamble
                + """

                public interface IA
                {
                    int M<T>(T x);
                }

                public interface IB
                {
                    int M<U>(U x);
                }

                public interface IC : IA, IB { }

                public class C : IC
                {
                    public int M<T>(T x) => 1;
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
        Console.WriteLine($"DIAGS: {string.Join("|", result.DiagnosticText)}");

        // One member, not two: no second, explicitly-qualified declaration.
        var declarations = result
            .GeneratedOutput.Split('\n')
            .Where(line => line.Contains("public int M<", StringComparison.Ordinal))
            .ToList();
        Console.WriteLine($"DECLARATIONS: {string.Join(" || ", declarations)}");
        await Assert.That(declarations.Count).IsEqualTo(1);
        await Assert
            .That(result.GeneratedOutput.Contains("global::IB.M<", StringComparison.Ordinal))
            .IsFalse();

        // Nothing collided and nothing was lost: the member is still routed.
        await Assert.That(result.DiagnosticIds.Contains("PICO113")).IsFalse();
        await Assert.That(result.GeneratedOutput.Contains("new Invocation_IC_M")).IsTrue();

        // The positional form is a key, not text: it never reaches the file.
        await Assert
            .That(result.GeneratedOutput.Contains("!0", StringComparison.Ordinal))
            .IsFalse();

        await Assert.That(result.WarningText).IsEqualTo(string.Empty);
        await Assert.That(result.ErrorText).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// The same signature, with constraints that only differ by the type
    /// parameter's name: <c>where T : IComparable&lt;T&gt;</c> and
    /// <c>where U : IComparable&lt;U&gt;</c> are the same constraint set, so one
    /// implicit implementation satisfies both. <c>ConstraintKey</c> printed the
    /// constraint types by name, so the pair keyed differently and took the
    /// collision branch: <c>PICO113</c> for a collision that does not exist, and
    /// both members delegated — the calls still compile, but the interceptor is
    /// silently skipped. review-2026-11-02/rev-aop-5c.md, D-b; probe A2/P2.
    /// </summary>
    [Test]
    public async Task ConstraintRenamedGenericMethods_StayOneInterceptedMember()
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
                    int M<U>()
                        where U : IComparable<U>;
                }

                public interface IC : IA, IB { }

                public class C : IC
                {
                    public int M<T>()
                        where T : IComparable<T> => 1;
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
        Console.WriteLine($"DIAGS: {string.Join("|", result.DiagnosticText)}");

        // No phantom collision for one signature.
        await Assert.That(result.DiagnosticIds.Contains("PICO113")).IsFalse();

        // One member, and it is routed (delegating both is the silent half of
        // the defect: the code compiles either way).
        var declarations = result
            .GeneratedOutput.Split('\n')
            .Where(line => line.Contains("public int M<", StringComparison.Ordinal))
            .ToList();
        Console.WriteLine($"DECLARATIONS: {string.Join(" || ", declarations)}");
        await Assert.That(declarations.Count).IsEqualTo(1);
        await Assert.That(result.GeneratedOutput.Contains("new Invocation_IC_M")).IsTrue();
        await Assert
            .That(result.GeneratedOutput.Contains("!0", StringComparison.Ordinal))
            .IsFalse();

        await Assert.That(result.WarningText).IsEqualTo(string.Empty);
        await Assert.That(result.ErrorText).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// A constraint list is a set: <c>where T : IDisposable, IComparable&lt;T&gt;</c>
    /// and <c>where U : IComparable&lt;U&gt;, IDisposable</c> constrain the type
    /// parameter identically. <c>ConstraintKey</c> joined the constraint types in
    /// declaration order, so reversing them produced a false PICO113 and, worse,
    /// delegated both members — interception lost with no compiler error to show
    /// it. review-2026-11-02/rev-aop-5c.md, D-b; probe P3.
    /// </summary>
    [Test]
    public async Task ReversedConstraintOrder_DoesNotSplitTheSignature()
    {
        var result = CompileGenerated(
            Preamble
                + """

                public interface IA
                {
                    int M<T>()
                        where T : IDisposable, IComparable<T>;
                }

                public interface IB
                {
                    int M<U>()
                        where U : IComparable<U>, IDisposable;
                }

                public interface IC : IA, IB { }

                public class C : IC
                {
                    public int M<T>()
                        where T : IComparable<T>, IDisposable => 1;
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
        Console.WriteLine($"DIAGS: {string.Join("|", result.DiagnosticText)}");

        await Assert.That(result.DiagnosticIds.Contains("PICO113")).IsFalse();
        var declarations = result
            .GeneratedOutput.Split('\n')
            .Where(line => line.Contains("public int M<", StringComparison.Ordinal))
            .ToList();
        Console.WriteLine($"DECLARATIONS: {string.Join(" || ", declarations)}");
        await Assert.That(declarations.Count).IsEqualTo(1);
        await Assert.That(result.GeneratedOutput.Contains("new Invocation_IC_M")).IsTrue();

        await Assert.That(result.WarningText).IsEqualTo(string.Empty);
        await Assert.That(result.ErrorText).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// The same positional rule for the *return* type: <c>T M&lt;T&gt;()</c> and
    /// <c>U M&lt;U&gt;()</c> are one member too, so the key has to render the
    /// return type positionally as well — printing <c>T</c> vs <c>U</c> leaves
    /// the pair looking distinct and emits both (CS0111: the return type does not
    /// separate two methods that take no parameters).
    /// </summary>
    [Test]
    public async Task SameNamedGenericMethodsReturningTheirTypeParameter_CompileAsOneMember()
    {
        var result = CompileGenerated(
            Preamble
                + """

                public interface IA
                {
                    T M<T>(T x);
                }

                public interface IB
                {
                    U M<U>(U x);
                }

                public interface IC : IA, IB { }

                public class C : IC
                {
                    public T M<T>(T x) => x;
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
        Console.WriteLine($"DIAGS: {string.Join("|", result.DiagnosticText)}");

        var declarations = result
            .GeneratedOutput.Split('\n')
            .Where(line => line.Contains(" M<T>(T x)", StringComparison.Ordinal))
            .ToList();
        Console.WriteLine($"DECLARATIONS: {string.Join(" || ", declarations)}");
        await Assert.That(declarations.Count).IsEqualTo(1);
        await Assert
            .That(result.GeneratedOutput.Contains("global::IB.M<", StringComparison.Ordinal))
            .IsFalse();
        await Assert.That(result.DiagnosticIds.Contains("PICO113")).IsFalse();
        await Assert.That(result.GeneratedOutput.Contains("new Invocation_IC_M")).IsTrue();
        await Assert.That(result.ErrorText).IsEqualTo(string.Empty);
        await Assert.That(result.WarningText).IsEqualTo(string.Empty);
    }

    private static List<string> ReadCalls(Assembly assembly) =>
        (List<string>)assembly.GetType("CountingInterceptor")!.GetField("Calls")!.GetValue(null)!;
}
