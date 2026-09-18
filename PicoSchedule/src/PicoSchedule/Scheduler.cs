namespace PicoSchedule;

/// <summary>
/// Embedded in-memory scheduler — the timing-wheel core (framework spec §5).
/// THE WHEEL IS THE FACT STORE: slot-entries hold the full registration
/// (job/sink/payload/pattern); a reverse index keeps O(1) management ops.
/// Persistence, audit and recovery belong to the host; the scheduler only
/// accepts registrations. Lifecycle is ONE-SHOT: Start once, Stop once;
/// a host restart creates a new instance.
/// </summary>
public sealed class Scheduler
{
    private readonly SchedulerOptions _options;
    private readonly ITimeProvider _clock;
    private readonly Action<ScheduleLogEntry>? _log;

    // Timing wheel: UTC minute slot → entries that fire in that minute.
    // Absolute keys are unbounded — weekly/monthly patterns are plain keys
    // (no ring overflow, no hierarchy needed).
    private readonly SortedDictionary<long, List<JobEntry>> _wheel = new();

    // Reverse index: jobId → (slot key (null = parked stub), entry object).
    private readonly Dictionary<Guid, (long? Slot, JobEntry Entry)> _byJobId = new();
    private readonly Dictionary<Guid, string> _expressionText = new();
    private readonly Dictionary<Guid, GraceMode> _graceModes = new();
    private readonly Dictionary<Guid, TimeSpan> _fixedGraces = new();

    private readonly object _gate = new();
    private IReadOnlyList<JobSnapshot> _snapshot = Array.Empty<JobSnapshot>();
    private DateTimeOffset _lastFlushedUtc = DateTimeOffset.MinValue;
    private volatile bool _running;
    private bool _stopped;
    private CancellationTokenSource? _loopCts;

    public Scheduler(SchedulerOptions options)
    {
        _options = options;
        _clock = options.Clock ?? new SystemClock();
        _log = options.Log;
    }

    /// <summary>CQS read snapshot — never blocks long, never throws. Built
    /// lazily: management ops mark the cache dirty instead of rebuilding O(N)
    /// on every mutation (10k-batch registration stays O(N²)-free).</summary>
    public IReadOnlyList<JobSnapshot> Snapshot
    {
        get
        {
            lock (_gate)
            {
                if (_snapshotCache is null || _dirty)
                {
                    SnapshotRebuilds++;
                    _snapshotCache = BuildSnapshot();
                    _dirty = false;
                }
                return _snapshotCache;
            }
        }
    }

    private IReadOnlyList<JobSnapshot>? _snapshotCache;
    private bool _dirty;

    private IReadOnlyList<JobSnapshot> BuildSnapshot()
    {
        var snap = new JobSnapshot[_byJobId.Count];
        var i = 0;
        foreach (var (id, pair) in _byJobId)
            snap[i++] = pair.Entry.ToSnapshot(_expressionText[id]);
        return snap;
    }

    /// <summary>Starts the background tick loop. One-shot lifecycle: only
    /// valid before the first Stop — a stopped scheduler cannot be restarted
    /// (a host restart creates a new instance).</summary>
    public void Start()
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_stopped)
                throw new InvalidOperationException(
                    "scheduler was stopped — lifecycle is one-shot; create a new instance"
                );
            if (_running)
                throw new InvalidOperationException("scheduler already started");
            _running = true;
            cts = _loopCts = new CancellationTokenSource();
        }
        // Capture the source locally: StopAsync nulls _loopCts, and a
        // start-then-immediate-stop race must not NRE inside the loop lambda.
        _ = _loopTask = Task.Run(() => RunLoopAsync(cts.Token));
    }

    /// <summary>Stops the loop and waits for in-flight sink calls to finish
    /// (bounded wait, then abandons and logs).</summary>
    public async ValueTask StopAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate)
        {
            _stopped = true;
            cts = _loopCts;
            loop = _loopTask;
            _running = false;
            _loopCts = null;
        }
        cts?.Cancel();
        if (loop is not null)
        {
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                _log?.Invoke(
                    new ScheduleLogEntry(_clock.UtcNow, Guid.Empty, "Warn", "stop wait timed out")
                );
            }
        }
        cts?.Dispose(); // cancel → join → dispose: token source never outlives the loop wait
        // Wait for in-flight sinks (bounded) — UNCONDITIONAL: sinks started
        // via test/manual flush paths are drained even when the loop never ran.
        List<Task> inFlight;
        lock (_gate)
        {
            inFlight = _inFlightTasks.Values.ToList();
        }
        if (inFlight.Count > 0)
        {
            try
            {
                await Task.WhenAll(inFlight).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                _log?.Invoke(
                    new ScheduleLogEntry(
                        _clock.UtcNow,
                        Guid.Empty,
                        "Warn",
                        "in-flight sink wait timed out"
                    )
                );
            }
            catch
            {
                // sink failures are already recorded per job; ignore here
            }
        }
    }

    /// <summary>Registers (or overwrites) a job. A repeated jobId REPLACES the
    /// entry — new lifecycle: nextFire recomputed, failure counters cleared,
    /// in-flight callbacks from the old entry are not recalled (host-side
    /// idempotency via dueUtc). The jobId is host-provided (power idempotent
    /// re-registration after a host restart).</summary>
    public Guid Register(
        Guid jobId,
        string cronExpression,
        ITriggerSink sink,
        IReadOnlyDictionary<string, string?>? payload = null,
        ScheduleJobOptions? options = null
    )
    {
        var opts = options ?? new ScheduleJobOptions();
        var cron = CronPattern.Parse(cronExpression); // throws ArgumentException
        var entry = new JobEntry
        {
            JobId = jobId,
            Sink = sink,
            Payload = payload ?? new Dictionary<string, string?>(),
            Cron = cron,
            Tz = opts.ResolvedTz,
            Enabled = opts.Enabled,
            NextFireUtc = default,
        };

        lock (_gate)
        {
            if (_byJobId.TryGetValue(jobId, out var existing))
            {
                // Overwrite: remove old entry from its slot first.
                if (existing.Slot is { } oldSlot && _wheel.TryGetValue(oldSlot, out var list))
                {
                    list.Remove(existing.Entry);
                    if (list.Count == 0)
                        _wheel.Remove(oldSlot); // empty-slot pruning (§5)
                }
            }
            entry.NextFireUtc = NextFireTracked(entry.Cron, _clock.UtcNow, entry.Tz);
            var slot = SlotOf(entry.NextFireUtc);
            AddToSlot(slot, entry);
            _byJobId[jobId] = (slot, entry);
            _expressionText[jobId] = cronExpression;
            _graceModes[jobId] = opts.Grace;
            if (opts.FixedGrace is { } fg)
                _fixedGraces[jobId] = fg;
            else
                _fixedGraces.Remove(jobId);
            MarkDirty();
        }
        return jobId;
    }

    /// <summary>Pause: the entry leaves the wheel; the reverse index keeps a
    /// stub (Enabled=false, failure counters preserved — paused time does not
    /// count failures). In-flight callbacks from the old entry keep running
    /// (already-published fires are not recalled).</summary>
    public void Pause(Guid jobId)
    {
        lock (_gate)
        {
            if (!_byJobId.TryGetValue(jobId, out var existing))
                return; // silent
            RemoveFromSlot(existing.Slot, existing.Entry);
            existing.Entry.Enabled = false;
            _byJobId[jobId] = (null, existing.Entry);
            MarkDirty();
        }
    }

    /// <summary>Resume: recomputes nextFire from NOW (the paused window is
    /// skipped — no backlog) and re-enters the wheel. Unknown jobId: silent.</summary>
    public void Resume(Guid jobId)
    {
        lock (_gate)
        {
            if (!_byJobId.TryGetValue(jobId, out var existing))
                return; // silent
            existing.Entry.Enabled = true;
            existing.Entry.NextFireUtc = NextFireTracked(
                existing.Entry.Cron,
                _clock.UtcNow,
                existing.Entry.Tz
            );
            existing.Entry.IntervalEstimate = null; // new fire cycle — recompute lazily
            var slot = SlotOf(existing.Entry.NextFireUtc);
            AddToSlot(slot, existing.Entry);
            _byJobId[jobId] = (slot, existing.Entry);
            MarkDirty();
        }
    }

    /// <summary>Reschedule: recompiles the pattern, recomputes nextFire and
    /// moves the entry to the new slot. Invalid pattern throws and leaves the
    /// entry untouched.</summary>
    public void Reschedule(Guid jobId, string newCronExpression)
    {
        var cron = CronPattern.Parse(newCronExpression); // throws first
        lock (_gate)
        {
            if (!_byJobId.TryGetValue(jobId, out var existing))
                return; // silent
            RemoveFromSlot(existing.Slot, existing.Entry);
            existing.Entry.Cron = cron;
            existing.Entry.NextFireUtc = NextFireTracked(cron, _clock.UtcNow, existing.Entry.Tz);
            existing.Entry.IntervalEstimate = null; // cron changed — stale estimate
            var slot = SlotOf(existing.Entry.NextFireUtc);
            AddToSlot(slot, existing.Entry);
            _byJobId[jobId] = (slot, existing.Entry);
            _expressionText[jobId] = newCronExpression;
            MarkDirty();
        }
    }

    /// <summary>Removes the job entirely (wheel slot and reverse index).</summary>
    public void Unregister(Guid jobId)
    {
        lock (_gate)
        {
            if (!_byJobId.TryGetValue(jobId, out var existing))
                return; // unknown jobId: silent (contract)
            RemoveFromSlot(existing.Slot, existing.Entry);
            _byJobId.Remove(jobId);
            _expressionText.Remove(jobId);
            _graceModes.Remove(jobId);
            _fixedGraces.Remove(jobId);
            MarkDirty();
        }
    }

    /// <summary>Internal: run one flush WITHOUT waiting for sinks (used by
    /// tests that hold sinks in-flight; production loops use this too).</summary>
    internal Task FlushCoreOnlyAsync(DateTimeOffset nowUtc, CancellationToken ct = default) =>
        FlushCoreAsync(nowUtc, ct);

    /// <summary>Internal test hook — runs one full flush AND waits for the
    /// sinks it started (deterministic assertions). The production tick loop
    /// calls <see cref="FlushCoreAsync"/> without waiting (fire-and-forget
    /// sink execution, spec §8).</summary>
    internal async Task FlushNowAsync(DateTimeOffset? nowUtc = null, CancellationToken ct = default)
    {
        await FlushCoreAsync(nowUtc ?? _clock.UtcNow, ct);
        Task[] started;
        lock (_gate)
        {
            started = _inFlightTasks.Values.ToArray();
        }
        if (started.Length > 0)
        {
            try
            {
                await Task.WhenAll(started);
            }
            catch
            {
                // sink failures are recorded per job inside the fire task
            }
        }
    }

    private async Task FlushCoreAsync(DateTimeOffset now, CancellationToken ct)
    {
        List<(JobEntry Entry, DateTimeOffset DueUtc)> toFire = new();
        lock (_gate)
        {
            if (now < _lastFlushedUtc)
                return; // monotonic guard — stale clock input ignored
            _lastFlushedUtc = now;

            // 1) candidates: enabled entries in slots ≤ now. Keys are ordered —
            // collect due slots and STOP at the first future slot (a full-key
            // LINQ scan would examine every future slot on every tick).
            var due = new List<(JobEntry Entry, string TzId)>();
            var nowSlot = SlotOf(now);
            var dueSlots = new List<long>();
            foreach (var slot in _wheel.Keys)
            {
                WheelSlotsExamined++;
                if (slot > nowSlot)
                    break;
                dueSlots.Add(slot);
            }
            foreach (var slot in dueSlots)
            {
                if (!_wheel.TryGetValue(slot, out var list))
                    continue;
                foreach (var entry in list.ToList())
                {
                    if (!entry.Enabled || entry.NextFireUtc > now)
                        continue;
                    // 2) grace judgment (per-entry policy) — the [60s, 2h] clamp
                    // bounds short-circuit the O(minutes) interval estimate when
                    // it cannot change the outcome (see BeyondGrace).
                    if (BeyondGrace(entry, now))
                    {
                        try
                        {
                            FastForward(entry, now); // 3) beyond grace / disabled
                        }
                        catch (Exception ex)
                        {
                            // Per-entry containment: one failing entry must never
                            // abort the flush for every other due job.
                            RecordFailure(entry, ex);
                        }
                    }
                    else
                    {
                        due.Add((entry, entry.Tz.Id));
                    }
                }
            }

            // 4) burst limit: earliest-due first
            due.Sort((a, b) => a.Entry.NextFireUtc.CompareTo(b.Entry.NextFireUtc));
            var batch = due.Take(_options.BurstLimit).ToList();

            // 5) snapshot due timestamps BEFORE advancing (Advance relocates
            //    NextFireUtc into the future — the sink must receive the slot
            //    that actually fired).
            var dueTimes = batch.Select(b => (b.Entry, b.Entry.NextFireUtc)).ToList();

            // 6) advance ONLY the admitted batch (now-based, never chase the
            //    period); the burst-excess entries STAY overdue and keep
            //    competing in the next tick's ordering (spec §6). Advancing
            //    every due entry would silently skip the capped remainder.
            //    An advance failure is contained per entry: a bad entry stays
            //    out of this pulse and the failure governance (RecordFailure)
            //    auto-pauses it after MaxConsecutiveFailures — the flush must
            //    never abort for the other due jobs.
            foreach (var (entry, dueUtc) in dueTimes)
            {
                try
                {
                    Advance(entry, now);
                    toFire.Add((entry, dueUtc));
                }
                catch (Exception ex)
                {
                    RecordFailure(entry, ex);
                }
            }
        }

        foreach (var (entry, dueUtc) in toFire)
        {
            Task? running;
            lock (_gate)
            {
                _inFlightTasks.TryGetValue(entry.JobId, out running);
                if (running is not null && !running.IsCompleted)
                    continue; // skip-if-busy: this pulse is skipped, next is fresh
            }

            var task = Task.Run(async () =>
            {
                try
                {
                    await entry
                        .Sink.FireAsync(entry.JobId, dueUtc, entry.Payload, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Loop/shutdown cancellation, not a job failure — recording it
                    // would count a clean shutdown against the failure budget.
                }
                catch (Exception ex)
                {
                    RecordFailure(entry, ex);
                }
            });
            lock (_gate)
            {
                _inFlightTasks[entry.JobId] = task;
            }
            _ = task.ContinueWith(
                static (completed, state) =>
                {
                    var (scheduler, jobId) = ((Scheduler, Guid))state!;
                    scheduler.UntrackInFlight(completed, jobId);
                },
                (this, entry.JobId),
                TaskScheduler.Default
            );
        }
    }

    /// <summary>Removes a completed fire task from the in-flight map — but only
    /// when it is still the task registered for the job. A late continuation
    /// from an earlier pulse must never untrack a NEWER running pulse (that
    /// would defeat skip-if-busy and let the same job run concurrently).</summary>
    private void UntrackInFlight(Task completed, Guid jobId)
    {
        lock (_gate)
        {
            if (
                _inFlightTasks.TryGetValue(jobId, out var current)
                && ReferenceEquals(current, completed)
            )
            {
                _inFlightTasks.Remove(jobId);
            }
        }
    }

    /// <summary>True when the slot is overdue beyond the entry's grace window.
    /// <c>HalfPeriodClamped</c> grace is clamped to [60s, 2h]: an overdue at or
    /// below the floor is always inside, above the ceiling always beyond — the
    /// exact estimate (O(minutes-to-next-fire) per call) is only needed for the
    /// (60s, 2h] band, where the cached per-cycle value is reused across ticks.</summary>
    private bool BeyondGrace(JobEntry entry, DateTimeOffset now)
    {
        var overdue = now - entry.NextFireUtc;
        var mode = _graceModes.TryGetValue(entry.JobId, out var m)
            ? m
            : GraceMode.HalfPeriodClamped;
        switch (mode)
        {
            case GraceMode.Disabled:
                return overdue > TimeSpan.Zero;
            case GraceMode.Fixed:
                return overdue
                    > (_fixedGraces.TryGetValue(entry.JobId, out var f) ? f : TimeSpan.Zero);
            default:
                if (overdue <= TimeSpan.FromSeconds(60))
                    return false; // inside the clamp floor — always due, no estimate
                if (overdue > TimeSpan.FromHours(2))
                    return true; // beyond the clamp ceiling — always fast-forward
                return overdue > HalfPeriodClamped(entry, now);
        }
    }

    private TimeSpan HalfPeriodClamped(JobEntry entry, DateTimeOffset now)
    {
        // Interval estimation probes minute-by-minute (O(minutes-to-next-fire) per
        // call, e.g. ~33ms for a yearly pattern). Cache it per entry per fire cycle
        // so a tick pays it once instead of once per due entry per tick.
        var interval = entry.IntervalEstimate ??= ComputeInterval(entry, now);
        var half = interval / 2;
        if (half < TimeSpan.FromSeconds(60))
            return TimeSpan.FromSeconds(60);
        if (half > TimeSpan.FromHours(2))
            return TimeSpan.FromHours(2);
        return half;
    }

    private TimeSpan ComputeInterval(JobEntry entry, DateTimeOffset now)
    {
        GraceIntervalComputations++;
        return entry.Cron.EstimateInterval(now, entry.Tz);
    }

    private void FastForward(JobEntry entry, DateTimeOffset now)
    {
        ThrowIfAdvanceBombed(entry);
        // recompute to a future slot without firing
        var next = NextFireTracked(entry.Cron, now, entry.Tz);
        if (_byJobId.TryGetValue(entry.JobId, out var idx))
        {
            RemoveFromSlot(idx.Slot, entry);
            entry.NextFireUtc = next;
            entry.IntervalEstimate = null; // new fire cycle — recompute lazily
            var slot = SlotOf(next);
            AddToSlot(slot, entry);
            _byJobId[entry.JobId] = (slot, entry);
            MarkDirty();
        }
    }

    private void Advance(JobEntry entry, DateTimeOffset now)
    {
        ThrowIfAdvanceBombed(entry);
        var next = NextFireTracked(entry.Cron, now, entry.Tz);
        if (_byJobId.TryGetValue(entry.JobId, out var idx))
        {
            RemoveFromSlot(idx.Slot, entry);
            entry.NextFireUtc = next;
            entry.LastTriggeredUtc = now;
            entry.IntervalEstimate = null; // new fire cycle — recompute lazily
            var slot = SlotOf(next);
            AddToSlot(slot, entry);
            _byJobId[entry.JobId] = (slot, entry);
            MarkDirty();
        }
    }

    private void RecordFailure(JobEntry entry, Exception ex)
    {
        lock (_gate)
        {
            entry.ConsecutiveFailures++;
            entry.LastError = ex.Message;
            _log?.Invoke(new ScheduleLogEntry(_clock.UtcNow, entry.JobId, "Error", ex.Message));
            MarkDirty();

            if (entry.Enabled && entry.ConsecutiveFailures >= _options.MaxConsecutiveFailures)
            {
                // Auto-pause: the entry leaves the wheel, the stub keeps the failure
                // counters (health governance — a sick job must not occupy slots;
                // the host may Resume to reset and retry).
                if (_byJobId.TryGetValue(entry.JobId, out var idx))
                {
                    RemoveFromSlot(idx.Slot, entry);
                    entry.Enabled = false;
                    _byJobId[entry.JobId] = (null, entry);
                    MarkDirty();
                }
                _log?.Invoke(
                    new ScheduleLogEntry(
                        _clock.UtcNow,
                        entry.JobId,
                        "Warn",
                        $"auto-paused after {entry.ConsecutiveFailures} consecutive failures"
                    )
                );
            }
        }
    }

    private Task? _loopTask;

    /// <summary>TEST-ONLY bomb arm: the next FlushCore throws before doing any
    /// work. Verifies the loop's catch-all (review R4-1) — a transient flush
    /// failure must never kill the scheduler thread.</summary>
    internal volatile bool BombFlushNextTick;

    /// <summary>TEST ARM: the next Advance/FastForward of the given job throws
    /// before doing any work. Verifies per-entry containment — one failing entry
    /// must not abort the flush for every other due job.</summary>
    internal Guid? BombAdvanceForJobId;

    private void ThrowIfAdvanceBombed(JobEntry entry)
    {
        if (BombAdvanceForJobId == entry.JobId)
        {
            BombAdvanceForJobId = null;
            throw new InvalidOperationException("test bomb inside advance");
        }
    }

    /// <summary>TEST HOOK — cumulative cron search steps spent on next-fire
    /// computations. Contract: field-scaled, never a per-minute probe.</summary>
    internal long CronSearchSteps;

    /// <summary>TEST HOOK — number of times the lazily built snapshot cache was
    /// rebuilt. Contract: management mutations only mark it dirty; a 10k
    /// registration burst rebuilds zero times until the snapshot is read.</summary>
    internal long SnapshotRebuilds;

    private DateTimeOffset NextFireTracked(CronPattern cron, DateTimeOffset after, TimeZoneInfo tz)
    {
        var next = cron.NextFireCore(after, tz, out var steps);
        CronSearchSteps += steps;
        return next;
    }

    /// <summary>TEST HOOK — cumulative count of grace interval estimates that were
    /// actually computed. Contract: one estimate per job entry per fire cycle,
    /// never one per tick (the 10k scale-test budget depends on it).</summary>
    internal long GraceIntervalComputations;

    /// <summary>TEST HOOK — cumulative count of wheel keys visited by due-slot
    /// scans. Contract: due slots plus the first future slot that stops the scan
    /// (README: "flush touches only due slots").</summary>
    internal long WheelSlotsExamined;

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_options.NormalizedTick, ct);
                try
                {
                    if (BombFlushNextTick)
                    {
                        BombFlushNextTick = false;
                        throw new InvalidOperationException("test bomb inside flush");
                    }
                    await FlushCoreAsync(_clock.UtcNow, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Resilience contract: a transient flush failure is logged
                    // as Critical and the loop LIVES — the next tick retries.
                    // A dead tick loop is the worst failure mode of a scheduler
                    // (silent stoppage); never let an inner exception kill it.
                    _log?.Invoke(
                        new ScheduleLogEntry(
                            _clock.UtcNow,
                            Guid.Empty,
                            "Critical",
                            $"flush failed: {ex.Message}"
                        )
                    );
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal stop
        }
    }

    private readonly Dictionary<Guid, Task> _inFlightTasks = new();

    private static long SlotOf(DateTimeOffset utc) => utc.ToUnixTimeSeconds() / 60;

    private void AddToSlot(long slot, JobEntry entry)
    {
        if (!_wheel.TryGetValue(slot, out var list))
        {
            list = new List<JobEntry>();
            _wheel[slot] = list;
        }
        list.Add(entry);
    }

    private void RemoveFromSlot(long? slot, JobEntry entry)
    {
        if (slot is not { } s)
            return;
        if (_wheel.TryGetValue(s, out var list))
        {
            list.Remove(entry);
            if (list.Count == 0)
                _wheel.Remove(s); // empty-slot pruning
        }
    }

    private void MarkDirty() => _dirty = true;
}
