namespace PicoLog;

internal static class LogEntryPool
{
    private const int MaxPoolSize = 256;
    private static readonly ConcurrentQueue<LogEntry> _pool = new();

    /// <summary>
    /// Number of pool slots held by <see cref="Return"/>: one per entry in the
    /// queue, plus — transiently — one per caller that has claimed a slot and not
    /// yet enqueued (or dropped) its entry.
    /// </summary>
    /// <remarks>
    /// Claiming the slot <em>before</em> the enqueue is what makes the bound real:
    /// a check-then-act on the queue length let two callers both read "one slot
    /// free" and both enqueue. This counter can never exceed
    /// <see cref="MaxPoolSize"/>, so "the pool is at most MaxPoolSize entries" is
    /// an invariant of the account, not a hope about the interleaving. It returns
    /// to the true queue length as soon as the returners have finished, because
    /// every claim is released exactly once — by the drop path, or by a successful
    /// dequeue in <see cref="Rent"/>.
    /// </remarks>
    private static int _claimed;

    /// <summary>
    /// Gets the current number of entries in the pool.
    /// Used for testing pool size enforcement.
    /// </summary>
    internal static int Count => _pool.Count;

    public static LogEntry Rent()
    {
        if (_pool.TryDequeue(out var entry))
        {
            // Release the slot after the dequeue, never before: for an instant the
            // counter over-counts by one, which can only make a racing Return drop
            // one entry for the GC. Under-counting would let Return exceed the
            // bound, which is the defect this counter exists to prevent.
            Interlocked.Decrement(ref _claimed);
            entry.Reset();
            return entry;
        }

        return new LogEntry();
    }

    public static void Return(LogEntry entry)
    {
        if (entry is null)
            return;

        // Reset before returning to release references
        entry.Reset();

        // Bounded enqueue: when the pool is at capacity, excess entries are
        // silently dropped and will be garbage collected. The slot is reserved
        // before the entry goes in, so two callers can never both be told there is
        // room for the same last slot.
        if (!TryClaimSlot())
            return;

        try
        {
            _pool.Enqueue(entry);
        }
        catch
        {
            // Unreachable in practice (Enqueue only faults on OOM). Release the
            // claim anyway: a leaked slot would shrink the bound for the lifetime
            // of the process, silently and permanently.
            Interlocked.Decrement(ref _claimed);
            throw;
        }
    }

    /// <summary>
    /// Reserves one of the <see cref="MaxPoolSize"/> slots, or returns
    /// <see langword="false"/> when the pool is at capacity. Lock-free and
    /// allocation-free: one compare-exchange per call on the uncontended path, and
    /// no loop at all once the pool is full.
    /// </summary>
    private static bool TryClaimSlot()
    {
        var claimed = Volatile.Read(ref _claimed);
        while (claimed < MaxPoolSize)
        {
            var seen = Interlocked.CompareExchange(ref _claimed, claimed + 1, claimed);
            if (seen == claimed)
                return true;

            claimed = seen;
        }

        return false;
    }
}
