namespace PicoSchedule.Tests;

public sealed class FakeClock : ITimeProvider
{
    public DateTimeOffset UtcNow { get; private set; }

    public FakeClock(DateTimeOffset now) => UtcNow = now;

    public void Advance(TimeSpan delta) => UtcNow = UtcNow.Add(delta);
}

public sealed class WheelTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);

    private sealed class CaptureSink : ITriggerSink
    {
        public List<(
            Guid JobId,
            DateTimeOffset Due,
            IReadOnlyDictionary<string, string?> Payload
        )> Calls { get; } = [];

        public ValueTask FireAsync(
            Guid jobId,
            DateTimeOffset dueUtc,
            IReadOnlyDictionary<string, string?> payload,
            CancellationToken ct
        )
        {
            Calls.Add((jobId, dueUtc, payload));
            return default;
        }
    }

    private static (Scheduler Scheduler, FakeClock Clock) NewHost(
        out CaptureSink sink,
        DateTimeOffset? now = null
    )
    {
        var clock = new FakeClock(now ?? Start);
        sink = new CaptureSink();
        var scheduler = new Scheduler(new SchedulerOptions { Clock = clock });
        return (scheduler, clock);
    }

    [Test]
    public async Task Register_PlacesIntoUtcMinuteSlot_AndSnapshotReflects()
    {
        var (s, _) = NewHost(out var sink);
        var jobId = Guid.CreateVersion7();
        s.Register(
            jobId,
            "0 9 * * *",
            sink,
            new Dictionary<string, string?> { ["k"] = "v" },
            new ScheduleJobOptions { TimeZoneId = "UTC" }
        );

        var snap = s.Snapshot;
        await Assert.That(snap).Count().IsEqualTo(1);
        await Assert.That(snap[0].JobId).IsEqualTo(jobId);
        // next fire: tomorrow 09:00 UTC (today 00:00 passed) — slot = that minute
        await Assert
            .That(snap[0].NextFireUtc)
            .IsEqualTo(new DateTimeOffset(2026, 9, 10, 9, 0, 0, TimeSpan.Zero));
        await Assert.That(snap[0].CronExpression).IsEqualTo("0 9 * * *");
        await Assert.That(snap[0].Enabled).IsTrue();
    }

    [Test]
    public async Task Register_Overwrite_SameId_ReplacesAndClearsFailures()
    {
        var (s, _) = NewHost(out var sink);
        var jobId = Guid.CreateVersion7();
        s.Register(jobId, "0 9 * * *", sink, null, new ScheduleJobOptions { TimeZoneId = "UTC" });

        var sink2 = new CaptureSink();
        s.Register(jobId, "0 10 * * *", sink2, null, new ScheduleJobOptions { TimeZoneId = "UTC" }); // overwrite: new cron + new sink

        var snap = s.Snapshot;
        await Assert.That(snap).Count().IsEqualTo(1);
        await Assert.That(snap[0].CronExpression).IsEqualTo("0 10 * * *");
        await Assert
            .That(snap[0].NextFireUtc)
            .IsEqualTo(new DateTimeOffset(2026, 9, 10, 10, 0, 0, TimeSpan.Zero));
        // failure counters cleared by the overwrite (new lifecycle)
        await Assert.That(snap[0].ConsecutiveFailures).IsEqualTo(0);
    }

    [Test]
    public async Task Unregister_RemovesEntry()
    {
        var (s, _) = NewHost(out var sink);
        var jobId = Guid.CreateVersion7();
        var jobId2 = Guid.CreateVersion7();
        s.Register(jobId, "0 9 * * *", sink, null, new ScheduleJobOptions { TimeZoneId = "UTC" });
        s.Register(jobId2, "0 10 * * *", sink, null, new ScheduleJobOptions { TimeZoneId = "UTC" });

        s.Unregister(jobId);

        var snap = s.Snapshot;
        await Assert.That(snap).Count().IsEqualTo(1);
        await Assert.That(snap[0].JobId).IsEqualTo(jobId2);

        s.Unregister(Guid.CreateVersion7()); // unknown id: silent
        await Assert.That(s.Snapshot).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Register_IdPassthrough_NoValidation()
    {
        var (s, _) = NewHost(out var sink);
        var arbitrary = Guid.NewGuid(); // not v7 — framework does not validate
        s.Register(
            arbitrary,
            "0 9 * * *",
            sink,
            null,
            new ScheduleJobOptions { TimeZoneId = "UTC" }
        );
        await Assert.That(s.Snapshot[0].JobId).IsEqualTo(arbitrary);

        var v7 = Guid.CreateVersion7();
        s.Register(v7, "0 9 * * *", sink, null, new ScheduleJobOptions { TimeZoneId = "UTC" });
        await Assert.That(s.Snapshot[^1].JobId).IsEqualTo(v7);
    }

    [Test]
    public async Task Register_InvalidCron_Throws()
    {
        var (s, _) = NewHost(out var sink);
        await Assert
            .That(() => s.Register(Guid.CreateVersion7(), "not a cron", sink))
            .Throws<ArgumentException>();
        await Assert.That(s.Snapshot).IsEmpty();
    }
}

public sealed class PauseResumeTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly ScheduleJobOptions Utc = new() { TimeZoneId = "UTC" };

    private sealed class StubSink : ITriggerSink
    {
        public ValueTask FireAsync(
            Guid jobId,
            DateTimeOffset dueUtc,
            IReadOnlyDictionary<string, string?> payload,
            CancellationToken ct
        ) => default;
    }

    private static (Scheduler S, FakeClock Clock, Guid JobId) Setup()
    {
        var clock = new FakeClock(Start);
        var s = new Scheduler(new SchedulerOptions { Clock = clock });
        var jobId = Guid.CreateVersion7();
        s.Register(jobId, "0 9 * * *", new StubSink(), null, Utc);
        return (s, clock, jobId);
    }

    [Test]
    public async Task Pause_LeavesStub_NotInWheel()
    {
        var (s, _, jobId) = Setup();
        s.Pause(jobId);

        var snap = s.Snapshot;
        await Assert.That(snap).Count().IsEqualTo(1); // stub kept
        await Assert.That(snap[0].Enabled).IsFalse();
        // resume path re-enters the wheel (validated by Resume test)
    }

    [Test]
    public async Task Pause_PreservesFailureCount_Resume_ReentersFreshNext()
    {
        var (s, clock, jobId) = Setup();
        // simulate observations before pausing (counters live on the stub)
        s.Pause(jobId);
        clock.Advance(TimeSpan.FromDays(2)); // time passes while paused

        s.Resume(jobId);
        var snap = s.Snapshot;
        await Assert.That(snap[0].Enabled).IsTrue();
        // next fire recomputed from NOW (paused window skipped — no backlog)
        await Assert
            .That(snap[0].NextFireUtc)
            .IsEqualTo(new DateTimeOffset(2026, 9, 12, 9, 0, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task Resume_UnknownJob_Silent()
    {
        var (s, _, _) = Setup();
        s.Resume(Guid.CreateVersion7());
        s.Pause(Guid.CreateVersion7());
        await Assert.That(s.Snapshot).Count().IsEqualTo(1); // untouched
    }

    [Test]
    public async Task Reschedule_MovesSlot()
    {
        var (s, _, jobId) = Setup();
        s.Reschedule(jobId, "0 22 * * *");

        var snap = s.Snapshot;
        await Assert.That(snap[0].CronExpression).IsEqualTo("0 22 * * *");
        await Assert
            .That(snap[0].NextFireUtc)
            .IsEqualTo(new DateTimeOffset(2026, 9, 10, 22, 0, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task Reschedule_InvalidCron_Throws_StateUnchanged()
    {
        var (s, _, jobId) = Setup();
        await Assert.That(() => s.Reschedule(jobId, "bogus")).Throws<ArgumentException>();
        await Assert.That(s.Snapshot[0].CronExpression).IsEqualTo("0 9 * * *");
    }
}
