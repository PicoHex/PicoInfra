# Changelog

Breaking changes only — one section per unreleased change, newest first. Everything else is in
[`docs/releasing.md`](docs/releasing.md) and in the generated GitHub release notes.

## Unreleased

### PicoLog — `SeqSink` moved from `PicoLog` to `PicoLog.Json`

- **Breaking (binary).** `PicoLog.SeqSink` now ships in the **`PicoLog.Json`** package, not in
  **`PicoLog`**. The type and its namespace are unchanged, so **source** is compatible after
  `dotnet add package PicoLog.Json` — no code change follows.
- An assembly compiled against `PicoLog` ≤ **2026.10** keeps a `PicoLog.SeqSink` type reference into
  assembly `PicoLog` and throws `TypeLoadException` at first use: **recompile it against
  `PicoLog.Json`**. No type forwarder and no `[Obsolete]` stub are shipped, on purpose — a forwarder
  would make `PicoLog.dll` reference `PicoLog.Json.dll` (and through it the JSON serializer), and a
  stub would have to keep the JSON-writing code in core. "Core references no serializer" is the rule
  the move exists to enforce.
