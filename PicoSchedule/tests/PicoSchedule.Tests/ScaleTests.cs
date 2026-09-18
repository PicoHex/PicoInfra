namespace PicoSchedule.Tests;

/// <summary>Scale contracts for 10k jobs, asserted deterministically — no
/// wall-clock budgets: a cross-process parallel test run cannot measure time
/// reliably. Registration must mark the snapshot dirty instead of rebuilding it
/// per mutation, and next-fire searches must stay field-scaled rather than
/// per-minute (both are counted by the scheduler's test hooks).</summary>
[NotInParallel]
public sealed class ScaleTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly ScheduleJobOptions Utc = new() { TimeZoneId = "UTC" };

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
            Interlocked.Increment(ref Fired);
            return default;
        }
    }

    [Test]
    public async Task TenThousandJobs_RegisterAndFlush_StaysAlgorithmicallyLinear()
    {
        var clock = new FakeClock(Start);
        var sink = new CountingSink();
        var s = new Scheduler(new SchedulerOptions { Clock = clock, BurstLimit = 20_000 });

        for (var i = 0; i < 10_000; i++)
            s.Register(Guid.CreateVersion7(), "0 9 * * *", sink, null, Utc);

        // Registration only marks the snapshot dirty: no O(N) rebuild per mutation.
        // The next-fire search is field-scaled — a per-minute probe would exceed
        // ~5M steps for these registrations alone.
        await Assert.That(s.SnapshotRebuilds).IsEqualTo(0);
        await Assert.That(s.CronSearchSteps).IsLessThan(1_000_000);

        await Assert.That(s.Snapshot).Count().IsEqualTo(10_000);
        await Assert.That(s.SnapshotRebuilds).IsEqualTo(1);
        _ = s.Snapshot; // cached — no second rebuild
        await Assert.That(s.SnapshotRebuilds).IsEqualTo(1);

        clock.Advance(TimeSpan.FromHours(9));
        await s.FlushNowAsync();
        await Assert.That(sink.Fired).IsEqualTo(10_000);

        // Every entry advanced one day at ~90 search steps; a per-minute probe
        // would cost ~14M steps here.
        await Assert.That(s.CronSearchSteps).IsLessThan(2_000_000);
        await Assert.That(s.GraceIntervalComputations).IsEqualTo(0); // exactly due — no estimate

        // all entries advanced into tomorrow — snapshot consistent
        await Assert
            .That(
                s.Snapshot.All(x =>
                    x.NextFireUtc.Year == 2026
                    && x.NextFireUtc.Month == 9
                    && x.NextFireUtc.Day == 11
                )
            )
            .IsTrue();
    }

    [Test]
    public async Task Snapshot_ReadWhileFlush_NoCorruption()
    {
        var clock = new FakeClock(Start);
        var sink = new CountingSink();
        var s = new Scheduler(new SchedulerOptions { Clock = clock, BurstLimit = 20_000 });
        for (var i = 0; i < 500; i++)
            s.Register(Guid.CreateVersion7(), "* * * * *", sink, null, Utc);

        var stop = false;
        var reader = Task.Run(() =>
        {
            var reads = 0;
            while (!Volatile.Read(ref stop))
            {
                var snap = s.Snapshot; // never blocks; must never throw
                reads += snap.Count;
            }
            return reads;
        });

        for (var i = 0; i < 20; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(65));
            await s.FlushNowAsync();
        }
        Volatile.Write(ref stop, true);
        await reader.WaitAsync(TimeSpan.FromSeconds(10));

        // every job fired at least once (advance every minute, 20 flush rounds)
        await Assert.That(sink.Fired).IsGreaterThanOrEqualTo(500);
    }
}
