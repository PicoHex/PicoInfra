namespace PicoSchedule;

/// <summary>Trigger target port — implemented by the host per use case.
/// One sink per job entry; several jobs may share a single sink instance.
/// The framework never creates/disposes/releases sinks and never retries a
/// failed call (framework spec §4.3/§7): fire once, record failure, the next
/// slot is the natural retry.</summary>
public interface ITriggerSink
{
    /// <summary>Invoked when a job's scheduled time arrives. Payload is the
    /// opaque registration dictionary. <paramref name="dueUtc"/> is the
    /// absolute slot that fired — usable as an idempotency key.</summary>
    ValueTask FireAsync(
        Guid jobId,
        DateTimeOffset dueUtc,
        IReadOnlyDictionary<string, string?> payload,
        CancellationToken ct
    );
}

/// <summary>Clock abstraction — every time judgment inside the scheduler runs
/// through this (testability; FakeClock injection).</summary>
public interface ITimeProvider
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>Default clock — the system wall clock.</summary>
public sealed class SystemClock : ITimeProvider
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Misfire policy — what to do with a slot that was missed
/// (Quartz MisfireInstruction, reduced to three modes; framework spec §5.0/§6).</summary>
public enum GraceMode
{
    /// <summary>Default: grace = half the estimated period, clamped 60s..2h —
    /// a miss inside the window replays once; beyond it fast-forwards.</summary>
    HalfPeriodClamped,

    /// <summary>Fixed grace window — <see cref="ScheduleJobOptions.FixedGrace"/>.</summary>
    Fixed,

    /// <summary>Never replay a missed slot — always fast-forward.</summary>
    Disabled,
}

/// <summary>Per-job registration options.</summary>
public sealed record ScheduleJobOptions(
    bool Enabled = true,
    string? TimeZoneId = null,
    GraceMode Grace = GraceMode.HalfPeriodClamped,
    TimeSpan? FixedGrace = null
)
{
    /// <summary>Resolved time zone for this job (null TimeZoneId = local).</summary>
    public TimeZoneInfo ResolvedTz =>
        TimeZoneId is null ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
}

/// <summary>Scheduler-wide options.</summary>
public sealed record SchedulerOptions(
    TimeSpan TickInterval = default, // default 10s (see Normalize)
    int BurstLimit = 50,
    int MaxConsecutiveFailures = 5,
    Action<ScheduleLogEntry>? Log = null,
    ITimeProvider? Clock = null
)
{
    internal TimeSpan NormalizedTick =>
        TickInterval == default ? TimeSpan.FromSeconds(10) : TickInterval;
}

/// <summary>Zero-dependency log line (host adapts it, e.g. one-line PicoLog wrapper).</summary>
public sealed record ScheduleLogEntry(
    DateTimeOffset AtUtc,
    Guid JobId,
    string Level,
    string Message
);

/// <summary>CQS snapshot of one job — reads never block the scheduler loop.</summary>
public sealed record JobSnapshot(
    Guid JobId,
    string CronExpression,
    bool Enabled,
    DateTimeOffset NextFireUtc,
    DateTimeOffset LastTriggeredUtc,
    int ConsecutiveFailures,
    string? LastError
);
