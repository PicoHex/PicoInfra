# PicoInfra

**AOT-First Universal Minimal Infrastructure for .NET**

Zero runtime reflection. Zero `Activator.CreateInstance`. Zero expression tree compilation.
All infrastructure wiring happens at **compile time** through C# source generators.

[![NuGet](https://img.shields.io/nuget/v/PicoDI)](https://nuget.org/packages/PicoDI)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](https://github.com/PicoHex/PicoInfra/blob/main/LICENSE)
[![CI](https://github.com/PicoHex/PicoInfra/actions/workflows/ci.yml/badge.svg)](https://github.com/PicoHex/PicoInfra/actions/workflows/ci.yml)

---

## Overview

PicoInfra is a **modular infrastructure toolkit** for .NET — five libraries that replace the `Microsoft.Extensions.*` family in AOT-trimmed / Native AOT environments, plus **PicoSchedule**, an embedded AOT-first scheduling framework.

| Module | Role | Packages |
|---|---|---|
| **PicoDI** | Dependency Injection | `PicoDI` `PicoDI.Abs` `PicoDI.Gen` |
| **PicoCfg** | Configuration | `PicoCfg` `PicoCfg.Abs` `PicoCfg.Gen` `PicoCfg.*` |
| **PicoLog** | Structured Logging | `PicoLog` `PicoLog.Abs` `PicoLog.Gen` |
| **PicoAop** | AOT Interception (AOP) | `PicoAop.Abs` `PicoAop.Gen` `PicoAop.DI` |
| **PicoMediator** | In-process Messaging | `PicoMediator` `PicoMediator.Abs` `PicoMediator.Gen` |
| **PicoSchedule** | Embedded AOT Scheduling (cron) | `PicoSchedule` |

Each module is **independent** — use one, some, or all. DI integration packages (`PicoCfg.DI`, `PicoLog.DI`, `PicoMediator.DI`, `PicoAop.DI`) bridge modules into the container.

---

## Comparison with Microsoft.Extensions

| Concern | `Microsoft.Extensions.*` | PicoInfra |
|---|---|---|
| **Runtime reflection** | Heavy (`Activator.CreateInstance`, expression trees) | **Zero** — all code paths are source-generated |
| **Native AOT readiness** | Requires careful opt-in, trimming annotations, reflection-free config | **AOT First** — compiles natively out of the box |
| **HostBuilder ceremony** | Required (`Host.CreateDefaultBuilder(args)` + pipeline) | **None** — `new SvcContainer()` is all you need |
| **Package count** | Proliferating (`Microsoft.Extensions.*` ≥ 20+ packages) | Minimal (15 packages, composable) |
| **Module wiring** | Runtime `IServiceCollection` scanning | Compile-time via `ModuleInitializer` + source generators |
| **Binary size** | Large (reflection fallbacks, expression compiler) | Minimal (trimmable, linker-friendly) |
| **Cold start** | Fast (JIT) | **Fast** (pre-generated code paths, no warm-up) |
| **Service resolution** | Expression tree compilation per registration | **Pre-compiled factory delegates** emitted by source generators |
| **Open generics** | Runtime type-argument matching | **Closed generic materialization** at compile time |
| **Interceptor / AOP** | Castle.Core (runtime proxy generation, reflection-heavy) | **Zero-allocation struct invocations**, generated at compile time |

---

## Modules in Detail

### PicoDI — Zero-Reflection DI Container

```shell
dotnet add package PicoDI
```

A high-performance DI container that resolves services through **pre-compiled factory delegates** rather than runtime reflection.

```csharp
using PicoDI;

var container = new SvcContainer();
container.RegisterSingleton<IService>(_ => new MyService());
container.RegisterTransient<IHandler, Handler>();
container.RegisterScoped<IContext, RequestContext>();
container.Build();

await using var scope = container.CreateScope();
var svc = scope.GetService<IService>();
```

**Key design decisions discovered in source:**

- **Registration → Freeze → Resolve** — `Register()` fills a `Dictionary<Type, List<SvcRuntimeRegistration>>`; `Build()` freezes it into a `FrozenDictionary<Type, SvcRuntimeRegistration[]>` for O(1) lookup.
- **Three resolution tiers** — (1) Single-singleton fast path via `Volatile.Read` pair (~2ns), (2) multi-singleton `Dictionary+lock` (~20ns), (3) lifetime-aware dispatch for scoped/transient.
- **Deadlock-safe singleton creation** — Factory is invoked **outside** the lock; duplicate creations from contended threads are disposed (factories must be idempotent).
- **LIFO disposal** — Singleton and scoped instances track `CreationOrder` and dispose in reverse order so dependencies outlive dependents.
- **Scope hierarchy** — Child scopes auto-dispose with parent; orphan detection handles concurrent `DisposeAsync` races.
- **Open generics** — `PicoDI.Gen` scans for closed generic usages and emits pre-compiled factory delegates.

**Source generators (`PicoDI.Gen`):**

Generate `ModuleInitializer` methods that register factory delegates automatically. The `SvcContainerAutoConfiguration` pattern coordinates registration across assemblies:

```csharp
// PicoDI.Gen emits this at compile time:
[ModuleInitializer]
internal static void RegisterGeneratedServices()
{
    SvcContainerAutoConfiguration.RegisterConfigurator(
        "AssemblyName",
        container => {
            container.RegisterSingleton<IService>(GeneratedFactories.CreateService);
            // ...
        }
    );
}
```

---

### PicoAop — AOT-First Interception (AOP)

```shell
dotnet add package PicoAop.Abs
dotnet add package PicoAop.DI
```

Compile-time AOP with **zero allocation** on the invocation path — no boxing, no reflection, no runtime proxy generation.

```csharp
using PicoAop.Abs;

// 1. Define an interceptor
public class LoggingInterceptor : InterceptorBase
{
    public override TResult Invoke<TInvocation, TResult>(
        TInvocation inv, Func<TInvocation, TResult> next)
    {
        Console.WriteLine($">> {inv.MethodName}");
        var result = next(inv);
        Console.WriteLine($"<< {inv.MethodName} = {result}");
        return result;
    }
}

// 2. Attach to a service at registration time
container.RegisterScoped<IService, MyService>()
    .InterceptBy<LoggingInterceptor>();
// PicoAop.Gen generates: Invocation struct + proxy class at compile time
```

**Architecture from source:**

- **`IInterceptor` interface** — Four methods covering sync void, sync return, async void, async return. All use **struct generics** with cached static delegates — zero boxing.
- **`InterceptorBase`** — Pass-through defaults; override only what you need.
- **Generated structs** — `PicoAop.Gen` emits a unique invocation struct (e.g., `Invocation_MyService_DoWork_LoggingInterceptor`) per method × interceptor combination, holding:
  - `_target` — reference to the real service
  - `_i0`…`_iN` — interceptor references
  - `_{param}` — method parameters as fields
  - `InvokeTargetAsync()` / `InvokeTarget()` — direct call to the real method
- **Generated proxy classes** — `ProxyEmitter` emits a sealed proxy class implementing the service interface, forwarding each method through the interceptor chain.
- **Multi-interceptor chains** — Each interceptor wraps the next via a static delegate chain, outermost to innermost.
- **Global interceptors** — `container.AddInterceptor<T>()` applies an interceptor to every registered service.
- **Property interception** — Getter/setter structs generated for public properties.
- **`ref`/`out` rejection** — Parameters with `RefKind` produce a diagnostic (cannot be struct-embedded).

---

### PicoCfg — Async-First Configuration

```shell
dotnet add package PicoCfg
```

Async-first configuration with source-generated typed binding. Supports multiple source types with override layering.

```csharp
var cfg = await Cfg.CreateBuilder()
    .Add("App:Name=MyApp\nApp:Version=1.0")
    .AddEnvironmentVariables("MYAPP_")
    .AddCommandLine(args)
    .BuildAsync();

// Exact-lookup reads (no tree walking)
var name = cfg.GetValue("App:Name");

// Or: source-generated typed binding
var settings = CfgBind.Bind<AppSettings>(cfg, "App");
```

**Architecture from source:**

- **`CfgBuilder`** — Collects `ICfgSource` instances; `BuildAsync()` opens all providers and composes them into a `CfgRoot`.
- **`CfgRoot`** — Manages multi-provider composition with `ReloadAsync()` support. Uses a `SemaphoreSlim` gate for thread-safe reload, `CancellationTokenSource` for graceful disposal.
- **Snapshot composition** — When all providers are native `CfgSnapshot`, flattens into a single `Dictionary<string, string>`; non-native snapshots use read-time fallback enumeration.
- **Change notification** — `CfgChangeSignal` provides `WaitForChangeAsync()` for reactive config.
- **Fingerprint-based change detection** — `ConfigDataComparer.ComputeFingerprint()` prevents unnecessary snapshot publishing.
- **Source types:**
  - Inline string (`Key=Value` lines)
  - `Dictionary<string, string>`
  - Stream (`Func<CancellationToken, ValueTask<Stream>>`)
  - Environment variables (`__` → `:`)
  - Command-line args (`--key=value`, `--key value`, `/key`)
  - Key-per-file directory
  - JSON, YAML, INI, TOML (separate packages: `PicoCfg.Json`, `PicoCfg.Yaml`, etc.) — file sources (`AddJsonFile`, `AddYamlFile`, `AddIniFile`, `AddTomlFile`) are file-watching with debounced auto-reload; string sources (`AddJson(string)`, …) are static
- **Binding (`PicoCfg.Gen`):**
  - Generates `Bind<T>` / `TryBind<T>` / `BindInto<T>` delegates for any type used with `CfgBind.Bind<T>()`
  - **Topological sort** of nested types to ensure inner types generate before their parents
  - Contract versioning (`CfgBindRuntime.ContractVersion = 2`) — generated code checks compatibility
  - Maximum nesting depth of 8 with diagnostic on truncation
  - Positional records (primary constructors) bind natively — the generator
    constructs them through their primary constructor, so `record R(int A)` and
    records with `IReadOnlyList<T>` members work as nested or dictionary-value types
- **Options pattern** — `CfgOptions<T>` (cached at construction) and `CfgOptionsSnapshot<T>` (rebinds on every access)
- **Validation** — `CfgValidator.ValidateOrThrow()` with `System.ComponentModel.DataAnnotations`
- **DI integration** (`PicoCfg.DI`) — `RegisterCfgRoot()`, `RegisterCfgTransient<T>()`, `RegisterCfgOptionsSingleton<T>()`, etc.

---

### PicoLog — Structured Logging

```shell
dotnet add package PicoLog
```

Structured logging with multiple sink targets, `FormattableString` message templates, and optional source-generated extension methods.

```csharp
var sink = new ColoredConsoleSink(new ConsoleFormatter());
await using var factory = new LoggerFactory([sink],
    new LoggerFactoryOptions { MinLevel = LogLevel.Info });

var logger = factory.CreateLogger("App");
logger.Info("Application started with {Count} items", items.Count);

// Or DI integration:
container.AddPicoLog(o => {
    o.MinLevel = LogLevel.Debug;
    o.WriteTo.ColoredConsole();
});
```

**Architecture from source:**

- **9 log levels** — `Emergency` (0) through `Trace` (8), plus `None`.
- **`ILogger` interface** — 8 explicit methods covering every combination of message format (string / `FormattableString` / `EventId`-qualified) × sync/async. Extension methods provide convenience overloads.
- **`LoggerFactory`** — Creates per-category `InternalLogger` instances wrapped in a `CategoryPipeline`. Locks around first-creation per category, then lock-free fast path.
- **Fast path (`IFastLogSink`)** — When all sinks implement `IFastLogSink`, the pipeline dispatches entries **synchronously** on the calling thread, skipping the async queue entirely.
- **`CategoryPipeline`** — Each category has its own bounded channel + processing task. Entries are dispatched to sinks, then returned to the `LogEntryPool`.
- **Fast path** — ~100ns per entry when all sinks are `IFastLogSink`.
- **Sinks:**
  - `ColoredConsoleSink` — Color-coded output per log level (Gray/Cyan/Green/Yellow/Red/Magenta, etc.)
  - `ConsoleSink` — Plain text output
  - `FileSink` — Bounded-channel file writer with file rotation, sync I/O on background thread
  - `SeqSink` — HTTP batch POST to Seq server with retry, backoff, periodic flush, and optional console fallback
- **`ILogFormatter`** — Custom formatter interface. `ConsoleFormatter` uses thread-static `StringBuilder` cache for zero-allocation formatting on hot paths.
- **`LogEntry`** — Reusable pool object (`LogEntryPool`) for fast-path entries; full allocation for slow path.
- **Scoped logging** — `LoggerScopeProvider` captures scope state and properties, merges them into every log entry.
- **Source generator (`PicoLog.Gen`):**
  - `[PicoLogMessage(LogLevel.Info, Message = "User {Name} logged in")]`
  - Generates `static partial` extension methods on `ILogger` with compile-time message template parsing
  - Handles format specifiers, ternary expressions, nested braces
- **DI integration (`PicoLog.DI`)** — `AddPicoLog(Action<LoggingOptions>)` with `WriteTo.{Console|ColoredConsole|File|Custom}()` configuration.
- **Performance** — `LogEntryPool` with `Rent()`/`Return()`; `LogEntry` reset clears all fields for reuse.

---

### PicoMediator — Compile-Time Command/Event Dispatch

```shell
dotnet add package PicoMediator
```

In-process messaging with clean port separation: `IRequester` for command/response, `IPublisher` for pub/sub, `IMediator` for both.

```csharp
// Define a command
public record GetUser(int Id) : ICommand<User>;

// Define a handler
public class GetUserHandler : ICommandHandler<GetUser, User>
{
    public ValueTask<User> Handle(GetUser command, CancellationToken ct)
        => ValueTask.FromResult(new User(command.Id, "Alice"));
}

// Dispatch
var user = await mediator.Send<GetUser, User>(new GetUser(1));
```

**Architecture from source:**

- **Protocol separation** — `IRequester` (Send only), `IPublisher` (Publish/PublishParallel only), `IMediator` (both). Prevents orchestration-layer pollution in domain code.
- **Protocol markers** — `ICommand<TResponse>` (1:1), `IEvent` (1:N).
- **`GeneratedDispatch`** — Static registry of switch dispatch functions registered via `ModuleInitializer`. Tries each registered switch in order; falls through to DI resolution.
- **`MediatorGenerator` (`PicoMediator.Gen`):**
  - Scans for `ICommandHandler<TCommand, TResponse>` / `ISubscriber<TEvent>` implementations
  - Emits a `MediatorSwitchDispatch` class with a type-switch over all request types
  - Registers via `GeneratedDispatch.RegisterSwitch()` in a `ModuleInitializer`
  - Compile-time dispatch avoids `IMediator.Send<T>` boxing overhead on the hot path
- **`PublishParallel`** — Fan-out with `Task.WhenAll`, collects exceptions into `AggregateException`.
- **`OnNoSubscribers`** — Optional callback for events with zero subscribers; silent drop by default (PUB/SUB semantics).
- **DI integration (`PicoMediator.DI`)** — `container.AddPicoMediator()` registers `IMediator` as Scoped by default (`SvcLifetime.Singleton` opt-in); `autoRegisterHandlers: true` (default) applies the generated declare-and-subscribe registrations.

---

## Source Generator Architecture

Every PicoInfra module uses `IIncrementalGenerator` for caching, incremental builds, and fast IDE experience.

| Generator | Input | Output |
|---|---|---|
| **PicoDI.Gen** | `ISvcContainer.Register*()` calls, open generic usages | Factory delegates per service, closed generic materialization, intercepted registration rewrites |
| **PicoAop.Gen** | `.InterceptBy<T>()`, `.AddInterceptor<T>()`, `.WithoutInterceptor<T>()`, `.WithoutInterceptors()` calls | Per-method invocation structs, proxy classes, wrapper factories |
| **PicoCfg.Gen** | `CfgBind.Bind<T>()` / `TryBind<T>()` / `BindInto<T>()` calls, nested types | `Bind<T>` / `TryBind<T>` / `BindInto<T>` delegates with topological sort |
| **PicoLog.Gen** | `[PicoLogMessage]` attribute on partial methods | Typed logging extension methods with string interpolation |
| **PicoMediator.Gen** | `ICommandHandler<TCommand, TResponse>` / `ISubscriber<TEvent>` implementations | Type-switch dispatch method + `ModuleInitializer` registration + handler auto-registrations |

All generators follow the same wire-up pattern:

1. Emit `ModuleInitializer` that registers generated code into the runtime's static registry
2. Runtime checks contract version for compatibility
3. Incremental caching via `Equatable` models and `IIncrementalGenerator` pipeline

---

## Getting Started

### Standalone DI

```csharp
dotnet add package PicoDI

var container = new SvcContainer();
container.RegisterSingleton<IApp>(_ => new App());
container.Build();
var app = container.CreateScope().GetService<IApp>();
```

### Standalone Configuration

```csharp
dotnet add package PicoCfg

var cfg = await Cfg.CreateBuilder()
    .Add("Key=Value")
    .BuildAsync();
var val = cfg.GetValue("Key");
```

### Standalone Logging

```csharp
dotnet add package PicoLog

var sink = new ColoredConsoleSink(new ConsoleFormatter());
await using var factory = new LoggerFactory([sink]);
var logger = factory.CreateLogger("App");
logger.Info("Hello, {Name}!", "World");
```

### Standalone Mediator

```csharp
dotnet add package PicoMediator
dotnet add package PicoMediator.DI

container.AddPicoMediator();
container.Build();
var mediator = container.CreateScope().GetService<IMediator>();
```

### Full Integration (Config + DI + Logging + Mediator + AOP)

```csharp
dotnet add package PicoCfg.DI
dotnet add package PicoLog.DI
dotnet add package PicoMediator.DI
dotnet add package PicoAop.DI

var container = new SvcContainer();

// Configuration
var cfg = await Cfg.CreateBuilder()
    .AddEnvironmentVariables("APP_")
    .AddCommandLine(args)
    .BuildAsync();
container.RegisterCfgRoot(cfg);

// Logging
container.AddPicoLog(o => {
    o.MinLevel = LogLevel.Info;
    o.WriteTo.ColoredConsole();
});

// Mediator
container.AddPicoMediator();

// AOP interceptor on a service
container.RegisterSingleton<IService, MyService>()
    .InterceptBy<LoggingInterceptor>();

container.Build();
await using var scope = container.CreateScope();
var logger = scope.GetService<ILogger<Program>>();
var mediator = scope.GetService<IMediator>();
```

---

## Package Reference

### DI
| Package | Description |
|---|---|
| **PicoDI** | Zero-reflection DI container |
| **PicoDI.Abs** | Abstractions (`ISvcContainer`, `ISvcScope`, `SvcDescriptor`) |
| **PicoDI.Gen** | Compile-time registration source generator + open generic materialization |

### AOP
| Package | Description |
|---|---|
| **PicoAop.Abs** | AOT-first interceptor abstractions — `IInterceptor`, `IInvocation`, `InterceptorBase` |
| **PicoAop.Gen** | Compile-time invocation struct + proxy class generation |
| **PicoAop.DI** | DI integration — `.InterceptBy<T>()`, `.AddInterceptor<T>()`, `.WithoutInterceptor<T>()`, `.WithoutInterceptors()` |

### Configuration
| Package | Description |
|---|---|
| **PicoCfg** | Async-first configuration root, builder, providers (env, cmd-line, stream, dictionary, key-per-file) |
| **PicoCfg.Abs** | Configuration abstractions (`ICfg`, `ICfgRoot`, `ICfgSource`, `ICfgProvider`, `ICfgSnapshot`) |
| **PicoCfg.Gen** | Typed binding source generator (`Bind<T>`, `TryBind<T>`, `BindInto<T>`) |
| **PicoCfg.DI** | DI integration — `RegisterCfgRoot()`, `RegisterCfgTransient<T>()`, `ICfgOptions<T>` |
| **PicoCfg.Json** | JSON configuration source |
| **PicoCfg.Yaml** | YAML configuration source |
| **PicoCfg.Ini** | INI configuration source |
| **PicoCfg.Toml** | TOML configuration source |

### Logging
| Package | Description |
|---|---|
| **PicoLog** | Structured logging with sinks (console, colored-console, file, Seq) |
| **PicoLog.Abs** | Logging abstractions (`ILogger`, `ILoggerFactory`, `ILogSink`, `ILogFormatter`, `LogEntry`, `LogLevel`) |
| **PicoLog.Gen** | `[PicoLogMessage]` source generator — typed logging extension methods |
| **PicoLog.DI** | DI integration — `AddPicoLog(Action<LoggingOptions>)` |
| **PicoLog.Json** | JSON log formatting |

### Mediator
| Package | Description |
|---|---|
| **PicoMediator** | Compile-time command/event dispatch |
| **PicoMediator.Abs** | Abstractions (`IMediator`, `IRequester`, `IPublisher`, `ICommand<T>`, `IEvent`, handler interfaces) |
| **PicoMediator.Gen** | Handler → switch dispatch source generator |
| **PicoMediator.DI** | DI integration — `AddPicoMediator()` |

---

## Design Philosophy

**克制 (Restraint)** — DI, Config, Logging, AOP, Mediator. The common infrastructure every app needs. No Web, no ORM, no message queue.

**专注 (Focus)** — Each module does one thing. Deep specialization over shallow generality.

**优雅 (Elegance)** — `new SvcContainer()` replaces pages of `Host.CreateDefaultBuilder()` ceremony. Source generators handle wiring at compile time.

**高效 (Efficiency)** — AOT First. Zero reflection. `FrozenDictionary` lookups. Volatile-read fast paths. Struct generics. Object pooling. Everything resolvable at compile time is resolved at compile time.

---

## Build System

- **`Directory.Build.props`** — AOT strategy (`minimal`/`aggressive`), NuGet metadata, SourceLink, XML doc enforcement
- **`Directory.Build.targets`** — `PublishAotIfRequested` target: auto-AOT-publishes before `dotnet run` in CI
- **`Directory.Packages.props`** — Central package version management
- **AOT levels:**
  - `minimal` — `PublishAot=true`, `TrimMode=full` (default for tests)
  - `aggressive` — Full trimming, `IlcDisableReflection=true`, `IlcOptimizationPreference=Size` (samples & benchmarks)
- **`netstandard2.0`** — Explicitly blocked from AOT; sources target `net10.0` (main) and `netstandard2.0` (abstractions only)

---

## Learn More

- [PicoDI](PicoDI/README.md) — DI container, registration API, source generator
- [PicoAop](PicoAop/README.md) — AOT-first interception (zero-allocation Invocation structs + proxy generation)
- [PicoCfg](PicoCfg/README.md) — Configuration providers, binding, file watching
- [PicoLog](PicoLog/README.md) — Structured logging, sinks, message templates
- [PicoMediator](PicoMediator/README.md) — Command/event dispatch
- [Contributing](CONTRIBUTING.md)
- [Security](SECURITY.md)

---

## Benchmarks

Each module ships its own benchmarks under `*/benchmarks/`. Key results:

- **PicoDI**: Singleton resolution ~2ns (single) / ~20ns (multi), scope creation ~150ns
- **PicoCfg**: Config source build ~1μs (inline), value lookup ~50ns
- **PicoLog**: Fast-path log dispatch ~100ns per entry (all `IFastLogSink`)
- **PicoAop**: Intercepted method call overhead ~5ns (struct invocation, no allocation)
- **PicoMediator**: Send dispatch ~30ns (generated switch) / ~80ns (DI fallback)

---

MIT License. [https://github.com/PicoHex/PicoInfra](https://github.com/PicoHex/PicoInfra)
