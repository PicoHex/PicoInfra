# PicoLog.Benchmarks

Run the benchmark project in `Release` mode.

## Run commands

- Run both suites:
  - `dotnet run -c Release --project benchmarks/PicoLog.Benchmarks -f net10.0`
- Run only the main suite:
  - `dotnet run -c Release --project benchmarks/PicoLog.Benchmarks -f net10.0 -- main`
- Run only the wait suite:
  - `dotnet run -c Release --project benchmarks/PicoLog.Benchmarks -f net10.0 -- wait`

## Suites

- `main` runs `LoggingBenchmarks`
- `wait` runs `WaitLoggingBenchmarks`

The main suite keeps two MEL baselines on purpose:

- `MicrosoftAsyncHandoff` is the lightweight string-channel handoff baseline
- `MicrosoftAsyncEntryHandoff` is the fairer full-entry handoff baseline that mirrors PicoLog's timestamp/category/message envelope cost without adding real I/O

The wait suite is intentionally small and local-friendly:

- `PicoWaitControl_CachedMessage` is the lightweight wait-mode control case
- `PicoWaitHandoff_CachedMessage` adds forced backpressure through `BackpressureSink`

## Results

The benchmark app writes `benchmark-results.md` next to the executable in the output directory.
It also writes per-suite files when you run suites independently:

- `benchmark-results-main.md`
- `benchmark-results-wait.md`

Interpret wait-suite numbers as relative backpressure comparisons, not absolute throughput claims.
