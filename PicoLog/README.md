# PicoLog

High-performance structured logging for .NET Native AOT.

[![NuGet](https://img.shields.io/nuget/v/PicoLog)](https://nuget.org/packages/PicoLog)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](https://github.com/PicoHex/PicoInfra/blob/main/LICENSE)

[English](README.md) | [简体中文](README.zh.md) | [日本語](README.ja.md) | [Español](README.es.md) | [Português](README.pt.md) | [繁體中文](README.zh-tw.md) | [한국어](README.ko.md) | [Français](README.fr.md) | [Deutsch](README.de.md) | [Русский](README.ru.md)

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
using var factory = new LoggerFactory([sink],
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
using var factory = new LoggerFactory(
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

Add `PicoLog.Gen` as an analyzer to enable compile-time message generation:

```xml
<PackageReference Include="PicoLog.Gen" PrivateAssets="all" />
```

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

### Built-in

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
using var factory = new LoggerFactory(sinks, options);

// Flush without shutdown — drains pending entries, factory remains usable
await factory.FlushAsync();

// Dispose drains all pipelines and sinks, aggregates errors
await factory.DisposeAsync();
```

Set `ShutdownTimeout` to bound the drain:

```csharp
var options = new LoggerFactoryOptions
{
    ShutdownTimeout = TimeSpan.FromSeconds(5)  // TimeSpan.Zero = wait indefinitely
};
```

On shutdown timeout, sinks receive a drain `CancellationToken`. The factory waits for the processing pipeline to finish draining before disposing resources.

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

## Packages

| Package | TFM | Description |
|---|---|---|
| **PicoLog** | net10.0 | Logging runtime: `LoggerFactory`, sinks, formatters |
| **PicoLog.Abs** | netstandard2.0 | `ILogger`, `ILogSink`, `LogLevel`, `LogEntry`, `EventId` |
| **PicoLog.Gen** | netstandard2.0 | `[PicoLogMessage]` source generator |
| **PicoLog.DI** | net10.0 | DI integration (`AddPicoLog`, `WriteTo`, `ReadFrom`) |

[← Back to PicoInfra](../README.md)
