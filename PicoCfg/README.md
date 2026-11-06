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

## Text Formats & Key Collisions

The text backends flatten a document into one `':'`-separated key space, so a name that itself
contains `':'` addresses the same key as real nesting, and the later value silently won:

| Format | Real nesting | A literal `':'` in a name | Key both address |
|---|---|---|---|
| YAML | `a:\n  b: 1` | `"a:b": 2` | `a:b` |
| TOML | `on = false` under `[db.ssl]` | `"ssl:on" = true` under `[db]` | `db:ssl:on` |
| INI | `k=1` under `[A.C]` (`.` reads as nesting) | `k=1` under `[A:C]`, or `B:C=1` under `[A]` | `A:C` / `A:B:C` |

YAML and TOML keep the name as written and reject the result: one document may not produce the same
key from two different positions, and that is a `CfgKeyCollisionException` — thrown from `BuildAsync`
(or when a watched file reopens), never from a later lookup — which carries the key, the format and
both positions (line + raw path segments), so the two places can be found and renamed. Those are
`Key`, `Format`, `ExistingPosition` and `IncomingPosition`; the exception lives in `PicoCfg.Abs`
and derives from `InvalidOperationException` — so an existing `catch (InvalidOperationException)`
around `BuildAsync` still catches it. `ExistingPosition` is `null` only for the INI case below,
where no other spelling produced the key.

INI is stricter, because it already nests with `'.'`: `':'` is the separator and not a legal character
inside a section name or a key, so `[A:C]` — and `B:C=1`, and `[A.C]` together with `[A:C]` — is
rejected at its own position (naming the `[A.C]` that already produces `A:C` when the document has
one). That keeps the INI key space injective, one spelling per key, instead of two sections merging
into one. `[Parent.Child]` → `Parent:Child` is unchanged, so no existing document moves a key.

The contract is **not** an escaping scheme: escaping would rename every existing key and break
`CfgBind` path matching. The snapshot dictionary type and the `ICfg` shapes are unchanged.

What is *not* a collision:

- duplicate keys inside one mapping (`a: 1` then `a: 2`, `k=1` then `k=2` in one INI section or TOML
  table) — last wins, unchanged;
- a `':'` inside a **value** (`Url="http://host:80/path"`) — values are not path segments;
- a repeated INI section header or TOML table header naming the same section — same mapping, last wins.

`PicoCfg.Json` is not covered yet: it flattens the same way, so `{"a:b": 1}` and `{"a":{"b": 1}}`
still land on one key there, with the later value winning.

### Arrays and Sequences

`AddYaml` / `AddYamlFile` (`PicoCfg.Yaml`) and `AddToml` / `AddTomlFile` (`PicoCfg.Toml`) take an
optional `flattenArrays` flag, `false` by default — the same flag `AddJson` / `AddJsonFile` carry.

```csharp
using PicoCfg;
using PicoCfg.Toml;
using PicoCfg.Yaml;

var cfg = await Cfg.CreateBuilder()
    .AddYamlFile("servers.yaml", flattenArrays: true)  // Servers:0, Servers:1
    .AddTomlFile("app.toml", flattenArrays: true)      // ports:0, servers:0:name
    .BuildAsync();
```

Off (the default) an array or sequence is skipped entirely; on, every element contributes a numeric
index segment — the list format `CfgBind` binds to `List<T>`, `T[]` and the nested collection
shapes: `ports:0` for a TOML value array, `servers:0:name` for a TOML array of tables, `Servers:1`
for a YAML sequence of scalars. A YAML sequence of mappings is the one shape the reader does not
split — the first member of a `- name: x` item arrives as that item's own value (`Items:0` is
`"name: x"`, the rest under `Items:0:port`) — so bind a sequence of scalars from YAML.

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

The source generator is **embedded in `PicoCfg.Abs`** (no extra package). Call `CfgBind.Bind<T>` — the generator emits binding delegates at compile time.

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
| **PicoCfg.Gen** | `CfgBind.Bind<T>` source generator (embedded in `PicoCfg.Abs`) |
| **PicoCfg.DI** | DI integration |
| **PicoCfg.Json** | JSON configuration source (via PicoJetson) |
| **PicoCfg.Yaml** | YAML configuration source (via PicoYaml) |
| **PicoCfg.Ini** | INI configuration source (via PicoIni) |
| **PicoCfg.Toml** | TOML configuration source (via PicoToml) |

[← Back to PicoInfra](../README.md)
