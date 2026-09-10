# PicoSchedule

**Embedded · AOT-first · Scheduling framework** — a zero-dependency in-memory
scheduler built around a timing wheel with a complete cron dialect.

## Positioning

- **Embedded** — `new Scheduler(...)` is a plain object: no DI, no hosting,
  no configuration system, no persistence. The host owns lifecycle, persistence
  and job-definition state; the scheduler only accepts registrations.
- **AOT** — `<IsAotCompatible>true</IsAotCompatible>` +
  `<IsTrimmable>true</IsTrimmable>`; **zero reflection, zero expression trees,
  zero runtime code generation** — no IL2xxx exception list (unlike Quartz,
  which keeps a Job-by-string reflection surface). The host's own sink /
  persistence code carries its own AOT responsibility.
- **Timing wheel** — absolute UTC-minute slots (`SortedDictionary`), no ring
  overflow, no hierarchy: weekly/monthly/yearly patterns are plain keys.
  Registration and slot moves are O(log N); flush touches only due slots.

## Quick Start

```csharp
using PicoSchedule;

var scheduler = new Scheduler(new SchedulerOptions { TickInterval = TimeSpan.FromSeconds(10) });
scheduler.Start();

var jobId = Guid.CreateVersion7();
scheduler.Register(
    jobId,
    "0 9 * * MON",                    // 09:00 every Monday (full cron dialect)
    new MyTriggerSink(),               // your ITriggerSink implementation
    new Dictionary<string, string?> { ["prompt"] = "morning report" }
);

// ... at shutdown:
await scheduler.StopAsync();
```

## API Surface

| Member | Purpose |
|---|---|
| `Scheduler(SchedulerOptions)` / `Start()` / `StopAsync()` | One-shot lifecycle; host-driven |
| `Register(jobId, cron, sink, payload?, options?)` | Register or OVERWRITE (new lifecycle, counters cleared) |
| `Pause / Resume / Reschedule / Unregister(jobId)` | Management — unknown ids are silent |
| `Snapshot` | CQS read — never blocks, never throws |
| `ITriggerSink.FireAsync(jobId, dueUtc, payload, ct)` | Trigger port — one per job, multi-type coexistence |

## Semantics (summary)

- **Misfire**: `GraceMode.HalfPeriodClamped` (default — half period, 60s..2h
  clamp) / `Fixed` / `Disabled`; beyond grace fast-forwards, never replays.
- **Burst**: `BurstLimit` (default 50) per flush — crash-recovery storms are
  spread over ticks; the excess stays overdue and keeps competing.
- **Execution**: sinks run outside the loop; per-job serial + skip-if-busy;
  cross-job parallelism allowed (sink thread-safety is the host's job).
- **Failure**: fire once, log (`Action<ScheduleLogEntry>`), count; auto-pause
  at `MaxConsecutiveFailures` (stub keeps counters; Resume retries). NO retry
  inside the framework — the next slot is the natural retry.
- **Persistence/recovery**: host-owned. Recovery = re-register (overwrite
  semantics); missed slots are handled by the grace policy.

## Cron Dialect (language-completeness contract)

`*` `*/n` `a-b` `a,b,c` `?` seconds (6-field) dow 0-7 name aliases
(`MON`/`jan`) `N#K` (K-th weekday) `L`/`NL` (last) `Nw` (nearest weekday)
`@hourly/@daily/@weekly/@monthly/@yearly/@annually/@midnight`.
Excluded by design: `H/R` hash (distributed semantics) and a year field (YAGNI).

## Boundaries

No persistence · no retry · no audit events · no distributed mode ·
no Generic Host integration (host wires Start/Stop). GUID v7 is the
identifier convention (`.NET 9+ Guid.CreateVersion7()`); the framework
does not validate versions.