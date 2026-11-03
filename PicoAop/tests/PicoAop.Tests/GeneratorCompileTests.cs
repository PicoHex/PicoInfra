namespace PicoAop.Tests;

/// <summary>
/// Compile-level generator tests for the reviewer's repro
/// (review-2026-11-02/di-aop-mediator.md, finding 2): a single type-based
/// registration plus one <c>InterceptBy&lt;TInterceptor&gt;()</c>.
/// <para>
/// These assert the two things the existing suite never checked: that the
/// emitted <c>PicoAop.Invocations.g.cs</c> actually <em>compiles</em>, and that
/// an intercepted property / generic method really routes through the
/// interceptor instead of being silently skipped.
/// </para>
/// </summary>
public class GeneratorCompileTests : GeneratorTestBase
{
    private const string Preamble = """
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;
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

            public override ValueTask<TResult> InvokeAsync<TInvocation, TResult>(
                TInvocation inv,
                Func<TInvocation, ValueTask<TResult>> next
            )
            {
                Calls.Add("async:" + inv.MethodName);
                return next(inv);
            }

            public override ValueTask InvokeAsyncVoid<TInvocation>(
                TInvocation inv,
                Func<TInvocation, ValueTask> next
            )
            {
                Calls.Add("asyncvoid:" + inv.MethodName);
                return next(inv);
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

    private const string Registration = """

        static class Registration
        {
            static void Do(IDummyContainer c)
            {
                c.Register<ISvc, Svc>().InterceptBy<CountingInterceptor>();
            }
        }
        """;

    // ── Property shape ────────────────────────────────────────────────

    [Test]
    public async Task InterfaceWithProperty_EmittedCodeCompiles()
    {
        // RED (before the fix): CS1729 (the invocation structs have no
        // constructor) and CS0103 (s_get_ValueNext / s_set_ValueNext are never
        // declared).
        var result = CompileGenerated(
            Shape(
                """
                public interface ISvc
                {
                    void Do();
                    int Value { get; set; }
                }

                public class Svc : ISvc
                {
                    public int Value { get; set; }
                    public void Do() { }
                }
                """
            )
        );
        Console.WriteLine(result.GeneratedOutput);
        Console.WriteLine(result.ErrorText);
        await Assert.That(result.ErrorText).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task InterfaceProperty_GetterAndSetter_RouteThroughInterceptor()
    {
        var assembly = CompileAndLoad(
            Shape(
                """
                public interface ISvc
                {
                    int Value { get; set; }
                }

                public class Svc : ISvc
                {
                    public int Value { get; set; } = 7;
                }
                """
            ),
            out _
        );

        var proxy = CreateProxy(assembly, "Intercepted_ISvc_CountingInterceptor");
        var property = proxy.GetType().GetProperty("Value")!;

        await Assert.That((int)property.GetValue(proxy)!).IsEqualTo(7);
        property.SetValue(proxy, 42);
        await Assert.That((int)property.GetValue(proxy)!).IsEqualTo(42);

        var calls = ReadCalls(assembly);
        Console.WriteLine($"CALLS: {string.Join(",", calls)}");
        await Assert.That(calls.Contains("sync:get_Value")).IsTrue();
        await Assert.That(calls.Contains("void:set_Value")).IsTrue();
    }

    [Test]
    public async Task InterfaceWithGetOnlyAndSetOnlyProperties_EmittedCodeCompiles()
    {
        var result = CompileGenerated(
            Shape(
                """
                public interface ISvc
                {
                    int ReadOnly { get; }
                    int WriteOnly { set; }
                }

                public class Svc : ISvc
                {
                    public int ReadOnly => 1;
                    public int WriteOnly
                    {
                        set { }
                    }
                }
                """
            )
        );
        Console.WriteLine(result.GeneratedOutput);
        Console.WriteLine(result.ErrorText);
        await Assert.That(result.ErrorText).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task InterfaceProperty_WithTwoInterceptors_RoutesThroughBoth()
    {
        var assembly = CompileAndLoad(
            """
            using System;
            using System.Collections.Generic;
            using PicoAop.Abs;

            public static class Trace
            {
                public static readonly List<string> Entries = new();
            }

            public class Outer : InterceptorBase
            {
                public override TResult Invoke<TInvocation, TResult>(
                    TInvocation inv,
                    Func<TInvocation, TResult> next
                )
                {
                    Trace.Entries.Add("outer:" + inv.MethodName);
                    return next(inv);
                }

                public override void InvokeVoid<TInvocation>(
                    TInvocation inv,
                    Func<TInvocation, object?> next
                )
                {
                    Trace.Entries.Add("outer:" + inv.MethodName);
                    next(inv);
                }
            }

            public class Inner : InterceptorBase
            {
                public override TResult Invoke<TInvocation, TResult>(
                    TInvocation inv,
                    Func<TInvocation, TResult> next
                )
                {
                    Trace.Entries.Add("inner:" + inv.MethodName);
                    return next(inv);
                }

                public override void InvokeVoid<TInvocation>(
                    TInvocation inv,
                    Func<TInvocation, object?> next
                )
                {
                    Trace.Entries.Add("inner:" + inv.MethodName);
                    next(inv);
                }
            }

            public interface ISvc
            {
                int Value { get; set; }
            }

            public class Svc : ISvc
            {
                public int Value { get; set; } = 3;
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

            static class Registration
            {
                static void Do(IDummyContainer c)
                {
                    c.Register<ISvc, Svc>().InterceptBy<Outer>().InterceptBy<Inner>();
                }
            }
            """,
            out _
        );

        var proxyType = assembly.GetType("PicoAop.Generated.Intercepted_ISvc_Outer_Inner")!;
        var proxy = Activator.CreateInstance(
            proxyType,
            Activator.CreateInstance(assembly.GetType("Svc")!),
            Activator.CreateInstance(assembly.GetType("Outer")!),
            Activator.CreateInstance(assembly.GetType("Inner")!)
        )!;
        var property = proxyType.GetProperty("Value")!;

        await Assert.That((int)property.GetValue(proxy)!).IsEqualTo(3);
        property.SetValue(proxy, 11);
        await Assert.That((int)property.GetValue(proxy)!).IsEqualTo(11);

        var entries = (List<string>)assembly.GetType("Trace")!.GetField("Entries")!.GetValue(null)!;
        Console.WriteLine($"TRACE: {string.Join(",", entries)}");
        await Assert
            .That(string.Join(",", entries))
            .IsEqualTo(
                "outer:get_Value,inner:get_Value,outer:set_Value,inner:set_Value,outer:get_Value,inner:get_Value"
            );
    }

    // ── Generic method shape ──────────────────────────────────────────

    [Test]
    public async Task InterfaceWithGenericMethod_EmittedCodeCompiles()
    {
        // RED (before the fix): CS0246 (the type parameter 'T' is not in scope
        // in the invocation struct or in the proxy override) and CS0535 (the
        // interface member is not implemented).
        var result = CompileGenerated(
            Shape(
                """
                public interface ISvc
                {
                    T Echo<T>(T input);
                    TResult Convert<TInput, TResult>(TInput input)
                        where TInput : class
                        where TResult : class, new();
                    ValueTask<TAsync> EchoAsync<TAsync>(TAsync input);
                    Task<TAsync> EchoTaskAsync<TAsync>(TAsync input);
                    Task FireAsync<TAsync>(TAsync input);
                }

                public class Svc : ISvc
                {
                    public T Echo<T>(T input) => input;

                    public TResult Convert<TInput, TResult>(TInput input)
                        where TInput : class
                        where TResult : class, new() => new TResult();

                    public ValueTask<TAsync> EchoAsync<TAsync>(TAsync input) =>
                        new(input);

                    public Task<TAsync> EchoTaskAsync<TAsync>(TAsync input) =>
                        Task.FromResult(input);

                    public Task FireAsync<TAsync>(TAsync input) => Task.CompletedTask;
                }
                """
            )
        );
        Console.WriteLine(result.GeneratedOutput);
        Console.WriteLine(result.ErrorText);
        await Assert.That(result.ErrorText).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task InterfaceGenericMethod_RoutesThroughInterceptor()
    {
        var assembly = CompileAndLoad(
            Shape(
                """
                public interface ISvc
                {
                    T Echo<T>(T input);
                    T EchoConstrained<T>(T input) where T : struct;
                    string Describe<T>(T input) where T : class, new();
                }

                public class Svc : ISvc
                {
                    public T Echo<T>(T input) => input;

                    public T EchoConstrained<T>(T input) where T : struct => input;

                    public string Describe<T>(T input) where T : class, new() =>
                        typeof(T).Name + ":" + input.GetType().Name;
                }
                """
            ),
            out _
        );

        var proxy = CreateProxy(assembly, "Intercepted_ISvc_CountingInterceptor");
        var proxyType = proxy.GetType();

        var echoed = (string)
            proxyType
                .GetMethod("Echo")!
                .MakeGenericMethod(typeof(string))
                .Invoke(proxy, ["payload"])!;
        await Assert.That(echoed).IsEqualTo("payload");

        var constrained = proxyType
            .GetMethod("EchoConstrained")!
            .MakeGenericMethod(typeof(int))
            .Invoke(proxy, [21]);
        await Assert.That((int)constrained!).IsEqualTo(21);

        var described = (string)
            proxyType
                .GetMethod("Describe")!
                .MakeGenericMethod(typeof(object))
                .Invoke(proxy, ["x"])!;
        await Assert.That(described).IsEqualTo("Object:String");

        var calls = ReadCalls(assembly);
        Console.WriteLine($"CALLS: {string.Join(",", calls)}");
        await Assert.That(calls.Contains("sync:Echo")).IsTrue();
        await Assert.That(calls.Contains("sync:EchoConstrained")).IsTrue();
        await Assert.That(calls.Contains("sync:Describe")).IsTrue();
    }

    [Test]
    public async Task InterfaceGenericMethod_WithTwoInterceptors_EmittedCodeCompiles()
    {
        var result = CompileGenerated(
            """
            using System;
            using System.Collections.Generic;
            using PicoAop.Abs;

            public class Int1 : InterceptorBase { }
            public class Int2 : InterceptorBase { }

            public interface ISvc
            {
                T Echo<T>(T input) where T : class;
                TOut Map<TIn, TOut>(TIn input) where TIn : class where TOut : class;
            }

            public class Svc : ISvc
            {
                public T Echo<T>(T input) where T : class => input;

                public TOut Map<TIn, TOut>(TIn input)
                    where TIn : class
                    where TOut : class => default!;
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

            static class Registration
            {
                static void Do(IDummyContainer c)
                {
                    c.Register<ISvc, Svc>().InterceptBy<Int1>().InterceptBy<Int2>();
                }
            }
            """
        );
        Console.WriteLine(result.GeneratedOutput);
        Console.WriteLine(result.ErrorText);
        await Assert.That(result.ErrorText).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task OverloadedMethods_EmittedCodeCompiles()
    {
        // Plain overloads never reach the CS0101 struct collision, but the
        // per-method delegate cache is named from method.Name alone
        // (s_{Name}Next / s_{Name}_Step0), so two overloads collide: CS0102.
        var result = CompileGenerated(
            Shape(
                """
                public interface ISvc
                {
                    int Get(int id);
                    int Get(string name);
                    void Put(int id, string name);
                    void Put(string name);
                }

                public class Svc : ISvc
                {
                    public int Get(int id) => id;
                    public int Get(string name) => name.Length;
                    public void Put(int id, string name) { }
                    public void Put(string name) { }
                }
                """
            )
        );
        Console.WriteLine(result.GeneratedOutput);
        Console.WriteLine(result.ErrorText);
        Console.WriteLine($"DIAGS: {string.Join(",", result.DiagnosticIds)}");
        await Assert.That(result.ErrorText).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task InterfaceInheritingBaseInterface_EmittedCodeCompiles()
    {
        // Members inherited from a base interface are not in
        // serviceType.GetMembers(), so the proxy never implements them: CS0535.
        var result = CompileGenerated(
            Shape(
                """
                public interface IBase
                {
                    int Count { get; }
                    void Reset();
                }

                public interface ISvc : IBase
                {
                    void Do();
                }

                public class Svc : ISvc
                {
                    public int Count => 3;
                    public void Reset() { }
                    public void Do() { }
                }
                """
            )
        );
        Console.WriteLine(result.GeneratedOutput);
        Console.WriteLine(result.ErrorText);
        Console.WriteLine($"DIAGS: {string.Join(",", result.DiagnosticIds)}");
        await Assert.That(result.ErrorText).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task ClassWithVirtualProperty_RoutesThroughInterceptor()
    {
        var assembly = CompileAndLoad(
            """
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

            public class Svc
            {
                public virtual int Value { get; set; } = 5;
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

            static class Registration
            {
                static void Do(IDummyContainer c)
                {
                    c.Register<Svc, Svc>().InterceptBy<CountingInterceptor>();
                }
            }
            """,
            out _
        );

        var proxy = CreateProxy(assembly, "Intercepted_Svc_CountingInterceptor");
        var property = proxy.GetType().GetProperty("Value")!;
        await Assert.That((int)property.GetValue(proxy)!).IsEqualTo(5);
        property.SetValue(proxy, 9);
        await Assert.That((int)property.GetValue(proxy)!).IsEqualTo(9);

        var calls = ReadCalls(assembly);
        Console.WriteLine($"CALLS: {string.Join(",", calls)}");
        await Assert.That(calls.Contains("sync:get_Value")).IsTrue();
        await Assert.That(calls.Contains("void:set_Value")).IsTrue();
    }

    // ── Shapes that must be diagnosed instead of emitted ───────────────

    [Test]
    public async Task OverloadsDifferingOnlyByGenericArgument_ReportDiagnosticInsteadOfCs0101()
    {
        // RED (before the fix): CS0101 (duplicate struct), CS0102 (duplicate
        // s_TakeNext), CS0111 and CS0229 — InvocationEmitter builds names from
        // `p.Type.Name`, which drops the type arguments.
        var result = CompileGenerated(
            Shape(
                """
                public interface ISvc
                {
                    void Take(List<int> items);
                    void Take(List<string> items);
                }

                public class Svc : ISvc
                {
                    public void Take(List<int> items) { }
                    public void Take(List<string> items) { }
                }
                """
            )
        );
        Console.WriteLine(result.GeneratedOutput);
        Console.WriteLine(result.ErrorText);
        Console.WriteLine($"DIAGS: {string.Join(",", result.DiagnosticIds)}");
        await Assert.That(result.DiagnosticIds.Any(id => id == "PICO113")).IsTrue();
        await Assert.That(result.ErrorText).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task RefOutMethod_EmittedCodeCompiles_AndStillWarnsPico110()
    {
        // RED (before the fix): the proxy override drops the `ref`/`out`/`in`
        // modifier, so CS0535 "does not implement interface member" fired even
        // though no invocation struct was emitted for the method.
        var result = CompileGenerated(
            Shape(
                """
                public interface ISvc
                {
                    void Do(ref int x);
                    bool Try(out int value);
                    int Read(in int value);
                }

                public class Svc : ISvc
                {
                    public void Do(ref int x) => x = 1;

                    public bool Try(out int value)
                    {
                        value = 2;
                        return true;
                    }

                    public int Read(in int value) => value;
                }
                """
            )
        );
        Console.WriteLine(result.GeneratedOutput);
        Console.WriteLine(result.ErrorText);
        Console.WriteLine($"DIAGS: {string.Join(",", result.DiagnosticIds)}");
        await Assert.That(result.DiagnosticIds.Any(id => id == "PICO110")).IsTrue();
        await Assert.That(result.ErrorText).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Indexer_ReportsDiagnostic_AndEmittedCodeCompiles()
    {
        // RED (before the fix): the indexer is skipped entirely, so CS0535
        // "does not implement interface member 'ISvc.this[int]'".
        var result = CompileGenerated(
            Shape(
                """
                public interface ISvc
                {
                    int this[int index] { get; set; }
                }

                public class Svc : ISvc
                {
                    private readonly int[] _values = new int[4];

                    public int this[int index]
                    {
                        get => _values[index];
                        set => _values[index] = value;
                    }
                }
                """
            )
        );
        Console.WriteLine(result.GeneratedOutput);
        Console.WriteLine(result.ErrorText);
        Console.WriteLine($"DIAGS: {string.Join(",", result.DiagnosticIds)}");
        await Assert.That(result.DiagnosticIds.Any(id => id == "PICO111")).IsTrue();
        await Assert.That(result.ErrorText).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Event_ReportsDiagnostic_AndEmittedCodeCompiles()
    {
        var result = CompileGenerated(
            Shape(
                """
                public interface ISvc
                {
                    event EventHandler? Changed;
                    void Do();
                }

                public class Svc : ISvc
                {
                    public event EventHandler? Changed;
                    public void Do() { }
                }
                """
            )
        );
        Console.WriteLine(result.GeneratedOutput);
        Console.WriteLine(result.ErrorText);
        Console.WriteLine($"DIAGS: {string.Join(",", result.DiagnosticIds)}");
        await Assert.That(result.DiagnosticIds.Any(id => id == "PICO111")).IsTrue();
        await Assert.That(result.ErrorText).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task InitOnlyProperty_ReportsDiagnostic_AndEmittedCodeCompiles()
    {
        var result = CompileGenerated(
            Shape(
                """
                public interface ISvc
                {
                    int Value { get; init; }
                }

                public class Svc : ISvc
                {
                    public int Value { get; init; }
                }
                """
            )
        );
        Console.WriteLine(result.GeneratedOutput);
        Console.WriteLine(result.ErrorText);
        Console.WriteLine($"DIAGS: {string.Join(",", result.DiagnosticIds)}");
        await Assert.That(result.DiagnosticIds.Any(id => id == "PICO112")).IsTrue();
        await Assert.That(result.ErrorText).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task RefReturningMethod_ReportsDiagnostic_AndEmittedCodeCompiles()
    {
        var result = CompileGenerated(
            Shape(
                """
                public interface ISvc
                {
                    ref int Slot();
                    int Read();
                }

                public class Svc : ISvc
                {
                    private int _slot;

                    public ref int Slot() => ref _slot;
                    public int Read() => _slot;
                }
                """
            )
        );
        Console.WriteLine(result.GeneratedOutput);
        Console.WriteLine(result.ErrorText);
        Console.WriteLine($"DIAGS: {string.Join(",", result.DiagnosticIds)}");
        await Assert.That(result.DiagnosticIds.Any(id => id == "PICO111")).IsTrue();
        await Assert.That(result.ErrorText).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task RefStructParameter_ReportsDiagnostic_AndEmittedCodeCompiles()
    {
        var result = CompileGenerated(
            Shape(
                """
                public interface ISvc
                {
                    int Sum(Span<int> values);
                }

                public class Svc : ISvc
                {
                    public int Sum(Span<int> values)
                    {
                        int total = 0;
                        foreach (var value in values)
                            total += value;
                        return total;
                    }
                }
                """
            )
        );
        Console.WriteLine(result.GeneratedOutput);
        Console.WriteLine(result.ErrorText);
        Console.WriteLine($"DIAGS: {string.Join(",", result.DiagnosticIds)}");
        await Assert.That(result.DiagnosticIds.Any(id => id == "PICO111")).IsTrue();
        await Assert.That(result.ErrorText).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// <c>allows ref struct</c> is an anti-constraint, and an implementation of
    /// the member has to repeat it — a single interface, nothing to merge:
    /// <code>
    /// interface ISvc { int M&lt;T&gt;() where T : allows ref struct; }
    /// </code>
    /// used to emit <c>public int M&lt;T&gt;()</c> with no <c>where</c> at all:
    /// <code>
    /// CS0425: The constraints for type parameter 'T' of method 'M&lt;T&gt;()' must
    ///         match the constraints for type parameter 'T' of interface method
    ///         'ISvc.M&lt;T&gt;()'
    /// </code>
    /// The method is (correctly) delegated, but the pass-through still has to
    /// carry the clause. review-2026-11-02/rev-aop-5c.md, D-d; probe A4.
    /// </summary>
    [Test]
    public async Task AllowsRefStructConstraint_IsRepeatedOnTheEmittedMember()
    {
        var result = CompileGenerated(
            Shape(
                """
                public interface ISvc
                {
                    int M<T>()
                        where T : allows ref struct;
                }

                public class Svc : ISvc
                {
                    public int M<T>()
                        where T : allows ref struct => 1;
                }
                """
            )
        );
        Console.WriteLine(result.GeneratedOutput);
        Console.WriteLine("----- errors -----");
        Console.WriteLine(result.ErrorText);
        Console.WriteLine($"DIAGS: {string.Join(",", result.DiagnosticIds)}");

        // The clause is emitted, and last: `allows` after any other constraint is
        // CS9242 ("must be the last constraint specified").
        var declaration = result
            .GeneratedOutput.Split('\n')
            .Where(line => line.Contains("int M<T>()", StringComparison.Ordinal))
            .ToList();
        Console.WriteLine($"DECLARATIONS: {string.Join(" || ", declaration)}");
        await Assert.That(declaration.Count).IsEqualTo(1);
        await Assert
            .That(declaration[0].Contains("where T : allows ref struct", StringComparison.Ordinal))
            .IsTrue();
        // Still delegated, and the diagnostic still says so.
        await Assert
            .That(
                result.GeneratedOutput.Contains("return _inner.M<T>();", StringComparison.Ordinal)
            )
            .IsTrue();
        await Assert.That(result.DiagnosticIds.Count(id => id == "PICO111")).IsEqualTo(1);
        await Assert.That(result.ErrorText).IsEqualTo(string.Empty);
        // No WarningText assertion here: this file's Preamble has no
        // `#nullable enable`, so CS8632 is reported against the probe source, not
        // against the generated file.
    }

    [Test]
    public async Task SkippedService_StillEmitsPassthroughWrapper_SoTheOnlyErrorIsTheDiagnostic()
    {
        // PicoDI.Gen names the wrapper from the registration alone; if the Aop
        // generator skipped the service without publishing one, the consumer
        // saw CS0234/CS0117 in PicoDI's generated file on top of the
        // diagnostic. The wrapper must exist and return the inner instance.
        var result = CompileGenerated(
            """
            using System;
            using System.Collections.Generic;
            using PicoAop.Abs;

            public class CountingInterceptor : InterceptorBase { }

            public sealed class SealedSvc
            {
                public void Do() { }
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

            static class Registration
            {
                static void Do(IDummyContainer c)
                {
                    c.Register<SealedSvc, SealedSvc>().InterceptBy<CountingInterceptor>();
                }
            }
            """
        );
        Console.WriteLine(result.GeneratedOutput);
        Console.WriteLine(result.ErrorText);
        Console.WriteLine($"DIAGS: {string.Join(",", result.DiagnosticIds)}");
        await Assert.That(result.DiagnosticIds.Any(id => id == "PICO101")).IsTrue();
        await Assert
            .That(result.GeneratedOutput.Contains("Wrap_SealedSvc_CountingInterceptor"))
            .IsTrue();
        await Assert.That(result.GeneratedOutput.Contains("=> inner;")).IsTrue();
        await Assert.That(result.ErrorText).IsEqualTo(string.Empty);
    }

    // ── Harness helpers ──────────────────────────────────────────────

    private static string Shape(string shape) => Preamble + "\n\n" + shape + Registration;

    private static object CreateProxy(
        Assembly assembly,
        string proxyTypeName,
        string serviceName = "Svc"
    )
    {
        var proxyType =
            assembly.GetType("PicoAop.Generated." + proxyTypeName)
            ?? throw new InvalidOperationException(
                $"type '{proxyTypeName}' not found; generated: {string.Join(",", assembly.GetTypes().Select(t => t.FullName))}"
            );
        var inner = Activator.CreateInstance(assembly.GetType(serviceName)!)!;
        var interceptor = Activator.CreateInstance(assembly.GetType("CountingInterceptor")!)!;
        return Activator.CreateInstance(proxyType, [inner, interceptor])!;
    }

    private static List<string> ReadCalls(Assembly assembly) =>
        (List<string>)assembly.GetType("CountingInterceptor")!.GetField("Calls")!.GetValue(null)!;
}
