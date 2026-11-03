namespace PicoAop.Tests;

/// <summary>
/// <see cref="IInvocation.ServiceType"/> has to name the service that is being
/// proxied, for every member of that proxy. The method side used to render it
/// from <c>method.ContainingType</c>, so a member inherited from a base
/// interface reported the <em>base</em> interface while the property side
/// reported the service (review-2026-11-02/rev-aop-d603729.md, defect 3): an
/// interceptor keyed on <c>ServiceType</c> silently missed inherited members.
/// </summary>
public class InvocationServiceTypeTests : GeneratorTestBase
{
    private const string Preamble = """
        #nullable enable
        using System;
        using System.Collections.Generic;
        using PicoAop.Abs;

        public class RecordingInterceptor : InterceptorBase
        {
            public static readonly List<string> Seen = new();

            public override TResult Invoke<TInvocation, TResult>(
                TInvocation inv,
                Func<TInvocation, TResult> next
            )
            {
                Seen.Add(inv.MethodName + ":" + inv.ServiceType.Name);
                return next(inv);
            }

            public override void InvokeVoid<TInvocation>(
                TInvocation inv,
                Func<TInvocation, object?> next
            )
            {
                Seen.Add(inv.MethodName + ":" + inv.ServiceType.Name);
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

    private const string Shape = """

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

        static class Registration
        {
            static void Do(IDummyContainer c)
            {
                c.Register<ISvc, Svc>().InterceptBy<RecordingInterceptor>();
            }
        }
        """;

    [Test]
    public async Task EveryInvocationOfOneProxy_ReportsTheProxiedService()
    {
        // RED (before the fix, measured on d603729): the emitted structs were
        //   struct Invocation_ISvc_Do_...        ServiceType => typeof(global::ISvc)
        //   struct Invocation_ISvc_Reset_...     ServiceType => typeof(global::IBase)   <- base
        //   struct Invocation_ISvc_Count_Getter  ServiceType => typeof(global::ISvc)
        // so an interceptor keyed on ServiceType missed `Reset`.
        var result = CompileGenerated(Preamble + Shape);
        Console.WriteLine("----- generated -----");
        Console.WriteLine(result.GeneratedOutput);
        Console.WriteLine(result.ErrorText);

        // No invocation struct may name an interface other than the service.
        await Assert.That(result.GeneratedOutput.Contains("typeof(global::IBase)")).IsFalse();
        await Assert.That(result.ErrorText).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task InheritedMember_ReportsTheServiceAtRuntime()
    {
        var assembly = CompileAndLoad(Preamble + Shape, out _);
        var proxyType = assembly.GetType(
            "PicoAop.Generated.Intercepted_ISvc_RecordingInterceptor"
        )!;
        var proxy = Activator.CreateInstance(
            proxyType,
            Activator.CreateInstance(assembly.GetType("Svc")!),
            Activator.CreateInstance(assembly.GetType("RecordingInterceptor")!)
        )!;

        proxyType.GetMethod("Do")!.Invoke(proxy, []);
        _ = proxyType.GetProperty("Count")!.GetValue(proxy);
        proxyType.GetMethod("Reset")!.Invoke(proxy, []);

        var seen =
            (List<string>)
                assembly.GetType("RecordingInterceptor")!.GetField("Seen")!.GetValue(null)!;
        Console.WriteLine($"SEEN: {string.Join(",", seen)}");

        // Every member of the proxy reports the same service — including the one
        // inherited from IBase.
        await Assert.That(string.Join(",", seen)).IsEqualTo("Do:ISvc,get_Count:ISvc,Reset:ISvc");
    }
}
