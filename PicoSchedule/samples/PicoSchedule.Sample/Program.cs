// PicoSchedule runnable sample — embedded, AOT-first scheduling framework.
// Zero dependencies, zero reflection: everything below uses the public
// surface only (Scheduler + CronPattern + ports and record shapes).

Console.WriteLine("=== PicoSchedule Demo ===\n");
var passed = true;

// ------------------------------------------------------------------
// 1. Live trigger — register, fire, observe (real wall clock)
// ------------------------------------------------------------------
Console.WriteLine("1. Live trigger: cron \"*/2 * * * * *\" (every 2s)");

var scheduler = new Scheduler(
    new SchedulerOptions
    {
        TickInterval = TimeSpan.FromMilliseconds(500),
        MaxConsecutiveFailures = 2,
        Log = entry => Console.WriteLine($"   [log:{entry.Level}] {entry.Message}"),
    }
);
scheduler.Start();

var reportSink = new ConsoleSink("report");
var reportJobId = scheduler.Register(
    Guid.CreateVersion7(),
    "*/2 * * * * *",
    reportSink,
    new Dictionary<string, string?> { ["prompt"] = "morning report", ["retries"] = "0" }
);
passed &= await WaitUntilAsync(() => reportSink.Fired >= 2, TimeSpan.FromSeconds(8));
Console.WriteLine($"   job={Short(reportJobId)} fired={reportSink.Fired}x\n");

// ------------------------------------------------------------------
// 2. Cron dialect — parse + plan next fires without waiting
// ------------------------------------------------------------------
Console.WriteLine("2. Cron dialect (NextFire in UTC)");

var cronExamples = new[]
{
    "0 9 * * MON", // 09:00 every Monday (5-field form)
    "@daily", // shorthand expansions
    "0 0 0 L * *", // last day of month
    "0 0 9 15W * *", // nearest weekday to the 15th
    "0 0 12 ? * FRI#3", // third Friday (alias + `#` compose)
};
foreach (var expr in cronExamples)
{
    var next = CronPattern.Parse(expr).NextFire(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
    Console.WriteLine($"   {expr, -18} -> {next:u}");
}
Console.WriteLine();

// ------------------------------------------------------------------
// 3. Failure — fire once, count, auto-pause (no retry inside)
// ------------------------------------------------------------------
Console.WriteLine("3. Failure handling: MaxConsecutiveFailures = 2");

var failingJobId = scheduler.Register(Guid.CreateVersion7(), "* * * * * *", new FailingSink());
passed &= await WaitUntilAsync(
    () => FindJob(scheduler, failingJobId) is { Enabled: false },
    TimeSpan.FromSeconds(8)
);
var failing = FindJob(scheduler, failingJobId);
Console.WriteLine(
    $"   enabled={failing?.Enabled} consecutiveFailures={failing?.ConsecutiveFailures} lastError={failing?.LastError}"
);
Console.WriteLine("   Resume() retries — the next natural slot is the retry\n");

// ------------------------------------------------------------------
// 4. Management — Pause / Resume / Reschedule / Unregister + Snapshot
// ------------------------------------------------------------------
Console.WriteLine("4. Management ops");

var dailyJobId = scheduler.Register(
    Guid.CreateVersion7(),
    "0 0 12 * * *",
    new ConsoleSink("daily"),
    options: new ScheduleJobOptions(TimeZoneId: "UTC")
);
Console.WriteLine($"   registered:  next={FindJob(scheduler, dailyJobId)!.NextFireUtc:u}");

scheduler.Pause(dailyJobId);
Console.WriteLine($"   paused:      enabled={FindJob(scheduler, dailyJobId)!.Enabled}");

scheduler.Resume(dailyJobId);
Console.WriteLine($"   resumed:     next={FindJob(scheduler, dailyJobId)!.NextFireUtc:u}");

scheduler.Reschedule(dailyJobId, "0 30 12 * * *");
Console.WriteLine(
    $"   rescheduled: next={FindJob(scheduler, dailyJobId)!.NextFireUtc:u} cron={FindJob(scheduler, dailyJobId)!.CronExpression}"
);

scheduler.Unregister(dailyJobId);
Console.WriteLine($"   unregistered: present={FindJob(scheduler, dailyJobId) is not null}");

scheduler.Pause(Guid.CreateVersion7());
scheduler.Resume(Guid.CreateVersion7());
scheduler.Reschedule(Guid.CreateVersion7(), "0 0 12 * * *");
scheduler.Unregister(Guid.CreateVersion7());
Console.WriteLine(
    "   unknown jobId: Pause/Resume/Reschedule/Unregister are all silent (no exception)"
);
Console.WriteLine($"   snapshot: {scheduler.Snapshot.Count} job(s)\n");

// ------------------------------------------------------------------
// 5. Deterministic clock — GraceMode + BurstLimit (injected ITimeProvider)
// ------------------------------------------------------------------
Console.WriteLine("5. Deterministic clock (FakeClock via SchedulerOptions.Clock)");

var fakeClock = new FakeClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
var fakeScheduler = new Scheduler(
    new SchedulerOptions
    {
        TickInterval = TimeSpan.FromMilliseconds(100),
        BurstLimit = 3,
        Log = entry => Console.WriteLine($"   [log:{entry.Level}] {entry.Message}"),
        Clock = fakeClock,
    }
);
fakeScheduler.Start();

// 5a: overdue INSIDE the grace window -> replay once, dueUtc keeps the missed slot.
var replaySink = new ConsoleSink("replay");
var replayJobId = fakeScheduler.Register(
    Guid.CreateVersion7(),
    "* * * * * *",
    replaySink,
    options: new ScheduleJobOptions(TimeZoneId: "UTC")
);
fakeClock.Advance(TimeSpan.FromSeconds(30));
passed &= await WaitUntilAsync(() => replaySink.Fired >= 1, TimeSpan.FromSeconds(5));
Console.WriteLine(
    $"   5a grace replay:    fired={replaySink.Fired} next={FindJob(fakeScheduler, replayJobId)!.NextFireUtc:u}"
);
fakeScheduler.Unregister(replayJobId);

// 5b: overdue BEYOND the grace window -> fast-forward, never replay.
var fastForwardSink = new ConsoleSink("fast-forward");
var fastForwardJobId = fakeScheduler.Register(
    Guid.CreateVersion7(),
    "* * * * * *",
    fastForwardSink,
    options: new ScheduleJobOptions(TimeZoneId: "UTC")
);
fakeClock.Advance(TimeSpan.FromSeconds(90));
await Task.Delay(500);
passed &= fastForwardSink.Fired == 0;
Console.WriteLine(
    $"   5b beyond grace:    fired={fastForwardSink.Fired} next={FindJob(fakeScheduler, fastForwardJobId)!.NextFireUtc:u} (> now {fakeClock.UtcNow:u})"
);
fakeScheduler.Unregister(fastForwardJobId);

// 5c: burst — 8 jobs due in the same slot, BurstLimit drains 3 per flush.
var burstSink = new ConsoleSink("burst");
for (var i = 0; i < 8; i++)
    fakeScheduler.Register(
        Guid.CreateVersion7(),
        "* * * * * *",
        burstSink,
        options: new ScheduleJobOptions(TimeZoneId: "UTC")
    );
fakeClock.Advance(TimeSpan.FromSeconds(30));
var burstFires = new List<int> { 0 };
passed &= await WaitUntilAsync(
    () =>
    {
        if (burstFires[^1] != burstSink.Fired)
            burstFires.Add(burstSink.Fired);
        return burstSink.Fired >= 8;
    },
    TimeSpan.FromSeconds(5)
);
Console.WriteLine($"   5c burst (limit 3): fires=[{string.Join(", ", burstFires)}]");
Console.WriteLine();

await fakeScheduler.StopAsync();
await scheduler.StopAsync();

Console.WriteLine(passed ? "=== Demo Complete ===" : "=== Demo INCOMPLETE (assertion failed) ===");

static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (!condition() && DateTime.UtcNow < deadline)
        await Task.Delay(50);
    return condition();
}

static JobSnapshot? FindJob(Scheduler scheduler, Guid jobId) =>
    scheduler.Snapshot.FirstOrDefault(job => job.JobId == jobId);

return passed ? 0 : 1;

static string Short(Guid jobId) => jobId.ToString("N")[^6..];

// ------------------------------------------------------------------
// Trigger sink port implementations (one per use case)
// ------------------------------------------------------------------
internal sealed class ConsoleSink(string name) : ITriggerSink
{
    private int _fired;

    public int Fired => Volatile.Read(ref _fired);

    public ValueTask FireAsync(
        Guid jobId,
        DateTimeOffset dueUtc,
        IReadOnlyDictionary<string, string?> payload,
        CancellationToken ct
    )
    {
        Interlocked.Increment(ref _fired);
        var data =
            payload.Count == 0
                ? "-"
                : string.Join(", ", payload.Select(kv => $"{kv.Key}={kv.Value}"));
        Console.WriteLine(
            $"   [fire] {name} job={jobId.ToString("N")[^6..]} due={dueUtc:u} payload=[{data}]"
        );
        return ValueTask.CompletedTask;
    }
}

internal sealed class FailingSink : ITriggerSink
{
    public ValueTask FireAsync(
        Guid jobId,
        DateTimeOffset dueUtc,
        IReadOnlyDictionary<string, string?> payload,
        CancellationToken ct
    ) => throw new InvalidOperationException("downstream API unavailable (demo failure)");
}

// Deterministic clock — the framework takes ITimeProvider, so demos and tests
// run without waiting for wall-clock time to pass.
internal sealed class FakeClock(DateTimeOffset start) : ITimeProvider
{
    private long _unixMs = start.ToUnixTimeMilliseconds();

    public DateTimeOffset UtcNow =>
        DateTimeOffset.FromUnixTimeMilliseconds(Volatile.Read(ref _unixMs));

    public void Advance(TimeSpan delta) =>
        Interlocked.Add(ref _unixMs, (long)delta.TotalMilliseconds);
}
