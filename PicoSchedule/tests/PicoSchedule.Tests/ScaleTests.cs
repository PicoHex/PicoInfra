using System.Diagnostics;

namespace PicoSchedule.Tests;

/// <summary>Wall-clock budget tests — run in isolation so parallel test load
/// cannot distort the measurements (the budgets encode the algorithmic
/// contracts asserted deterministically in PerformanceContractTests).</summary>
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
    public async Task TenThousandJobs_RegisterAndFlush_WithinBudget()
    {
        var clock = new FakeClock(Start);
        var sink = new CountingSink();
        var s = new Scheduler(new SchedulerOptions { Clock = clock, BurstLimit = 20_000 });

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 10_000; i++)
            s.Register(Guid.CreateVersion7(), "0 9 * * *", sink, null, Utc);
        sw.Stop();
        await Assert.That(sw.ElapsedMilliseconds).IsLessThan(5000);
        await Assert.That(s.Snapshot).Count().IsEqualTo(10_000);

        clock.Advance(TimeSpan.FromHours(9));
        sw.Restart();
        await s.FlushNowAsync();
        sw.Stop();
        await Assert.That(sink.Fired).IsEqualTo(10_000);
        await Assert.That(sw.ElapsedMilliseconds).IsLessThan(5000);

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
