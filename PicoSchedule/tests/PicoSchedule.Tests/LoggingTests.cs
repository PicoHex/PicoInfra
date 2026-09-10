namespace PicoSchedule.Tests;

public sealed class LoggingTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly ScheduleJobOptions Utc = new() { TimeZoneId = "UTC" };

    private sealed class ThrowingSink : ITriggerSink
    {
        public ValueTask FireAsync(
            Guid jobId,
            DateTimeOffset dueUtc,
            IReadOnlyDictionary<string, string?> payload,
            CancellationToken ct
        ) => throw new InvalidOperationException("boom");
    }

    [Test]
    public async Task SinkFailure_LogsError_WithJobId()
    {
        var clock = new FakeClock(Start);
        var entries = new List<ScheduleLogEntry>();
        var s = new Scheduler(new SchedulerOptions { Clock = clock, Log = entries.Add });
        var id = Guid.CreateVersion7();
        s.Register(id, "0 9 * * *", new ThrowingSink(), null, Utc);

        clock.Advance(TimeSpan.FromHours(9));
        await s.FlushNowAsync();

        await Assert.That(entries).Count().IsGreaterThanOrEqualTo(1);
        var err = entries.First(e => e.Level == "Error");
        await Assert.That(err.JobId).IsEqualTo(id);
        await Assert.That(err.Message).IsEqualTo("boom");
        await Assert.That(s.Snapshot[0].ConsecutiveFailures).IsEqualTo(1);
    }

    [Test]
    public async Task ConsecutiveFailures_ReachThreshold_AutoPauses()
    {
        var clock = new FakeClock(Start);
        var entries = new List<ScheduleLogEntry>();
        var s = new Scheduler(
            new SchedulerOptions
            {
                Clock = clock,
                Log = entries.Add,
                MaxConsecutiveFailures = 3,
            }
        );
        var id = Guid.CreateVersion7();
        s.Register(id, "*/1 * * * *", new ThrowingSink(), null, Utc);
        // every minute: next at 00:01, 00:02, … (only 00:00 gap at start)

        for (var i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(90)); // >1 min per step → each flush sees a due pulse
            await s.FlushNowAsync();
        }

        // failure 3 hit the threshold → auto-paused (stub kept, out of wheel)
        await Assert.That(s.Snapshot[0].Enabled).IsFalse();
        await Assert.That(s.Snapshot[0].ConsecutiveFailures).IsEqualTo(3);
        await Assert
            .That(
                entries.Any(e =>
                    e.Level == "Warn"
                    && e.JobId == id
                    && e.Message.Contains("paus", StringComparison.OrdinalIgnoreCase)
                )
            )
            .IsTrue();

        // paused → further flushes never fire
        var before = s.Snapshot[0].ConsecutiveFailures;
        clock.Advance(TimeSpan.FromHours(1));
        await s.FlushNowAsync();
        await Assert.That(s.Snapshot[0].ConsecutiveFailures).IsEqualTo(before);

        // resume re-enters the wheel with counters intact (clear on next fail cycle)
        s.Resume(id);
        await Assert.That(s.Snapshot[0].Enabled).IsTrue();
    }
}
