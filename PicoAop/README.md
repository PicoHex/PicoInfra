# PicoAop

Compile-time AOP decorator generation for PicoDI. Zero runtime proxy, zero reflection.

## Quick Start

```shell
dotnet add package PicoAop
```

```csharp
using PicoDI;
using PicoAop.Abs;
using PicoAop.DI;

var container = new SvcContainer();

// Register interceptors
container.RegisterSingleton<LoggingInterceptor>();

// Mark services for interception — source generator emits decorator classes
container.Register<IGreeter, Greeter>(SvcLifetime.Scoped)
    .InterceptBy<LoggingInterceptor>();

container.Build();
await using var scope = container.CreateScope();

var greeter = scope.GetService<IGreeter>();
var log = scope.GetService<LoggingInterceptor>();

// Generated: IGreeter_LoggingInterceptorDecorator
var decorated = new IGreeter_LoggingInterceptorDecorator(greeter, log);
Console.WriteLine(decorated.Greet("World"));
```

## Core Concepts

### IInterceptor

The interceptor contract — 4 methods for the cross-product of {sync, async} × {has result, void}:

```csharp
public interface IInterceptor
{
    TResult Invoke<TResult>(IInvocation<TResult> invocation,
        Func<IInvocation<TResult>, TResult> next);

    void InvokeVoid(IInvocation<VoidResult> invocation,
        Action<IInvocation<VoidResult>> next);

    ValueTask<TResult> InvokeAsync<TResult>(IInvocation<TResult> invocation,
        Func<IInvocation<TResult>, ValueTask<TResult>> next);

    ValueTask InvokeAsyncVoid(IInvocation<VoidResult> invocation,
        Func<IInvocation<VoidResult>, ValueTask> next);
}
```

The 4-method split is necessary to avoid allocations: `ValueTask<VoidResult>` cannot be returned where `ValueTask` is expected (no inheritance relationship). Collapsing to 2 methods would require async state machines for pass-through void calls.

### IInvocation<TResult>

Call context exposed to interceptors:

```csharp
public interface IInvocation<TResult>
{
    string MethodName { get; }    // Which method was called
    Type ServiceType { get; }     // Which service type
    TResult Result { get; set; }  // Modify return value
}
```

### InterceptorBase

Abstract base with virtual pass-through defaults. Override only the methods you need:

```csharp
public sealed class LoggingInterceptor : InterceptorBase
{
    public override TResult Invoke<TResult>(
        IInvocation<TResult> inv, Func<IInvocation<TResult>, TResult> next)
    {
        Console.WriteLine($"[LOG] {inv.ServiceType.Name}.{inv.MethodName}()");
        var result = next(inv);
        Console.WriteLine($"[LOG] → {result}");
        return result;
    }
}
```

## How It Works

PicoAop.Gen detects `.InterceptBy<T>()` at compile time and emits:

1. **Invocation structs** — per-method value types that capture parameters and delegate to the target
2. **Decorator classes** — sealed classes implementing the service interface, wrapping `_inner` + `_i0`
3. **DI registrations** — `ModuleInitializer` that auto-registers decorator chains

All wiring is resolved at build time. No `DispatchProxy`, no `Castle.Core`, no IL emit.

### Decoration chain (onion model):

```
Interceptor B (outermost)
  └→ Interceptor A
       └→ Real implementation (innermost)
```

```csharp
container.Register<IGreeter, Greeter>()
    .InterceptBy<LoggingInterceptor>()    // outer
    .InterceptBy<TimingInterceptor>();    // inner

// Generated: TimingDecorator(LoggingDecorator(Greeter))
// Call: Timing.Invoke → next → Logging.Invoke → next → Greeter.Greet()
```

## Per-Service Interception

```csharp
container.Register<IGreeter, Greeter>(SvcLifetime.Scoped)
    .InterceptBy<LoggingInterceptor>()
    .InterceptBy<TimingInterceptor>();
```

## Global Interception

Apply an interceptor to all matching services:

```csharp
container.AddInterceptor<MetricsInterceptor>();
```

Filter chain (compile-time markers, detected by source generator):

```csharp
container.AddInterceptor<LoggingInterceptor>()
    .WhereNamespace("MyApp.Services")
    .WhereImplements<IValidator>()
    .Except<HealthCheck>();
```

## Excluding Interceptors

```csharp
// Exclude specific interceptors from a service
container.Register<IGreeter, Greeter>()
    .WithoutInterceptor<MetricsInterceptor>()
    .InterceptBy<LoggingInterceptor>();

// Remove all interceptors (both per-service and global)
container.Register<IHealthCheck, HealthCheck>()
    .WithoutInterceptors();
```

## Limitations

- **ref / out / in parameters** — delegated directly without interception (C# structs cannot store ref fields); reported as `PICO110`
- **Indexers, events, ref-returning methods, ref struct / pointer parameters** — delegated directly; reported as `PICO111`
- **Non-virtual or sealed members of a class service** — the proxy derives from the class, and `Wrap_*` returns the class, so a member that is not `virtual` / `abstract` / `override` (or that is a `sealed override`) cannot be intercepted: calls execute on the inherited implementation in the proxy instance, never on the inner instance; reported as `PICO111`
- **Overloads that differ only by generic type arguments** (`M(List<int>)` / `M(List<string>)`) — the generated invocation name is built from the parameter's short type name, so those members are delegated; reported as `PICO113`
- **One member name inherited from two base interfaces with incompatible signatures** (`IA { int V { get; } }` + `IB { string V { get; } }`) — both members are implemented (the second one explicitly as `IB.V`), but only one generated name exists, so neither is intercepted; reported as `PICO113`
- **Properties with an `init` accessor** — cannot be implemented by a proxy, so interception for that service is skipped; reported as `PICO112`

Properties (getter and setter), generic methods (type parameters and their constraints) and members inherited from base interfaces are intercepted.

## Diagnostics

| Code | Severity | Description |
|------|----------|-------------|
| PICO101 | Error | Type in InterceptBy<T>() (or the registered type) is sealed |
| PICO102 | Error | Registered type is a value type |
| PICO105 | Error | Interceptor type does not implement IInterceptor |
| PICO110 | Warning | Method with ref/out/in parameters is delegated without interception |
| PICO111 | Warning | Member cannot be intercepted: indexer, event, ref-returning method, ref struct/pointer member, or a non-virtual/sealed member of a class service |
| PICO112 | Error | Property with an `init` accessor cannot be proxied — interception for the service is skipped |
| PICO113 | Warning | Two members share one generated invocation name (overloads differing only by generic type arguments, or one name inherited from two base interfaces) — those members are delegated |

Registration-analysis diagnostics (PICO002–PICO006) come from PicoDI.Gen.

## Packages

| Package | Description |
|---|---|
| **PicoAop.Abs** | `IInterceptor`, `IInvocation<TResult>`, `InterceptorBase` |
| **PicoAop.Gen** | Roslyn source generator — emits decorator classes (embedded in `PicoAop.Abs`) |
| **PicoAop.DI** | DI extensions: `InterceptBy<T>()`, `AddInterceptor<T>()` |

## Thread Safety

All wiring is resolved at compile time. At runtime:

- **Interceptors** must be thread-safe if registered as Singleton (shared across concurrent requests)
- **Decorators** are stateless wrappers — thread safety depends on the inner service
- **Invocation structs** are stack-allocated value types — never shared across threads

## Comparison

| | PicoAop | Castle.DynamicProxy | Metalama |
|---|---|---|---|
| **Weaving** | Source generator (build time) | Runtime IL emit | Build-time IL rewrite |
| **AOT compatible** | ✅ Yes | ❌ No | ✅ Yes |
| **Zero runtime reflection** | ✅ | ❌ | ✅ |
| **Debug generated code** | ✅ Readable C# | ❌ IL | ❌ IL |
| **Interceptor interface** | 4 methods | `IInterceptor` (1 method) | Override aspect class |
| **Method interception** | ✅ | ✅ | ✅ |
| **Property interception** | ✅ | ✅ | ✅ |
| **ref/out/in params** | ❌ Delegated | ✅ | ✅ |
| **Generic methods** | ✅ | ✅ | ✅ |

[← Back to PicoInfra](../README.md)
