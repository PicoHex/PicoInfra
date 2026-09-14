namespace PicoSchedule;

/// <summary>
/// One scheduled entry — a plan entity that lives ON the timing wheel
/// (framework spec §5). Holds the full registration: identity, the trigger
/// sink (how to call), the opaque payload (parameters), the compiled pattern
/// (when) and runtime observations (failures). Mutated only inside the
/// scheduler loop or management calls; never shared across threads.
/// </summary>
internal sealed class JobEntry
{
    public required Guid JobId { get; init; }
    public required ITriggerSink Sink { get; set; }
    public required IReadOnlyDictionary<string, string?> Payload { get; init; }
    public required CronPattern Cron { get; set; }
    public required TimeZoneInfo Tz { get; init; }
    public required bool Enabled { get; set; }
    public required DateTimeOffset NextFireUtc { get; set; }
    public DateTimeOffset LastTriggeredUtc { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string? LastError { get; set; }

    /// <summary>Cached <c>EstimateInterval</c> for the CURRENT fire cycle — set on
    /// first grace judgment, cleared whenever next-fire moves (advance, fast-forward,
    /// resume, reschedule). Keeps the estimate out of the per-tick hot path.</summary>
    public TimeSpan? IntervalEstimate { get; set; }

    public JobSnapshot ToSnapshot(string expressionText) =>
        new(
            JobId,
            expressionText,
            Enabled,
            NextFireUtc,
            LastTriggeredUtc,
            ConsecutiveFailures,
            LastError
        );
}
