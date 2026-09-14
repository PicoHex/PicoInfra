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

- **ref / out / in parameters** — delegated directly without interception (C# structs cannot store ref fields)
- **Generic methods** — type parameter substitution not implemented
- **Properties** — delegated transparently, not intercepted

## Diagnostics

| Code | Severity | Description |
|------|----------|-------------|
| PICO010 | Error | Type in InterceptBy<T>() does not implement IInterceptor |
| PICO011 | Error | WhereImplements<T> requires an interface type |
| PICO012 | Warning | No interceptors matched for service |
| PICO013 | Error | Interceptor both globally declared and per-service excluded |
| PICO014 | Warning | InterceptBy<T>() follows multiple Register calls |
| PICO015 | Warning | Sanitized type names collide — second type skipped |

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
| **Property interception** | ❌ Delegated | ✅ | ✅ |
| **ref/out/in params** | ❌ Delegated | ✅ | ✅ |
| **Generic methods** | ❌ Future | ✅ | ✅ |

[← Back to PicoInfra](../README.md)
