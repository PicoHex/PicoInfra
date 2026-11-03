namespace PicoLog.Tests;

[NotInParallel]
public sealed class LogEntryPoolTests
{
    [Before(Test)]
    public void ClearPool()
    {
        // Other test classes may have left entries in the shared static pool.
        // Drain it so each test starts in a known-empty state.
        while (LogEntryPool.Count > 0)
            LogEntryPool.Rent();
    }

    [Test]
    public async Task Rent_ReturnsInstance_WithDefaultValues()
    {
        var entry = LogEntryPool.Rent();

        await Assert.That(entry).IsNotNull();
        await Assert.That(entry.Timestamp).IsEqualTo(default(DateTimeOffset));
        await Assert.That(entry.Message).IsNull();
        await Assert.That(entry.Category).IsNull();
    }

    [Test]
    public async Task ReturnAndRent_ReusesInstance()
    {
        var first = LogEntryPool.Rent();
        first.Message = "hello";
        LogEntryPool.Return(first);

        var second = LogEntryPool.Rent();

        await Assert.That(ReferenceEquals(first, second)).IsTrue();
        // After Rent, the entry should be reset
        await Assert.That(second.Message).IsNull();
    }

    [Test]
    public async Task RentMultipleTimes_WhenPoolEmpty_ReturnsNewInstances()
    {
        var a = LogEntryPool.Rent();
        var b = LogEntryPool.Rent();

        await Assert.That(ReferenceEquals(a, b)).IsFalse();
    }

    [Test]
    public async Task Return_WhenPoolExceedsMaxSize_DropsExcessEntries()
    {
        // 🔴 RED: MaxPoolSize = 256 is declared but never enforced.
        // Return() enqueues unconditionally, allowing unbounded growth.
        // After fix, the pool should drop entries beyond MaxPoolSize.

        var overflow = 300; // > MaxPoolSize (256)
        var entries = new LogEntry[overflow];

        // Rent and return many entries to trigger pool overflow
        for (var i = 0; i < overflow; i++)
            entries[i] = LogEntryPool.Rent();

        for (var i = 0; i < overflow; i++)
            LogEntryPool.Return(entries[i]);

        // Assert: the pool keeps EXACTLY MaxPoolSize (256) and drops only the excess.
        // `Count <= 256` was satisfied by any bound at all: bounding at 100, at 1, or dropping
        // every entry left this test green, so the constant the name quotes was never pinned
        // (test-validity audit row 12).
        await Assert.That(LogEntryPool.Count).IsEqualTo(256);

        // Assert: the entries that survived are the ones handed back — the pool bounds its queue,
        // it does not throw the batch away.
        var pooled = LogEntryPool.Rent();
        await Assert.That(pooled).IsNotNull();
        await Assert
            .That(entries.Any(entry => ReferenceEquals(entry, pooled)))
            .IsTrue()
            .Because("Rent hands back a returned entry, not a fresh one");

        // Return it for cleanup
        LogEntryPool.Return(pooled);

        // Restore pool to empty state so other tests are not affected
        while (LogEntryPool.Count > 0)
            LogEntryPool.Rent();
    }
}
