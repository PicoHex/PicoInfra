# PicoDI

Zero-reflection, AOT-compatible dependency injection for .NET.

## Quick Start

```shell
dotnet add package PicoDI
```

```csharp
using PicoDI;
using PicoDI.Abs;

var container = new SvcContainer();
container.RegisterSingleton<IService>(scope => new MyService());
container.Build();

await using var scope = container.CreateScope();
var svc = scope.GetService<IService>();
```

## Registration

All `Register*` methods return `ISvcContainer` for fluent chaining.

### Factory-Based (always works, no source gen required)

```csharp
// Lifetime-specific factory overloads
container.RegisterSingleton<IService>(scope => new Service(scope.GetService<IDep>()));
container.RegisterScoped<IRepository>(scope => new Repository());
container.RegisterTransient<IValidator>(scope => new Validator());

// Generic Register with explicit lifetime
container.Register<ICache>(scope => new MemoryCache(), SvcLifetime.Singleton);
container.Register<ILogger, MyLogger>(scope => new MyLogger(), SvcLifetime.Scoped);

// Pre-built singleton instance
container.RegisterSingle<IClock>(SystemClock.Instance);

// Multiple registrations at once
container.RegisterRange(new[] {
    SvcDescriptor.Create<IServiceA>(scope => new ServiceA()),
    SvcDescriptor.FromInstance(typeof(IServiceB), existingInstance)
});
```

### Type-Based via `typeof()` (runtime, no source gen required)

```csharp
// These create a SvcDescriptor and register it at runtime
container.Register(typeof(IService), typeof(Service), SvcLifetime.Singleton);
container.RegisterScoped(typeof(IRepository), typeof(SqlRepository));
container.RegisterTransient(typeof(IValidator), typeof(EmailValidator));

// Self-type registration
container.Register(typeof(MyService), SvcLifetime.Transient);
```

> These `typeof()` overloads delegate to `SvcDescriptor` and work with or without the source generator.
> They record the type mapping in the container but do not provide a factory — pair with a factory
> registration or the source generator for actual instance creation.

### Type-Based via Generic Type Parameters (uses the embedded PicoDI.Gen analyzer)

```csharp
// These compile to zero-allocation factory delegates
// The PicoDI.Gen analyzer ships embedded in PicoDI.Abs — available automatically
container.RegisterSingleton<IService, Service>();
container.RegisterScoped<IRepository, SqlRepository>();
container.RegisterTransient<IValidator, EmailValidator>();
```

> These generic overloads are compile-time markers. PicoDI.Gen scans them and generates
> AOT-compatible factory code. Without the source generator they throw
> `SourceGeneratorRequiredException` at runtime.

### Open Generics

```csharp
// Open generic registration — closed versions resolved at compile time
container.Register(typeof(IRepository<>), typeof(SqlRepository<>), SvcLifetime.Scoped);
container.Register(typeof(ILogger<>), typeof(ConsoleLogger<>), SvcLifetime.Singleton);
```

### Hosted Services

```csharp
container.RegisterHostedSvc<BackgroundWorker>();
container.RegisterHostedSvc<HealthCheckService>(scope => new HealthCheckService(/* deps */));
```

## Resolution

```csharp
using var scope = container.CreateScope();

// Typed resolution — dictionary lookup with cached registrations
// (generated factories inline constructor chains for dependencies)
var svc = scope.GetService<IService>();
var repos = scope.GetServices<IRepository>();

// Type-based resolution
var instance = scope.GetService(typeof(IService));
var allInstances = scope.GetServices(typeof(IRepository));

// Try-pattern — no exception for missing services
if (scope.TryGetService(typeof(IOptional), out var optional))
    Console.WriteLine($"Got: {optional}");

if (scope.TryGetServices(typeof(IPlugin), out var plugins))
    foreach (var p in plugins) Console.WriteLine(p);

// IsRegistered — check before resolving
if (container.IsRegistered(typeof(IService)))
    Console.WriteLine("IService is available");
```

## Lifetimes

| Lifetime | Instantiation | Disposal |
|---|---|---|
| **Transient** | New every resolution | Tracked by resolving scope, disposed in reverse creation order (LIFO) |
| **Scoped** | Once per scope per successful construction — a factory that throws is retried by the next resolve that starts after the failure | Disposed when scope disposes |
| **Singleton** | Once per container per successful construction — a factory that throws is retried on the next resolve | Disposed when container disposes |

Multiple registrations for the same service type are supported. Resolution returns the last (most recent) registration. `GetServices<T>()` returns all registrations in order.

**Singleton factories run against the container root scope.** A singleton factory's `scope` parameter is the container-internal root scope (not the resolving scope), so dependencies resolved inside a singleton factory live until container disposal — they never die with a short-lived request scope. Scoped/transient disposables created this way are disposed by the container.

**Container disposal order: hosted services → user scopes → singletons → the container root scope (behaviour change).** `SvcContainer.DisposeAsync()` disposes in dependency order — a disposer may use what it depends on, so a dependency is released after its dependents:

1. hosted services stop first, LIFO by start order — their `StopAsync` may still use any service, including singletons;
2. user root scopes (children before parents, LIFO within each scope) — a scoped `Dispose` may use the singletons it depends on;
3. container-owned singletons, LIFO by construction order;
4. the container-internal root scope last — it owns the scoped/transient instances that singleton factories captured, so it outlives the singletons that captured them.

Step 2 used to run *after* step 3: singletons were disposed before the user root scopes, so a scoped `Dispose` ran against already-disposed singletons. Each group keeps its own inner ordering.

## Source Generator (PicoDI.Gen)

PicoDI.Gen is **embedded** in `PicoDI.Abs` and activated automatically — no explicit
package reference needed. Installing `PicoDI` or `PicoDI.Abs` is sufficient.

The generator scans all generic `Register*` call sites (e.g. `RegisterScoped<TService, TImpl>()`)
in your codebase and emits:

The generator scans all `Register*` call sites in your codebase and emits:

- **`ConfigureGeneratedServices()`** — factory delegates with inlined `new` expressions
  and inlined dependency chains (internal typed resolvers, pruned to referenced
  dependencies only)
- **Compile-time circular dependency detection** — PICO002 error at build time
- **Open generic metadata** — cross-assembly discovery for `Register(typeof(I<>), typeof(C<>))`
- **`[ModuleInitializer]` auto-configurator** — zero-config startup, no manual Build step needed

The source generator validates implementation types at compile time and emits diagnostics:

| Code | Severity | Description |
|------|----------|-------------|
| PICO002 | Error | Circular dependency detected |
| PICO003 | Error | Abstract type registered as implementation |
| PICO004 | Error | Implementation type has no public constructor |
| PICO005 | Error | Multiple constructors marked with `[SvcConstructor]` |

Mark the preferred constructor with `[SvcConstructor]` when the implementation has multiple constructors:

```csharp
public sealed class MyService : IService
{
    [SvcConstructor] // Source generator picks this one
    public MyService(IDepA a, IDepB b) { }

    public MyService(IDepA a) { } // Ignored
}
```

## Hosted Services

### Basic: `IHostedSvc`

```csharp
public sealed class Worker(ILogger<Worker> logger) : IHostedSvc
{
    public async Task StartAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            logger.Info("Working...");
            await Task.Delay(1000, ct);
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
```

### Advanced: `IHostedLifecycleSvc`

For fine-grained lifecycle control with four-phase startup and shutdown:

```csharp
public sealed class Server(ILogger<Server> logger) : IHostedLifecycleSvc
{
    public async Task StartingAsync(CancellationToken ct) { /* pre-start init */ }
    public async Task StartAsync(CancellationToken ct)       { /* bind port */ }
    public async Task StartedAsync(CancellationToken ct)     { /* post-start */ }
    public async Task StoppingAsync(CancellationToken ct)    { /* drain connections */ }
    public async Task StopAsync(CancellationToken ct)        { /* close port */ }
    public async Task StoppedAsync(CancellationToken ct)     { /* cleanup */ }
}
```

### Convenience: `BackgroundSvc`

Template method pattern — override `ExecuteAsync` only:

```csharp
public sealed class PollingService : BackgroundSvc
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await PollAsync();
            await Task.Delay(5000, stoppingToken);
        }
    }
}
```

### SvcHostBuilder

Fluent builder for hosted services with built-in lifecycle management:

```csharp
var builder = new SvcHostBuilder();
var host = await builder
    .ConfigureServices(container =>
    {
        container.RegisterSingleton<IService>(_ => new Service());
        container.RegisterHostedSvc<Worker>();
    })
    .BuildAsync();

// ... application runs ...

await host.StopAsync();
```

Startup order is per service type, then registration order within the type: the registrations are grouped by service type, so registering `a1`, `b`, `a2` (type `A` registered twice, type `B` once) starts `a1`, `a2`, `b` — not `a1`, `b`, `a2`. Shutdown is the exact reverse of startup (LIFO), including for an instance that sits behind services that started after it. Each hosted service is a one-shot — once stopped, it cannot be restarted without creating a new container.

**Every registration counts, not every type (behaviour change).** A hosted type registered more than once — two instances, two factories, or the same implementation registered twice with different configuration — starts **all** of its registrations and stops them in the exact reverse of the order they started. Only the last registration per type used to be tracked, so the earlier instances never started and never stopped while the host reported a successful start. **Only `GetServices<T>()` reaches the earlier instance**: type resolution is still last-wins, so `GetService<T>()` answers with the last registration's singleton — removing the second registration therefore changes which object was serving, not just how many. The one exception is the same *object* registered twice: one object starts and stops once, because the start and stop phases run at most once per hosted service instance.

**A failed start reaches the caller (behaviour change).** `StartAsync` follows the .NET host contract: when a hosted service's `StartAsync` (or `StartingAsync`/`StartedAsync`) throws, the caller's `StartAsync` faults with that same exception — same type, message and stack trace, not wrapped, so `catch (InvalidOperationException)` around `StartAsync` keeps working. Previously the exception went only to `OnError` (default `null`), so a failed start reported success and `SvcHostBuilder.BuildAsync` handed back a "started" host whose service had never started. The failure also aborts the sequence:

- services registered after the failing one are **not** started;
- the services that already started are stopped first, in the exact reverse of the order they started (LIFO) — a failed start must not leave background work running behind a host you were told to throw away;
- the service whose start threw does **not** receive `StopAsync`: its `StartAsync` never completed, so its state is unknown (the same rule `Microsoft.Extensions.Hosting` applies);
- the host is left **stopped**: later `StartAsync` / `StopAsync` / `DisposeAsync` calls are no-ops, because the one-shot start was consumed by the failed attempt. `SvcHostBuilder.BuildAsync` disposes the container and rethrows;
- `OnError` still reports the failure — it is just no longer the only channel.

**A failed stop also reaches the caller (behaviour change).** `StopAsync` keeps its fault-tolerant shutdown — every service is still stopped — and then throws an `AggregateException` carrying the individual failures, the shape `Microsoft.Extensions.Hosting` uses for `Host.StopAsync`. The stop phases run at most once per hosted service instance, so a service is never stopped twice (and never after a failed start). `DisposeAsync` never throws: its internal stop reports to `OnError` instead.

**`new SvcHost(container)` needs no explicit `Build()` (behaviour change).** `StartAsync` builds the container first — the same lazy build `CreateScope()` performs — because `Build()` is what collects the hosted-service registrations. Without it, a host over a container that had not been built started nothing and still consumed its one-shot start, so even after a later `Build()` a second `StartAsync` started nothing (silently). `Build()` freezes the registrations, so register every service before the first `StartAsync`; on a disposed container `StartAsync` throws `ObjectDisposedException` and the one-shot start is *not* consumed. `SvcHostBuilder.BuildAsync` is unaffected — it already builds explicitly.

## Interceptor / AOP (Compile-Time Decorators)

PicoDI.Gen is **embedded** in PicoDI.Abs and runs automatically — no separate
package reference needed. The source generator detects `InterceptBy<T>()` markers
at compile time and emits decorator classes.

### How it works

1. Write interceptors extending `InterceptorBase`
2. Register them as services in the container
3. Chain `.InterceptBy<TInterceptor>()` after `Register()` — the source generator detects these calls at compile time and emits decorator classes
4. Manually wire decorator chains using the generated classes

```csharp
// Step 1: Define interceptors
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

public sealed class RetryInterceptor(int maxRetries) : InterceptorBase
{
    public override TResult Invoke<TResult>(
        IInvocation<TResult> inv, Func<IInvocation<TResult>, TResult> next)
    {
        for (var i = 1; ; i++)
        {
            try { return next(inv); }
            catch (Exception ex) when (i < maxRetries)
                => Console.WriteLine($"[RETRY] attempt {i}: {ex.Message}");
        }
    }
}
```

```csharp
// Step 2: Register interceptors + services
var container = new SvcContainer();

container.RegisterSingleton<LoggingInterceptor>();
container.RegisterSingleton<RetryInterceptor>(_ => new RetryInterceptor(3));

// InterceptBy<T>() is a compile-time marker — detected by the source generator
container.Register<ICalculator>(_ => new Calculator(), SvcLifetime.Scoped)
    .InterceptBy<LoggingInterceptor>();
container.Register<IApiClient>(_ => new FlakyApiClient(), SvcLifetime.Scoped)
    .InterceptBy<RetryInterceptor>();

container.Build();
```

```csharp
// Step 3: Resolve and manually build decorator chains
await using var scope = container.CreateScope();

var calc = scope.GetService<ICalculator>()!;
var api = scope.GetService<IApiClient>()!;
var log = scope.GetService<LoggingInterceptor>()!;
var retry = scope.GetService<RetryInterceptor>()!;

// Generated class names: {ServiceType}_{InterceptorType}Decorator
var calcDecorated = new ICalculator_LoggingInterceptorDecorator(calc, log);
var apiDecorated = new IApiClient_RetryInterceptorDecorator(api, retry);

calcDecorated.Add(3, 4);    // [LOG] ICalculator.Add() → 7
apiDecorated.Fetch();        // [RETRY] attempt 1: API down...
```

All wiring is resolved at compile time — zero reflection. See [PicoDI.Sample.Aop](samples/PicoDI.Sample.Aop/) for a complete working example with Logging, Timing, Validation, and Retry interceptors.

## Error Handling

### Resolution Errors

- **Unregistered service**: `GetService()` throws `PicoDiException`. Use `TryGetService()` / `TryGetServices()` for optional dependencies.
- **Circular dependency**: a singleton or scoped factory that resolves the service it is constructing — directly or through a chain — throws `PicoDiException` naming the cycle (`A -> B -> A`) instead of overflowing the stack. (Transient-to-transient cycles are still not detected: transients are not tracked as under construction.)
- **Exception type change for scoped cycles**: this diagnostic replaced an older failure mode. A *scoped* cycle used to surface as `System.InvalidOperationException` (`ValueFactory attempted to access the Value property of this instance.`) — the scoped instance is a `Lazy<T>`, and `Lazy` throws on re-entrance. It now surfaces as `PicoDiException`, which derives from `Exception` and **not** from `InvalidOperationException`: code that wrapped a scoped resolve in `catch (InvalidOperationException)` silently stops catching it, so catch `PicoDiException` (or `Exception`) instead. Singleton cycles went from a `Stack overflow.` process abort to the same `PicoDiException`.
- **Scoped factory retry (behaviour change)**: a scoped factory that throws no longer poisons the scope. The failed instance is evicted and a resolve that *starts* after the failure runs the factory again, so a transient failure is retried instead of rethrowing the cached exception forever, and a factory that always throws throws again on every later resolve. This holds for resolves that begin after the failure: a resolve already in flight and blocked on that instance in that scope — the scoped value is a shared `Lazy<T>` — still receives the original exception. A successful scoped instance is still created exactly once and disposed exactly once; the cost of the retry is that a scoped factory with side effects may now run more than once per scope.
- **Disposed scope/container**: Throws `ObjectDisposedException`.
- **Source generator not applied**: Throws `SourceGeneratorRequiredException` for type-based registrations.

### Hosted Service Errors

- **Failed start**: the caller's `StartAsync` faults with the service's own exception, the start sequence aborts, the already-started services are stopped (LIFO) and the failing one is not. See [Hosted Services](#svchostbuilder). `SvcHostBuilder.BuildAsync` disposes the container and rethrows.
- **Failed stop**: `StopAsync` throws an `AggregateException` after every service has been stopped. `DisposeAsync` swallows it into `OnError` — a disposal path never throws.
- **`OnError` is the diagnostic channel, never the only one**: hosted-service start/stop failures are reported to it *and* propagated (or, on disposal, only reported).

### Disposal Errors

Disposal is fault-tolerant — one service failing to dispose does not prevent others from being cleaned up. Errors are reported via the `OnError` callback:

```csharp
var container = new SvcContainer();
container.OnError = (ex, context) =>
{
    Console.Error.WriteLine($"[{context}] {ex.Message}");
};
```

## Thread Safety

PicoDI is designed for concurrent use:

- **Registration**: Serialized under lock. Must complete before `Build()`.
- **`Build()`**: Idempotent, and serialized per container by a dedicated build gate. Freezes the registration cache into a lock-free `FrozenDictionary`; later calls are a no-op.
- **Lazy build**: With `autoConfigureFromGenerator: true` (the default) the container may never be built explicitly — the first `CreateScope()` / `GetImplicitRootScope()` / `IHost.StartAsync()` triggers `Build()`. Concurrent first callers wait on the build gate, so no caller can freeze the container while the deferred source-generator configurators are still calling `Register()`. `StartAsync` builds *before* it consumes its one-shot start, so a host over an unbuilt container starts its services (it used to start nothing and burn the one-shot); a disposed container faults `StartAsync` with `ObjectDisposedException` instead, and the one-shot is not consumed.
- **Deferred auto-configuration**: Generator configurators run outside the registration lock (they call `Register()`) but inside the build gate; `Register()` takes only the registration lock, so the gate → registration lock order cannot self-deadlock.
- **Resolution**: Lock-free reads. Singleton creation uses deadlock-safe double-checked locking. Scoped instances use `ConcurrentDictionary` + `Lazy<T>`.
- **Disposal**: Idempotent. `DisposeAsync()` can be called concurrently and multiple times safely.

## Packages

| Package | Description |
|---|---|
| **PicoDI** | DI container runtime |
| **PicoDI.Abs** | Abstractions: `ISvcContainer`, `ISvcScope`, `SvcDescriptor`, `SvcLifetime`, `IHostedSvc`, `BackgroundSvc`, `SourceGeneratorRequiredException` |
| **PicoDI.Gen** | Roslyn source generator + diagnostic analyzer (**embedded** in PicoDI.Abs — no separate reference needed) |

[← Back to PicoInfra](../README.md)
