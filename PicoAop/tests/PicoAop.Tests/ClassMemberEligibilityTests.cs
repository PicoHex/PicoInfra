namespace PicoAop.Tests;

/// <summary>
/// A class service can only be intercepted where the member is overridable.
/// <c>Wrap_*</c> returns the service type, so a consumer holds the class, never
/// the generated <c>Intercepted_*</c> type — a non-virtual member emitted as a
/// hiding member is therefore dead interception: everything compiles, CS0108
/// lands in <c>obj/Generated/</c> (fatal under <c>TreatWarningsAsErrors</c>, and
/// unsuppressable there), and the interceptor never runs
/// (review-2026-11-02/rev-aop-d603729.md, defect 1).
/// </summary>
public class ClassMemberEligibilityTests : GeneratorTestBase
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

        static class Registration
        {
            static void Do(IDummyContainer c)
            {
                c.Register<Svc, Svc>().InterceptBy<CountingInterceptor>();
            }
        }
        """;

    /// <summary>
    /// The class shape from the review: one auto-property and one plain method,
    /// neither overridable, next to one virtual property and one virtual method
    /// that a derived proxy really can intercept.
    /// </summary>
    private const string ClassShape = """

        public class Svc
        {
            public int Value { get; set; } = 7;
            public int Plain() => 11;
            public virtual int VirtualValue { get; set; } = 3;
            public virtual int Reachable() => 5;
        }
        """;

    [Test]
    public async Task ClassWithNonVirtualMembers_ReportsPico111_AndAdvertisesNoInterception()
    {
        // RED (before the fix, measured on d603729): errors=0, genDiags=[], but
        //   W CS0108: 'Intercepted_Svc_CountingInterceptor.Plain()' hides inherited member 'Svc.Plain()'
        //   W CS0108: 'Intercepted_Svc_CountingInterceptor.Value' hides inherited member 'Svc.Value'
        // and the proxy carried Invocation_Svc_Plain / Invocation_Svc_Value_Getter
        // structs, i.e. it advertised interception that no consumer can reach.
        var result = CompileGenerated(Preamble + ClassShape);
        Console.WriteLine("----- generated -----");
        Console.WriteLine(result.GeneratedOutput);
        Console.WriteLine("----- errors -----");
        Console.WriteLine(result.ErrorText);
        Console.WriteLine("----- warnings -----");
        Console.WriteLine(result.WarningText);
        Console.WriteLine(
            $"----- generator diagnostics: {string.Join(",", result.DiagnosticIds)} -----"
        );

        // The interceptor is not falsely advertised: no invocation struct, no
        // delegate chain, no routing body for a member the proxy cannot override.
        await Assert.That(result.GeneratedOutput.Contains("Invocation_Svc_Plain")).IsFalse();
        await Assert.That(result.GeneratedOutput.Contains("Invocation_Svc_Value_Getter")).IsFalse();
        await Assert.That(result.GeneratedOutput.Contains("s_Plain_Step0")).IsFalse();
        await Assert.That(result.GeneratedOutput.Contains("s_get_Value_Step0")).IsFalse();

        // The overridable members on the same class are still intercepted.
        await Assert.That(result.GeneratedOutput.Contains("Invocation_Svc_Reachable")).IsTrue();
        await Assert
            .That(result.GeneratedOutput.Contains("Invocation_Svc_VirtualValue_Getter"))
            .IsTrue();

        // One diagnostic per un-interceptable member, naming it.
        Console.WriteLine($"DIAGS: {string.Join("|", result.DiagnosticText)}");
        await Assert.That(result.DiagnosticIds.Count(id => id == "PICO111")).IsEqualTo(2);
        await Assert.That(result.DiagnosticText.Contains("'Plain'")).IsTrue();
        await Assert.That(result.DiagnosticText.Contains("'Value'")).IsTrue();

        // The hiding member is emitted deliberately (`new`), so the generated
        // file no longer produces CS0108 warnings the consumer cannot edit.
        await Assert.That(result.WarningText).IsEqualTo(string.Empty);
        await Assert.That(result.ErrorText).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task NonVirtualClassMember_IsDelegatedWhileVirtualMemberIsStillIntercepted()
    {
        var assembly = CompileAndLoad(Preamble + ClassShape, out _);
        var proxy = CreateProxy(assembly);
        var proxyType = proxy.GetType();

        // The proxy decorates; the review measured viaProxy.Value=7,
        // viaProxy.Plain()=11 with callsAfter=2 — i.e. the interceptor ran for a
        // member a consumer holding `Svc` can never route through the proxy.
        await Assert.That((int)proxyType.GetProperty("Value")!.GetValue(proxy)!).IsEqualTo(7);
        await Assert.That((int)proxyType.GetMethod("Plain")!.Invoke(proxy, [])!).IsEqualTo(11);
        await Assert
            .That((int)proxyType.GetProperty("VirtualValue")!.GetValue(proxy)!)
            .IsEqualTo(3);
        await Assert.That((int)proxyType.GetMethod("Reachable")!.Invoke(proxy, [])!).IsEqualTo(5);

        var calls = ReadCalls(assembly);
        Console.WriteLine($"CALLS: {string.Join(",", calls)}");
        // The delegated members must not claim interception ...
        await Assert.That(calls.Contains("sync:Plain")).IsFalse();
        await Assert.That(calls.Contains("sync:get_Value")).IsFalse();
        // ... and the overridable ones on the same class must still route.
        await Assert.That(calls.Contains("sync:Reachable")).IsTrue();
        await Assert.That(calls.Contains("sync:get_VirtualValue")).IsTrue();
    }

    /// <summary>
    /// A <c>sealed override</c> is an override but not one a further derived
    /// type can override, so the proxy cannot route it either: it used to be
    /// planned as intercepted and emitted as <c>public override</c> (CS0239 in
    /// the generated file, zero diagnostics).
    /// review-2026-11-02/rev-aop-3c.md, D1.
    /// </summary>
    [Test]
    public async Task SealedOverrideClassMember_ReportsPico111_AndIsNeverEmittedAsOverride()
    {
        var result = CompileGenerated(
            Preamble
                + """

                public class Svc
                {
                    public sealed override string ToString() => "svc";
                    public virtual string Reachable() => "r";
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

        // The sealed member is reported instead of being planned as
        // intercepted ...
        await Assert.That(result.DiagnosticIds.Count(id => id == "PICO111")).IsEqualTo(1);
        await Assert.That(result.DiagnosticText.Contains("'ToString'")).IsTrue();
        await Assert.That(result.GeneratedOutput.Contains("Invocation_Svc_ToString")).IsFalse();
        await Assert.That(result.GeneratedOutput.Contains("override string ToString")).IsFalse();

        // ... and the overridable member next to it is still routed.
        await Assert.That(result.GeneratedOutput.Contains("Invocation_Svc_Reachable")).IsTrue();
        await Assert.That(result.ErrorText).IsEqualTo(string.Empty);
        await Assert.That(result.WarningText).IsEqualTo(string.Empty);
    }

    private static object CreateProxy(Assembly assembly)
    {
        var proxyType =
            assembly.GetType("PicoAop.Generated.Intercepted_Svc_CountingInterceptor")
            ?? throw new InvalidOperationException(
                $"proxy type not found; generated: {string.Join(",", assembly.GetTypes().Select(t => t.FullName))}"
            );
        var inner = Activator.CreateInstance(assembly.GetType("Svc")!)!;
        var interceptor = Activator.CreateInstance(assembly.GetType("CountingInterceptor")!)!;
        return Activator.CreateInstance(proxyType, [inner, interceptor])!;
    }

    private static List<string> ReadCalls(Assembly assembly) =>
        (List<string>)assembly.GetType("CountingInterceptor")!.GetField("Calls")!.GetValue(null)!;
}
