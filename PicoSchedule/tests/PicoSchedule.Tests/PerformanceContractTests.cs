namespace PicoSchedule.Tests;

/// <summary>
/// Deterministic performance contracts (no wall-clock budgets — they encode the
/// algorithmic invariants the ScaleTests budgets depend on):
///  - the grace interval is estimated once per entry per fire cycle, never per tick;
///  - the flush due-slot scan stops at the first future slot.
/// </summary>
public sealed class PerformanceContractTests
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
    public async Task Flush_ZeroOverdue_SkipsIntervalEstimateEntirely()
    {
        // grace for HalfPeriodClamped is clamped to [60s, 2h]; an entry that is not
        // overdue at all is inside the floor — no O(minutes) estimate is needed.
        var clock = new FakeClock(Start);
        var sink = new CountingSink();
        var s = new Scheduler(new SchedulerOptions { Clock = clock, BurstLimit = 1000 });
        for (var i = 0; i < 10; i++)
            s.Register(Guid.CreateVersion7(), "0 9 * * *", sink, null, Utc);

        clock.Advance(TimeSpan.FromHours(9)); // exactly due — overdue 0
        await s.FlushNowAsync();

        await Assert.That(s.GraceIntervalComputations).IsEqualTo(0);
        await Assert.That(sink.Fired).IsEqualTo(10);
    }

    [Test]
    public async Task Flush_RepeatedTicks_ComputesIntervalOncePerEntry()
    {
        var clock = new FakeClock(Start);
        var sink = new CountingSink();
        var s = new Scheduler(new SchedulerOptions { Clock = clock, BurstLimit = 1 });
        for (var i = 0; i < 10; i++)
            s.Register(Guid.CreateVersion7(), "0 9 * * *", sink, null, Utc);

        // 30 min overdue — inside (floor, ceiling], so the exact estimate IS needed
        // (daily grace is 2h > 30 min → all 10 stay due; burst admits 1 per tick).
        clock.Advance(TimeSpan.FromHours(9).Add(TimeSpan.FromMinutes(30)));
        await s.FlushNowAsync();
        var afterFirstFlush = s.GraceIntervalComputations;
        await s.FlushNowAsync(); // the other 9 stay overdue
        await s.FlushNowAsync();

        await Assert.That(afterFirstFlush).IsEqualTo(10); // one per entry, not per tick
        await Assert.That(s.GraceIntervalComputations).IsEqualTo(afterFirstFlush);
        await Assert.That(sink.Fired).IsEqualTo(3); // burst 1 per tick, 3 ticks
    }

    [Test]
    public async Task Flush_OnlyVisitsDueSlots_StopsAtFirstFutureSlot()
    {
        var clock = new FakeClock(Start);
        var sink = new CountingSink();
        var s = new Scheduler(new SchedulerOptions { Clock = clock, BurstLimit = 1000 });
        s.Register(Guid.CreateVersion7(), "0 9 * * *", sink, null, Utc); // due
        s.Register(Guid.CreateVersion7(), "1 9 * * *", sink, null, Utc); // due
        for (var h = 10; h <= 23; h++) // 14 future slots
            s.Register(Guid.CreateVersion7(), $"0 {h} * * *", sink, null, Utc);

        clock.Advance(TimeSpan.FromHours(9).Add(TimeSpan.FromMinutes(1))); // 09:01
        await s.FlushNowAsync();

        await Assert.That(s.WheelSlotsExamined).IsEqualTo(3); // 2 due + the future stop slot
        await Assert.That(sink.Fired).IsEqualTo(2);
    }
}
