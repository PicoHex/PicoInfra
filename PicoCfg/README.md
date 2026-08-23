# PicoCfg

Async-first configuration management for .NET Native AOT.

## Quick Start

```shell
dotnet add package PicoCfg
```

```csharp
using PicoCfg;
using PicoCfg.Abs;
using PicoCfg.Extensions;

var cfg = await Cfg.CreateBuilder()
    .Add("App:Name=MyApp\nApp:Version=1.0")
    .BuildAsync();

var value = cfg.GetValue("App:Name");
```

## Provider Model

```
Sources ──→ Providers ──→ Root ──→ Consumer
```

Sources define **how** config is produced. Providers own snapshot lifecycle. The root composes provider snapshots into a single unified view. Consumers query via `TryGetValue` or `GetValue`.

## Built-in Sources

| Source | Description |
|---|---|
| **Dictionary** | In-memory key-value pairs |
| **Environment Variables** | OS environment, prefix filtering, `__` → `:` mapping |
| **Command Line** | `--key=value`, `--key value`, `-key value`, `/key value` |
| **Stream** | Line-based `key=value` text parsing |
| **File Watching** | Auto-reload on file change with debounce (default 200ms) |
| **Chained** | Live delegation to another `ICfg` instance |
| **KeyPerFile** | Kubernetes ConfigMap style — filename=key, content=value |

Sources are evaluated in insertion order, with later sources overriding earlier ones on key conflict.

## CfgBuilder

```csharp
var cfg = await Cfg.CreateBuilder()
    .AddEnvironmentVariables("APP_")
    .Add(() => File.OpenRead("appsettings.cfg"))
    .Add(() => File.OpenRead($"appsettings.{env}.cfg"),
        watchPath: $"appsettings.{env}.cfg")
    .AddCommandLine(args)
    .AddKeyPerFile("/etc/config")
    .BuildAsync();
```

Version stamps enable incremental reloads — when a stamp matches the previously accepted value, the source is skipped:

```csharp
var stamp = 0;
builder.Add(() => fetchRemoteConfig(),
    versionStampFactory: () => Interlocked.Read(ref stamp));
```

## Reading Configuration

```csharp
// Exact lookup — returns null when key is absent
var name = cfg.GetValue("App:Name");

// Try-pattern — no allocation on miss
if (cfg.TryGetValue("App:Timeout", out var raw))
    Console.WriteLine(raw);

// Hierarchical section — live view that reflects parent reloads
var app = cfg.GetSection("App");
var name = app.GetValue("Name");          // resolves "App:Name"

// Enumerate all keys (native snapshots only)
var all = cfg.GetAll();
```

## Change Notification & Reload

```csharp
// Explicit reload — returns true when snapshot changed
var changed = await root.ReloadAsync();
if (changed)
    Console.WriteLine(cfg.GetValue("App:Name"));

// Block until next change
await root.WaitForChangeAsync(cts.Token);

// Using delegates for reactive patterns
using var cts = new CancellationTokenSource();
_ = Task.Run(async () =>
{
    while (!cts.IsCancellationRequested)
    {
        await root.WaitForChangeAsync(cts.Token);
        Console.WriteLine(cfg.GetValue("App:Name"));
    }
});
```

## Source-Generated Binding

Add `PicoCfg.Gen` as an analyzer. Call `CfgBind.Bind<T>` — the generator emits binding delegates at compile time.

```csharp
using PicoCfg;

public sealed class AppSettings
{
    public string Name { get; init; }
    public int MaxRetries { get; init; } = 3;
}

var settings = CfgBind.Bind<AppSettings>(cfg, "App");

// TryBind — returns false instead of throwing on parse failures
if (CfgBind.TryBind<AppSettings>(cfg, out var result, "App"))
    Console.WriteLine(result.Name);

// BindInto — populate an existing instance
var instance = new AppSettings();
CfgBind.BindInto(cfg, instance, "App");

// Note: BindInto requires the type to have been used with CfgBind.BindInto<T>()
// (or Bind<T>()/TryBind<T>() for classes with a public parameterless
// constructor). Nested-only types — referenced exclusively from other DTOs —
// register only a constructing bind; calling BindInto on them throws
// PicoCfgBindRegistrationException.
```

Supported property types: `string`, `bool`, `int`, `long`, `float`, `double`, `decimal`, `Guid`, `enum`, `DateTime`, `DateTimeOffset`, `DateOnly`, `TimeOnly`, `TimeSpan`, `Uri`, `Version`, `BigInteger`, nested classes, `List<T>`, `T[]`, `Dictionary<string,T>`, `IReadOnlyList<T>`, `IReadOnlyCollection<T>`, `IEnumerable<T>`.

Init-only properties and `record` types are fully supported — including **positional records** with primary constructors: the generator constructs them through their primary constructor (parameters map to properties by name), so `record R(int A)` and records with `IReadOnlyList<T>` members bind natively as nested, collection-element, or dictionary-value types. **Nested collections bind natively at any depth** — `Dictionary<string, Dictionary<string, string>>`, `List<List<int>>`, `Dictionary<string, List<T>>` etc. use the indexed format extended per level (`Section:0:Key`, `Section:0:Value:0:Key`, `Section:0:Value:0:Value`). The generator expands up to **8 nested type levels**; deeper chains are reported with the `PCFGGEN009` diagnostic and the affected properties are skipped instead of generating broken code. Only truly unbindable element types (structs, `HashSet<T>`) are rejected with the `PCFGGEN010` diagnostic rather than binding silently to an empty collection.

## Options Pattern

Access typed configuration through `ICfgOptions<T>`:

```csharp
// DI registration — singleton (bind once, cache forever)
container.RegisterCfgOptionsSingleton<AppSettings>("App");

// DI registration — scoped (rebind on every resolution)
container.RegisterCfgOptionsScoped<AppSettings>("App");

// Resolve via scope
var options = scope.GetService<ICfgOptions<AppSettings>>();
var settings = options.Value;
```

`ICfgOptions<out T>` exposes a single `T Value { get; }` property. Singleton options bind at registration time and cache the result. Scoped options rebind from the current configuration state on every `Value` access.

## Validation

Fully AOT-compatible — uses source-generated binding and `IValidatableObject`, neither of which require runtime reflection.

```csharp
// Implement IValidatableObject on your binding types
public sealed class AppSettings : IValidatableObject
{
    public string Name { get; init; }
    public int MaxRetries { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext ctx)
    {
        if (MaxRetries < 0)
            yield return new ValidationResult("MaxRetries must be >= 0");
    }
}

// ValidateOrThrow — throws CfgValidationException on failure
CfgValidator.ValidateOrThrow(settings);

// BindAndValidate — bind then validate in one call
var settings = cfg.BindAndValidate<AppSettings>("App");
```

## DI Integration (PicoCfg.DI)

```csharp
container.RegisterCfgRoot(root);                       // ICfgRoot + ICfg
container.RegisterCfgSingleton<AppSettings>("App");    // POCO bound once
container.RegisterCfgScoped<RequestSettings>("Req");   // bound per scope
container.RegisterCfgOptionsSingleton<AppSettings>();  // ICfgOptions<T> cached
container.RegisterCfgOptionsScoped<AppSettings>();     // ICfgOptions<T> snapshot
```

## Packages

| Package | Description |
|---|---|
| **PicoCfg** | Configuration runtime |
| **PicoCfg.Abs** | `ICfg`, `ICfgRoot`, `ICfgSection`, `ICfgOptions<T>` |
| **PicoCfg.Gen** | `CfgBind.Bind<T>` source generator |
| **PicoCfg.DI** | DI integration |

[← Back to PicoHex](../README.md)
