namespace PicoLog.Tests;

public sealed class FlushQuiesceCoordinatorTests
{
    [Test]
    public async Task EnterWriteOperationSync_Enters_WhenNoFlush()
    {
        var c = new FlushQuiesceCoordinator();

        // With no flush pending, entry must not consult the timeout at all: it takes the write
        // operation and returns. TimeSpan.Zero is the sharpest form of that claim — the sibling
        // EnterWriteOperationSync_TimesOut_WhenFlushPending test calls the same method with a
        // flush pending and gets a TimeoutException, so a zero timeout here passing can only mean
        // the flush-pending branch was not entered.
        c.EnterWriteOperationSync(TimeSpan.Zero);

        try
        {
            await Assert.That(c.HasActiveWriteOperations()).IsTrue();
        }
        finally
        {
            c.ExitWriteOperation();
        }
    }

    [Test]
    public async Task EnterWriteOperationSync_IncrementsActiveWrites()
    {
        var c = new FlushQuiesceCoordinator();
        c.EnterWriteOperationSync(TimeSpan.FromSeconds(1));
        await Assert.That(c.HasActiveWriteOperations()).IsTrue();
        c.ExitWriteOperation();
        await Assert.That(c.HasActiveWriteOperations()).IsFalse();
    }

    [Test]
    public async Task EnterWriteOperationSync_TimesOut_WhenFlushPending()
    {
        var c = new FlushQuiesceCoordinator();
        await c.BlockWritesAsync(CancellationToken.None);
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(async () =>
                await Task.Run(() => c.EnterWriteOperationSync(TimeSpan.FromMilliseconds(10)))
            );
        }
        finally
        {
            c.ResumeWrites();
        }
    }

    [Test]
    public async Task EnterWriteOperationAsync_Enters_WhenNoFlush()
    {
        var c = new FlushQuiesceCoordinator();
        await c.EnterWriteOperationAsync(CancellationToken.None);
        c.ExitWriteOperation();
    }

    [Test]
    public async Task EnterWriteOperationAsync_Waits_UntilFlushResumes()
    {
        var c = new FlushQuiesceCoordinator();
        await c.BlockWritesAsync(CancellationToken.None);

        var enterTask = c.EnterWriteOperationAsync(CancellationToken.None).AsTask();
        await Assert.That(enterTask.IsCompleted).IsFalse();

        c.ResumeWrites();
        await enterTask;
        c.ExitWriteOperation();
    }

    [Test]
    public async Task EnterWriteOperationAsync_RespectsCancellation()
    {
        var c = new FlushQuiesceCoordinator();
        await c.BlockWritesAsync(CancellationToken.None);
        try
        {
            using var cts = new CancellationTokenSource();
            var enterTask = c.EnterWriteOperationAsync(cts.Token).AsTask();
            cts.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await enterTask);
        }
        finally
        {
            c.ResumeWrites();
        }
    }

    [Test]
    public async Task ExitWriteOperation_SignalsQuiesceWhenFlushWaiting()
    {
        var c = new FlushQuiesceCoordinator();
        c.EnterWriteOperationSync(TimeSpan.FromSeconds(1));

        var blockTask = c.BlockWritesAsync(CancellationToken.None).AsTask();
        c.ExitWriteOperation();

        await blockTask;
        c.ResumeWrites();
    }

    [Test]
    public async Task BlockWritesAsync_ReturnsImmediately_WhenNoActiveWrites()
    {
        var c = new FlushQuiesceCoordinator();
        var blockTask = c.BlockWritesAsync(CancellationToken.None).AsTask();
        await Assert.That(blockTask.IsCompleted).IsTrue();
        c.ResumeWrites();
    }

    [Test]
    public async Task IsFlushPending_ReflectsFlushState()
    {
        var c = new FlushQuiesceCoordinator();
        await c.BlockWritesAsync(CancellationToken.None);
        await Assert.That(c.IsFlushPending()).IsTrue();
        c.ResumeWrites();
        await Assert.That(c.IsFlushPending()).IsFalse();
    }

    [Test]
    public async Task ResumeWrites_UnblocksPendingWriteEntries()
    {
        var c = new FlushQuiesceCoordinator();
        await c.BlockWritesAsync(CancellationToken.None);

        var enterTask = c.EnterWriteOperationAsync(CancellationToken.None).AsTask();
        await Assert.That(enterTask.IsCompleted).IsFalse();

        c.ResumeWrites();
        await enterTask;
        c.ExitWriteOperation();
    }

    [Test]
    public async Task WaitForIdleAsync_ReturnsImmediately_WhenAlreadyIdle()
    {
        var c = new FlushQuiesceCoordinator();
        await c.BlockWritesAsync(CancellationToken.None);

        var waitTask = c.WaitForIdleAsync(static () => true, CancellationToken.None).AsTask();
        await Assert.That(waitTask.IsCompleted).IsTrue();
        c.ResumeWrites();
    }

    [Test]
    public async Task WaitForIdleAsync_Waits_ThenReturnsAfterActivityCompletes()
    {
        var c = new FlushQuiesceCoordinator();
        var activities = new ActivityCounter();

        await c.BlockWritesAsync(CancellationToken.None);
        c.BeginOwnerActivity(() => activities.Active++);

        var waitTask = c.WaitForIdleAsync(activities.IsIdle, CancellationToken.None).AsTask();
        await Assert.That(waitTask.IsCompleted).IsFalse();

        c.EndOwnerActivity(() => activities.Active--, activities.IsIdle);
        await waitTask;
        c.ResumeWrites();
    }

    [Test]
    public async Task WaitForIdleAsync_WaitsForMultipleActivities()
    {
        var c = new FlushQuiesceCoordinator();
        var activities = new ActivityCounter();

        await c.BlockWritesAsync(CancellationToken.None);
        c.BeginOwnerActivity(() => activities.Active++);
        c.BeginOwnerActivity(() => activities.Active++);

        var waitTask = c.WaitForIdleAsync(activities.IsIdle, CancellationToken.None).AsTask();
        await Assert.That(waitTask.IsCompleted).IsFalse();

        c.EndOwnerActivity(() => activities.Active--, activities.IsIdle);
        await Assert.That(waitTask.IsCompleted).IsFalse();

        c.EndOwnerActivity(() => activities.Active--, activities.IsIdle);
        await waitTask;
        c.ResumeWrites();
    }

    [Test]
    public async Task FlushPattern_WithConcurrentWrite_CompletesCorrectly()
    {
        var c = new FlushQuiesceCoordinator();
        c.EnterWriteOperationSync(TimeSpan.FromSeconds(1));

        var blockTask = c.BlockWritesAsync(CancellationToken.None).AsTask();
        await Assert.That(blockTask.IsCompleted).IsFalse();

        c.ExitWriteOperation();
        await blockTask;

        await c.WaitForIdleAsync(static () => true, CancellationToken.None);
        c.ResumeWrites();
    }

    [Test]
    public async Task MultipleEnterOperations_AllReleased_WhenFlushEnds()
    {
        var c = new FlushQuiesceCoordinator();
        await c.BlockWritesAsync(CancellationToken.None);

        var t1 = c.EnterWriteOperationAsync(CancellationToken.None).AsTask();
        var t2 = c.EnterWriteOperationAsync(CancellationToken.None).AsTask();
        var t3 = c.EnterWriteOperationAsync(CancellationToken.None).AsTask();

        await Assert.That(t1.IsCompleted).IsFalse();
        await Assert.That(t2.IsCompleted).IsFalse();
        await Assert.That(t3.IsCompleted).IsFalse();

        c.ResumeWrites();
        await Task.WhenAll(t1, t2, t3);

        c.ExitWriteOperation();
        c.ExitWriteOperation();
        c.ExitWriteOperation();
    }

    [Test]
    public async Task EndOwnerActivity_IdleDetection_WhenWaitArrivesLate()
    {
        var c = new FlushQuiesceCoordinator();
        var activities = new ActivityCounter();

        await c.BlockWritesAsync(CancellationToken.None);
        c.BeginOwnerActivity(() => activities.Active++);
        c.EndOwnerActivity(() => activities.Active--, activities.IsIdle);

        var waitTask = c.WaitForIdleAsync(activities.IsIdle, CancellationToken.None).AsTask();
        await Assert.That(waitTask.IsCompleted).IsTrue();
        c.ResumeWrites();
    }

    [Test]
    public async Task ResumeWrites_UnblocksPendingIdleWaiters()
    {
        var c = new FlushQuiesceCoordinator();
        var activities = new ActivityCounter();

        await c.BlockWritesAsync(CancellationToken.None);
        c.BeginOwnerActivity(() => activities.Active++);

        var waitTask = c.WaitForIdleAsync(activities.IsIdle, CancellationToken.None).AsTask();
        await Assert.That(waitTask.IsCompleted).IsFalse();

        c.ResumeWrites();
        await waitTask.WaitAsync(TimeSpan.FromSeconds(1));

        c.EndOwnerActivity(() => activities.Active--, activities.IsIdle);
    }

    private sealed class ActivityCounter
    {
        public int Active;

        public bool IsIdle() => Active == 0;
    }
}
