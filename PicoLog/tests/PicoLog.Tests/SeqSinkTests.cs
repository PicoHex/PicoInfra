namespace PicoLog.Tests;

public sealed class SeqSinkTests
{
    private sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        ) => Task.FromResult(handler(request));
    }

    [Test]
    public async Task WriteBatchAsync_SendsCompactJson_ToCorrectEndpoint()
    {
        string? capturedBody = null;
        var handler = new FakeHttpHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.Created);
        });
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5341") };
        var sink = new SeqSink(httpClient);

        var batch = new List<LogEntry>
        {
            new LogEntry
            {
                Timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                Level = LogLevel.Info,
                Message = "Hello Seq",
            },
        };

        await sink.WriteBatchAsync(batch);
        // Force flush to send
        await sink.FlushAsync();

        await Assert.That(capturedBody).IsNotNull();
        await Assert.That(capturedBody!).Contains("\"@t\"");
        await Assert.That(capturedBody!).Contains("Hello Seq");
    }

    [Test]
    public async Task WriteBatchAsync_SendsApiKeyHeader_WhenConfigured()
    {
        string? capturedApiKey = null;
        var handler = new FakeHttpHandler(req =>
        {
            capturedApiKey = req.Headers.GetValues("X-Seq-ApiKey").FirstOrDefault();
            return new HttpResponseMessage(HttpStatusCode.Created);
        });
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5341") };
        var sink = new SeqSink(httpClient, apiKey: "test-key");

        await sink.WriteBatchAsync([new LogEntry { Message = "test" }]);
        await sink.FlushAsync();

        await Assert.That(capturedApiKey).IsEqualTo("test-key");
    }

    [Test]
    public async Task FlushAsync_SendsBufferedEntries()
    {
        string? body = null;
        var handler = new FakeHttpHandler(req =>
        {
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.Created);
        });
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5341") };
        var sink = new SeqSink(httpClient);

        await sink.WriteAsync(new LogEntry { Message = "one" });
        await sink.WriteAsync(new LogEntry { Message = "two" });
        await sink.FlushAsync();

        await Assert.That(body).IsNotNull();
        await Assert.That(body!).Contains("one");
        await Assert.That(body!).Contains("two");
    }

    [Test]
    public async Task HttpFailure_IncrementsFailureCount_AndFallsBack()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(
            HttpStatusCode.ServiceUnavailable
        ));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5341") };
        var sink = new SeqSink(httpClient, enableConsoleFallback: true);

        await sink.WriteBatchAsync([new LogEntry { Message = "fail" }]);
        await sink.FlushAsync();

        await Assert.That(sink.FailureCount).IsGreaterThan(0);
        await Assert.That(sink.LastFailureTime).IsNotNull();
    }

    [Test]
    public async Task PeriodicFlush_SendsEntriesBelowBatchThreshold()
    {
        string? body = null;
        var signal = new TaskCompletionSource();
        var handler = new FakeHttpHandler(req =>
        {
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            signal.TrySetResult();
            return new HttpResponseMessage(HttpStatusCode.Created);
        });
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5341") };
        var sink = new SeqSink(httpClient, flushInterval: TimeSpan.FromMilliseconds(100));

        // Write a single entry (below batch threshold)
        await sink.WriteAsync(new LogEntry { Message = "periodic-flush" });

        // Wait for the periodic timer to trigger (up to 5 seconds)
        var completed = await Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        await Assert.That(completed).IsEqualTo(signal.Task);
        await Assert.That(body).IsNotNull();
        await Assert.That(body!).Contains("periodic-flush");

        await sink.DisposeAsync();
    }

    [Test]
    public async Task DisposeAsync_FlushesRemainingEntries()
    {
        string? body = null;
        var handler = new FakeHttpHandler(req =>
        {
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.Created);
        });
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5341") };
        var sink = new SeqSink(httpClient);

        await sink.WriteAsync(new LogEntry { Message = "dispose-test" });
        await sink.DisposeAsync();

        await Assert.That(body).IsNotNull();
        await Assert.That(body!).Contains("dispose-test");
    }

    private sealed class GatedHttpHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public readonly SemaphoreSlim Entered = new(0);
        public int Requests;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            Interlocked.Increment(ref Requests);
            Entered.Release();
            await _release.Task.WaitAsync(ct).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.Created);
        }

        public void Release() => _release.TrySetResult();
    }

    [Test]
    public async Task DisposeAsync_WaitsForInFlightThresholdDrain()
    {
        // The size-threshold drain is fire-and-forget. Disposing the sink while
        // that send is in flight used to dispose the HttpClient mid-request and
        // silently drop the batch; disposal must wait for the send to settle.
        var handler = new GatedHttpHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5341") };
        var sink = new SeqSink(httpClient, flushInterval: TimeSpan.FromHours(1));

        for (var i = 0; i < 100; i++) // the 100th entry trips the batch threshold
            await sink.WriteAsync(new LogEntry { Message = $"entry-{i}" });

        await handler.Entered.WaitAsync(TimeSpan.FromSeconds(5)); // send is in flight

        var dispose = sink.DisposeAsync().AsTask();
        await Task.Delay(300);
        await Assert.That(dispose.IsCompleted).IsFalse(); // must wait, not drop

        handler.Release();
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(handler.Requests).IsEqualTo(1);
    }

    private sealed class DisposalTrackingHandler : HttpMessageHandler
    {
        public bool Disposed;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        ) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created));

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    [Test]
    public async Task WriteAsync_AfterDispose_IsRejectedAndCounted()
    {
        // Disposal closes admission atomically: a write arriving afterwards must
        // be rejected and counted, never silently buffered for a drain that will
        // not happen. Assert the instance-scoped counter, not the process-wide
        // metric (parallel tests increment the same meter).
        var handler = new DisposalTrackingHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5341") };
        var sink = new SeqSink(client);
        await sink.DisposeAsync();

        await sink.WriteAsync(new LogEntry { Message = "late" }); // must not throw

        await Assert.That(sink.RejectedAfterClose).IsEqualTo(1);
    }

    [Test]
    public async Task DisposeAsync_TwiceWithLateWrites_DoesNotThrow()
    {
        // Guard for the disposal handshake: double dispose is a no-op and late
        // writes are rejected without touching the (now disposed) drain gate.
        var handler = new DisposalTrackingHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5341") };
        var sink = new SeqSink(client);

        await sink.DisposeAsync();
        await sink.DisposeAsync(); // idempotent
        await sink.WriteAsync(new LogEntry { Message = "late-1" });
        await sink.WriteAsync(new LogEntry { Message = "late-2" });

        await Assert.That(sink.RejectedAfterClose).IsEqualTo(2);
    }

    [Test]
    public async Task DisposeAsync_DoesNotDisposeInjectedHttpClient()
    {
        // Ownership mechanism: the HttpClient is a caller-owned dependency.
        // Disposing the sink used to dispose it, breaking every other consumer
        // that shares the client (DI / IHttpClientFactory) and poisoning the
        // pooled handler.
        var handler = new DisposalTrackingHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5341") };
        var sink = new SeqSink(client);

        await sink.WriteAsync(new LogEntry { Message = "before-dispose" });
        await sink.DisposeAsync();

        await Assert.That(handler.Disposed).IsFalse();

        // The caller's client is still fully usable after the sink is gone.
        var response = await client.GetAsync("/health");
        await Assert.That(response.IsSuccessStatusCode).IsTrue();
    }
}
