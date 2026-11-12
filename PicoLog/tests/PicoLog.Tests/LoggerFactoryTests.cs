namespace PicoLog.Tests;

public sealed class LoggerFactoryTests
{
    [Test]
    public async Task FileSink_DoesNotThrowWhenWritesRaceWithDisposeAsync()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"pico-logger-{Guid.NewGuid():N}.log");
        await using var sink = new FileSink(
            new ConsoleFormatter(),
            new FileSinkOptions
            {
                FilePath = filePath,
                BatchSize = 8,
                AllowFlushInterrupt = true,
            }
        );

        var writeTasks = Enumerable
            .Range(0, 64)
            .Select(index =>
                sink.WriteAsync(
                    new LogEntry
                    {
                        Timestamp = DateTimeOffset.UtcNow,
                        Level = LogLevel.Info,
                        Category = nameof(LoggerFactoryTests),
                        Message = $"message-{index}",
                    }
                )
            )
            .ToArray();

        await sink.DisposeAsync();
        await Task.WhenAll(writeTasks);

        if (File.Exists(filePath))
            File.Delete(filePath);
    }

    [Test]
    public async Task FileSink_DisposeAsync_FlushesExistingContent_And_IgnoresLaterWrites()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"pico-logger-sync-{Guid.NewGuid():N}.log");

        try
        {
            var sink = new FileSink(new ConsoleFormatter(), filePath);

            await sink.WriteAsync(
                new LogEntry
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    Level = LogLevel.Info,
                    Category = nameof(LoggerFactoryTests),
                    Message = "before-dispose",
                }
            );

            await sink.DisposeAsync();

            await sink.WriteAsync(
                new LogEntry
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    Level = LogLevel.Warning,
                    Category = nameof(LoggerFactoryTests),
                    Message = "after-dispose",
                }
            );

            var contents = await File.ReadAllTextAsync(filePath);
            await Assert.That(contents).Contains("before-dispose");
            await Assert.That(contents.Contains("after-dispose")).IsFalse();
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }

    [Test]
    public async Task FileSink_BatchesQueuedMessages_And_PersistsAllContent()
    {
        var filePath = Path.Combine(
            Path.GetTempPath(),
            $"pico-logger-batch-{Guid.NewGuid():N}.log"
        );

        try
        {
            await using var sink = new FileSink(
                new ConsoleFormatter(),
                new FileSinkOptions
                {
                    FilePath = filePath,
                    BatchSize = 16,
                    AllowFlushInterrupt = true,
                }
            );

            var writes = Enumerable
                .Range(0, 40)
                .Select(index =>
                    sink.WriteAsync(
                        new LogEntry
                        {
                            Timestamp = DateTimeOffset.UtcNow,
                            Level = LogLevel.Info,
                            Category = nameof(LoggerFactoryTests),
                            Message = $"batch-{index}",
                        }
                    )
                );

            await Task.WhenAll(writes);
            await sink.DisposeAsync();

            var contents = await File.ReadAllTextAsync(filePath);

            for (var index = 0; index < 40; index++)
                await Assert.That(contents).Contains($"batch-{index}");
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }

    [Test]
    public async Task CreateLogger_CachesLoggerPerCategory()
    {
        var sink = new CollectingSink();
        await using var factory = new LoggerFactory([sink]);

        var first = factory.CreateLogger("Tests.Category");
        var second = factory.CreateLogger("Tests.Category");

        await Assert.That(first).IsSameReferenceAs(second);
    }

    [Test]
    public async Task CreateLogger_ReturnsLogger_ThatIsNotDisposable()
    {
        var sink = new CollectingSink();
        await using var factory = new LoggerFactory([sink]);

        var logger = factory.CreateLogger("Tests.Category");

        await Assert.That(logger is IDisposable).IsFalse();
        await Assert.That(logger is IAsyncDisposable).IsFalse();
    }

    [Test]
    public async Task CreateLogger_PreservesStructuredProperties_ThroughNativeILoggerOverload()
    {
        var sink = new CollectingSink();
        await using var factory = new LoggerFactory([sink]);

        var logger = factory.CreateLogger("Tests.Category");
        IReadOnlyList<KeyValuePair<string, object?>> properties =
        [
            new("tenant", "alpha"),
            new("attempt", 3),
        ];

        logger.Log(LogLevel.Warning, "structured-runtime-instance", properties, exception: null);

        await factory.DisposeAsync();

        var entry = sink.Entries.Single();
        await Assert.That(entry.Properties).IsNotNull();
        await Assert.That(entry.Properties!).Count().IsEqualTo(2);
        await Assert.That(entry.Properties[0].Key).IsEqualTo("tenant");
        await Assert.That(entry.Properties[0].Value).IsEqualTo("alpha");
        await Assert.That(entry.Properties[1].Key).IsEqualTo("attempt");
        await Assert.That(entry.Properties[1].Value).IsEqualTo(3);
    }

    [Test]
    public async Task CreateLogger_ConcurrentSameCategory_ReturnsSharedLogger_And_CreatesOnePipeline()
    {
        var sink = new CollectingSink();
        var factory = new LoggerFactory([sink]);

        try
        {
            const int callerCount = 128;
            const string categoryName = "Tests.Concurrent.Category";
            using var startGate = new ManualResetEventSlim(initialState: false);
            var createTasks = Enumerable
                .Range(0, callerCount)
                .Select(_ =>
                    Task.Run(() =>
                    {
                        startGate.Wait();
                        return factory.CreateLogger(categoryName);
                    })
                )
                .ToArray();

            startGate.Set();

            var loggers = await Task.WhenAll(createTasks);
            var firstLogger = loggers[0];

            await Assert.That(loggers).Count().IsEqualTo(callerCount);

            foreach (var logger in loggers.Skip(1))
                await Assert.That(logger).IsSameReferenceAs(firstLogger);

            await Assert.That(GetFactoryRegistrationCount(factory)).IsEqualTo(1);
            await Assert.That(GetQueueDepthProviderCount(factory)).IsEqualTo(1);

            await factory.DisposeAsync();

            await Assert.That(GetQueueDepthProviderCount(factory)).IsEqualTo(0);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [Test]
    public async Task TypedLogger_DelegatesSyncAsyncLogs_And_Scopes()
    {
        var sink = new CollectingSink();
        await using var factory = new LoggerFactory([sink]);
        ILogger<LoggerConsumer> logger = new Logger<LoggerConsumer>(factory);

        using (logger.BeginScope("typed-scope"))
        {
            logger.Warning("typed-sync");
            await logger.CriticalAsync("typed-async", new InvalidOperationException("typed-boom"));
        }

        await factory.DisposeAsync();

        var entries = sink.Entries.ToArray();
        await Assert.That(entries).Count().IsEqualTo(2);
        await Assert.That(entries[0].Category).IsEqualTo(typeof(LoggerConsumer).FullName!);
        await Assert.That(entries[0].Message).IsEqualTo("typed-sync");
        await Assert.That(entries[0].Level).IsEqualTo(LogLevel.Warning);
        await Assert.That(entries[1].Category).IsEqualTo(typeof(LoggerConsumer).FullName!);
        await Assert.That(entries[1].Message).IsEqualTo("typed-async");
        await Assert.That(entries[1].Level).IsEqualTo(LogLevel.Critical);
        await Assert.That(entries[1].Exception is InvalidOperationException).IsTrue();

        var scopes = (entries[1].Scopes ?? [])
            .Select(scope => scope.ToString() ?? string.Empty)
            .ToArray();
        await Assert.That(scopes).IsEquivalentTo(["typed-scope"]);
    }

    [Test]
    public async Task TypedLogger_StructuredExtensions_PreserveProperties_ThroughSharedDispatchPath()
    {
        var sink = new CollectingSink();
        await using var factory = new LoggerFactory([sink]);
        ILogger<LoggerConsumer> logger = new Logger<LoggerConsumer>(factory);
        IReadOnlyList<KeyValuePair<string, object?>> properties =
        [
            new("tenant", "alpha"),
            new("attempt", 3),
        ];

        logger.Log(LogLevel.Warning, "typed-sync-structured", properties, exception: null);
        await logger.LogAsync(
            LogLevel.Error,
            "typed-async-structured",
            properties,
            exception: null
        );
        await factory.DisposeAsync();

        var entries = sink.Entries.ToArray();
        var firstProperties = entries[0].Properties;
        var secondProperties = entries[1].Properties;
        await Assert.That(entries).Count().IsEqualTo(2);
        await Assert.That(entries[0].Category).IsEqualTo(typeof(LoggerConsumer).FullName!);
        await Assert.That(entries[0].Message).IsEqualTo("typed-sync-structured");
        await Assert.That(firstProperties).IsNotNull();
        await Assert.That(firstProperties!).Count().IsEqualTo(2);
        await Assert.That(firstProperties[0].Key).IsEqualTo("tenant");
        await Assert.That(firstProperties[0].Value).IsEqualTo("alpha");
        await Assert.That(entries[1].Category).IsEqualTo(typeof(LoggerConsumer).FullName!);
        await Assert.That(entries[1].Message).IsEqualTo("typed-async-structured");
        await Assert.That(secondProperties).IsNotNull();
        await Assert.That(secondProperties!).Count().IsEqualTo(2);
        await Assert.That(secondProperties[1].Key).IsEqualTo("attempt");
        await Assert.That(secondProperties[1].Value).IsEqualTo(3);
    }

    [Test]
    public async Task StructuredLogger_PreservesProperties_And_FormatterRendersThem()
    {
        var sink = new CollectingSink();
        await using var factory = new LoggerFactory([sink]);
        var logger = factory.CreateLogger("Tests.Category");
        IReadOnlyList<KeyValuePair<string, object?>> properties =
        [
            new("tenant", "alpha"),
            new("attempt", 3),
            new("nullable", null),
        ];

        logger.Log(LogLevel.Warning, "structured-message", properties, exception: null);
        await factory.DisposeAsync();

        var entry = sink.Entries.Single();
        await Assert.That(entry.Properties).IsNotNull();
        await Assert.That(entry.Properties!).Count().IsEqualTo(3);
        await Assert.That(entry.Properties![0].Key).IsEqualTo("tenant");
        await Assert.That(entry.Properties[0].Value).IsEqualTo("alpha");
        await Assert.That(entry.Properties[1].Key).IsEqualTo("attempt");
        await Assert.That(entry.Properties[1].Value).IsEqualTo(3);
        await Assert.That(entry.Properties[2].Key).IsEqualTo("nullable");
        await Assert.That(entry.Properties[2].Value is null).IsTrue();

        var rendered = new ConsoleFormatter().Format(entry);
        await Assert.That(rendered).Contains("structured-message");
        await Assert.That(rendered).Contains("{tenant=\"alpha\", attempt=3, nullable=null}");
    }

    [Test]
    public async Task ConsoleFormatter_RendersExpectedOutput_WithEscapedProperties_Exception_And_Scopes()
    {
        var formatter = new ConsoleFormatter();
        var entry = new LogEntry
        {
            Timestamp = new DateTimeOffset(2026, 4, 13, 8, 9, 10, 123, TimeSpan.Zero),
            Level = LogLevel.Warning,
            Category = "Tests.Category",
            Message = "hello",
            Exception = new InvalidOperationException("boom"),
            Properties =
            [
                new("quote", "a\"b"),
                new("line", "x\ny"),
                new("tab", "\t"),
                new("slash", "\\"),
                new("letter", 'z'),
                new("number", 3),
                new("nullable", null),
            ],
            Scopes = ["outer", "inner"],
        };

        var rendered = formatter.Format(entry);
        var expected =
            "[2026-04-13 08:09:10.123] WARNING   [Tests.Category] hello {quote=\"a\\\"b\", line=\"x\\ny\", tab=\"\\t\", slash=\"\\\\\", letter=\"z\", number=3, nullable=null}"
            + Environment.NewLine
            + "EXCEPTION: System.InvalidOperationException: boom"
            + Environment.NewLine
            + "SCOPES: [outer => inner]";

        await Assert.That(rendered).IsEqualTo(expected);
    }

    [Test]
    public async Task StructuredLogger_CopiesProperties_On_EntryCreation()
    {
        var sink = new CollectingSink();
        await using var factory = new LoggerFactory([sink]);
        var logger = factory.CreateLogger("Tests.Category");
        var properties = new List<KeyValuePair<string, object?>> { new("tenant", "alpha") };

        logger.Log(LogLevel.Info, "snapshot", properties, exception: null);
        properties[0] = new KeyValuePair<string, object?>("tenant", "mutated");
        properties.Add(new KeyValuePair<string, object?>("attempt", 2));

        await factory.DisposeAsync();

        var entry = sink.Entries.Single();
        await Assert.That(entry.Properties).IsNotNull();
        await Assert.That(entry.Properties!).Count().IsEqualTo(1);
        await Assert.That(entry.Properties[0].Key).IsEqualTo("tenant");
        await Assert.That(entry.Properties[0].Value).IsEqualTo("alpha");
    }

    [Test]
    public async Task StructuredLogger_CopiesArrayProperties_On_EntryCreation()
    {
        var sink = new CollectingSink();
        await using var factory = new LoggerFactory([sink]);
        var logger = factory.CreateLogger("Tests.Category");
        KeyValuePair<string, object?>[] properties = [new("tenant", "alpha")];

        logger.Log(LogLevel.Info, "snapshot", properties, exception: null);
        properties[0] = new KeyValuePair<string, object?>("tenant", "mutated");

        await factory.DisposeAsync();

        var entry = sink.Entries.Single();
        await Assert.That(entry.Properties).IsNotNull();
        await Assert.That(entry.Properties!).Count().IsEqualTo(1);
        await Assert.That(entry.Properties[0].Key).IsEqualTo("tenant");
        await Assert.That(entry.Properties[0].Value).IsEqualTo("alpha");
    }

    [Test]
    public async Task StructuredLogger_EmptyProperties_RemainNull()
    {
        var sink = new CollectingSink();
        await using var factory = new LoggerFactory([sink]);
        var logger = factory.CreateLogger("Tests.Category");
        KeyValuePair<string, object?>[] properties = [];

        logger.Log(LogLevel.Info, "empty", properties, exception: null);
        await factory.DisposeAsync();

        var entry = sink.Entries.Single();
        await Assert.That(entry.Properties is null).IsTrue();
    }

    [Test]
    public async Task DisposeAsync_FlushesQueuedEntriesAndScopes()
    {
        var sink = new CollectingSink();
        await using var factory = new LoggerFactory([sink]);
        var logger = factory.CreateLogger("Tests.Category");

        using (logger.BeginScope("outer"))
        using (logger.BeginScope("inner"))
        {
            logger.Info("sync-message");
            await logger.ErrorAsync("async-message", new InvalidOperationException("boom"));
        }

        await factory.DisposeAsync();

        var entries = sink.Entries.ToArray();
        var firstScopes = (entries[0].Scopes ?? [])
            .Select(scope => scope.ToString() ?? string.Empty)
            .ToArray();
        var secondScopes = (entries[1].Scopes ?? [])
            .Select(scope => scope.ToString() ?? string.Empty)
            .ToArray();

        await Assert.That(entries).Count().IsEqualTo(2);
        await Assert.That(entries[0].Level).IsEqualTo(LogLevel.Info);
        await Assert.That(entries[0].Message).IsEqualTo("sync-message");
        await Assert.That(firstScopes).IsEquivalentTo(["outer", "inner"]);
        await Assert.That(entries[1].Level).IsEqualTo(LogLevel.Error);
        await Assert.That(entries[1].Message).IsEqualTo("async-message");
        await Assert.That(entries[1].Exception is InvalidOperationException).IsTrue();
        await Assert.That(secondScopes).IsEquivalentTo(["outer", "inner"]);
    }

    [Test]
    public async Task BeginScope_FlowsAcrossCategoriesWithinFactory()
    {
        var sink = new CollectingSink();
        await using var factory = new LoggerFactory([sink]);
        var outerLogger = factory.CreateLogger("Tests.Outer");
        var innerLogger = factory.CreateLogger("Tests.Inner");

        using (outerLogger.BeginScope("shared-scope"))
            innerLogger.Warning("other-category");

        await factory.DisposeAsync();

        var entry = sink.Entries.Single();
        var scopes = (entry.Scopes ?? [])
            .Select(scope => scope.ToString() ?? string.Empty)
            .ToArray();

        await Assert.That(entry.Category).IsEqualTo("Tests.Inner");
        await Assert.That(scopes).IsEquivalentTo(["shared-scope"]);
    }

    [Test]
    public async Task BeginScope_ReturnsILogScope_WithOriginalState()
    {
        var sink = new CollectingSink();
        await using var factory = new LoggerFactory([sink]);
        var logger = factory.CreateLogger("Tests.Category");

        using var scope = logger.BeginScope("inspectable-scope");

        await Assert.That(scope is ILogScope).IsTrue();
        await Assert.That(((ILogScope)scope).State).IsEqualTo("inspectable-scope");
    }

    [Test]
    public async Task BeginScope_DoesNotResurrectDisposedOuterScope_WhenDisposedOutOfOrder()
    {
        var sink = new CollectingSink();
        await using var factory = new LoggerFactory([sink]);
        var logger = factory.CreateLogger("Tests.Category");

        var outer = logger.BeginScope("outer");
        var inner = logger.BeginScope("inner");

        outer.Dispose();
        inner.Dispose();

        logger.Info("after-out-of-order-dispose");
        await factory.DisposeAsync();

        var entry = sink.Entries.Single();
        await Assert.That(entry.Message).IsEqualTo("after-out-of-order-dispose");
        await Assert.That(entry.Scopes ?? []).Count().IsEqualTo(0);
    }

    [Test]
    public async Task BeginScope_CapturesDisposedOuterScope_WhileInnerIsStillActive()
    {
        var sink = new CollectingSink();
        await using var factory = new LoggerFactory([sink]);
        var logger = factory.CreateLogger("Tests.Category");

        var outer = logger.BeginScope("outer");
        using var inner = logger.BeginScope("inner");

        outer.Dispose();
        logger.Info("while-inner-still-active");
        await factory.DisposeAsync();

        var entry = sink.Entries.Single();
        var scopes = (entry.Scopes ?? [])
            .Select(scope => scope.ToString() ?? string.Empty)
            .ToArray();

        await Assert.That(scopes).IsEquivalentTo(["outer", "inner"]);
    }

    [Test]
    public async Task DisposeAsync_FlushesAsyncTailMessages()
    {
        var sink = new CollectingSink();
        await using var factory = new LoggerFactory([sink]);
        var logger = factory.CreateLogger("Tests.Category");

        await logger.DebugAsync("tail-debug");
        await logger.NoticeAsync("tail-notice");
        await logger.InfoAsync("tail-info");

        await factory.DisposeAsync();

        var messages = sink
            .Entries.Select(entry => entry.Message?.ToString() ?? string.Empty)
            .ToArray();

        await Assert.That(messages).IsEquivalentTo(["tail-debug", "tail-notice", "tail-info"]);
    }

    [Test]
    public async Task MinLevel_FiltersMessagesBelowThreshold()
    {
        var sink = new CollectingSink();
        await using var factory = new LoggerFactory([sink]) { MinLevel = LogLevel.Warning };
        var logger = factory.CreateLogger("Tests.Category");

        logger.Info("ignored");
        await logger.ErrorAsync("recorded");

        await WaitForEntriesAsync(sink, 1);

        var entries = sink.Entries.ToArray();
        await Assert.That(entries).Count().IsEqualTo(1);
        await Assert.That(entries[0].Level).IsEqualTo(LogLevel.Error);
        await Assert.That(entries[0].Message).IsEqualTo("recorded");
    }

    [Test]
    public async Task AlreadyCreatedLogger_ObservesLaterMinLevelChanges()
    {
        var sink = new CollectingSink();
        await using var factory = new LoggerFactory([sink]) { MinLevel = LogLevel.Warning };
        var logger = factory.CreateLogger("Tests.Category");

        logger.Info("ignored-before-change");
        factory.MinLevel = LogLevel.Info;
        logger.Info("recorded-after-lowering-threshold");
        factory.MinLevel = LogLevel.Error;
        logger.Warning("ignored-after-tightening-threshold");
        await logger.ErrorAsync("recorded-after-tightening-threshold");

        await factory.DisposeAsync();

        var entries = sink.Entries.ToArray();
        await Assert.That(entries).Count().IsEqualTo(2);
        await Assert.That(entries[0].Level).IsEqualTo(LogLevel.Info);
        await Assert.That(entries[0].Message).IsEqualTo("recorded-after-lowering-threshold");
        await Assert.That(entries[1].Level).IsEqualTo(LogLevel.Error);
        await Assert.That(entries[1].Message).IsEqualTo("recorded-after-tightening-threshold");
    }

    [Test]
    public async Task DropWrite_Mode_ReportsDroppedMessages_WhenQueueIsFull()
    {
        long reportedDropCount = 0;
        var sink = new BlockingSink();
        var options = new LoggerFactoryOptions
        {
            QueueCapacity = 1,
            QueueFullMode = LogQueueFullMode.DropWrite,
            OnMessagesDropped = (_, droppedCount) => reportedDropCount = droppedCount,
        };
        await using var factory = new LoggerFactory([sink], options);
        var logger = factory.CreateLogger("Tests.Category");

        logger.Info("first");
        await sink.WriteStarted;
        logger.Info("second");
        logger.Info("third");

        sink.Release();
        await factory.DisposeAsync();

        await Assert.That(reportedDropCount).IsEqualTo(1);
        await Assert.That(sink.WrittenCount).IsEqualTo(2);
    }

    [Test]
    public async Task DropWrite_Mode_AsyncWrites_ReportDroppedMessages_WhenQueueIsFull()
    {
        long reportedDropCount = 0;
        var sink = new BlockingSink();
        var options = new LoggerFactoryOptions
        {
            QueueCapacity = 1,
            QueueFullMode = LogQueueFullMode.DropWrite,
            OnMessagesDropped = (_, droppedCount) => reportedDropCount = droppedCount,
        };
        await using var factory = new LoggerFactory([sink], options);
        var logger = factory.CreateLogger("Tests.Category");

        await logger.InfoAsync("first");
        await sink.WriteStarted;
        await logger.InfoAsync("second");
        await logger.InfoAsync("third");

        sink.Release();
        await factory.DisposeAsync();

        await Assert.That(reportedDropCount).IsEqualTo(1);
        await Assert.That(sink.WrittenCount).IsEqualTo(2);
    }

    [Test]
    public async Task DropOldest_Mode_ReportsDroppedMessages_WhenQueueIsFull()
    {
        long reportedDropCount = 0;
        var sink = new BlockingSink();
        var options = new LoggerFactoryOptions
        {
            QueueCapacity = 1,
            QueueFullMode = LogQueueFullMode.DropOldest,
            OnMessagesDropped = (_, droppedCount) => reportedDropCount = droppedCount,
        };
        await using var factory = new LoggerFactory([sink], options);
        var logger = factory.CreateLogger("Tests.Category");

        logger.Info("first");
        await sink.WriteStarted;
        logger.Info("second");
        logger.Info("third");

        sink.Release();
        await factory.DisposeAsync();

        await Assert.That(reportedDropCount).IsEqualTo(1);
        await Assert.That(sink.WrittenCount).IsEqualTo(2);
    }

    [Test]
    public async Task DropOldest_Mode_AsyncWrites_ReportDroppedMessages_WhenQueueIsFull()
    {
        long reportedDropCount = 0;
        var sink = new BlockingSink();
        var options = new LoggerFactoryOptions
        {
            QueueCapacity = 1,
            QueueFullMode = LogQueueFullMode.DropOldest,
            OnMessagesDropped = (_, droppedCount) => reportedDropCount = droppedCount,
        };
        await using var factory = new LoggerFactory([sink], options);
        var logger = factory.CreateLogger("Tests.Category");

        await logger.InfoAsync("first");
        await sink.WriteStarted;
        await logger.InfoAsync("second");
        await logger.InfoAsync("third");

        sink.Release();
        await factory.DisposeAsync();

        await Assert.That(reportedDropCount).IsEqualTo(1);
        await Assert.That(sink.WrittenCount).IsEqualTo(2);
    }

    [Test]
    public async Task Wait_Mode_BlocksSyncWrites_UntilQueueSpaceIsAvailable()
    {
        var sink = new ThreadGateSink();
        long reportedDropCount = 0;
        // Room in the queue appears only when the pipeline finishes the write that is in flight, so
        // this test owns the thread that write is parked on (ThreadGateSink blocks its caller's own
        // thread on an OS gate and Release hands it straight back). With an awaiting sink, as this
        // test used to use, making room needed a ThreadPool thread to resume the pipeline's
        // continuation, which turned the interval into a scheduling quantity: measured 17-36 ms
        // idle and 1.0 s / 3.9 s / 8.8 s with the whole suite on a 4-vCPU view while 16 CPU burners
        // run. The last one outlives the 5 s SyncWriteTimeout the queued write is given, so the
        // write was dropped and the count below read 2 instead of 3. Nothing here asserts how long
        // anything took: the 300 ms window is a "must not happen yet" check and the 60 s one is a
        // deadlock guard. Both are sampled against Stopwatch, and the window is measured from the
        // writer's own enqueue attempt (it publishes a timestamp right before the call) rather than
        // from this thread: a window that opens before the writer is scheduled can expire before the
        // write was ever tried, and that vacuity was measured — with capacity+1 injected and a 400 ms
        // pre-sleep in the writer, the window-starts-here shape passed 8 runs out of 8 while the
        // queue had room the whole time.
        var options = new LoggerFactoryOptions
        {
            QueueCapacity = 1,
            QueueFullMode = LogQueueFullMode.Wait,
            SyncWriteTimeout = TimeSpan.FromSeconds(5),
            OnMessagesDropped = (_, droppedCount) => reportedDropCount = droppedCount,
        };
        await using var factory = new LoggerFactory([sink], options);
        var logger = factory.CreateLogger("Tests.Category");

        Thread? thirdWriter = null;
        // Carries the instant the writer is about to attempt its enqueue, published from the writer
        // thread: the observation window below is measured from there and from nowhere else.
        var thirdWriteAttempted = new TaskCompletionSource<long>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var thirdWriteReturned = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        try
        {
            logger.Info("first");
            await sink.WriteStarted; // ensure "first" is dequeued and blocking inside the sink
            logger.Info("second"); // now fills the capacity-1 queue

            // The third write gets its own thread rather than the pool: its backoff loop is an OS
            // sleep on the writing thread, and running it on the pool would make the pool part of
            // the object under test.
            thirdWriter = new Thread(() =>
            {
                try
                {
                    thirdWriteAttempted.TrySetResult(Stopwatch.GetTimestamp());
                    logger.Info("third");
                }
                finally
                {
                    thirdWriteReturned.TrySetResult();
                }
            })
            {
                IsBackground = true,
                Name = "PicoLog.Test.ThirdWriter",
            };
            thirdWriter.Start();

            await Assert
                .That(await CompletesWithinAsync(thirdWriteAttempted.Task, SyncWriteDeadlockGuard))
                .IsTrue()
                .Because(
                    "the third writer must reach its enqueue attempt; after "
                        + $"{SyncWriteDeadlockGuard.TotalSeconds:0} s it had not even tried to write, "
                        + "which is a stuck thread, not a slow one."
                );

            // Outcome assertion: a full Wait-mode queue must hold the writer. The window starts at
            // the instant the writer published, immediately before its enqueue attempt, so it
            // provably brackets that attempt: an admitted write has returned by the time the window
            // closes, and a held one has not. A window started on this thread instead would be
            // vacuous whenever the writer had not been scheduled yet — measured with capacity+1
            // injected and a 400 ms pre-sleep in the writer, that shape passed 8 runs out of 8.
            await Assert
                .That(
                    StaysIncomplete(
                        thirdWriteReturned.Task,
                        thirdWriteAttempted.Task.Result,
                        BlockedObservationWindow
                    )
                )
                .IsTrue()
                .Because(
                    "a full Wait-mode queue must block a sync write, not admit it past capacity and"
                        + " not drop it at once"
                );
            await Assert.That(sink.WrittenCount).IsEqualTo(1);
        }
        finally
        {
            sink.Release();
        }

        await Assert
            .That(await CompletesWithinAsync(thirdWriteReturned.Task, SyncWriteDeadlockGuard))
            .IsTrue()
            .Because(
                "the third write must return once the queue has room; it was still in its backoff"
                    + $" loop {SyncWriteDeadlockGuard.TotalSeconds:0} s later, which is a stuck"
                    + " writer, not a slow one."
            );
        thirdWriter.Join();
        await factory.DisposeAsync();

        // What the pipeline observed: every write arrived, none was dropped, and they arrived in the
        // order they were written — the blocked write was admitted, not replaced and not lost.
        await Assert.That(reportedDropCount).IsEqualTo(0);
        await Assert.That(sink.WrittenCount).IsEqualTo(3);
        await Assert
            .That(string.Join("|", sink.Entries.Select(static entry => entry.Message)))
            .IsEqualTo("first|second|third");
    }

    /// <summary>
    /// How long a sync write is observed before it may be admitted. Not a duration assertion: "has
    /// not happened yet" can only be checked over a window. The window is sampled against a
    /// monotonic clock and starts at the instant the writer publishes immediately before its
    /// enqueue attempt, so it brackets that attempt even when the writer thread is scheduled late —
    /// a starved ThreadPool can neither shorten the window, end it early, nor let it expire before
    /// the write was tried.
    /// </summary>
    private static readonly TimeSpan BlockedObservationWindow = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Deadlock guard for a sync write that must return — once its queue has room, or once shutdown
    /// has been observed. Never read a value near this bound as a timing measurement.
    /// </summary>
    private static readonly TimeSpan SyncWriteDeadlockGuard = TimeSpan.FromSeconds(60);

    /// <summary>
    /// What a sync writer that ignored the shutdown signal would sleep out in its backoff loop
    /// before giving up. Derived from the guard rather than picked by hand, and deliberately far
    /// above it: this is the separation that makes the guard in
    /// <see cref="Wait_Mode_SyncWrite_ReturnsPromptlyWhenShutdownStartsDuringBackoff"/> an outcome
    /// claim, because a slept-out backoff cannot land inside the guard — so "returned before the
    /// guard expired" and "returned because shutdown was observed" say the same thing.
    /// </summary>
    private static readonly TimeSpan BackoffSleepOutTimeout = SyncWriteDeadlockGuard * 5;

    private static bool StaysIncomplete(Task task, long sinceTimestamp, TimeSpan window)
    {
        while (Stopwatch.GetElapsedTime(sinceTimestamp) < window)
        {
            if (task.IsCompleted)
                return false;

            Thread.Sleep(1);
        }

        return !task.IsCompleted;
    }

    private static async Task<bool> CompletesWithinAsync(Task task, TimeSpan guard)
    {
        try
        {
            await task.WaitAsync(guard);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    [Test]
    public async Task Wait_Mode_SyncWrite_ReturnsPromptlyWhenShutdownStartsDuringBackoff()
    {
        // Regression test: TryEnqueueSyncWithWait used to ignore the channel's completed state and
        // keep sleeping in its backoff loop until the full SyncWriteTimeout elapsed, because
        // ChannelWriter.TryWrite returns false (rather than throwing ChannelClosedException) on a
        // completed channel. Under concurrent shutdown this pinned ThreadPool workers for the full
        // timeout each, intermittently tripping CI hang detection under coverage instrumentation on
        // linux-x64.
        //
        // Contract: once factory shutdown has been signalled, a sync writer already waiting in the
        // backoff loop returns — it does not sleep out the rest of SyncWriteTimeout.
        //
        // What is asserted is that outcome, never how long this thread took to notice it. Noticing
        // a completed TaskCompletionSource is itself a ThreadPool work item
        // (RunContinuationsAsynchronously hands the completion to the pool) and Task.WaitAsync's
        // timeout is a pool timer, so the 10 s WaitAsync this test used to end with raced the
        // writer's return against the pool scheduling the notification. Measured with all 199 tests
        // on a 4-vCPU view (DOTNET_PROCESSOR_COUNT=4) while CPU burners held every core at 100%:
        // in 18 loaded runs the writer returned 22-190 ms after the disposer thread was started,
        // i.e. within one backoff tick of the shutdown signal, while this thread observed that same
        // completion anywhere from 43 ms to 17.5 s after that same moment — and the 10 s bound
        // reddened one full-suite run in seven with the write itself long returned.
        //
        // So the return is observed by joining the writer's own dedicated thread — OS scheduling,
        // no pool — against the shared sync-write deadlock guard, and the guard is only allowed to
        // mean "stuck" because SyncWriteTimeout (what a writer that ignored the shutdown signal
        // would sleep out) is kept far outside it.
        var sink = new BlockingSink();
        long reportedDropCount = 0;
        var options = new LoggerFactoryOptions
        {
            QueueCapacity = 1,
            QueueFullMode = LogQueueFullMode.Wait,
            SyncWriteTimeout = BackoffSleepOutTimeout,
            // The drain has to stay parked on the sink for the whole observation: its own timeout
            // cancels the in-flight write, which would free the queue and let the writer return
            // through a different door. Drain timeouts are covered by
            // DisposeAsync_CancelsInFlightSinkWrites_WhenShutdownTimeoutExpires.
            ShutdownTimeout = TimeSpan.Zero,
            OnMessagesDropped = (_, droppedCount) => reportedDropCount = droppedCount,
        };
        var factory = new LoggerFactory([sink], options);
        var logger = factory.CreateLogger("Tests.Category");

        // Carries the instant the writer is about to attempt its enqueue, published from the writer
        // thread: the observation window below is measured from there and from nowhere else.
        var thirdWriteAttempted = new TaskCompletionSource<long>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var thirdWriteReturned = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        Thread? thirdWriteThread = null;
        Task? drain = null;

        try
        {
            // Drive the queue to the state where a third write must block:
            //   - "first" is dispatched and the sink parks it.
            //   - "second" fills the bounded queue (capacity 1).
            logger.Info("first");
            await sink.WriteStarted;
            logger.Info("second");

            // The sync write runs on a dedicated thread: its backoff loop is an OS sleep on the
            // writing thread, so running it on the pool would put the pool inside the object under
            // test.
            thirdWriteThread = new Thread(() =>
            {
                try
                {
                    thirdWriteAttempted.TrySetResult(Stopwatch.GetTimestamp());
                    logger.Info("third");
                }
                finally
                {
                    thirdWriteReturned.TrySetResult();
                }
            })
            {
                IsBackground = true,
                Name = "PicoLog.Test.ThirdWriter",
            };
            thirdWriteThread.Start();

            await Assert
                .That(await CompletesWithinAsync(thirdWriteAttempted.Task, SyncWriteDeadlockGuard))
                .IsTrue()
                .Because(
                    "the third writer must reach its enqueue attempt; after "
                        + $"{SyncWriteDeadlockGuard.TotalSeconds:0} s it had not even tried to write, "
                        + "which is a stuck thread, not a slow one."
                );

            // Outcome assertion: a full Wait-mode queue must hold the writer, stated as a fact
            // rather than as a delay long enough to hope for it. The window starts at the instant
            // the writer published, immediately before its enqueue attempt, so it brackets that
            // attempt even when the writer thread is scheduled late; a window started on this
            // thread could expire before the writer ever entered its backoff loop.
            await Assert
                .That(
                    StaysIncomplete(
                        thirdWriteReturned.Task,
                        thirdWriteAttempted.Task.Result,
                        BlockedObservationWindow
                    )
                )
                .IsTrue()
                .Because(
                    "a full Wait-mode queue must hold a sync write in its backoff loop: not admit"
                        + " it past capacity and not drop it early"
                );
            await Assert.That(sink.WrittenCount).IsEqualTo(1);

            // Signal shutdown. DisposeAsync completes the queue in its synchronous prefix, before
            // its first incomplete await, so the signal the writer has to observe has already been
            // sent by the time this returns to this thread.
            drain = factory.DisposeAsync().AsTask();

            // The assertion under test: the write returns. Joining the writer's own thread keeps
            // the observation off the pool, and the guard is shorter than the backoff a writer that
            // ignored shutdown would sleep out, so returning inside it means shutdown was observed.
            await Assert
                .That(thirdWriteThread.Join(SyncWriteDeadlockGuard))
                .IsTrue()
                .Because(
                    "the write must return once shutdown is signalled instead of sleeping out"
                        + $" SyncWriteTimeout; it was still in its backoff loop "
                        + $"{SyncWriteDeadlockGuard.TotalSeconds:0} s later, which is a stuck"
                        + " writer, not a slow one."
                );

            // Exact state at the moment the write returned: the sink is still parked on "first", so
            // no room appeared, the drain is still blocked on that sink, and the third write never
            // reached the sink. Observing the shutdown is the only thing that could have released
            // it.
            await Assert
                .That(sink.WrittenCount)
                .IsEqualTo(1)
                .Because(
                    "the blocked write must return without reaching the sink — nothing may be"
                        + " written while the queue is full and the sink is parked"
                );
            await Assert
                .That(drain.IsCompleted)
                .IsFalse()
                .Because(
                    "the blocked write must return while the drain is still blocked on the sink;"
                        + " otherwise the drain, not the shutdown signal, could have released it"
                );
        }
        finally
        {
            // Always unblock the sink so the dispatcher can drain and DisposeAsync can return, even
            // if an assertion threw above: the held sink plus a live factory would otherwise leak
            // into the rest of the run.
            sink.Release();
            drain ??= factory.DisposeAsync().AsTask();
        }

        // Not the subject of this test, but the drain must not be stuck either.
        await Assert
            .That(await CompletesWithinAsync(drain, DrainDeadlockGuard))
            .IsTrue()
            .Because(
                "the drain must finish once the sink is released; it was still running "
                    + $"{DrainDeadlockGuard.TotalSeconds:0} s later, which is stuck, not slow."
            );

        // What the pipeline observed: the third write was rejected, so it never entered the queue
        // and never displaced the write it was waiting behind; the writes that were queued arrived
        // in the order they were written.
        await Assert.That(reportedDropCount).IsEqualTo(0);
        await Assert.That(sink.WrittenCount).IsEqualTo(2);
        await Assert
            .That(string.Join("|", sink.Entries.Select(static entry => entry.Message)))
            .IsEqualTo("first|second");
    }

    [Test]
    public async Task Wait_Mode_BlocksAsyncWrites_UntilQueueSpaceIsAvailable()
    {
        var sink = new BlockingSink();
        long reportedDropCount = 0;
        var options = new LoggerFactoryOptions
        {
            QueueCapacity = 1,
            QueueFullMode = LogQueueFullMode.Wait,
            SyncWriteTimeout = TimeSpan.FromSeconds(1),
            OnMessagesDropped = (_, droppedCount) => reportedDropCount = droppedCount,
        };
        await using var factory = new LoggerFactory([sink], options);
        var logger = factory.CreateLogger("Tests.Category");

        Task thirdWrite;

        try
        {
            await logger.InfoAsync("first");
            await sink.WriteStarted;
            await logger.InfoAsync("second");

            thirdWrite = logger.InfoAsync("third");

            await Task.Delay(100);
            await Assert.That(thirdWrite.IsCompleted).IsFalse();
            await Assert.That(sink.WrittenCount).IsEqualTo(1);
        }
        finally
        {
            sink.Release();
        }

        await thirdWrite;
        await factory.DisposeAsync();

        await Assert.That(sink.WrittenCount).IsEqualTo(3);
        await Assert.That(reportedDropCount).IsEqualTo(0);
    }

    [Test]
    public async Task Logging_ContinuesWhenOneSinkThrowsWithoutConsoleFallback()
    {
        var collectingSink = new CollectingSink();
        await using var factory = new LoggerFactory([new ThrowingSink(), collectingSink]);
        var logger = factory.CreateLogger("Tests.Category");

        await logger.WarningAsync("still-recorded");

        await WaitForEntriesAsync(collectingSink, 1);

        await factory.DisposeAsync();

        var entries = collectingSink.Entries.ToArray();
        await Assert.That(entries).Count().IsEqualTo(1);
        await Assert.That(entries[0].Level).IsEqualTo(LogLevel.Warning);
        await Assert.That(entries[0].Message).IsEqualTo("still-recorded");
    }

    [Test]
    public async Task Logging_WithConsoleFallback_WritesSinkFailureDetails()
    {
        using var writer = new StringWriter();
        await using var factory = new LoggerFactory([
            new ThrowingSink(),
            new ColoredConsoleSink(new TestFormatter(), writer),
        ]);
        var logger = factory.CreateLogger("Tests.Category");

        await logger.WarningAsync("payload");
        await factory.DisposeAsync();

        var output = writer.ToString();

        await Assert
            .That(output)
            .Contains("Error|Failed to write log entry to sink: payload|Sink failure");
    }

    [Test]
    public async Task Logging_WithPlainConsoleFallback_WritesSinkFailureDetails()
    {
        using var writer = new StringWriter();
        await using var factory = new LoggerFactory([
            new ThrowingSink(),
            new ConsoleSink(new TestFormatter(), writer),
        ]);
        var logger = factory.CreateLogger("Tests.Category");

        await logger.WarningAsync("payload");
        await factory.DisposeAsync();

        await Assert
            .That(writer.ToString())
            .Contains("Error|Failed to write log entry to sink: payload|Sink failure");
    }

    [Test]
    public async Task Logging_WithMultipleConsoleFallbacks_UsesLastRegisteredConsoleSink()
    {
        using var firstWriter = new StringWriter();
        using var secondWriter = new StringWriter();
        await using var factory = new LoggerFactory([
            new ThrowingSink(),
            new ConsoleSink(new TestFormatter(), firstWriter),
            new ColoredConsoleSink(new TestFormatter(), secondWriter),
        ]);
        var logger = factory.CreateLogger("Tests.Category");

        await logger.WarningAsync("payload");
        await factory.DisposeAsync();

        await Assert
            .That(firstWriter.ToString())
            .DoesNotContain("Failed to write log entry to sink: payload");
        await Assert
            .That(secondWriter.ToString())
            .Contains("Error|Failed to write log entry to sink: payload|Sink failure");
    }

    [Test]
    public async Task Logging_WithCustomSink_DoesNotUseIt_AsConsoleFallback()
    {
        var customSink = new CustomFallbackLookingSink();
        var collectingSink = new CollectingSink();
        await using var factory = new LoggerFactory([
            new ThrowingSink(),
            customSink,
            collectingSink,
        ]);
        var logger = factory.CreateLogger("Tests.Category");

        await logger.WarningAsync("payload");
        await factory.DisposeAsync();

        await Assert.That(collectingSink.Entries).Count().IsEqualTo(1);
        await Assert.That(collectingSink.Entries.Single().Message).IsEqualTo("payload");
        await Assert.That(customSink.Entries).Count().IsEqualTo(1);
        await Assert.That(customSink.Entries.Single().Message).IsEqualTo("payload");
    }

    [Test]
    public async Task ConsoleSink_WritesMessages_For_All_LogLevels()
    {
        using var writer = new StringWriter();
        await using var sink = new ConsoleSink(new TestFormatter(), writer);

        foreach (var level in Enum.GetValues<LogLevel>())
        {
            await sink.WriteAsync(
                new LogEntry
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    Level = level,
                    Category = nameof(LoggerFactoryTests),
                    Message = $"level-{level}",
                }
            );
        }

        var output = writer.ToString();

        foreach (var level in Enum.GetValues<LogLevel>())
            await Assert.That(output).Contains($"{level}|level-{level}");
    }

    [Test]
    public async Task ConsoleSink_SerializesWrites_WhenSharingNonConsoleWriter()
    {
        await AssertBuiltInConsoleSinkSerializesWritesToSharedNonConsoleWriterAsync(
            static writer => new ConsoleSink(new TestFormatter(), writer)
        );
    }

    [Test]
    public async Task ColoredConsoleSink_WritesPlainText_WhenWriterIsNotConsoleOut()
    {
        using var writer = new StringWriter();
        await using var sink = new ColoredConsoleSink(new TestFormatter(), writer);

        await sink.WriteAsync(
            new LogEntry
            {
                Timestamp = DateTimeOffset.UtcNow,
                Level = LogLevel.Warning,
                Category = nameof(LoggerFactoryTests),
                Message = "colored-message",
            }
        );

        await Assert.That(writer.ToString()).Contains("Warning|colored-message");
    }

    [Test]
    public async Task ColoredConsoleSink_SerializesWrites_WhenSharingNonConsoleWriter()
    {
        await AssertBuiltInConsoleSinkSerializesWritesToSharedNonConsoleWriterAsync(
            static writer => new ColoredConsoleSink(new TestFormatter(), writer)
        );
    }

    [Test]
#pragma warning disable TUnit0055 // Intentional: this test must exercise the real Console.Out path.
    public async Task ColoredConsoleSink_UsesConsoleOutPath_And_RestoresForegroundColor()
    {
        var originalWriter = Console.Out;
        using var redirectedWriter = new StringWriter();

        try
        {
            Console.SetOut(redirectedWriter);
            var colorBeforeWrite = Console.ForegroundColor;

            await using var sink = new ColoredConsoleSink(new TestFormatter());

            await sink.WriteAsync(
                new LogEntry
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    Level = LogLevel.Error,
                    Category = nameof(LoggerFactoryTests),
                    Message = "console-out-message",
                }
            );

            await Assert.That(redirectedWriter.ToString()).Contains("Error|console-out-message");
            await Assert.That(Console.ForegroundColor).IsEqualTo(colorBeforeWrite);
        }
        finally
        {
            Console.SetOut(originalWriter);
        }
    }
#pragma warning restore TUnit0055

    [Test]
    public async Task AddLogging_ResolvesTypedLoggerFromContainer()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"pico-logger-di-{Guid.NewGuid():N}.log");

        try
        {
            ISvcContainer container = new SvcContainer();
            container.AddPicoLog(options =>
            {
                options.MinLevel = LogLevel.Info;
                options.File.FilePath = filePath;
                options.WriteTo.File();
            });
            container.RegisterScoped<LoggerConsumer, LoggerConsumer>();

            await using var scope = container.CreateScope();
            var consumer = scope.GetService<LoggerConsumer>();
            var factory = scope.GetService<ILoggerFactory>();

            await Assert.That(consumer).IsNotNull();
            await Assert.That(consumer!.Logger).IsNotNull();

            await factory.DisposeAsync();
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }

    [Test]
    public async Task AddLogging_UsesConfiguredFilePath()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"pico-logger-di-{Guid.NewGuid():N}.log");

        try
        {
            ISvcContainer container = new SvcContainer();
            container.AddPicoLog(options =>
            {
                options.MinLevel = LogLevel.Info;
                options.File.FilePath = filePath;
                options.WriteTo.File();
            });

            await using var scope = container.CreateScope();
            var factory = (ILoggerFactory)scope.GetService(typeof(ILoggerFactory));
            var logger = factory.CreateLogger("Tests.Category");

            await logger.InfoAsync("configured-path");

            await factory!.DisposeAsync();

            await Assert.That(File.Exists(filePath)).IsTrue();
            var contents = await File.ReadAllTextAsync(filePath);
            await Assert.That(contents).Contains("configured-path");
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }

    [Test]
    public async Task LoggerFactory_PersistsTailMessagesToFileSink()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"pico-logger-file-{Guid.NewGuid():N}.log");

        try
        {
            var formatter = new ConsoleFormatter();
            await using var factory = new LoggerFactory([new FileSink(formatter, filePath)]);
            var logger = factory.CreateLogger("Tests.Category");

            await logger.DebugAsync("tail-debug");
            await logger.NoticeAsync("tail-notice");
            await logger.InfoAsync("tail-info");

            await factory.DisposeAsync();

            var contents = await File.ReadAllTextAsync(filePath);
            await Assert.That(contents).Contains("tail-debug");
            await Assert.That(contents).Contains("tail-notice");
            await Assert.That(contents).Contains("tail-info");
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }

    [Test]
    public async Task AddLogging_TypedLoggerUsesResolvedFactory()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"pico-logger-di-{Guid.NewGuid():N}.log");

        try
        {
            ISvcContainer container = new SvcContainer();
            container.AddPicoLog(options =>
            {
                options.MinLevel = LogLevel.Warning;
                options.File.FilePath = filePath;
                options.WriteTo.File();
            });
            container.RegisterScoped<LoggerConsumer, LoggerConsumer>();

            await using var scope = container.CreateScope();
            var consumer = scope.GetService<LoggerConsumer>();
            var factory = (ILoggerFactory)scope.GetService(typeof(ILoggerFactory));

            await Assert.That(consumer).IsNotNull();

            consumer!.Logger.Info("ignored-by-min-level");
            await consumer.Logger.ErrorAsync("written-by-typed-logger");

            await factory.DisposeAsync();

            var contents = await File.ReadAllTextAsync(filePath);
            await Assert.That(contents.Contains("ignored-by-min-level")).IsFalse();
            await Assert.That(contents).Contains("written-by-typed-logger");
            await Assert.That(contents).Contains(typeof(LoggerConsumer).FullName!);
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }

    [Test]
    public async Task DisposeAsync_AggregatesSinkDisposalFailures()
    {
        await using var factory = new LoggerFactory([new ThrowOnDisposeSink()]);

        AggregateException? exception = null;

        try
        {
            await factory.DisposeAsync();
        }
        catch (AggregateException ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.InnerExceptions).Count().IsEqualTo(1);
        await Assert.That(exception.InnerExceptions[0].Message).IsEqualTo("dispose failure");
    }

    [Test]
    public async Task DisposeAsync_WaitsForActiveSinkWrites_BeforeDisposingSinks()
    {
        // Contract: a sink is disposed only after the writes it is already running have finished.
        //
        // Nothing here is a duration, and the "not disposed yet" observation is structural. Two
        // things make it so, and the shape below leans on both:
        //
        //  - ShutdownTimeout is Zero, so the drain owns no deadline and can never cancel the parked
        //    write, which means it can never get past it either. Under the default 30 s deadline a
        //    starved pool could cancel the write and dispose the sink inside the old 50 ms window,
        //    which turned the assertion into a claim about this machine's scheduling instead of
        //    about the factory. (This suite has measured Task.Delay(10) continuations at a 185.6 ms
        //    worst case and a 17.5 s straggle just to observe one completion on a 4-vCPU view with
        //    CPU burners holding every core.)
        //  - DisposeAsync signals shutdown in its synchronous prefix, before its first incomplete
        //    await, so by the time it hands this thread a Task the pipelines are already being
        //    drained and the sink disposal is queued behind that drain by construction. The
        //    ObjectDisposedException from CreateLogger below is the witness that it happened, which
        //    is what keeps the assertion below from being vacuous rather than merely late.
        //
        // The disposal order is also recorded by the sink itself, so the contract survives as an
        // outcome even in the absence of the observation above.
        var sink = new CoordinatedDisposalSink();
        var options = new LoggerFactoryOptions { ShutdownTimeout = TimeSpan.Zero };
        await using var factory = new LoggerFactory([sink], options);
        var logger = factory.CreateLogger("Tests.Category");

        await logger.InfoAsync("payload");
        await sink.WriteStarted;

        var disposeTask = factory.DisposeAsync().AsTask();

        // Witness, before anything is asserted: shutdown has begun, so the drain this test is about
        // is already running. CreateLogger only rejects once writes are no longer accepted, i.e.
        // inside DisposeAsync's synchronous prefix; if that ever became asynchronous this fails
        // instead of the test quietly passing on a factory that had not started disposing at all.
        await Assert
            .That(() => factory.CreateLogger("Tests.DuringShutdown"))
            .Throws<ObjectDisposedException>()
            .Because(
                "DisposeAsync must have signalled shutdown by the time it returns a Task here"
            );

        await Assert
            .That(sink.DisposeCallCount)
            .IsEqualTo(0)
            .Because(
                "a write is still parked inside the sink and the drain has already begun, so the"
                    + " sink must not be disposed; the drain cannot pass a write it is waiting on"
            );
        await Assert
            .That(sink.DisposeCallsWhileWriteInFlight)
            .IsEqualTo(0)
            .Because("the sink must never be disposed while one of its writes is still in flight");

        sink.ReleaseWrite();

        // Whatever the sink was waiting on, the drain must then finish. This is a deadlock guard,
        // not a latency budget: a drain that never waits for its writes disposes the sink before
        // this point, and one that never finishes is caught by any finite bound.
        await Assert
            .That(await DrainsWithinAsync(disposeTask, DrainDeadlockGuard))
            .IsTrue()
            .Because(
                $"DisposeAsync must finish once the in-flight write returns; it was still running "
                    + $"{DrainDeadlockGuard.TotalSeconds:0} s later, which is stuck, not slow."
            );

        await Assert.That(sink.DisposeCallCount).IsEqualTo(1);
        await Assert
            .That(sink.CompletedWriteCount)
            .IsEqualTo(1)
            .Because("the in-flight write must be allowed to finish, not cancelled by a deadline");
        await Assert
            .That(sink.DisposeCallsWhileWriteInFlight)
            .IsEqualTo(0)
            .Because("the sink must never be disposed while one of its writes is still in flight");
    }

    [Test]
    public async Task DisposeAsync_CancelsInFlightSinkWrites_WhenShutdownTimeoutExpires()
    {
        var sink = new BlockingSink();
        var options = new LoggerFactoryOptions { ShutdownTimeout = TimeSpan.FromMilliseconds(500) };
        await using var factory = new LoggerFactory([sink], options);
        var logger = factory.CreateLogger("Tests.Category");

        try
        {
            await logger.InfoAsync("payload");
            await sink.WriteStarted;

            // The one exact statement available here is a LOWER bound on when the cancellation may
            // become visible: the drain gives the in-flight write a full ShutdownTimeout to finish,
            // so the write cannot be cancelled before that budget elapsed. It still forbids the
            // behaviour worth forbidding: with the drain's own deadline shortened to 5 ms — i.e.
            // cancelled on the spot — the cancellation is observed after 21-25 ms, a twentieth of the
            // budget, so this bound goes red. It is also the only shape that survives load:
            // every scheduling effect (a late ThreadPool timer callback, a starved pool, a
            // continuation that waits for a worker) moves the observation LATER, never earlier,
            // whereas an upper bound read at this instant is a claim about this thread's own
            // scheduling: it can be descheduled between the call below and the read for longer than
            // the whole budget. The origin is taken immediately before the drain begins and the
            // elapsed value is stamped where the cancellation is observed — inside the sink, on the
            // pipeline's own thread — not here.
            //
            // The floor is measured, not assumed: a bare new CancellationTokenSource(500 ms) awaited
            // on the pool cancelled at 500.7 ms at the earliest of 100 idle probes and 503.4 ms at
            // the earliest of 100 probes under the induced load used for this suite, so a ThreadPool
            // timer does not undercut its own due time and the bound holds at the floor.
            sink.MeasuresCancellationFrom(Stopwatch.GetTimestamp());
            var disposeTask = factory.DisposeAsync().AsTask();

            // The behaviour under test is the OUTCOME — the expired shutdown timeout cancels
            // the in-flight write instead of letting it complete — so that is what is asserted,
            // not how long the drain took to unwind. The shutdown timeout is a ThreadPool timer
            // callback and every hop back to DisposeAsync needs a pool thread, so a busy runner
            // stretches the drain without bound. Measured on this suite: 507-516 ms idle;
            // 2.6-10.4 s with all 199 tests on a 4-vCPU view while 16 CPU burners run (a 10 s
            // bound failed 1 run in 8 there, which is exactly CI run 41813752088 — 10 s timeout
            // on win-x64 while the same suite passed on linux-x64); and unbounded once every
            // pool thread is occupied, because a saturated pool never dispatches the timer
            // callback at all. So the drain bound below is only a deadlock guard: a drain that never
            // cancels the write never returns, so any finite bound catches it, and 60 s is ~6x
            // the worst drain measured under induced load.
            await Assert
                .That(await DrainsWithinAsync(disposeTask, DrainDeadlockGuard))
                .IsTrue()
                .Because(
                    $"DisposeAsync must finish once the expired shutdown timeout cancels the "
                        + $"in-flight write; it was still running {DrainDeadlockGuard.TotalSeconds:0} s "
                        + "later, which means the drain is stuck, not merely slow."
                );

            await Assert.That(sink.ObservedCancellationCount).IsEqualTo(1);
            await Assert
                .That(sink.ObservedCancellationDelay)
                .IsGreaterThanOrEqualTo(options.ShutdownTimeout)
                .Because(
                    "the in-flight write may only be cancelled once the shutdown timeout expired; "
                        + $"it was cancelled {sink.ObservedCancellationDelay.TotalMilliseconds:0} ms "
                        + $"into a {options.ShutdownTimeout.TotalMilliseconds:0} ms budget, i.e. the "
                        + "drain cancelled it on the spot instead of giving it the time it was granted."
                );
            await Assert.That(sink.CompletedWriteCount).IsEqualTo(0);
            await Assert.That(sink.WrittenCount).IsEqualTo(1);
        }
        finally
        {
            sink.Release();
        }
    }

    /// <summary>
    /// Deadlock guard for a drain: <see langword="false"/> means the drain outlived the bound
    /// and is stuck. Never treat a value near this bound as a timing measurement — see the
    /// comment in <see cref="DisposeAsync_CancelsInFlightSinkWrites_WhenShutdownTimeoutExpires"/>.
    /// </summary>
    private static readonly TimeSpan DrainDeadlockGuard = TimeSpan.FromSeconds(60);

    private static async Task<bool> DrainsWithinAsync(Task drain, TimeSpan guard)
    {
        try
        {
            await drain.WaitAsync(guard);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    [Test]
    public async Task DisposeAsync_RejectsWrites_After_Shutdown_Begins()
    {
        // Contract: once factory shutdown has begun, a write is rejected at the door — it never
        // enters the queue, so it can never be written to a sink the drain is about to tear down.
        //
        // This test used to wait 50 ms after DisposeAsync "so that the shutdown could arrive". That
        // wait asserted nothing the API guarantees, and it made the test weaker the slower the
        // machine got: a write issued 50 ms late lands *after* an implementation that signalled
        // shutdown 49 ms late, so detection quietly depended on this thread's scheduling rather
        // than on the factory's behaviour. DisposeAsync signals shutdown in its synchronous prefix,
        // before its first incomplete await, so the writes below are issued strictly after the
        // signal with no window at all — and the CreateLogger witness proves the signal was sent.
        var sink = new CoordinatedDisposalSink();
        var options = new LoggerFactoryOptions { ShutdownTimeout = TimeSpan.Zero };
        await using var factory = new LoggerFactory([sink], options);
        var logger = factory.CreateLogger("Tests.Category");

        await logger.InfoAsync("before-dispose");
        await sink.WriteStarted;

        var disposeTask = factory.DisposeAsync().AsTask();

        await Assert
            .That(() => factory.CreateLogger("Tests.AfterShutdown"))
            .Throws<ObjectDisposedException>()
            .Because(
                "DisposeAsync must have signalled shutdown by the time it returns a Task here"
            );

        // The drain is still parked inside the sink, so "no disposal yet" cannot be explained by a
        // pipeline that already finished and tore the sink down.
        await Assert
            .That(sink.DisposeCallCount)
            .IsEqualTo(0)
            .Because(
                "the drain is parked on the in-flight write and cannot have disposed the sink"
            );

        logger.Warning("after-dispose-sync");
        await logger.ErrorAsync("after-dispose-async");

        // Not the conviction on its own — an accepted write would still be sitting in the queue at
        // this instant — but it keeps the shape honest if the queue were ever drained eagerly.
        await Assert.That(sink.Entries).Count().IsEqualTo(1);

        sink.ReleaseWrite();
        await Assert
            .That(await DrainsWithinAsync(disposeTask, DrainDeadlockGuard))
            .IsTrue()
            .Because(
                $"DisposeAsync must finish once the in-flight write returns; it was still running "
                    + $"{DrainDeadlockGuard.TotalSeconds:0} s later, which is stuck, not slow."
            );

        // The conviction: had either write after shutdown been accepted, the drain would have
        // written it to this sink when it resumed above, so the door has to be shut, not just
        // slow to open.
        var messages = sink.Entries.Select(entry => entry.Message).ToArray();
        await Assert.That(messages).Count().IsEqualTo(1);
        await Assert.That(messages[0]).IsEqualTo("before-dispose");
        await Assert
            .That(sink.CompletedWriteCount)
            .IsEqualTo(1)
            .Because("the in-flight write must finish; a cancelled write would reject nothing");
    }

    [Test]
    public async Task FlushAsync_Completes_And_PreservesLoggerReuse_ForSameCategory()
    {
        var sink = new CollectingSink();
        await using var factory = new LoggerFactory([sink]);

        var firstLogger = factory.CreateLogger("Tests.FlushReuse");

        firstLogger.Info("before-flush");
        await factory.FlushAsync();

        var secondLogger = factory.CreateLogger("Tests.FlushReuse");
        secondLogger.Info("after-flush");

        await factory.DisposeAsync();

        await Assert.That(firstLogger).IsSameReferenceAs(secondLogger);
        await Assert
            .That(sink.Entries.Select(entry => entry.Message ?? string.Empty).ToArray())
            .IsEquivalentTo(["before-flush", "after-flush"]);
    }

    [Test]
    public async Task FactoryDisposeAsync_ClassifiesPendingWaitWrites_AsRejectedAfterShutdown()
    {
        using var listener = new MeterListener();
        var measurements = new ConcurrentDictionary<string, ConcurrentQueue<double>>(
            StringComparer.Ordinal
        );

        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == PicoLogMetrics.MeterName)
                meterListener.EnableMeasurementEvents(instrument);
        };

        listener.SetMeasurementEventCallback<long>(
            (instrument, measurement, _, _) =>
                measurements
                    .GetOrAdd(instrument.Name, _ => new ConcurrentQueue<double>())
                    .Enqueue(measurement)
        );
        listener.Start();

        var sink = new BlockingSink();
        long reportedDropCount = 0;
        var options = new LoggerFactoryOptions
        {
            QueueCapacity = 1,
            QueueFullMode = LogQueueFullMode.Wait,
            // Keeps the drain deadline out of this test: the parked write must be released by
            // sink.Release below and never cancelled by the drain, so the write that is pending
            // here is pending for the reason this test is about.
            ShutdownTimeout = TimeSpan.Zero,
            OnMessagesDropped = (_, count) => reportedDropCount = count,
        };
        await using var factory = new LoggerFactory([sink], options);
        var logger = factory.CreateLogger("Tests.LoggerDispose");

        logger.Info("first");
        await sink.WriteStarted;
        logger.Info("second");

        Task pendingWriteTask;
        Task disposeTask;

        try
        {
            pendingWriteTask = logger.InfoAsync("third");

            // The precondition, stated without a window. The write is pending by construction, not
            // because less than 50 ms have passed: the Wait-mode async path parks on the channel
            // with no timeout of its own, the capacity-1 queue is full, and its only reader is
            // parked inside the sink. (The delay that used to sit here was inert rather than
            // dangerous — a later observation can only strengthen a "still pending" claim — but it
            // asserted nothing on its own and cost a window of real time.)
            await Assert
                .That(pendingWriteTask.IsCompleted)
                .IsFalse()
                .Because(
                    "a Wait-mode write behind a full queue and a parked sink is pending, not dropped"
                );

            disposeTask = factory.DisposeAsync().AsTask();

            // Witness that shutdown has begun: DisposeAsync signals it in its synchronous prefix,
            // so the pending write is classified against a factory that has already stopped
            // accepting writes — not against one that might be about to.
            await Assert
                .That(() => factory.CreateLogger("Tests.AfterShutdown"))
                .Throws<ObjectDisposedException>()
                .Because(
                    "DisposeAsync must have signalled shutdown by the time it returns a Task here"
                );

            sink.Release();
        }
        finally
        {
            sink.Release();
        }

        // Deadlock guard, not a latency budget: both must finish once the sink has room, and any
        // finite bound catches the pipeline that never does.
        await Assert
            .That(
                await CompletesWithinAsync(
                    Task.WhenAll(disposeTask, pendingWriteTask),
                    DrainDeadlockGuard
                )
            )
            .IsTrue()
            .Because(
                "the pending write and the drain must both finish once the sink is released; they"
                    + $" were still running {DrainDeadlockGuard.TotalSeconds:0} s later, which is"
                    + " stuck, not slow."
            );

        listener.RecordObservableInstruments();

        // What the pipeline observed: the write that was waiting for room never reached the sink —
        // it was rejected, not admitted and not dropped — and the writes that did fit kept their
        // order.
        await Assert.That(sink.WrittenCount).IsEqualTo(2);
        await Assert.That(reportedDropCount).IsEqualTo(0);
        await Assert
            .That(string.Join("|", sink.Entries.Select(static entry => entry.Message)))
            .IsEqualTo("first|second");
        // Classification: rejected-after-shutdown rather than dropped. The counter is process-wide
        // (every factory in the run feeds it), so this asserts that the classification was recorded,
        // not that this factory was its only source.
        await AssertMeasurementAtLeastAsync(
            measurements,
            PicoLogMetrics.ShutdownRejectedWritesName,
            1
        );
    }

    [Test]
    public async Task DropOldest_Mode_ReportsAcceptedAfterEviction_AsDropped_Publicly()
    {
        long reportedDropCount = 0;
        var sink = new BlockingSink();
        var options = new LoggerFactoryOptions
        {
            QueueCapacity = 1,
            QueueFullMode = LogQueueFullMode.DropOldest,
            OnMessagesDropped = (_, droppedCount) => reportedDropCount = droppedCount,
        };
        await using var factory = new LoggerFactory([sink], options);
        var logger = factory.CreateLogger("Tests.Category");

        logger.Info("first");
        await sink.WriteStarted;
        logger.Info("second");
        logger.Info("third");

        sink.Release();
        await factory.DisposeAsync();

        var messages = sink.Entries.Select(entry => entry.Message ?? string.Empty).ToArray();
        await Assert.That(messages).IsEquivalentTo(["first", "third"]);
        await Assert.That(reportedDropCount).IsEqualTo(1);
    }

    [Test]
    public async Task DropWrite_Mode_ReportsDroppedNewWrite_Publicly_WithoutEvictingQueuedEntry()
    {
        long reportedDropCount = 0;
        var sink = new BlockingSink();
        var options = new LoggerFactoryOptions
        {
            QueueCapacity = 1,
            QueueFullMode = LogQueueFullMode.DropWrite,
            OnMessagesDropped = (_, droppedCount) => reportedDropCount = droppedCount,
        };
        await using var factory = new LoggerFactory([sink], options);
        var logger = factory.CreateLogger("Tests.Category");

        logger.Info("first");
        await sink.WriteStarted;
        logger.Info("second");
        logger.Info("third");

        sink.Release();
        await factory.DisposeAsync();

        var messages = sink.Entries.Select(entry => entry.Message ?? string.Empty).ToArray();
        await Assert.That(messages).IsEquivalentTo(["first", "second"]);
        await Assert.That(reportedDropCount).IsEqualTo(1);
    }

    [Test]
    public async Task Metrics_Record_RejectedWrites_DroppedWrites_SinkFailures_And_ShutdownDuration()
    {
        using var listener = new MeterListener();
        var measurements = new ConcurrentDictionary<string, ConcurrentQueue<double>>(
            StringComparer.Ordinal
        );

        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == PicoLogMetrics.MeterName)
                meterListener.EnableMeasurementEvents(instrument);
        };

        listener.SetMeasurementEventCallback<long>(
            (instrument, measurement, _, _) =>
                measurements
                    .GetOrAdd(instrument.Name, _ => new ConcurrentQueue<double>())
                    .Enqueue(measurement)
        );
        listener.SetMeasurementEventCallback<double>(
            (instrument, measurement, _, _) =>
                measurements
                    .GetOrAdd(instrument.Name, _ => new ConcurrentQueue<double>())
                    .Enqueue(measurement)
        );
        listener.Start();

        var sink = new BlockingSink();
        var options = new LoggerFactoryOptions
        {
            QueueCapacity = 1,
            QueueFullMode = LogQueueFullMode.DropWrite,
        };
        await using var dropFactory = new LoggerFactory([sink], options);
        var dropLogger = dropFactory.CreateLogger("Tests.Drop");

        try
        {
            dropLogger.Info("first");
            await sink.WriteStarted;
            dropLogger.Info("second");
            await RecordObservableMeasurementUntilAsync(
                listener,
                measurements,
                PicoLogMetrics.QueuedEntriesName,
                value => value >= 1
            );
            dropLogger.Info("third");
        }
        finally
        {
            sink.Release();
        }

        await dropFactory.DisposeAsync();

        var collectingSink = new CollectingSink();
        await using var failureFactory = new LoggerFactory([new ThrowingSink(), collectingSink]);
        var failureLogger = failureFactory.CreateLogger("Tests.Failures");
        await failureLogger.WarningAsync("payload");
        await failureFactory.DisposeAsync();

        var coordinatedSink = new CoordinatedDisposalSink();
        await using var shutdownFactory = new LoggerFactory([coordinatedSink]);
        var shutdownLogger = shutdownFactory.CreateLogger("Tests.Shutdown");
        await shutdownLogger.InfoAsync("before-shutdown");
        await coordinatedSink.WriteStarted;

        var shutdownDisposeTask = shutdownFactory.DisposeAsync().AsTask();

        // DisposeAsync has already signalled shutdown in its synchronous prefix, so the write below
        // is rejected on this thread with no queue interaction and no arrival window. The 50 ms
        // delay that used to sit here could only ever have helped this assertion — never the
        // factory — and it hid the fact that nothing was being waited for.
        await Assert
            .That(() => shutdownFactory.CreateLogger("Tests.AfterShutdown"))
            .Throws<ObjectDisposedException>()
            .Because(
                "DisposeAsync must have signalled shutdown by the time it returns a Task here"
            );

        shutdownLogger.Info("after-shutdown");

        coordinatedSink.ReleaseWrite();
        await Assert
            .That(await DrainsWithinAsync(shutdownDisposeTask, DrainDeadlockGuard))
            .IsTrue()
            .Because(
                "the shutdown drain must finish once the parked write is released; it was still "
                    + $"running {DrainDeadlockGuard.TotalSeconds:0} s later, which is stuck, not slow."
            );
        listener.RecordObservableInstruments();

        await AssertMeasurementAtLeastAsync(measurements, PicoLogMetrics.EntriesEnqueuedName, 4);
        await AssertMeasurementAtLeastAsync(measurements, PicoLogMetrics.EntriesDroppedName, 1);
        await AssertMeasurementAtLeastAsync(measurements, PicoLogMetrics.SinkFailuresName, 1);
        await AssertMeasurementAtLeastAsync(
            measurements,
            PicoLogMetrics.ShutdownRejectedWritesName,
            1
        );
        await AssertMeasurementAtLeastAsync(
            measurements,
            PicoLogMetrics.ShutdownDrainDurationName,
            0
        );
        await AssertMeasurementObservedAtLeastAsync(
            measurements,
            PicoLogMetrics.QueuedEntriesName,
            1
        );
        await AssertAllMeasurementsAtLeastAsync(measurements, PicoLogMetrics.QueuedEntriesName, 0);
    }

    private sealed class CollectingSink : ILogSink
    {
        private readonly ConcurrentQueue<LogEntry> _entries = [];

        public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

        public Task WriteAsync(LogEntry entry, CancellationToken cancellationToken = default)
        {
            _entries.Enqueue(entry);
            return Task.CompletedTask;
        }

        public void Dispose() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingSink : ILogSink
    {
        public Task WriteAsync(LogEntry entry, CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("Sink failure"));

        public void Dispose() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingSink : ILogSink
    {
        private readonly ConcurrentQueue<LogEntry> _entries = [];
        private readonly TaskCompletionSource _writeStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _writtenCount;
        private int _observedCancellationCount;
        private int _completedWriteCount;
        private long _cancellationDeadlineOriginTimestamp;
        private long _observedCancellationDelayTicks = -1;

        public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

        public int WrittenCount => Volatile.Read(ref _writtenCount);

        /// <summary>Writes whose <see cref="CancellationToken"/> was cancelled mid-flight.</summary>
        public int ObservedCancellationCount => Volatile.Read(ref _observedCancellationCount);

        /// <summary>Writes released without being cancelled — i.e. that got through.</summary>
        public int CompletedWriteCount => Volatile.Read(ref _completedWriteCount);

        /// <summary>
        /// Arms a <see cref="Stopwatch"/> origin that <see cref="ObservedCancellationDelay"/> is
        /// measured from. Unarmed by default, because most callers only care that the write was
        /// cancelled and not how long it survived.
        /// </summary>
        public void MeasuresCancellationFrom(long originTimestamp) =>
            Volatile.Write(ref _cancellationDeadlineOriginTimestamp, originTimestamp);

        /// <summary>
        /// How long after the armed origin the cancellation <em>became visible</em>, stamped on the
        /// thread that observes it rather than on the test thread. <see cref="TimeSpan.Zero"/> when
        /// no origin was armed.
        /// </summary>
        public TimeSpan ObservedCancellationDelay
        {
            get
            {
                var ticks = Volatile.Read(ref _observedCancellationDelayTicks);
                return ticks < 0 ? TimeSpan.Zero : new TimeSpan(ticks);
            }
        }

        public Task WriteStarted => _writeStarted.Task;

        public async Task WriteAsync(LogEntry entry, CancellationToken cancellationToken = default)
        {
            _entries.Enqueue(entry);
            Interlocked.Increment(ref _writtenCount);
            _writeStarted.TrySetResult();

            try
            {
                await _release.Task.WaitAsync(cancellationToken);
                Interlocked.Increment(ref _completedWriteCount);
            }
            catch (OperationCanceledException)
            {
                var originTimestamp = Volatile.Read(ref _cancellationDeadlineOriginTimestamp);
                if (originTimestamp != 0)
                {
                    var elapsed = Stopwatch.GetElapsedTime(originTimestamp);
                    Volatile.Write(ref _observedCancellationDelayTicks, elapsed.Ticks);
                }

                Interlocked.Increment(ref _observedCancellationCount);
                throw;
            }
        }

        public void Release() => _release.TrySetResult();

        public void Dispose() => Release();

        public ValueTask DisposeAsync()
        {
            Release();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// A sink that holds its caller's <em>thread</em> on an OS gate instead of holding a pending
    /// <see cref="Task"/>. Releasing it hands that same thread straight back, so a test can make
    /// room in a full queue without waiting for a ThreadPool thread to run a continuation — see
    /// <see cref="Wait_Mode_BlocksSyncWrites_UntilQueueSpaceIsAvailable"/>.
    /// </summary>
    private sealed class ThreadGateSink : ILogSink
    {
        private readonly ConcurrentQueue<LogEntry> _entries = [];
        private readonly ManualResetEventSlim _gate = new(initialState: false);
        private readonly TaskCompletionSource _writeStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _writtenCount;

        public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

        public int WrittenCount => Volatile.Read(ref _writtenCount);

        public Task WriteStarted => _writeStarted.Task;

        public Task WriteAsync(LogEntry entry, CancellationToken cancellationToken = default)
        {
            _entries.Enqueue(entry);
            Interlocked.Increment(ref _writtenCount);
            _writeStarted.TrySetResult();
            _gate.Wait(cancellationToken);
            return Task.CompletedTask;
        }

        public void Release() => _gate.Set();

        public void Dispose() => Release();

        public ValueTask DisposeAsync()
        {
            Release();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowOnDisposeSink : ILogSink
    {
        public Task WriteAsync(LogEntry entry, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public void Dispose() => throw new InvalidOperationException("dispose failure");

        public ValueTask DisposeAsync() =>
            ValueTask.FromException(new InvalidOperationException("dispose failure"));
    }

    private sealed class CustomFallbackLookingSink : ILogSink
    {
        private readonly ConcurrentQueue<LogEntry> _entries = [];

        public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

        public Task WriteAsync(LogEntry entry, CancellationToken cancellationToken = default)
        {
            _entries.Enqueue(entry);
            return Task.CompletedTask;
        }

        public void Dispose() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// A sink that parks one write until <see cref="ReleaseWrite"/>, and records the disposal order
    /// it observes itself: how often it was disposed, and how many of those disposals happened while
    /// one of its own writes had not returned yet.
    /// </summary>
    /// <remarks>
    /// The in-flight counter is what lets
    /// <see cref="DisposeAsync_WaitsForActiveSinkWrites_BeforeDisposingSinks"/> state its contract as
    /// an outcome — "this sink was never disposed underneath a running write" — instead of as a
    /// window. The observation is made where the disposal happens and on the thread that performs
    /// it, so no amount of scheduling can move it out from under the assertion.
    /// </remarks>
    private sealed class CoordinatedDisposalSink : ILogSink
    {
        private readonly ConcurrentQueue<LogEntry> _entries = [];
        private readonly TaskCompletionSource _writeStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _inFlightWriteCount;
        private int _completedWriteCount;
        private int _disposeCallCount;
        private int _disposeCallsWhileWriteInFlight;

        public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

        public Task WriteStarted => _writeStarted.Task;

        public int DisposeCallCount => Volatile.Read(ref _disposeCallCount);

        /// <summary>Writes that were released rather than cancelled — i.e. that the drain let finish.</summary>
        public int CompletedWriteCount => Volatile.Read(ref _completedWriteCount);

        /// <summary>
        /// Disposals that happened while a write of this sink had not returned yet. Anything but zero
        /// means the factory tore the sink down underneath a write it was still running.
        /// </summary>
        public int DisposeCallsWhileWriteInFlight =>
            Volatile.Read(ref _disposeCallsWhileWriteInFlight);

        public async Task WriteAsync(LogEntry entry, CancellationToken cancellationToken = default)
        {
            _entries.Enqueue(entry);
            Interlocked.Increment(ref _inFlightWriteCount);
            _writeStarted.TrySetResult();

            try
            {
                await _release.Task.WaitAsync(cancellationToken);
                Interlocked.Increment(ref _completedWriteCount);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlightWriteCount);
            }
        }

        public void ReleaseWrite() => _release.TrySetResult();

        public void Dispose() => RecordDispose();

        public ValueTask DisposeAsync()
        {
            RecordDispose();
            return ValueTask.CompletedTask;
        }

        private void RecordDispose()
        {
            if (Volatile.Read(ref _inFlightWriteCount) > 0)
                Interlocked.Increment(ref _disposeCallsWhileWriteInFlight);

            Interlocked.Increment(ref _disposeCallCount);
        }
    }

    private sealed class TestFormatter : ILogFormatter
    {
        public string Format(LogEntry entry) =>
            $"{entry.Level}|{entry.Message}|{entry.Exception?.Message}";
    }

    private sealed class ConcurrentWriteDetectingWriter : TextWriter
    {
        private readonly ConcurrentQueue<string?> _lines = [];
        private readonly TaskCompletionSource _firstWriteEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _releaseWrites = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _activeWriteCount;
        private int _enteredWriteCount;
        private int _concurrentWriteCount;

        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public Task FirstWriteEntered => _firstWriteEntered.Task;

        public IReadOnlyCollection<string?> Lines => _lines.ToArray();

        public int ConcurrentWriteCount => Volatile.Read(ref _concurrentWriteCount);

        public override void WriteLine(string? value)
        {
            if (Interlocked.Increment(ref _activeWriteCount) > 1)
                Interlocked.Increment(ref _concurrentWriteCount);

            if (Interlocked.Increment(ref _enteredWriteCount) == 1)
                _firstWriteEntered.TrySetResult();

            try
            {
                _releaseWrites.Task.GetAwaiter().GetResult();
                _lines.Enqueue(value);
            }
            finally
            {
                Interlocked.Decrement(ref _activeWriteCount);
            }
        }

        public void ReleaseWrites() => _releaseWrites.TrySetResult();
    }

    private static async Task AssertMeasurementAtLeastAsync(
        ConcurrentDictionary<string, ConcurrentQueue<double>> measurements,
        string instrumentName,
        double minimumValue
    )
    {
        await Assert.That(measurements.TryGetValue(instrumentName, out var values)).IsTrue();
        await Assert.That(values!).IsNotEmpty();
        await Assert.That(values!.Sum()).IsGreaterThanOrEqualTo(minimumValue);
    }

    private static async Task AssertMeasurementObservedAtLeastAsync(
        ConcurrentDictionary<string, ConcurrentQueue<double>> measurements,
        string instrumentName,
        double minimumValue
    )
    {
        await Assert.That(measurements.TryGetValue(instrumentName, out var values)).IsTrue();
        await Assert.That(values!.Any(value => value >= minimumValue)).IsTrue();
    }

    private static async Task AssertAllMeasurementsAtLeastAsync(
        ConcurrentDictionary<string, ConcurrentQueue<double>> measurements,
        string instrumentName,
        double minimumValue
    )
    {
        await Assert.That(measurements.TryGetValue(instrumentName, out var values)).IsTrue();
        await Assert.That(values!).IsNotEmpty();
        await Assert.That(values!.All(value => value >= minimumValue)).IsTrue();
    }

    /// <summary>
    /// How long an observable instrument is <em>sampled</em>. This is the "keep sampling while the
    /// value is still on its way" window, not an assertion about how long the pipeline takes and
    /// not a duration budget: it may expire because the observation genuinely never appears, and it
    /// deliberately must not expire merely because the machine is slow to run the sampling loop.
    /// </summary>
    /// <remarks>
    /// Measurement is <b>polled</b>, not pushed: an <c>ObservableGauge</c> has no callback to wait
    /// on, so the test itself has to call <c>MeterListener.RecordObservableInstruments()</c> and the
    /// runtime invokes the gauge's observation callback then. There is therefore no signal to await
    /// and sampling is the only way to see the value; what must not be a <em>budget</em> is the
    /// attempt count. A count is a wall-clock budget in disguise, and that budget is a function of
    /// how often the pool schedules the <c>Task.Delay</c> continuation, i.e. of the machine, not of
    /// the pipeline under test: with the whole suite in parallel on a 4-processor view, a nominal
    /// <c>Task.Delay(10)</c> was measured here at a 15.6 ms median, 32.7 ms p99 and a 185.6 ms
    /// worst case, so a fixed 100-sample loop covers anywhere from ~1 s to tens of seconds of real
    /// time depending on load. A monotone deadline checked against <see cref="Stopwatch"/> instead
    /// spends a fixed amount of <em>time</em> regardless of scheduling, so a late sample cannot
    /// quietly shorten it.
    /// </remarks>
    private static readonly TimeSpan ObservableMeasurementSampleWindow = TimeSpan.FromSeconds(60);

    private static async Task RecordObservableMeasurementUntilAsync(
        MeterListener listener,
        ConcurrentDictionary<string, ConcurrentQueue<double>> measurements,
        string instrumentName,
        Func<double, bool> predicate
    )
    {
        for (
            var startedAt = Stopwatch.GetTimestamp();
            Stopwatch.GetElapsedTime(startedAt) < ObservableMeasurementSampleWindow;
            await Task.Delay(10)
        )
        {
            listener.RecordObservableInstruments();

            if (measurements.TryGetValue(instrumentName, out var values) && values.Any(predicate))
                return;
        }

        // A satisfying sample can land between the loop's final check and the deadline, so the
        // deadline is re-checked before failing: the guard fires only when the value is genuinely
        // still absent, never because the loop happened to stop one sample short of it.
        listener.RecordObservableInstruments();
        if (
            measurements.TryGetValue(instrumentName, out var finalValues)
            && finalValues.Any(predicate)
        )
            return;

        var observed = "nothing (the instrument was never observed)";
        if (finalValues is { Count: > 0 })
        {
            // Sampled every few ms, so the tail is the informative part; the count keeps the whole
            // history legible without printing all of it.
            var tail = finalValues.TakeLast(5).ToArray();
            observed =
                $"[{string.Join(", ", tail)}]" + $", {finalValues.Count} sample(s) in the window";
        }

        throw new InvalidOperationException(
            $"Did not observe a measurement satisfying the expected predicate for '{instrumentName}' "
                + $"within the {ObservableMeasurementSampleWindow.TotalSeconds:0} s sampling guard. "
                + $"The last sample of '{instrumentName}' was {observed}. That guard is a deadlock "
                + "bound, not a latency budget: a value that still does not satisfy the predicate at "
                + "that point is a stuck pipeline, not a slow machine."
        );
    }

    private static async Task AssertBuiltInConsoleSinkSerializesWritesToSharedNonConsoleWriterAsync(
        Func<TextWriter, ILogSink> createSink
    )
    {
        using var writer = new ConcurrentWriteDetectingWriter();
        await using var sink = createSink(writer);

        var firstWrite = Task.Run(() =>
            sink.WriteAsync(
                new LogEntry
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    Level = LogLevel.Warning,
                    Category = nameof(LoggerFactoryTests),
                    Message = "first",
                }
            )
        );

        await writer.FirstWriteEntered;

        var secondWrite = Task.Run(() =>
            sink.WriteAsync(
                new LogEntry
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    Level = LogLevel.Error,
                    Category = nameof(LoggerFactoryTests),
                    Message = "second",
                }
            )
        );

        await Task.Delay(50);
        writer.ReleaseWrites();

        await Task.WhenAll(firstWrite, secondWrite);

        await Assert.That(writer.ConcurrentWriteCount).IsEqualTo(0);
        await Assert.That(writer.Lines).Count().IsEqualTo(2);
    }

    private static async Task WaitForEntriesAsync(CollectingSink sink, int expectedCount)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (sink.Entries.Count >= expectedCount)
                return;

            await Task.Delay(10);
        }
    }

    private static int GetFactoryRegistrationCount(LoggerFactory factory)
    {
        var registrationsField = typeof(LoggerFactory).GetField(
            "_registrations",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
        );
        var registrations = registrationsField!.GetValue(factory)!;
        var countProperty = registrations.GetType().GetProperty("Count");

        return (int)countProperty!.GetValue(registrations)!;
    }

    private static int GetQueueDepthProviderCount(LoggerFactory factory)
    {
        var runtimeField = typeof(LoggerFactory).GetField(
            "_runtime",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
        );
        var runtime = runtimeField!.GetValue(factory)!;
        var providersField = typeof(PicoLogMetrics).GetField(
            "QueueDepthProviders",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic
        );
        var providers = (System.Collections.IDictionary)providersField!.GetValue(null)!;
        var matchingProviderCount = 0;

        foreach (System.Collections.DictionaryEntry providerEntry in providers)
        {
            if (providerEntry.Value is not Delegate provider)
                continue;

            var queue = provider.Target;

            if (queue is null)
                continue;

            var queueRuntimeField = queue
                .GetType()
                .GetField(
                    "_runtime",
                    System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic
                );
            var queueRuntime = queueRuntimeField?.GetValue(queue);

            if (ReferenceEquals(queueRuntime, runtime))
                matchingProviderCount++;
        }

        return matchingProviderCount;
    }
}

public sealed class LoggerConsumer(ILogger<LoggerConsumer> logger)
{
    public ILogger<LoggerConsumer> Logger { get; } = logger;
}
