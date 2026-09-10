# PicoMediator

Compile-time command/event dispatch for PicoDI. Zero reflection, AOT-first.

> **Requirements: .NET 10+** — the `PicoMediator` runtime and ALL generated code
> (dispatch switches, handler registrations, base-type bridges) require net10.0+
> consumers. `PicoMediator.Abs` alone targets netstandard2.0 but provides no
> runtime; the source generator is unusable from netstandard2.0 assemblies.

## Quick Start

```shell
dotnet add package PicoMediator
```

```csharp
using PicoDI;
using PicoMediator;
using PicoMediator.Abs;

var container = new SvcContainer();

// Declare-and-subscribe: PicoMediator.Gen scans handler implementations
// and AddPicoMediator() registers them automatically — no manual wiring.
container.AddPicoMediator();
container.Build();

await using var scope = container.CreateScope();
var mediator = scope.GetService<IMediator>();

// Send: 1:1 command → response
var result = await mediator.Send<Ping, string>(new Ping());

// Publish: 1:N event → all subscribers
await mediator.Publish(new OrderCreated(Guid.NewGuid(), "book"));
```

Handler implementations are discovered automatically:

```csharp
public record Ping(string Message) : ICommand<string>;

public sealed class PingHandler : ICommandHandler<Ping, string>
{
    public ValueTask<string> Handle(Ping c, CancellationToken ct) => new($"pong:{c.Message}");
}
```

## Core Concepts

### Interaction Patterns

PicoMediator follows the ZeroMQ-inspired principle: **interaction patterns are atomic primitives encoded by the type system**.

| Pattern | ZeroMQ | C# Type | Cardinality | Response |
|---------|--------|---------|:---:|:---:|
| Command | REQ/REP | `ICommand<TResponse>` | 1:1 | Yes |
| Event | PUB/SUB | `IEvent` | 1:N | No |

Both derive from `IMessage`. No `IQuery` split — a query is a command whose result is a read. Define your own via interface inheritance if needed:

```csharp
public interface IQuery<T> : ICommand<T> { }
```

No `IStreamRequest` — wrap `IAsyncEnumerable<T>` in the response:

```csharp
public record ExportUsers : ICommand<ExportUsersResponse>;
public record ExportUsersResponse(IAsyncEnumerable<User> Users);
```

### Void Commands

Use `VoidResult` (from `PicoDI.Abs`) for fire-and-forget commands:

```csharp
public record DeleteOrder(Guid Id) : ICommand<VoidResult>;

public class DeleteOrderHandler : ICommandHandler<DeleteOrder, VoidResult>
{
    public async ValueTask<VoidResult> Handle(DeleteOrder r, CancellationToken ct)
    {
        await DeleteAsync(r.Id, ct);
        return default;
    }
}
```

### Publisher/Subscriber Semantics

Publish follows ZeroMQ PUB/SUB semantics:
- Publisher does not know subscribers
- No return value (protocol forbids it)
- No subscribers → **silent drop** (not an error)
- Multiple subscribers → each receives the event

```csharp
// 2 subscribers
public sealed class EmailHandler : ISubscriber<OrderCreated> { ... }
public sealed class AuditHandler : ISubscriber<OrderCreated> { ... }

await mediator.Publish(new OrderCreated(id, item));
// → EmailHandler.Handle() called
// → AuditHandler.Handle() called
```

### Base-Type Publish

Publishing through a base-typed variable reaches concrete subscribers via
generated bridge subscribers (`PicoMediator.Gen` scans all `IEvent`
implementations and their inheritance; `AddPicoMediator()` registers one
bridge per non-concrete base type):

```csharp
IReadOnlyList<IEvent> events = [new OrderPaid(1), new OrderShipped(2)];
foreach (var e in events)
    await mediator.Publish(e);   // delivered to ISubscriber<OrderPaid> / ISubscriber<OrderShipped>
```

Contract of base-typed `Publish<T>`: base-key direct subscribers + all
concrete subscribers of the runtime type. Documented deltas vs exact-type
publish: `PublishParallel<Base>` forwards sequentially inside the bridge;
`OnNoSubscribers` does not fire for base-typed publishes; base-declared
subscribers (`ISubscriber<IEvent>`) do NOT receive concrete-typed publishes.
The generator warns (PMGEN001) at call sites where a base type has no known
concrete event types. Both `class` and `record struct` events are supported
(boxed dispatch through the bridge). No `xxUntyped` APIs exist by design —
dispatch is compile-time-table based, AOT-safe, zero reflection.

## Registration

### Declare-and-Subscribe (primary path)

PicoMediator.Gen scans all closed, non-abstract `ICommandHandler<T, R>` / `ISubscriber<T>` implementations in your assembly. `AddPicoMediator()` applies them as **Transient** registrations:

```csharp
container.AddPicoMediator(); // auto-registers all scanned handlers
```

- **Multi-assembly:** handlers in referenced library assemblies are included — every assembly that references `PicoMediator.Gen` contributes its own scanned handlers, and `AddPicoMediator()` applies configurators from all loaded assemblies (the "library of handlers" pattern).
- Constructor dependencies are resolved from the container (typed, zero reflection).
- One class implementing several handler interfaces yields one registration per interface.
- Open-generic handler classes are skipped (register closed forms manually).

### Manual Registration (override path)

Manual registrations made **before** `AddPicoMediator()` win over generated ones (dedup is per service type):

```csharp
// Custom lifetime or instance — manual wins, generator skips this service type
container.RegisterSingle<ISubscriber<OrderCreated>>(new EmailHandler());
container.AddPicoMediator();
```

To disable auto-registration entirely:

```csharp
container.AddPicoMediator(autoRegisterHandlers: false);
```

### Mediator

One line — registers `IMediator` as Scoped:

```csharp
container.AddPicoMediator();
```

Scoped is the default for request-scoped isolation. Use `AddPicoMediator(SvcLifetime.Singleton)` for a stateless mediator.

### Narrow Ports

Depend on the narrowest interface for your component:

```csharp
// Only sends commands
public sealed class OrderController(IRequester requester) { ... }

// Only publishes events
public sealed class EventSource(IPublisher publisher) { ... }

// Orchestration — needs both
public sealed class CheckoutService(IMediator mediator) { ... }
```

## Rename Map

The REQ/REP + PUB/SUB pattern vocabulary replaced the old protocol markers:

| Old | New |
|---|---|
| `IRequest<TResponse>` | `ICommand<TResponse>` |
| `IRequestHandler<T, R>` | `ICommandHandler<T, R>` |
| `INotification` | `IEvent` |
| `INotificationHandler<T>` | `ISubscriber<T>` |
| `ISender` | `IRequester` |

`IPublisher` / `IMediator` / `VoidResult` are unchanged. `IMessage` is the new root marker for both `ICommand<TResponse>` and `IEvent`.

## Pipeline Behaviors (via PicoAop)

PicoMediator does NOT define its own pipeline abstraction. Use PicoAop interceptors instead:

```csharp
// Mediator-level — applies to all Send/Publish calls
container.Register<IMediator, Mediator>(SvcLifetime.Scoped)
    .InterceptBy<MetricsInterceptor>();

// Handler-level — applies to a specific command type
container.Register<ICommandHandler<CreateOrder, OrderResult>, CreateOrderHandler>(SvcLifetime.Transient)
    .InterceptBy<LoggingInterceptor>()
    .InterceptBy<ValidationInterceptor>()
    .InterceptBy<TransactionInterceptor>();

// Decorator chain (onion model):
// Transaction → Validation → Logging → Handler
```

## Source Generator (PicoMediator.Gen)

Add `PicoMediator.Gen` as an analyzer:

```xml
<PackageReference Include="PicoMediator.Gen" PrivateAssets="all" />
```

The generator scans `ICommandHandler<T, R>` / `ISubscriber<T>` implementations and emits:

- **`MediatorSwitch.g.cs`** — switch-based typed `Send` dispatch (fully-qualified, non-generic resolution; compiles in non-friend assemblies with no `using PicoDI.Abs` dependency)
- **`MediatorHandlerRegistrations_<assembly>.g.cs`** — the declare-and-subscribe registrations applied by `AddPicoMediator()`
- **Runtime fallback** — `scope.GetService<T>()` for handlers not in the switch table

Without the generator, `Mediator.Send()` still works via the runtime `GetService` fallback, but handlers must be registered manually.

## Error Handling

| Scenario | Behavior |
|----------|----------|
| Send — no handler registered | `InvalidOperationException` |
| Send — handler throws | Exception propagates to caller |
| Publish — no subscribers | Silent (PUB/SUB semantics) |
| Publish — one handler throws | Exception propagates to caller |
| Publish — multiple handlers fail | `AggregateException` |

## Packages

| Package | Description |
|---|---|
| **PicoMediator.Abs** | `IMessage`, `ICommand<T>`, `IEvent`, `ICommandHandler<T, T>`, `ISubscriber<T>`, `IRequester`, `IPublisher`, `IMediator` |
| **PicoMediator** | `Mediator(ISvcScope)` runtime, `GeneratedDispatch`, `MediatorAutoSubscriptionRegistry` |
| **PicoMediator.Gen** | Source generator — switch dispatch + handler registrations |
| **PicoMediator.DI** | `container.AddPicoMediator()` |

[← Back to PicoInfra](../README.md)
