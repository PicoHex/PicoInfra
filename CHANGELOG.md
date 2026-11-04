# Changelog

Breaking changes only — one section per unreleased change, newest first. Everything else is in
[`docs/releasing.md`](docs/releasing.md) and in the generated GitHub release notes.

Each section names the commit that introduced the change: read that commit for the reasoning and
the test that pins it. Each one ends with what a consumer must do, not with a summary of internals.

## Unreleased

### PicoLog — `SeqSink` moved from `PicoLog` to `PicoLog.Json`

- **Breaking (binary).** `PicoLog.SeqSink` now ships in the **`PicoLog.Json`** package, not in
  **`PicoLog`**. The type and its namespace are unchanged, so **source** is compatible after
  `dotnet add package PicoLog.Json` — no code change follows. Moved in `5729d95`, merged as
  `a597920`.
- An assembly compiled against `PicoLog` ≤ **2026.10.1** keeps a `PicoLog.SeqSink` type reference
  into assembly `PicoLog` and throws `TypeLoadException` at first use: **recompile it against
  `PicoLog.Json`**. `v2026.10.1` is the newest tag and it still carries `SeqSink` inside `PicoLog`,
  so **every published version is affected** — the boundary is the next tag, not `2026.10`. No type
  forwarder and no `[Obsolete]` stub are shipped, on purpose — a forwarder would make `PicoLog.dll`
  reference `PicoLog.Json.dll` (and through it the JSON serializer), and a stub would have to keep
  the JSON-writing code in core. "Core references no serializer" is the rule the move exists to
  enforce.

### PicoDI — the internal stop budget is per service, not per pass

- **Breaking (behaviour).** `DisposeAsync` and the failed-start rollback give **each** hosted
  service its own hard 30 s wall-clock stop budget (`HostedSvcStopTimeout`, internal). When it
  expires that service's phase is **abandoned** (not cancelled) and reported as a
  `TimeoutException` — to `OnError`, and on the rollback into the start exception's `Data` — and
  the pass moves on to the **next** service. The budget used to be shared by the whole pass, so one
  service that ignored its token consumed it, every service behind it received no `StopAsync` at
  all, and those services were still recorded as stopped, so they could never be stopped later. A
  fault raised by an abandoned phase after the call returned is now reported to `OnError`; it used
  to surface only as an unobserved task exception at finalization. — `043968c`.
- **What to do.** Worst-case shutdown is now 30 s × the number of hung services instead of 30 s in
  total; if you sized a shutdown deadline on the old single cap, raise it or bound it with your own
  token. `OnError` is where an abandoned phase and its late fault are reported, and its default is
  `null` — set it on the container you own (`SvcContainer.OnError`; `ISvcContainer` does not
  expose it, so a host built by `SvcHostBuilder` cannot observe these at all). The caller-driven
  `StopAsync(token)` path is still bounded only by your token.

### PicoDI — `StopAsync` on a disposed host faults instead of returning

- **Breaking (behaviour).** `IHost.StopAsync` on a container that has already been disposed now
  throws `ObjectDisposedException`, symmetric with `StartAsync`. It used to return a completed
  task, which is indistinguishable from a shutdown that really happened: disposal already ran the
  single stop pass, so the call could never do work. `DisposeAsync` stays idempotent and never
  throws. — `66499a7`.
- **What to do.** If you call `StopAsync` on a path that can race disposal, catch
  `ObjectDisposedException` (or own the host's lifetime so the call cannot race) instead of relying
  on a silent success. The fault is a report only — no service is stopped a second time.

### PicoDI — hosted services are tracked per registration, not per type

- **Breaking (behaviour).** A hosted type registered more than once — two instances, two factories,
  or the same implementation registered twice with different configuration — now starts **and**
  stops **all** of its registrations, in start order and the reverse of it. Only the last
  registration per type used to be tracked, so the earlier instances never started and never
  stopped while the host reported a successful start. Each registration is resolved through its own
  registration, not through `GetService(type)`. One registration of the *same object* still starts
  once and stops once — start and stop each run at most once per instance, however many phases the
  lifecycle service has. — `97332ff`. The stop pass walks the start-order list backwards now, so a
  shared instance is stopped at the position at which it **started**, not at its last registration
  — `023e8b7`.
- **What to do.** If you registered the same hosted type twice, both will now run: remove the
  duplicate registration if that was accidental, or expect two starts. Note that type resolution is
  still last-wins, so `GetService<T>()` answers with the last registration's instance while
  `GetServices<T>()` reaches the others — deleting the second registration changes which object was
  serving, not just how many.

### PicoDI — container disposal order changed

- **Breaking (behaviour).** `SvcContainer.DisposeAsync()` disposes in dependency order: (1) hosted
  services, (2) user root scopes, (3) container-owned singletons, (4) the container root (implicit
  root) scope — the container-internal hosting scope is tracked as a root scope, so it is released
  inside step 2. Singletons used to be disposed **before** the user root scopes, so a scoped service
  whose `Dispose` used a singleton, or a scope whose `DisposeAsync` called one, ran against an
  instance that had already been disposed. — `09e3a3a`.
- **What to do.** If a scoped disposer relied on its singletons being gone (or you compensated for
  the old order), it no longer sees that — singletons are still alive while user scopes are
  disposed, which is the point: a disposer may use what it depends on. `DisposeAsync` still never
  throws; disposal failures go to `OnError`.

### PicoDI — hosted-service start and stop failures reach the caller

- **Breaking (behaviour).** A failing start phase (`StartingAsync` / `StartAsync` / `StartedAsync`)
  faults the caller's `StartAsync` with the service's **own** exception — same instance, type,
  message and stack trace, not wrapped. The start sequence aborts, so services registered after it
  never start; the services that already started are stopped newest start first (`023e8b7` — see the
  per-registration entry above for which order that is); the service whose start threw is **not**
  stopped, because its start never completed; and the host is left **stopped**, so a later
  `StartAsync` / `StopAsync` / `DisposeAsync` is a no-op for the sequence
  (`SvcHostBuilder.BuildAsync` disposes the container and rethrows). A stop failure during that
  rollback never replaces the start exception: it is folded into the start exception's `Data` under
  `PicoDI.HostedSvcRollBackStopFailures` (a `List<Exception>`, present only when something failed).
  A failing **stop** still stops every service, then throws an `AggregateException` carrying the
  individual failures. Every case used to vanish into `OnError`, whose default is `null`, so a
  failed start reported success. — `acbafdb` (rollback `Data` read: `206121f`).
- **What to do.** Wrap `StartAsync` and `StopAsync` in `try` / `catch` where you did not before:
  `catch (InvalidOperationException)` around `StartAsync` keeps working because the rethrow is
  unwrapped, but `StopAsync` now throws `AggregateException` — do not call it from a `finally` that
  assumed it could not throw. `DisposeAsync` remains the never-throwing path (it routes to
  `OnError`).

### PicoCfg — an ambiguous flattened key throws `CfgKeyCollisionException`

- **Breaking (behaviour).** The text backends flatten a document into one `':'`-separated key space,
  so a name that itself contains `':'` addresses the same key as real nesting: YAML `a:` + `b: 1`
  and `"a:b": 2`, TOML `on = false` under `[db.ssl]` and `"ssl:on" = true` under `[db]`. The second
  assignment used to overwrite the first silently. It now throws `CfgKeyCollisionException` (which
  carries the format, the key and both positions) **while the snapshot is built** —
  `CfgBuilder.BuildAsync`, or a watched file reopening — never from a later lookup. INI changes a
  *rule* rather than only gaining a guard: it already nests with `'.'` (`[Parent.Child]` →
  `Parent:Child`), so `':'` may not appear in a section name or a key any more — `[A:C]`, or `B:C=1`
  under `[A]`, is rejected at its own position instead of becoming a second spelling of `A:C`.
  Duplicate keys **inside one mapping** are still last-wins, and a `':'` inside a **value** is
  untouched. — `da73005`.
- **What to do.** A document that used to load can now fail at `BuildAsync`: rename one of the two
  colliding positions, or move one into a separate configuration source. INI documents migrate by
  spelling nesting with `'.'` — `[A:C]` → `[A.C]`, and `B:C=1` under `[A]` → `C=1` under `[A.B]`;
  every key that does not carry `':'` keeps the value it has today. The exception derives from
  `InvalidOperationException`, so an existing `catch (InvalidOperationException)` still catches it.
  `PicoCfg.Json` is **not** covered — it flattens the same way and stays last-wins.

### PicoDI — scoped cycles throw `PicoDiException`, not `InvalidOperationException`

- **Breaking (source: catch semantics).** A singleton or scoped factory that resolves the service it
  is constructing now throws `PicoDiException` naming the cycle (`A -> B -> A`). A *scoped* cycle
  used to surface as `InvalidOperationException` (`ValueFactory attempted to access the Value
  property of this instance.` — the scoped instance is a `Lazy<T>`); a *singleton* cycle aborted the
  process with a stack overflow. `PicoDiException` derives from `Exception`, **not** from
  `InvalidOperationException`. — `0644f5e`; the type documents it since `9cb99c8`.
- **What to do.** A `catch (InvalidOperationException)` around a scoped resolve silently stops
  catching a cycle — catch `PicoDiException` (or `Exception`) instead. Nothing else changes:
  lifetime, caching and retry semantics are untouched, and the code that had no cycle keeps its
  behaviour.

### PicoSchedule — cron `dow = 7` is Sunday

- **Breaking (behaviour).** `CronPattern` set bit 7 for `dow = 7`, but the day-indexed bit set is
  `0..6`, so `0 5 * * 7` parsed and then threw `cron pattern did not match within 400 years` out of
  `Register`, and `5-7` silently dropped Sunday. `7` now folds onto `0`: `0 5 * * 7` is the same
  schedule as `0 5 * * 0` and `0 5 * * sun`, `5-7` is Fri/Sat/Sun, and `0-7` is every day — the last
  one used to be "every day" only by accident, through the wildcard test counting the dead bit. The
  dow field also records `*` / `?` explicitly instead of counting set bits, because after the fold
  `'*'` and a `0-6` list both have seven bits set and `0-6` is still a restriction (day-of-month OR
  day-of-week). — `1cdba0a`.
- **What to do.** A schedule written as `dow = 7` that never fired now fires every Sunday — that is
  the fix, so check it is what you meant. `5-7` gains Sunday, which it was silently missing; range
  and list expressions that relied on 7 matching nothing change.
