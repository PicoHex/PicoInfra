# PicoLog

High-performance structured logging for .NET Native AOT.

[![NuGet](https://img.shields.io/nuget/v/PicoLog)](https://nuget.org/packages/PicoLog)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](https://github.com/PicoHex/PicoInfra/blob/main/LICENSE)

---

## Performance

PicoLog vs Microsoft.Extensions.Logging on .NET 10.0.5 (Windows 10, X64, Release).

| Test Case | PicoLog (ns) | MEL Baseline (ns) | Speedup |
|---|---|---|---|
| Cached message, single write | 303 | 434 | **1.43x** |
| Cached + 1 scope | 402 | 434 | **1.08x** |
| Cached + 4 properties | 324 | 434 | **1.34x** |
| Timestamp acquisition only | 59 | 434 | **7.36x** |
| LogEntry allocation only | 46 | 434 | **9.33x** |

**Summary**: 13/30 wins, average **3.51x** speedup, max **25.76x**.

PicoLog is faster than MEL on cached-message and allocation-heavy paths where
source generation eliminates boxing and reflection. On single-message throughput
(N=1), PicoLog is deliberately slower — roughly 700ns vs MEL's 200ns per call.
This is not a bug; it's the cost of an **async-first pipeline architecture**.

### Design Rationale: Async-First Pipeline

Every `Log()` call traverses the same pipeline regardless of whether you call
the synchronous or asynchronous overload:

```
Log() → CreateEntry → CategoryPipeline → Channel → ProcessEntriesAsync → DispatchEntryAsync → Sink
```

This is fundamentally different from MEL's synchronous model:

```
Log() → Logger.Log() → foreach (logger in loggers) → logger.Log()  // direct, no channel
```

| Aspect | PicoLog (async-first) | MEL (sync-first) |
|---|---|---|
| Delivery | Guaranteed through bounded channel | Best-effort; sync discard can drop if logger throws |
| Backpressure | `DropOldest` / `DropWrite` / `Wait` — configurable per factory | None — calling thread blocks or message is lost |
| Flush | `FlushAsync()` drains all pending entries before shutdown | No flush API; `Dispose()` is instant |
| Timestamp | Precise — captured at `CreateEntry()` before enqueue | Captured inside logger, may be delayed |
| Thread safety | Channel-based; no lock on hot path | Each logger manages its own synchronization |
| Per-call overhead | ~500–800ns (Channel + dispatcher + sink) | ~100–200ns (direct dispatch) |

PicoLog chose the async pipeline because production logging systems need
**guaranteed delivery, backpressure, and graceful shutdown** more than they
need the absolute lowest single-message latency. If you're logging at 1M msg/s,
the per-message overhead dominates. If you're logging at 1K msg/s with flush
semantics, PicoLog's guarantees matter more.

For direct throughput comparisons across all scenarios, see [benchmark results](benchmarks/PicoLog.Benchmarks/bin/Release/net10.0/benchmark-results.md).

---

## Quick Start

```shell
dotnet add package PicoLog
```

```csharp
using PicoLog;
using PicoLog.Abs;

var sink = new ColoredConsoleSink(new ConsoleFormatter());
await using var factory = new LoggerFactory([sink],
    new LoggerFactoryOptions { MinLevel = LogLevel.Info });
var logger = factory.CreateLogger("App");
logger.Info("Application started");
```

## Log Levels

| Level | Value | Usage |
|---|---|---|
| **Emergency** | 0 | System is unusable |
| **Alert** | 1 | Action must be taken immediately |
| **Critical** | 2 | Critical conditions |
| **Error** | 3 | Error conditions |
| **Warning** | 4 | Warning conditions |
| **Notice** | 5 | Normal but significant |
| **Info** | 6 | Informational messages |
| **Debug** | 7 | Debug-level messages |
| **Trace** | 8 | Detailed diagnostic tracing |
| **None** | 255 | Disables all logging |

## Core API

### LoggerFactory

```csharp
await using var factory = new LoggerFactory(
    sinks: [new ColoredConsoleSink(new ConsoleFormatter())],
    options: new LoggerFactoryOptions
    {
        MinLevel = LogLevel.Info,
        QueueCapacity = 65535,
        QueueFullMode = LogQueueFullMode.DropOldest,
        SyncWriteTimeout = TimeSpan.FromMilliseconds(250),
        ShutdownTimeout = TimeSpan.FromSeconds(5)
    });

ILogger logger = factory.CreateLogger("MyComponent");
```

### Typed Logger

```csharp
public sealed class OrderService(ILoggerFactory factory)
{
    private readonly ILogger<OrderService> _logger = new Logger<OrderService>(factory);

    public void Process(Order order)
    {
        _logger.Info($"Processing order {order.Id}");
    }
}
```

### Extension Methods

Level-specific convenience helpers provided as extension methods on `ILogger`. Not part of the interface contract — consumers only implement the 8 core interface methods.

```csharp
logger.Trace("Detailed diagnostic");
logger.Debug("Debug information");
logger.Info("Application event");
logger.Notice("Significant event");
logger.Warning("Unexpected condition");
logger.Error("Operation failed", exception);
logger.Critical("Severe failure");
logger.Alert("Immediate action required");
logger.Emergency("System is down");
```

Each has a `FormattableString` overload for deferred formatting and an `EventId` overload for structured event identification. Async variants (`TraceAsync`, `InfoAsync`, etc.) are available with `CancellationToken` support.

## Message Templates

```csharp
// FormattableString — template and arguments preserved for deferred formatting
logger.Info($"Processing order {orderId} for {customer}");

// Structured properties — explicit key-value pairs
logger.LogStructured(LogLevel.Error, "Payment failed",
    properties: [new("OrderId", orderId), new("Amount", amount)],
    exception: ex);

// EventId — tagged for monitoring and alerting
logger.Info(new EventId(1001, "OrderPlaced"), $"Order {orderId} placed");
```

## Source-Generated Messages

The `[PicoLogMessage]` generator is **embedded in `PicoLog.Abs`** (no extra package). Reference `PicoLog.Abs` and the analyzer activates automatically:

```csharp
public static partial class AppLogs
{
    [PicoLogMessage(LogLevel.Info, EventId = 1001, Message = "Order {OrderId} placed")]
    public static partial void OrderPlaced(this ILogger logger, string orderId);

    [PicoLogMessage(LogLevel.Error, EventId = 2001,
        EventName = "PaymentFailed", Message = "Payment of {Amount} failed")]
    public static partial void PaymentFailed(this ILogger logger, decimal amount);
}

logger.OrderPlaced("ORD-12345");
logger.PaymentFailed(99.99m);
```

The generator emits `Log` calls with `FormattableString` at compile time — zero runtime reflection.

## Sinks

### Built-in — `PicoLog`

```csharp
// Plain console
new ConsoleSink(new ConsoleFormatter())

// Color-coded console (Trace=Gray, Debug=Cyan, Info=Green, Error=Red, ...)
new ColoredConsoleSink(new ConsoleFormatter())

// File with batching
new FileSink(new ConsoleFormatter(), new FileSinkOptions
{
    FilePath = "logs/app.log",
    BatchSize = 32,
    QueueCapacity = 4096,
    FlushInterval = TimeSpan.FromMilliseconds(100)
})
```

`FileSink` writes the log as UTF-8 **without a byte-order mark** with **LF** line endings (not the platform's `Environment.NewLine`), and holds the file open with `FileShare.ReadWrite` so other processes can open the live file.

### Built-in — `PicoLog.Json`

The sinks that own JSON live in the `PicoLog.Json` package, because the core package carries the
log model, the levels and the sink abstraction — and no serializer.

```shell
dotnet add package PicoLog.Json
```

```csharp
using PicoLog;   // the namespace of both sinks, unchanged by the package they ship in

// One JSON object per line, to a file
new JsonLogSink("logs/app.jsonl")

// Batched HTTP POST to a Seq server (retry, backoff, periodic flush, optional console fallback)
new SeqSink(new HttpClient { BaseAddress = new Uri("http://localhost:5341") },
    apiKey: "your-api-key",
    flushInterval: TimeSpan.FromSeconds(2),
    enableConsoleFallback: true)
```

`SeqSink` moved here from the core package, so a consumer's **source** needs one thing: the package
reference — `using PicoLog;` and `new SeqSink(...)` still compile. An assembly that was already
compiled against the old `PicoLog` cannot be reused, though: there is no type forwarder and no
`[Obsolete]` stub, so it must be recompiled against `PicoLog.Json` (see [Package move](#packages)).

Both sinks write through PicoJetson's `JsonWriter`, so the batch body is UTF-8 from the first byte:
no `StringBuilder`, no per-entry string, and no hand-rolled escaper. Measured on the 100-entry
batch of `SeqSinkWireFormatTests` (JIT, Release, `GC.GetAllocatedBytesForCurrentThread`, median of
two runs): **1487 B/event before, 221 B/event after**. That is an allocation count, not a timing
claim — 109 B of the remainder is the one unavoidable copy of the body and ~90 B is the
`DateTime.ToString("O")` string the old path paid too.

Escaping is the writer's canonical form: `\n`, `\r`, `\t`, `\"` and `\\` keep their
two-character escapes, every other C0 control becomes `\u00XX` with **uppercase** hex digits,
U+2028/U+2029 stay raw, and an unpaired surrogate reaches the wire as U+FFFD. The body of every
batch is one valid JSON document for any payload.

### Custom

```csharp
public sealed class CustomSink : ILogSink
{
    public Task WriteAsync(LogEntry entry, CancellationToken ct = default)
    {
        // Write to your backend
        return Task.CompletedTask;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

Implement `IFlushableLogSink` to support explicit flush:

```csharp
public sealed class BufferedSink : IFlushableLogSink
{
    public Task WriteAsync(LogEntry entry, CancellationToken ct = default) { /* buffer */ }
    public ValueTask FlushAsync(CancellationToken ct = default) { /* flush buffer */ }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

### Custom Formatter

```csharp
public sealed class JsonFormatter : ILogFormatter
{
    public string Format(LogEntry entry)
    {
        return JsonSerializer.Serialize(new
        {
            entry.Timestamp,
            Level = entry.Level.ToString(),
            entry.Category,
            entry.Message,
            entry.Exception?.Message
        });
    }
}
```

## Logging Scopes

```csharp
using (logger.BeginScope(new { RequestId = "abc-123" }))
{
    logger.Info("Processing request");
    // Logs include scope context
}
```

## Configuration

### Queue Behavior

Three backpressure strategies when the queue is full:

| Mode | Behavior |
|---|---|
| **DropOldest** (default) | Evict the oldest entry, accept the new one |
| **DropWrite** | Discard the new entry |
| **Wait** | Block or await until space is available |

```csharp
var options = new LoggerFactoryOptions
{
    QueueCapacity = 10000,
    QueueFullMode = LogQueueFullMode.Wait,
    SyncWriteTimeout = TimeSpan.FromMilliseconds(500)
};
```

### Category Filtering

```csharp
var options = new LoggerFactoryOptions
{
    MinLevel = LogLevel.Info,  // global default
    FilterRules =
    {
        new LogFilterRule("Microsoft", LogLevel.Warning),
        new LogFilterRule("MyApp.Data", LogLevel.Debug)
    }
};
```

Rules are applied in reverse order — the last matching rule wins.

### Dropped Message Notification

```csharp
var options = new LoggerFactoryOptions
{
    QueueCapacity = 1000,
    OnMessagesDropped = (category, count) =>
        Console.Error.WriteLine($"Dropped {count} messages for {category}")
};
```

### Timestamp Control

```csharp
var options = new LoggerFactoryOptions
{
    TimestampProvider = TimeProvider.System  // default; inject fake for testing
};
```

## Flush & Shutdown

```csharp
await using var factory = new LoggerFactory(sinks, options);

// Flush without shutdown — drains pending entries, factory remains usable
await factory.FlushAsync();

// Leaving the scope runs DisposeAsync(): it drains all pipelines and sinks and
// aggregates sink errors. Do not call DisposeAsync() again — `await using` owns it.
```

Disposal is asynchronous, and that is the whole contract: `LoggerFactory` derives from
`ILoggerFactory`, which implements `IAsyncDisposable`, and nothing in `PicoLog` implements
`IDisposable`. A caller who writes `using var factory = new LoggerFactory(sinks);` gets a compile
error — `CS8418: 'LoggerFactory': type used in a using statement must implement
'System.IDisposable'. Did you mean 'await using' rather than 'using'?` — so every factory in this
README is released with `await using`. <!-- doc-lint: counter-example -->

Set `ShutdownTimeout` to bound the drain:

```csharp
var options = new LoggerFactoryOptions
{
    ShutdownTimeout = TimeSpan.FromSeconds(5)  // TimeSpan.Zero = wait indefinitely
};
```

On shutdown timeout, sinks receive a drain `CancellationToken`. The factory waits for the processing
pipeline to finish draining before disposing resources. That deadline is a `CancellationTokenSource`
timer — `ThreadPool` work — so under total pool starvation it is not honoured: the wait ends
whenever a worker frees up, not at the budget (recorded as a known limitation in
`docs/release-notes/v2026.11.0.md`, "Deadlines a starved `ThreadPool` does not honour").

## DI Integration (PicoLog.DI)

```shell
dotnet add package PicoLog.DI
```

```csharp
using PicoLog.DI;

container.AddPicoLog(o =>
{
    o.MinLevel = LogLevel.Info;
    o.WriteTo.ColoredConsole();
    o.WriteTo.File("logs/app.log");
    o.WriteTo.File(opts =>
    {
        opts.FilePath = "logs/errors.log";
        opts.BatchSize = 1;  // write immediately
    });
    o.Factory.QueueFullMode = LogQueueFullMode.DropOldest;
});

// Resolve typed logger
var logger = container.CreateScope().GetService<ILogger<Program>>();
```

### Consume Registered Sinks

```csharp
// Register a custom sink in DI, then tell PicoLog to use it
container.RegisterSingleton<ILogSink>(_ => new CustomSink());

container.AddPicoLog(o =>
{
    o.ReadFrom.RegisteredSinks();  // discover ILogSink from container
    o.WriteTo.ColoredConsole();    // additional owned sinks
});
```

### Custom Sink Registration

```csharp
container.AddPicoLog(o =>
{
    o.WriteTo.Sink(new CustomSink());
    o.WriteTo.Sink(() => new LazySink());         // factory
    o.WriteTo.Sink(fmt => new FormattedSink(fmt)); // formatter-aware factory
});
```

## OpenTelemetry Metrics

`PicoLogMetrics` exposes standard counters via `System.Diagnostics.Metrics`:

| Metric | Description |
|---|---|
| `picolog.entries.enqueued` | Total entries accepted |
| `picolog.entries.dropped` | Entries dropped due to queue full |
| `picolog.sinks.failures` | Sink write failures |
| `picolog.writes.rejected_after_shutdown` | Writes rejected after factory shutdown |
| `picolog.queue.entries` | Current queued entries (observable gauge) |
| `picolog.shutdown.drain.duration` | Shutdown drain time in ms (histogram) |

All metrics are AOT-compatible and integrate with OpenTelemetry collectors.

`picolog.writes.rejected_after_shutdown` is the one counter a sink in *another* package has to
record from outside this assembly, so `PicoLogMetrics.RecordRejectedAfterShutdown()` is
**public**: call it once per write you refuse because your sink is already closed (`SeqSink` and
`FileSink` both do). Every other recorder on `PicoLogMetrics` is `internal`, and recording is a
stateless `Add(1)` on the shared instrument, so the method carries no behaviour of its own. The
widening is pinned by a committed baseline — `api/PicoLog.public.txt`, one of the `api/` files
`scripts/api-surface/` generates and only the release tool refreshes, never hand-edited — so the
next release reports it as an API diff. The rule that matters more, that core carries no
serializer, is still enforced by `AssemblySurfaceTests.PicoLog_ReferencesNoSerializer`.

## Packages

| Package | TFM | Description |
|---|---|---|
| **PicoLog** | net10.0 | Logging runtime: `LoggerFactory`, console + file sinks, formatters |
| **PicoLog.Abs** | net10.0 | `ILogger`, `ILogSink`, `LogLevel`, `LogEntry`, `EventId` |
| **PicoLog.Gen** | netstandard2.0 | `[PicoLogMessage]` source generator (embedded in `PicoLog.Abs`; not a standalone package) |
| **PicoLog.DI** | net10.0 | DI integration (`AddPicoLog`, `WriteTo`, `ReadFrom`) |
| **PicoLog.Json** | net10.0 | The JSON sinks: `JsonLogSink` (NDJSON file) and `SeqSink` (batched Seq HTTP), both written with the PicoJetson `JsonWriter` |

**Package move.** `SeqSink` moved from `PicoLog` to `PicoLog.Json` — same type, same
`namespace PicoLog`, so a **source** build needs one line: `dotnet add package PicoLog.Json`.
No source change follows: `using PicoLog;` and `new SeqSink(...)` still compile.

It is **not binary-compatible.** An assembly compiled against `PicoLog` ≤ 2026.10.1 holds a
`PicoLog.SeqSink` type reference into the assembly `PicoLog`, and that type is no longer in it, so
the first use throws `TypeLoadException`. That is **every published version**: `2026.10.1` is the
newest tag and it still carries `SeqSink` inside `PicoLog`, so the boundary is the **next tag**, not
a date. Recompile it against `PicoLog.Json` — and note that both escape hatches a version like
this usually ships are refused on purpose:

- a `[TypeForwardedTo(typeof(SeqSink))]` in `PicoLog` would make `PicoLog.dll` reference
  `PicoLog.Json.dll`, and through it the serializer — exactly the dependency this split removes;
- an `[Obsolete]` stub left behind in `PicoLog` would have to keep the JSON-writing code, which is
  the same dependency with a deprecation notice on it.

Core `PicoLog` therefore references no serializer at all (it never did), and a test pins its whole
referenced-assembly list, so a sink that must write JSON cannot live there.

[← Back to PicoInfra](../README.md)
