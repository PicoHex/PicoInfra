using System.Collections.Concurrent;

namespace PicoLog.Json.Tests;

public class JsonLogSinkRaceTests
{
    /// <summary>
    /// After disposal, WriteAsync must throw ObjectDisposedException — not
    /// NullReferenceException and not silently recreate the writer.
    /// Regression test for: DisposeAsync nulls _writer; subsequent WriteAsync
    /// must detect disposed state rather than accessing _writer.
    /// </summary>
    [Test]
    public async Task WriteAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var tempFile = Path.GetTempFileName();
        var sink = new JsonLogSink(tempFile);

        // Pre-initialize writer
        await sink.WriteAsync(
            new LogEntry
            {
                Timestamp = DateTimeOffset.UtcNow,
                Level = LogLevel.Debug,
                Category = "Init",
                Message = "Pre-init",
            }
        );

        await sink.DisposeAsync();

        // After clean dispose, WriteAsync must throw ObjectDisposedException
        var hasNre = false;
        var hasOde = false;

        try
        {
            await sink.WriteAsync(
                new LogEntry
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    Level = LogLevel.Info,
                    Category = "Test",
                    Message = "After dispose",
                }
            );
        }
        catch (NullReferenceException)
        {
            hasNre = true;
        }
        catch (ObjectDisposedException)
        {
            hasOde = true;
        }

        await Assert
            .That(hasNre)
            .IsFalse()
            .Because("NullReferenceException after dispose means the disposed check is missing");
        await Assert
            .That(hasOde)
            .IsTrue()
            .Because("after dispose, WriteAsync must throw ObjectDisposedException");

        // Cleanup
        if (File.Exists(tempFile))
            File.Delete(tempFile);
    }

    /// <summary>
    /// Concurrent writes without dispose — baseline sanity check.
    /// Genuinely concurrent: every write is started on its own thread-pool thread BEFORE
    /// anything is awaited. The previous version handed the lazy <c>Select</c> straight to
    /// <c>Task.WhenAll</c>, which enumerates it and awaits each already-completed task in
    /// turn — and <c>JsonLogSink.WriteAsync</c> has no <c>await</c>, so no two writes ever
    /// overlapped and the test could not observe a race.
    /// </summary>
    [Test]
    public async Task WriteAsync_MultipleConcurrentWrites_AllSucceed()
    {
        var tempFile = Path.GetTempFileName();
        var sink = new JsonLogSink(tempFile);

        var writeTasks = Enumerable
            .Range(0, 8)
            .Select(i =>
                Task.Run(() =>
                    sink.WriteAsync(
                        new LogEntry
                        {
                            Timestamp = DateTimeOffset.UtcNow,
                            Level = LogLevel.Debug,
                            Message = $"Concurrent write {i}",
                            Category = "Baseline",
                        }
                    )
                )
            )
            .ToArray();

        await Task.WhenAll(writeTasks);
        await sink.DisposeAsync();

        var lines = await File.ReadAllLinesAsync(tempFile);
        await Assert.That(lines.Length).IsEqualTo(8);

        // All eight writes must have landed — not eight copies of whichever one won.
        for (var i = 0; i < 8; i++)
        {
            var expected = $"Concurrent write {i}";
            await Assert.That(lines.Any(line => line.Contains(expected))).IsTrue();
        }

        if (File.Exists(tempFile))
            File.Delete(tempFile);
    }

    /// <summary>
    /// High-frequency concurrent writes to stress-test lock contention.
    /// </summary>
    [Test]
    public async Task WriteAsync_ConcurrentBurst_NoExceptions()
    {
        var tempFile = Path.GetTempFileName();
        var sink = new JsonLogSink(tempFile);
        var exceptions = new ConcurrentQueue<Exception>();

        // Burst of concurrent writes without dispose
        var tasks = Enumerable
            .Range(0, 20)
            .Select(threadId =>
                Task.Run(async () =>
                {
                    try
                    {
                        for (int i = 0; i < 20; i++)
                        {
                            await sink.WriteAsync(
                                new LogEntry
                                {
                                    Timestamp = DateTimeOffset.UtcNow,
                                    Level = LogLevel.Info,
                                    Message = $"Thread {threadId} msg {i}",
                                    Category = "Burst",
                                }
                            );
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Enqueue(ex);
                    }
                })
            );

        await Task.WhenAll(tasks);
        await sink.DisposeAsync();

        await Assert
            .That(exceptions)
            .IsEmpty()
            .Because("all concurrent writes must succeed without exceptions");

        var lines = await File.ReadAllLinesAsync(tempFile);
        await Assert.That(lines.Length).IsEqualTo(400);

        if (File.Exists(tempFile))
            File.Delete(tempFile);
    }

    /// <summary>
    /// Verifies double DisposeAsync is idempotent.
    /// </summary>
    [Test]
    public async Task DisposeAsync_MultipleCalls_Idempotent()
    {
        var tempFile = Path.GetTempFileName();
        var sink = new JsonLogSink(tempFile);

        await sink.WriteAsync(
            new LogEntry
            {
                Timestamp = DateTimeOffset.UtcNow,
                Level = LogLevel.Debug,
                Category = "Test",
                Message = "Before first dispose",
            }
        );

        await sink.DisposeAsync();
        await sink.DisposeAsync(); // Second call must be safe

        if (File.Exists(tempFile))
            File.Delete(tempFile);
    }
}
