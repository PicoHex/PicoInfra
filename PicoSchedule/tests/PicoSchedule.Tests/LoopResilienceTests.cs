namespace PicoSchedule.Tests;

public sealed class LoopResilienceTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly ScheduleJobOptions Utc = new() { TimeZoneId = "UTC" };

    [Test]
    public async Task FlushBomb_LogsCritical_LoopKeepsRunning()
    {
        // A transient failure inside the tick loop must NOT kill the loop:
        // the framework's only scheduler thread dies silently otherwise
        // (review finding R4-1) — log Critical and continue with the next tick.
        var clock = new FakeClock(Start);
        var entries = new List<ScheduleLogEntry>();
        var scheduler = new Scheduler(
            new SchedulerOptions
            {
                Clock = clock,
                Log = entries.Add,
                TickInterval = TimeSpan.FromMilliseconds(50),
            }
        );
        var id = Guid.CreateVersion7();
        scheduler.Register(id, "* * * * *", new OkSink(), null, Utc);
        scheduler.BombFlushNextTick = true; // internal test bomb arm
        scheduler.Start();

        // the bombed tick logged Critical and consumed the bomb
        await WaitUntilAsync(() => entries.Any(e => e.Level == "Critical"));
        await Assert.That(scheduler.BombFlushNextTick).IsFalse();

        // the loop SURVIVED: a due pulse still fires afterwards
        clock.Advance(TimeSpan.FromMinutes(2));
        await WaitUntilAsync(() => scheduler.Snapshot[0].LastTriggeredUtc != default);
        await scheduler.StopAsync();
    }

    [Test]
    public async Task ManualFlush_SinkBomb_Recorded_NotLoopFatal()
    {
        // sink bombs are already caught per-job (existing semantics); keep the
        // loop alive contract explicit by firing through the same path
        var clock = new FakeClock(Start);
        var entries = new List<ScheduleLogEntry>();
        var scheduler = new Scheduler(new SchedulerOptions { Clock = clock, Log = entries.Add });
        var id = Guid.CreateVersion7();
        scheduler.Register(id, "0 9 * * *", new BombSink(), null, Utc);
        clock.Advance(TimeSpan.FromHours(9));
        await scheduler.FlushNowAsync();
        await Assert.That(scheduler.Snapshot[0].ConsecutiveFailures).IsEqualTo(1);
    }

    private sealed class OkSink : ITriggerSink
    {
        public ValueTask FireAsync(
            Guid jobId,
            DateTimeOffset dueUtc,
            IReadOnlyDictionary<string, string?> payload,
            CancellationToken ct
        ) => default;
    }

    private sealed class BombSink : ITriggerSink
    {
        public ValueTask FireAsync(
            Guid jobId,
            DateTimeOffset dueUtc,
            IReadOnlyDictionary<string, string?> payload,
            CancellationToken ct
        ) => throw new InvalidOperationException("bomb");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("condition not met within budget");
            await Task.Delay(20);
        }
    }
}
