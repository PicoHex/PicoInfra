namespace PicoSchedule.Tests;

public sealed class ExecutionTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly ScheduleJobOptions Utc = new() { TimeZoneId = "UTC" };

    private sealed class GatingSink : ITriggerSink
    {
        public readonly List<Guid> Fired = [];
        public readonly TaskCompletionSource Started = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        public int StartedCount;

        private readonly TaskCompletionSource _gate = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public TaskCompletionSource Gate => _gate;
        public int Calls => Fired.Count;

        public ValueTask FireAsync(
            Guid jobId,
            DateTimeOffset dueUtc,
            IReadOnlyDictionary<string, string?> payload,
            CancellationToken ct
        )
        {
            Fired.Add(jobId);
            Interlocked.Increment(ref StartedCount);
            Started.TrySetResult();
            return new ValueTask(_gate.Task); // hangs until the test releases it
        }

        public void ReleaseAll() => _gate.TrySetResult();
    }

    [Test]
    public async Task SlowSink_SkipIfBusy_NextPulseSkippedWhileRunning()
    {
        var clock = new FakeClock(Start);
        var gated = new GatingSink();
        var s = new Scheduler(new SchedulerOptions { Clock = clock });
        var id = Guid.CreateVersion7();
        s.Register(id, "* * * * *", gated, null, Utc); // next at 00:01

        clock.Advance(TimeSpan.FromSeconds(90)); // 00:01:30 — pulse 1 due
        await s.FlushCoreOnlyAsync(clock.UtcNow);
        await gated.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); // fire began
        await Assert.That(gated.Calls).IsEqualTo(1); // fired, still in-flight

        clock.Advance(TimeSpan.FromSeconds(60)); // 00:02:30 — pulse 2 due, pulse 1 still busy
        await s.FlushCoreOnlyAsync(clock.UtcNow);
        await Task.Delay(200); // let the (skipped) decision settle
        await Assert.That(gated.Calls).IsEqualTo(1); // skipped — no concurrent fire

        gated.ReleaseAll(); // release pulse 1
        // FlushNowAsync drains in-flight callbacks — deterministically wait for
        // pulse 1 to finish instead of sleeping (a fixed delay flakes under
        // full-suite parallel load). Nothing is due at 00:02:30 after the skip.
        await s.FlushNowAsync();
        clock.Advance(TimeSpan.FromSeconds(60)); // 00:03:30 — pulse 3 due, nothing in flight
        await s.FlushNowAsync();
        await Assert.That(gated.Calls).IsEqualTo(2); // fires again after the release
    }

    private sealed class OverlapDetectingSink : ITriggerSink
    {
        private int _running;
        private int _calls;
        private int _maxConcurrent;

        public int Calls => Volatile.Read(ref _calls);
        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

        public async ValueTask FireAsync(
            Guid jobId,
            DateTimeOffset dueUtc,
            IReadOnlyDictionary<string, string?> payload,
            CancellationToken ct
        )
        {
            Interlocked.Increment(ref _calls);
            var running = Interlocked.Increment(ref _running);
            int prev;
            while (running > (prev = Volatile.Read(ref _maxConcurrent)))
                Interlocked.CompareExchange(ref _maxConcurrent, running, prev);

            // Force a thread-pool hop so a slow continuation has a window to race
            // with the next pulse's in-flight tracking.
            await Task.Yield();
            Thread.SpinWait(500);

            Interlocked.Decrement(ref _running);
        }
    }

    [Test]
    public async Task SkipIfBusy_NeverRunsSameJobConcurrently_UnderRapidPulses()
    {
        // Regression: the in-flight removal continuation used to remove by job id
        // without checking task identity. A late continuation from a finished pulse
        // could untrack a NEWER running pulse, defeating skip-if-busy and letting the
        // same job run concurrently (and FlushNowAsync could miss draining it).
        var clock = new FakeClock(Start);
        var sink = new OverlapDetectingSink();
        var s = new Scheduler(new SchedulerOptions { Clock = clock, BurstLimit = 1000 });
        var id = Guid.CreateVersion7();
        s.Register(id, "* * * * *", sink, null, Utc);

        for (var i = 0; i < 2000; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(60)); // one pulse per iteration
            await s.FlushCoreOnlyAsync(clock.UtcNow); // fire-and-forget (no drain)
            if (i % 50 == 0)
                await s.FlushNowAsync(); // occasional drain
        }
        await s.FlushNowAsync();

        // Contract: per-job execution is serial — skip-if-busy never overlaps the
        // same job with itself.
        await Assert.That(sink.MaxConcurrent).IsEqualTo(1);
    }

    [Test]
    public async Task ConcurrentSinks_DifferentJobs_MayOverlap()
    {
        var clock = new FakeClock(Start);
        var gata = new GatingSink();
        var gatb = new GatingSink();
        var s = new Scheduler(new SchedulerOptions { Clock = clock });
        s.Register(Guid.CreateVersion7(), "* * * * *", gata, null, Utc);
        s.Register(Guid.CreateVersion7(), "* * * * *", gatb, null, Utc);

        clock.Advance(TimeSpan.FromSeconds(90));
        await s.FlushCoreOnlyAsync(clock.UtcNow);
        await gata.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await gatb.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(gata.Calls).IsEqualTo(1);
        await Assert.That(gatb.Calls).IsEqualTo(1); // both fired — cross-job parallelism allowed

        gata.ReleaseAll();
        gatb.ReleaseAll();
    }

    [Test]
    public async Task StopAsync_WaitsForInFlight()
    {
        var clock = new FakeClock(Start);
        var gated = new GatingSink();
        var s = new Scheduler(new SchedulerOptions { Clock = clock });
        var id = Guid.CreateVersion7();
        s.Register(id, "* * * * *", gated, null, Utc);

        clock.Advance(TimeSpan.FromSeconds(90));
        await s.FlushCoreOnlyAsync(clock.UtcNow);
        await gated.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); // pulse in flight

        var stopTask = s.StopAsync().AsTask();
        await Task.Delay(200);
        await Assert.That(stopTask.IsCompleted).IsFalse(); // still waiting on the in-flight sink

        gated.ReleaseAll();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(5)); // completes once released
    }

    [Test]
    public async Task SinkReceives_DueSlotSnapshot_NotAdvancedValue()
    {
        var clock = new FakeClock(Start);
        var captured = new List<DateTimeOffset>();
        var s = new Scheduler(new SchedulerOptions { Clock = clock, Log = e => { } });
        var sink = new RecordingSink(captured);
        var id = Guid.CreateVersion7();
        s.Register(id, "0 9 * * *", sink, null, Utc); // next 09:00 today

        clock.Advance(TimeSpan.FromHours(9));
        await s.FlushNowAsync();

        await Assert.That(captured).Count().IsEqualTo(1);
        // the sink receives the slot that actually fired (09:00), not the new
        // NextFireUtc (tomorrow 09:00) that Advance already wrote
        await Assert
            .That(captured[0])
            .IsEqualTo(new DateTimeOffset(2026, 9, 10, 9, 0, 0, TimeSpan.Zero));
        await Assert
            .That(s.Snapshot[0].NextFireUtc)
            .IsEqualTo(new DateTimeOffset(2026, 9, 11, 9, 0, 0, TimeSpan.Zero));
    }

    private sealed class RecordingSink(List<DateTimeOffset> captured) : ITriggerSink
    {
        public ValueTask FireAsync(
            Guid jobId,
            DateTimeOffset dueUtc,
            IReadOnlyDictionary<string, string?> payload,
            CancellationToken ct
        )
        {
            captured.Add(dueUtc);
            return default;
        }
    }
}
