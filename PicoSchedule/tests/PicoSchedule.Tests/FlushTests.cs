namespace PicoSchedule.Tests;

public sealed class FlushTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly ScheduleJobOptions Utc = new() { TimeZoneId = "UTC" };

    private sealed class CaptureSink : ITriggerSink
    {
        // thread-safe: sink calls run concurrently (out-of-loop execution) —
        // a bare List.Add races and miscounts under burst/parallel flushes
        private readonly List<Guid> _fired = [];
        private readonly object _lock = new();

        public IReadOnlyList<Guid> Fired
        {
            get
            {
                lock (_lock)
                    return _fired.ToList();
            }
        }

        public int Calls
        {
            get
            {
                lock (_lock)
                    return _fired.Count;
            }
        }

        public IReadOnlyList<Guid> Seen
        {
            get
            {
                lock (_lock)
                    return _fired.ToList();
            }
        }

        public ValueTask FireAsync(
            Guid jobId,
            DateTimeOffset dueUtc,
            IReadOnlyDictionary<string, string?> payload,
            CancellationToken ct
        )
        {
            lock (_lock)
                _fired.Add(jobId);
            return default;
        }
    }

    private sealed class ThrowingSink : ITriggerSink
    {
        public ValueTask FireAsync(
            Guid jobId,
            DateTimeOffset dueUtc,
            IReadOnlyDictionary<string, string?> payload,
            CancellationToken ct
        ) => throw new InvalidOperationException("boom");
    }

    private static (Scheduler S, FakeClock Clock, CaptureSink Sink) NewHost(int burstLimit = 50)
    {
        var clock = new FakeClock(Start);
        var sink = new CaptureSink();
        var s = new Scheduler(new SchedulerOptions { Clock = clock, BurstLimit = burstLimit });
        return (s, clock, sink);
    }

    [Test]
    public async Task Flush_DueEntry_FiresSink_AdvancesToFutureSlot()
    {
        var (s, clock, sink) = NewHost();
        var id = Guid.CreateVersion7();
        s.Register(id, "0 9 * * *", sink, null, Utc);
        await Assert.That(sink.Calls).IsEqualTo(0); // not due yet

        clock.Advance(TimeSpan.FromHours(9)); // 09:00
        await s.FlushNowAsync();
        await Assert.That(sink.Calls).IsEqualTo(1);
        // advanced: next fire is tomorrow 09:00
        await Assert
            .That(s.Snapshot[0].NextFireUtc)
            .IsEqualTo(new DateTimeOffset(2026, 9, 11, 9, 0, 0, TimeSpan.Zero));
        await Assert
            .That(s.Snapshot[0].LastTriggeredUtc)
            .IsEqualTo(new DateTimeOffset(2026, 9, 10, 9, 0, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task Flush_SameMinuteRepeated_NoDoubleFire()
    {
        var (s, clock, sink) = NewHost();
        var id = Guid.CreateVersion7();
        s.Register(id, "0 9 * * *", sink, null, Utc);

        clock.Advance(TimeSpan.FromHours(9));
        await s.FlushNowAsync();
        await s.FlushNowAsync(); // same minute again — state-driven idempotency
        await Assert.That(sink.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task Flush_ExpiredWithinGrace_ReplaysOnce()
    {
        var (s, clock, sink) = NewHost();
        var id = Guid.CreateVersion7();
        s.Register(id, "*/5 * * * *", sink, null, Utc); // next at 00:05

        clock.Advance(TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(30))); // 00:05:30
        await s.FlushNowAsync();
        await Assert.That(sink.Calls).IsEqualTo(1); // 30s late — inside grace (≥60s clamp → 2.5min)
        await Assert
            .That(s.Snapshot[0].NextFireUtc)
            .IsEqualTo(new DateTimeOffset(2026, 9, 10, 0, 10, 0, TimeSpan.Zero));

        await s.FlushNowAsync(); // re-flush same instant: no repeat
        await Assert.That(sink.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task Flush_BeyondGrace_FastForwards_NoFire()
    {
        var (s, clock, sink) = NewHost();
        var id = Guid.CreateVersion7();
        s.Register(id, "0 9 * * *", sink, null, Utc); // next 09:00

        clock.Advance(TimeSpan.FromHours(13)); // 13:00 — 4h overdue, beyond 2h cap
        await s.FlushNowAsync();
        await Assert.That(sink.Calls).IsEqualTo(0); // no replay
        await Assert
            .That(s.Snapshot[0].NextFireUtc)
            .IsEqualTo(new DateTimeOffset(2026, 9, 11, 9, 0, 0, TimeSpan.Zero)); // fast-forwarded
    }

    [Test]
    public async Task Flush_DisabledGrace_AlwaysFastForward()
    {
        var (s, clock, sink) = NewHost();
        var id = Guid.CreateVersion7();
        s.Register(id, "0 9 * * *", sink, null, Utc with { Grace = GraceMode.Disabled });

        clock.Advance(TimeSpan.FromHours(9).Add(TimeSpan.FromMinutes(1))); // 5 minutes overdue
        await s.FlushNowAsync();
        await Assert.That(sink.Calls).IsEqualTo(0);
        await Assert
            .That(s.Snapshot[0].NextFireUtc)
            .IsEqualTo(new DateTimeOffset(2026, 9, 11, 9, 0, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task Flush_FixedGrace_UsesFixed()
    {
        var (s, clock, sink) = NewHost();
        var id = Guid.CreateVersion7();
        var opts = new ScheduleJobOptions
        {
            TimeZoneId = "UTC",
            Grace = GraceMode.Fixed,
            FixedGrace = TimeSpan.FromMinutes(10),
        };
        s.Register(id, "0 9 * * *", sink, null, opts); // next 09:00

        clock.Advance(TimeSpan.FromHours(9).Add(TimeSpan.FromMinutes(5))); // 5 min late < 10
        await s.FlushNowAsync();
        await Assert.That(sink.Calls).IsEqualTo(1);

        // second fire moved to tomorrow; flush again at 09:20 would be 20min overdue…
        // but NextFireUtc is now tomorrow 09:00 — nothing is due
        await Assert
            .That(s.Snapshot[0].NextFireUtc)
            .IsEqualTo(new DateTimeOffset(2026, 9, 11, 9, 0, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task Flush_BurstLimit_CapsPerTick()
    {
        var (s, clock, sink) = NewHost(burstLimit: 6);
        var ids = new List<Guid>();
        for (var i = 0; i < 12; i++)
        {
            var id = Guid.CreateVersion7();
            ids.Add(id);
            s.Register(id, "0 9 * * *", sink, null, Utc);
        }

        clock.Advance(TimeSpan.FromHours(9));
        await s.FlushNowAsync();
        await Assert.That(sink.Calls).IsEqualTo(6); // earliest 6 of 12

        await s.FlushNowAsync(); // remaining stay overdue; next tick takes the rest
        await Assert.That(sink.Calls).IsEqualTo(12);
    }

    [Test]
    public async Task Flush_StaleNow_MonotonicIgnored()
    {
        var (s, clock, sink) = NewHost();
        var id = Guid.CreateVersion7();
        s.Register(id, "0 9 * * *", sink, null, Utc);

        clock.Advance(TimeSpan.FromHours(9));
        await s.FlushNowAsync(); // 09:00: fires once
        await s.FlushNowAsync(new DateTimeOffset(2026, 9, 10, 8, 0, 0, TimeSpan.Zero)); // stale clock
        await Assert.That(sink.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task Flush_Paused_NotFired()
    {
        var (s, clock, sink) = NewHost();
        var id = Guid.CreateVersion7();
        s.Register(id, "0 9 * * *", sink, null, Utc);
        s.Pause(id);

        clock.Advance(TimeSpan.FromHours(9));
        await s.FlushNowAsync();
        await Assert.That(sink.Calls).IsEqualTo(0); // stub left the wheel
        await Assert.That(s.Snapshot[0].Enabled).IsFalse();
    }

    [Test]
    public async Task Flush_SinkFailure_Recorded_AndCounted()
    {
        var (s, clock, _) = NewHost();
        var id = Guid.CreateVersion7();
        s.Register(id, "0 9 * * *", new ThrowingSink(), null, Utc);

        clock.Advance(TimeSpan.FromHours(9));
        await s.FlushNowAsync();

        await Assert.That(s.Snapshot[0].ConsecutiveFailures).IsEqualTo(1);
        await Assert.That(s.Snapshot[0].LastError).IsEqualTo("boom");
    }
}
