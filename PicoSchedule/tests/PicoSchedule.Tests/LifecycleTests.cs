namespace PicoSchedule.Tests;

public sealed class LifecycleTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly ScheduleJobOptions Utc = new() { TimeZoneId = "UTC" };

    [Test]
    public async Task Start_AfterStop_Throws_OneShotLifecycle()
    {
        var clock = new FakeClock(Start);
        var s = new Scheduler(
            new SchedulerOptions { Clock = clock, TickInterval = TimeSpan.FromHours(1) }
        );

        s.Start();
        await s.StopAsync();

        await Assert.That(() => s.Start()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Stop_BeforeStart_AlsoEndsTheLifecycle()
    {
        var clock = new FakeClock(Start);
        var s = new Scheduler(new SchedulerOptions { Clock = clock });

        await s.StopAsync(); // host may tear down before Start

        await Assert.That(() => s.Start()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task FailedOverwrite_LeavesTheOriginalJobOnTheWheel_AndFiring()
    {
        // Finding 3: the overwrite path removed the old entry from the wheel and
        // only THEN computed the new next-fire (inside the gate). A throw from
        // that computation left _byJobId pointing at an entry that was no longer
        // scheduled: Snapshot still said "enabled, next 09:00", the job never
        // fired again. Compute before mutating.
        var clock = new FakeClock(Start);
        var scheduler = new Scheduler(new SchedulerOptions { Clock = clock });
        var sink = new CountingSink();
        var id = Guid.CreateVersion7();
        scheduler.Register(id, "0 9 * * *", sink, null, Utc);

        clock.Advance(TimeSpan.FromHours(2)); // 02:00 — nothing is due yet
        scheduler.BombNextFireForJobId = id; // the new next-fire computation throws

        await Assert
            .That(() => scheduler.Register(id, "0 10 * * *", sink, null, Utc))
            .Throws<InvalidOperationException>();

        // the snapshot looked healthy even before the fix — the real question is
        // whether the original entry is still ON THE WHEEL
        await Assert.That(scheduler.Snapshot).Count().IsEqualTo(1);
        await Assert.That(scheduler.Snapshot[0].CronExpression).IsEqualTo("0 9 * * *");
        await Assert.That(scheduler.Snapshot[0].NextFireUtc).IsEqualTo(Start.AddHours(9));

        clock.Advance(TimeSpan.FromHours(7)); // 09:00 — the original slot
        await scheduler.FlushNowAsync();
        await Assert.That(sink.Fired).IsEqualTo(1);
    }

    [Test]
    public async Task FailedReschedule_LeavesTheOriginalJobOnTheWheel_AndFiring()
    {
        // Same invariant as Register (finding 3): Reschedule removed the entry
        // from the wheel BEFORE computing the new next-fire, so a throw left the
        // job reported as enabled and never able to fire again.
        var clock = new FakeClock(Start);
        var scheduler = new Scheduler(new SchedulerOptions { Clock = clock });
        var sink = new CountingSink();
        var id = Guid.CreateVersion7();
        scheduler.Register(id, "0 9 * * *", sink, null, Utc);

        clock.Advance(TimeSpan.FromHours(2));
        scheduler.BombNextFireForJobId = id;

        await Assert
            .That(() => scheduler.Reschedule(id, "0 10 * * *"))
            .Throws<InvalidOperationException>();

        await Assert.That(scheduler.Snapshot[0].CronExpression).IsEqualTo("0 9 * * *");
        await Assert.That(scheduler.Snapshot[0].NextFireUtc).IsEqualTo(Start.AddHours(9));

        clock.Advance(TimeSpan.FromHours(7));
        await scheduler.FlushNowAsync();
        await Assert.That(sink.Fired).IsEqualTo(1);
    }

    [Test]
    public async Task FailedResume_LeavesTheStubPaused_AndCanBeResumedAgain()
    {
        // Resume flipped Enabled BEFORE computing the next-fire: a throw left the
        // stub "enabled" in Snapshot with no wheel slot — a job that looks live
        // and can never fire (finding 3's invariant applied to Resume).
        var clock = new FakeClock(Start);
        var scheduler = new Scheduler(new SchedulerOptions { Clock = clock });
        var sink = new CountingSink();
        var id = Guid.CreateVersion7();
        scheduler.Register(id, "0 9 * * *", sink, null, Utc);
        scheduler.Pause(id);

        clock.Advance(TimeSpan.FromHours(2));
        scheduler.BombNextFireForJobId = id;

        await Assert.That(() => scheduler.Resume(id)).Throws<InvalidOperationException>();

        // still paused — not "enabled, next 09:00, never scheduled"
        await Assert.That(scheduler.Snapshot[0].Enabled).IsFalse();

        scheduler.Resume(id); // the healthy retry still works
        await Assert.That(scheduler.Snapshot[0].Enabled).IsTrue();

        clock.Advance(TimeSpan.FromHours(7)); // 09:00
        await scheduler.FlushNowAsync();
        await Assert.That(sink.Fired).IsEqualTo(1);
    }

    private sealed class CountingSink : ITriggerSink
    {
        public int Fired;

        public ValueTask FireAsync(
            Guid jobId,
            DateTimeOffset dueUtc,
            IReadOnlyDictionary<string, string?> payload,
            CancellationToken ct
        )
        {
            Fired++;
            return default;
        }
    }
}
