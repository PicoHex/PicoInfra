namespace PicoLog.Tests;

public class FileSinkRotationTests
{
    private static string GetTempFilePath() =>
        Path.Combine(Path.GetTempPath(), $"pico-rotate-{Guid.NewGuid():N}.log");

    [Test]
    public async Task RotationInterval_Zero_DoesNotRotate()
    {
        var filePath = GetTempFilePath();

        var sink = new FileSink(
            new ConsoleFormatter(),
            new FileSinkOptions { FilePath = filePath, RotationInterval = TimeSpan.Zero }
        );

        await sink.WriteAsync(
            new LogEntry
            {
                Timestamp = DateTimeOffset.UtcNow,
                Level = LogLevel.Info,
                Category = "Test",
                Message = "test message",
            }
        );

        // Don't dispose — just check the file was created
        await Assert.That(File.Exists(filePath)).IsTrue();

        // Cleanup: hard-delete after Dispose completes
        await sink.DisposeAsync();
        TryDelete(filePath);
    }

    [Test]
    public async Task FileSink_CreateWithRotationInterval_DoesNotThrow()
    {
        var filePath = GetTempFilePath();

        // Just constructing with RotationInterval should not throw
        var sink = new FileSink(
            new ConsoleFormatter(),
            new FileSinkOptions { FilePath = filePath, RotationInterval = TimeSpan.FromMinutes(30) }
        );

        await sink.DisposeAsync();
        TryDelete(filePath);
    }

    [Test]
    public async Task Rotation_AcrossRestart_DoesNotOverwritePreviousRunFiles()
    {
        var filePath = GetTempFilePath();
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var directory = Path.GetDirectoryName(filePath)!;

        var options = new FileSinkOptions
        {
            FilePath = filePath,
            MaxFileSizeBytes = 32, // every formatted line exceeds this -> rotate on each message
            BatchSize = 1, // one message per batch -> one rotation per message
            MaxRetainedFiles = 0, // keep all rotated files (default)
        };

        // First process run.
        var firstRun = new FileSink(new ConsoleFormatter(), options);
        await firstRun.WriteAsync(CreateEntry("run-1-A"));
        await firstRun.WriteAsync(CreateEntry("run-1-B"));
        await firstRun.DisposeAsync();

        // Second process run — simulates a restart of the application.
        var secondRun = new FileSink(new ConsoleFormatter(), options);
        await secondRun.WriteAsync(CreateEntry("run-2-C"));
        await secondRun.WriteAsync(CreateEntry("run-2-D"));
        await secondRun.DisposeAsync();

        // Every message from both runs must survive on disk. The bug clobbers
        // the previous run's app.1.log / app.2.log on the first rotations of
        // the new process, silently destroying run-1 data.
        var files = Directory.GetFiles(directory, $"{fileName}.*").ToList();
        var allText = string.Join("\n", files.Select(File.ReadAllText));

        await Assert.That(allText).Contains("run-1-A");
        await Assert.That(allText).Contains("run-1-B");
        await Assert.That(allText).Contains("run-2-C");
        await Assert.That(allText).Contains("run-2-D");

        foreach (var file in files)
            TryDelete(file);
    }

    private static LogEntry CreateEntry(string message) =>
        new()
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level = LogLevel.Info,
            Category = "Test",
            Message = message,
        };

    [Test]
    public async Task Rotation_Collision_RecordsSinkFailure()
    {
        var filePath = GetTempFilePath();
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var directory = Path.GetDirectoryName(filePath)!;

        var options = new FileSinkOptions
        {
            FilePath = filePath,
            MaxFileSizeBytes = 32, // every formatted line exceeds this -> rotate on each message
            BatchSize = 1,
        };

        using var listener = new MeterListener();
        var failures = new ConcurrentQueue<long>();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (
                instrument.Meter.Name == PicoLogMetrics.MeterName
                && instrument.Name == PicoLogMetrics.SinkFailuresName
            )
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>(
            (instrument, measurement, _, _) => failures.Enqueue(measurement)
        );
        listener.Start();

        var sink = new FileSink(new ConsoleFormatter(), options);

        // Occupy the file that the first rotation will target so that
        // File.Move(base, target, overwrite:false) collides. This simulates a
        // concurrent process creating the rotated file after seeding.
        File.WriteAllText(Path.Combine(directory, $"{fileName}.1.log"), "occupied");

        await sink.WriteAsync(CreateEntry("collision-trigger"));

        // The processing exception surfaces at dispose; swallow it here — the
        // point of the test is that the collision is observable via telemetry.
        try
        {
            await sink.DisposeAsync();
        }
        catch
        {
            // expected: processing failure rethrown at shutdown
        }

        await Assert.That(failures.IsEmpty).IsFalse();

        foreach (var f in Directory.GetFiles(directory, $"{fileName}.*"))
            TryDelete(f);
    }

    [Test]
    public async Task Rotation_Cleanup_DoesNotDeleteForeignFilesMatchingTheGlob()
    {
        // Retention cleanup must only delete files the sink itself produced
        // ({name}.{n}{ext}, n >= 1). A foreign "app.error.log" matches the
        // "{name}.*{ext}" glob and used to parse as index 0 — so it sorted
        // first and was deleted before any real rotated file.
        var directory = Path.Combine(Path.GetTempPath(), $"pico-rotate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, "app.log");
        var foreignPath = Path.Combine(directory, "app.error.log");
        File.WriteAllText(foreignPath, "foreign file — must survive retention cleanup");

        var sink = new FileSink(
            new ConsoleFormatter(),
            new FileSinkOptions
            {
                FilePath = filePath,
                MaxFileSizeBytes = 32, // every formatted line exceeds this -> rotates
                MaxRetainedFiles = 1,
            }
        );

        for (var i = 0; i < 4; i++)
        {
            await sink.WriteAsync(CreateEntry($"rotation-{i}"));
            await sink.FlushAsync(); // rotation runs at the end of every batch
        }
        await sink.DisposeAsync();

        await Assert.That(File.Exists(foreignPath)).IsTrue();

        var rotated = Directory
            .GetFiles(directory, "app.*.log")
            .Where(f => !string.Equals(f, foreignPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        await Assert.That(rotated.Count).IsEqualTo(1); // MaxRetainedFiles = 1

        foreach (var f in Directory.GetFiles(directory))
            TryDelete(f);
        try
        {
            Directory.Delete(directory);
        }
        catch
        { /* best-effort cleanup */
        }
    }

    [Test]
    public async Task ConcurrentWritesAndFlushes_ProduceEveryLine()
    {
        // The sink participates in the flush protocol: concurrent writers and
        // flushes must neither throw nor lose/corrupt lines (the processing task
        // and FlushAsync share the non-thread-safe StreamWriter).
        var filePath = GetTempFilePath();
        var sink = new FileSink(
            new ConsoleFormatter(),
            new FileSinkOptions { FilePath = filePath, BatchSize = 8 }
        );

        const int writerCount = 4;
        const int perWriter = 100;
        const int total = writerCount * perWriter;

        var writers = Enumerable
            .Range(0, writerCount)
            .Select(w =>
                Task.Run(async () =>
                {
                    for (var i = 0; i < perWriter; i++)
                    {
                        await sink.WriteAsync(CreateEntry($"w{w}-{i}"));
                        if (i % 25 == 0)
                            await sink.FlushAsync();
                    }
                })
            )
            .ToArray();
        var flusher = Task.Run(async () =>
        {
            for (var i = 0; i < 40; i++)
            {
                await sink.FlushAsync();
                await Task.Yield();
            }
        });

        await Task.WhenAll(writers);
        await flusher;
        await sink.FlushAsync();
        await sink.DisposeAsync();

        var lines = await File.ReadAllLinesAsync(filePath);
        await Assert.That(lines.Length).IsEqualTo(total);

        // every expected message appears exactly once (the formatter appends the
        // message after the last "] ")
        var actual = lines
            .Select(l => l[(l.LastIndexOf(']') + 2)..])
            .OrderBy(m => m, StringComparer.Ordinal)
            .ToList();
        var expected = Enumerable
            .Range(0, writerCount)
            .SelectMany(w => Enumerable.Range(0, perWriter).Select(i => $"w{w}-{i}"))
            .OrderBy(m => m, StringComparer.Ordinal)
            .ToList();
        await Assert.That(string.Join('|', actual)).IsEqualTo(string.Join('|', expected));

        TryDelete(filePath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        { /* best-effort cleanup */
        }
    }
}
