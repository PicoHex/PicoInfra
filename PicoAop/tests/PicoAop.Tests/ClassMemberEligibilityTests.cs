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

    /// <summary>
    /// PICO111 used to end every message with "and is delegated directly to the
    /// inner instance", which is false for a class service: a caller holding the
    /// service type binds to the inherited implementation on the *proxy*
    /// instance, so the state it mutates is the proxy's own copy — the emitted
    /// <c>new</c> pass-through that does reach <c>_inner</c> is never called by
    /// anyone (review-2026-11-02/rev-aop-3c.md, D4; probe P6:
    /// <c>after Inc -&gt; proxy.N=1 inner.N=0</c>).
    /// </summary>
    [Test]
    public async Task NonVirtualClassMember_RunsOnTheInheritedImplementation_NotOnTheInnerInstance()
    {
        var assembly = CompileAndLoad(
            Preamble
                + """

                public class Svc
                {
                    public int N;
                    public int VN;
                    public void Inc() { N++; }
                    public virtual void VInc() { VN++; }
                }
                """,
            out var result
        );
        var proxy = CreateProxy(assembly);
        var proxyType = proxy.GetType();
        var svcType = assembly.GetType("Svc")!;
        var inner = proxyType
            .GetField(
                "_inner",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
            )!
            .GetValue(proxy)!;

        // A consumer can only hold the service type (`Wrap_*` returns `Svc`), so
        // this is the call path that exists: the base declaration, invoked on the
        // proxy instance.
        svcType.GetMethod("Inc")!.Invoke(proxy, []);
        svcType.GetMethod("VInc")!.Invoke(proxy, []);

        var proxyN = (int)proxyType.GetField("N")!.GetValue(proxy)!;
        var innerN = (int)svcType.GetField("N")!.GetValue(inner)!;
        var proxyVN = (int)proxyType.GetField("VN")!.GetValue(proxy)!;
        var innerVN = (int)svcType.GetField("VN")!.GetValue(inner)!;
        Console.WriteLine($"after Inc  -> proxy.N={proxyN} inner.N={innerN}");
        Console.WriteLine($"after VInc -> proxy.VN={proxyVN} inner.VN={innerVN}");
        Console.WriteLine($"DIAGS: {string.Join("|", result.DiagnosticText)}");

        // The non-virtual call ran on the proxy instance, not on the inner one.
        await Assert.That(proxyN).IsEqualTo(1);
        await Assert.That(innerN).IsEqualTo(0);
        // The overridable one is routed to the inner instance, as advertised.
        await Assert.That(proxyVN).IsEqualTo(0);
        await Assert.That(innerVN).IsEqualTo(1);

        // The pass-through that does reach `_inner` is emitted (this is the
        // reword fix: the member stays, the message tells the truth about who can
        // call it) ...
        await Assert.That(result.GeneratedOutput.Contains("public new void Inc()")).IsTrue();

        // ... so the diagnostic must not claim delegation to the inner instance
        // for a member a caller reaches on the inherited implementation ...
        await Assert
            .That(result.DiagnosticText.Contains("delegated directly to the inner instance"))
            .IsFalse();
        // ... and must say what happens instead.
        await Assert
            .That(result.DiagnosticText.Contains("calls execute on the inherited implementation"))
            .IsTrue();
        await Assert.That(result.DiagnosticIds.Count(id => id == "PICO111")).IsEqualTo(1);
    }

    /// <summary>
    /// A class service is planned from its own members only (the proxy derives
    /// from it, so inherited members need no re-declaration), which meant a
    /// <c>virtual</c> member inherited from a base class was neither intercepted
    /// nor reported: the call silently ran the base implementation on the proxy
    /// instance. Same hole for a <c>protected virtual</c> member of the service
    /// itself, which <c>IsPlannedShape</c> excludes without a word.
    /// review-2026-11-02/rev-aop-3c.md, D5. A full base walk — planning,
    /// overriding and intercepting inherited members — is out of scope here; one
    /// PICO111 per skipped member is not.
    /// </summary>
    [Test]
    public async Task InheritedVirtualClassMember_IsReportedInsteadOfSkippedSilently()
    {
        var assembly = CompileAndLoad(
            Preamble
                + """

                public class Base
                {
                    public virtual int Inherited() => 1;
                }

                public class Svc : Base
                {
                    public virtual int Own() => 2;
                }
                """,
            out var result
        );
        var proxy = CreateProxy(assembly);
        var svcType = assembly.GetType("Svc")!;

        // A consumer holding `Svc` calls the inherited member on the proxy
        // instance; the chain never sees it.
        var inherited = (int)svcType.GetMethod("Inherited")!.Invoke(proxy, [])!;
        var own = (int)svcType.GetMethod("Own")!.Invoke(proxy, [])!;
        var calls = ReadCalls(assembly);
        Console.WriteLine($"Inherited()={inherited} Own()={own}");
        Console.WriteLine($"CALLS: {string.Join(",", calls)}");
        Console.WriteLine($"DIAGS: {string.Join("|", result.DiagnosticText)}");

        await Assert.That(inherited).IsEqualTo(1);
        await Assert.That(own).IsEqualTo(2);
        // The service's own virtual member is still routed ...
        await Assert.That(calls.Contains("sync:Own")).IsTrue();
        // ... the inherited one is not, and that is now reported instead of
        // being silent.
        await Assert.That(calls.Contains("sync:Inherited")).IsFalse();
        await Assert.That(result.GeneratedOutput.Contains("Invocation_Svc_Inherited")).IsFalse();
        await Assert.That(result.DiagnosticIds.Count(id => id == "PICO111")).IsEqualTo(1);
        await Assert.That(result.DiagnosticText.Contains("'Inherited'")).IsTrue();
        await Assert.That(result.DiagnosticText.Contains("'Base'")).IsTrue();
        await Assert.That(result.ErrorText).IsEqualTo(string.Empty);
        await Assert.That(result.WarningText).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// The same hole for the service's own <c>protected virtual</c> member: a
    /// derived implementation cannot widen its accessibility (CS0507), so it is
    /// neither planned nor intercepted — and it used to say nothing at all.
    /// </summary>
    [Test]
    public async Task ProtectedVirtualClassMemberOfTheService_IsReported()
    {
        var result = CompileGenerated(
            Preamble
                + """

                public class Svc
                {
                    protected virtual int Hidden() => 3;
                    public virtual int Ok() => 1;
                }
                """
        );
        Console.WriteLine("----- generated -----");
        Console.WriteLine(result.GeneratedOutput);
        Console.WriteLine("----- errors -----");
        Console.WriteLine(result.ErrorText);
        Console.WriteLine($"DIAGS: {string.Join("|", result.DiagnosticText)}");

        await Assert.That(result.DiagnosticIds.Count(id => id == "PICO111")).IsEqualTo(1);
        await Assert.That(result.DiagnosticText.Contains("'Hidden'")).IsTrue();
        // The public virtual member next to it is still intercepted.
        await Assert.That(result.GeneratedOutput.Contains("Invocation_Svc_Ok")).IsTrue();
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
