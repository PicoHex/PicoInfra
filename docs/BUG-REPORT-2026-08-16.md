# PicoHex Monorepo — Bug Report

> **Status (2026-09-14, HEAD `2a5bbe2`) — resolved:** every code finding in this
> report was verified fixed on `main`: BUG-1 (solution builds; PicoAop.Benchmarks
> compiles), BUG-2 (all four samples run managed — PicoDI, PicoDI.Sample.Host,
> PicoLog, PicoAop; CI additionally executes the published AOT binaries for
> PicoAop/PicoMediator/PicoSchedule), BUG-3 (`FileSink` seeds the rotation index
> from disk, `overwrite:false`, cleanup failures recorded via `PicoLogMetrics`),
> BUG-4 (no CS1574 in PicoCfg builds), BUG-5 (workaround documented in
> CONTRIBUTING.md). The report is kept as a historical record — the "Verified
> healthy" test counts and warning numbers below are from the 2026-08-16 commit,
> not current.

- **Repository**: PicoHex/PicoInfra (`D:/MyProjects/PicoHex/PicoInfra`)
- **Commit**: `e9ed4f8` — "build: update all NuGet packages to latest stable" (HEAD, 2026-08-13)
- **Environment**: Windows x64, .NET SDK `10.0.301`, net10.0, `PublishAot=true` (repo default), Release config
- **Date of investigation**: 2026-08-16 (fresh analysis; no source code was modified)

---

## Executive Summary

| ID | Severity | Area | Status |
|----|----------|------|--------|
| [BUG-1](#bug-1-critical--picoaopbenchmarks-does-not-compile) | 🔴 Critical | PicoAop.Benchmarks | Broken at HEAD; failing code added in `42fac19` (2026-05-30) |
| [BUG-2](#bug-2-critical--4-samples-crash-at-startup-in-managed-and-aot-modes) | 🔴 Critical | Samples (PicoDI, PicoLog, PicoAop) | PicoDI samples broken since `b531f43` (2026-05-12); PicoLog/PicoAop samples never referenced the generator |
| [BUG-3](#bug-3-medium--filesink-log-rotation-overwrites-oldest-file-after-restart) | 🟡 Medium | PicoLog.FileSink | Data loss on process restart |
| [BUG-4](#bug-4-low--unresolved-cref-in-picocfg-doc-comment) | 🟢 Low | PicoCfg | Doc-comment warning |
| [BUG-5](#bug-5-low--dotnet-test-unreliable-under-mtp-223) | 🟢 Low | Tooling | Environment-level flakiness |

Verified healthy (baseline for the report):
- ✅ **All 831 tests pass** (PicoDI 300, PicoAop 30, PicoCfg 263, PicoLog 193, PicoMediator 45) — matches the `831/831` claim in `e9ed4f8`.
- ✅ All 24 `src/` library projects build with **zero warnings**.
- ✅ All 11 sample projects **build**; 6 of the 10 runnable samples **execute correctly** (all PicoCfg samples, `PicoLog.Sample.SerDe`, `PicoMediator.Sample` — including the AOT-published `PicoMediator.Sample.exe`). The other 4 crash at startup — see BUG-2.
- ✅ `dotnet publish` (AOT, win-x64) succeeds for PicoDI.Sample, PicoAop.Sample, PicoMediator.Sample.

---

## BUG-1 🔴 Critical — PicoAop.Benchmarks does not compile

### Symptom

`dotnet build PicoHex.slnx -c Release` fails with 5 errors. Any clean checkout of the repo **cannot build the solution**.

```
PicoAop/benchmarks/PicoAop.Benchmarks/MethodCallBenchmarks.cs(72,6): error PBGEN003:
  Benchmark method 'D0' must be an instance, non-generic method with no parameters
PicoAop/benchmarks/PicoAop.Benchmarks/MethodCallBenchmarks.cs(75,6): error PBGEN003: ... 'D1' ...
PicoAop/benchmarks/PicoAop.Benchmarks/MethodCallBenchmarks.cs(78,6): error PBGEN003: ... 'D3' ...
PicoAop/benchmarks/PicoAop.Benchmarks/MethodCallBenchmarks.cs(81,6): error PBGEN003: ... 'D5' ...
PicoAop/benchmarks/PicoAop.Benchmarks/Program.cs(31,43): error CS0311:
  The type 'PicoAop.Benchmarks.MethodCallReturnBenchmarks' cannot be used as type
  parameter 'T' in the generic type or method 'BenchmarkRunner.Run<T>(BenchmarkConfig?)'.
  There is no implicit reference conversion from '...MethodCallReturnBenchmarks'
  to 'PicoBench.IBenchmarkClass'.
```

### Root cause

`MethodCallReturnBenchmarks` (in `MethodCallBenchmarks.cs`, lines 72–81) declares its benchmark methods with a plain `int` return type:

```csharp
[Benchmark(Baseline = true, Description = "D0 (raw)")]
public int D0() => _svc0.Get();   // ← line 72, and D1/D3/D5 likewise
```

PicoBench's source generator only accepts `void`, `Task`, `ValueTask` (or `Task<T>` / `ValueTask<T>` with the value discarded). The `int` return is rejected with **PBGEN003**, so no `IBenchmarkClass` implementation is generated for the class, which cascades into **CS0311** at `Program.cs(31,43)`.

Proof this is the return type and not the shape of the method: the neighboring `MethodCallTaskReturnBenchmarks` class uses the identical structure with `ValueTask<int>` and compiles cleanly.

Two secondary problems:
1. **The PBGEN003 message is misleading** — the methods *are* instance, non-generic and parameterless; the actual violation is the return type. (Diagnostic-quality issue, upstream in PicoBench 2026.2.5.)
2. **The failure is acknowledged but not fixed** — commit `e9ed4f8` states: *"PicoAop.Benchmarks keeps its pre-existing PBGEN003/CS0311 failures (unchanged by this bump)"*. The failing code was added by `42fac19` ("feat: add PicoAop method call throughput benchmarks (Part 1)", 2026-05-30, which added `MethodCallBenchmarks.cs` only — no csproj/package changes). The author of `e9ed4f8` confirms the failures are pre-existing, i.e. present at least since PicoBench 2026.2.4; the project has not built on HEAD ever since.

### Why CI misses it

`.github/workflows/ci.yml` contains **zero references to benchmark projects** — it builds only test projects and packs NuGet packages. The broken project is not in any CI matrix, so it has never blocked a PR.

### Impact

- Anyone opening the repo in an IDE gets red squiggles and a failing solution build.
- The PicoAop benchmark suite (README's benchmark data source for PicoAop) cannot be produced.
- Any future CI that builds `PicoHex.slnx` will fail immediately.

### Suggested fix

Change the four `int`-returning benchmark methods to discard the value with `void` bodies (or use `ValueTask<int>` where async is appropriate), e.g.:

```csharp
[Benchmark(Baseline = true, Description = "D0 (raw)")]
public void D0() => _ = _svc0.Get();
```

and add benchmark projects (or at least a build of them) to CI.

---

## BUG-2 🔴 Critical — 4 samples crash at startup in managed AND AOT modes

### Symptom

Four sample projects throw at the first type-based registration and terminate:

```
=== PicoDI Registration Methods Demo ===
Unhandled exception. PicoDI.Abs.SourceGeneratorRequiredException: Compile-time generated
registrations are required. Ensure PicoDI.Gen runs and that generated registrations are
applied to this container.
   at PicoDI.Abs.SvcContainerGeneratedRegistrationExtensions.RegisterSingleton[...](...)
   at Program.<Main>$(String[] args) in ...\PicoDI.Sample\Program.cs:line 17
```

Affected projects (reproduced with `dotnet run -c Release -p:PublishAot=false`, **and** with the AOT-published `.exe` where noted):

| Project | Managed run | AOT run (`dotnet publish -r win-x64` → execute) |
|---------|-------------|------------------------------------------------|
| `PicoDI/samples/PicoDI.Sample` | 💥 crashes, `Program.cs:17` | 💥 crashes (`PicoDI.Sample.exe` published OK, dies at startup) |
| `PicoDI/samples/PicoDI.Sample.Host` | 💥 crashes | not tested (same defect) |
| `PicoLog/samples/PicoLog.Sample` | 💥 crashes, `Program.cs:16-17` | not tested (same defect) |
| `PicoAop/samples/PicoAop.Sample` | 💥 crashes | 💥 crashes (`PicoAop.Sample.exe` published OK, dies at `Program.<<Main>$>d__0.MoveNext() + 0x27b0`) |

### Root cause

PicoDI's type-based registration markers (`RegisterSingleton<TService, TImplementation>()`, etc. in `PicoDI.Abs/SvcContainerGeneratedRegistrationExtensions.cs`) are compile-time declarations: the actual registration code is emitted by the **PicoDI.Gen** source generator (via a module initializer). When the generator hasn't run, `EnsureApplied(...)` throws `SourceGeneratorRequiredException` — by design.

None of the four samples reference the generator:

1. **`PicoDI.Sample` / `PicoDI.Sample.Host`** — the `PicoDI.Gen` analyzer reference was **removed** by commit `b531f43` (2026-05-12, "fix(bench): GetService asymmetry, async-over-sync, **sample conflict**, golden files, ..."):
   ```diff
     <ItemGroup>
       <ProjectReference Include="..\..\src\PicoDI\PicoDI.csproj" />
   -   <!-- Explicit analyzer reference to ensure source generator runs -->
   -   <ProjectReference Include="../../src/PicoDI.Gen/PicoDI.Gen.csproj"
   -                       OutputItemType="Analyzer"
   -                       ReferenceOutputAssembly="false" />
       <ProjectReference Include="..\PicoDI.Sample.Services\PicoDI.Sample.Services.csproj" />
     </ItemGroup>
   ```
   The same deletion was applied to `PicoDI.Sample.Host.csproj`. Git history (`-S "PicoDI.Gen"`) confirms the reference was never restored.
2. **`PicoLog.Sample`** — references `PicoLog.Gen` (for `[PicoLogMessage]`) but has **never** referenced `PicoDI.Gen`, while its `Program.cs` calls the type-based `RegisterSingleton` markers.
3. **`PicoAop.Sample`** — references **neither** `PicoDI.Gen` **nor** `PicoAop.Gen`. It is doubly broken: even after the DI crash is fixed, `InterceptBy<TInterceptor>()` (`PicoAop.DI/SvcContainerInterceptorExtensions.cs:25,34,43,53`) throws `InvalidOperationException` when the PicoAop generator hasn't run.

Note: the `PicoDI` NuGet package does **not** pull in `PicoDI.Gen` transitively (`PicoDI.csproj` has no reference to it; `PicoDI.Gen` is a standalone package). So the samples fail in both `UseProjectReferences=true` (repo default) and package mode.

### Why CI misses it

1. CI **never builds or runs any sample** except `dotnet publish` of `PicoAop.Sample` and `PicoMediator.Sample` (ci.yml lines 542, 637) — and those steps are **publish-only**; the produced native binary is never executed.
2. Commit `e9ed4f8` claims "11 samples build" as its verification bar — but the failure is at **runtime**, not build time. "Samples build" gives false confidence.
3. The "AOT publish validation" CI step passes for exactly the broken `PicoAop.Sample` (publish succeeds; the exe crashes on launch) — a textbook publish≠run gap.

### Impact

- Anyone following the README/docs and running a sample gets an exception on the first registration call.
- The sample CI claims to AOT-validate (`PicoAop.Sample`) actually crashes when executed.
- New contributors copying the sample csproj pattern will produce crashing apps.

### Suggested fix

Add the analyzer reference used by the test projects (same pattern as `PicoDI/tests/PicoDI.Test/PicoDI.Test.csproj`) to all four samples:

```xml
<ProjectReference Include="...\src\PicoDI.Gen\PicoDI.Gen.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

`PicoAop.Sample` additionally needs `PicoAop.Gen` as an analyzer reference. Then extend CI's AOT validation to **execute** the published binaries (and ideally `dotnet run` each sample in the managed path).

---

## BUG-3 🟡 Medium — FileSink log rotation overwrites the oldest file after restart

### Symptom

`PicoLog`'s `FileSink` (size- or interval-based rotation, `MaxRetainedFiles > 0`) silently loses rotated log data across process restarts.

### Root cause

`PicoLog/src/PicoLog/FileSink.cs` keeps the rotation counter **in memory only**:

```csharp
private int _rotationIndex;                       // line 17 — starts at 0 every run

private string GetRotatedFilePath()               // line 231
{
    ...
    return Path.Combine(dir, $"{name}.{_rotationIndex + 1}{ext}");   // line 236
}
...
File.Move(_baseFilePath, rotatedPath, overwrite: true);              // line 215
...
_rotationIndex++;                                 // line 227
```

On the **first rotation of every process**, the target is `app.1.log`. Because `File.Move(..., overwrite: true)` is used and `_rotationIndex` is not seeded from existing files on disk, a restart overwrites the previous run's `app.1.log` (the oldest retained file) without warning. Nothing on disk is consulted before the move (`GetRotatedFilePath` uses only the in-memory counter; `CleanUpOldFiles` runs only after the overwrite).

Additional weakness: `CleanUpOldFiles` swallows delete failures with a bare `catch { }` (line 263), so retention-cleanup failures are invisible (no `OnError`, no debug output).

### Impact

- Silent data loss of the oldest retained log segment on every process restart, for any app that enables rotation. This defeats the retention guarantee implied by `MaxRetainedFiles`.
- No diagnostics when cleanup fails.

### Suggested fix

Seed `_rotationIndex` from existing `{name}.*{ext}` files at construction (max numeric suffix), and report (or at least debug-log) delete failures in `CleanUpOldFiles`. Consider whether `overwrite: true` should be `false` once the index is seeded, so an unexpected collision surfaces instead of destroying data.

---

## BUG-4 🟢 Low — Unresolved `cref` in PicoCfg doc comment

`PicoCfg/src/PicoCfg/PicoCfgBindRuntime.cs(59,50)`:

```
warning CS1574: XML comment has cref attribute 'GetAll' that could not be resolved
```

The comment references `<see cref="GetAll"/>` but the method lives on `CfgSection` — the cref must be `<see cref="CfgSection.GetAll"/>` (the same comment block already uses the qualified form elsewhere). Cosmetic, but the warning leaks into every build that compiles PicoCfg (e.g. PicoCfg.Benchmarks builds show it).

---

## BUG-5 🟢 Low — `dotnet test` unreliable under MTP 2.2.3

### Symptom (observed during this investigation)

First attempts via `dotnet test` on healthy test projects failed:

```
PicoDI.Test.dll (net10.0) Zero tests ran
Exit code: 5
Test run summary: Zero tests ran
  error: 1 / total: 0 / failed: 0 / succeeded: 0 / skipped: 0
```

accompanied by:

```
warning MSB3026: Could not copy "obj\Release\net10.0\PicoDI.dll" to
"bin\Release\net10.0\PicoDI.dll". Beginning retry 1 in 1000ms. The process cannot
access the file ... because it is being used by another process.
```

This is the known Microsoft Testing Platform 2.2.3 discovery instability (test-count drift / hang) with stale `testhost.exe` processes holding file locks — already documented for this repo in the project's test-runner notes.

### Workaround (used to obtain the 831/831 green baseline above)

```powershell
taskkill //F //IM testhost.exe    # clear stale hosts holding file locks
dotnet run --project <test-project> -c Release -p:PublishAot=false
```

`-p:PublishAot=false` is required because the repo default `PublishAot=true` turns a managed test run into an AOT publish.

### Impact

Low — environment-level, not a code defect; but new contributors hitting "Zero tests ran" will chase ghosts. Consider pinning/documenting the working invocation in CONTRIBUTING.md.

### Update (2026-09-14, MTP 2.3.3 + SDK 10.0.400)

The dominant **reproducible** cause of `Zero tests ran` on the current toolchain is
passing `--nologo` to `dotnet test`: under the .NET 10 SDK's new test experience
it reports zero tests for **every** project (verified with and without `--nologo`:
0 vs 941 tests). Do not pass `--nologo`; use `--no-progress`. The verified green
invocation (mirrors CI, per project) is:

```powershell
dotnet build <test-project> -c Release
dotnet test <test-project> -c Release --no-build --no-progress
```

With that invocation the full suite is 941/941 (2026-09-14). CONTRIBUTING.md now
documents both this and the stale-`testhost.exe` hygiene step.

---

## Minor observations (not filed as bugs)

- **148 build warnings**, essentially all in **test projects**: `IL2026` (TUnit `IsEquivalentTo` structural comparison requires reflection — not AOT-compatible) and a few `IL2075` (`GetType().GetProperty(...)` reflection in PicoLog.Tests). Harmless today because tests run managed, but they mask real trim issues and would block any future AOT test run. Worth an eventual sweep.
- `PicoDI/src/PicoDI/SvcContainer.Scopes.cs:33` and `SvcScope.Resolution.cs:370` use intentional, documented sync-over-async disposal on race/cleanup paths; reviewed and acceptable (contract-restricted fast paths only).
- `CategoryPipeline.WriteAsync`'s `writeTask.Result` (line 81) is guarded by `IsCompletedSuccessfully` — safe.

---

## Appendix — Reproduction commands

```powershell
# BUG-1: solution build fails
dotnet build PicoHex.slnx -c Release

# BUG-2: samples crash (managed)
dotnet run --project PicoDI/samples/PicoDI.Sample -c Release -p:PublishAot=false
dotnet run --project PicoDI/samples/PicoDI.Sample.Host -c Release -p:PublishAot=false
dotnet run --project PicoLog/samples/PicoLog.Sample -c Release -p:PublishAot=false
dotnet run --project PicoAop/samples/PicoAop.Sample -c Release -p:PublishAot=false

# BUG-2: samples crash (AOT) — publish succeeds, exe dies at startup
dotnet publish PicoAop/samples/PicoAop.Sample -c Release -r win-x64 -o out
.\out\PicoAop.Sample.exe

# BUG-4: warning on any PicoCfg build
dotnet build PicoCfg/src/PicoCfg -c Release
```
