namespace PicoLog.Json.Tests;

public sealed class SeqSinkTests
{
    private const string SeqEndpoint = "http://localhost:5341/api/events/raw";

    /// <summary>
    /// Records what the sink actually put on the wire. This handler used to only answer 201 and
    /// never look at <see cref="HttpRequestMessage.RequestUri"/> — so posting the batch to a wrong
    /// path kept every SeqSink test green (test-validity audit row 8).
    /// </summary>
    private sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }
        public HttpMethod? LastMethod { get; private set; }
        public string? LastBody { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            LastRequestUri = request.RequestUri;
            LastMethod = request.Method;
            LastBody = request.Content?.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            return Task.FromResult(handler(request));
        }
    }

    /// <summary>
    /// The sink's contract with the server: one POST to <c>/api/events/raw</c> carrying a COMPACT
    /// JSON batch. The old assertions were <c>Contains("\"@t\"")</c> / <c>Contains("Hello Seq")</c>,
    /// which verified neither half: the wrong endpoint was never observed, and indentation (or a
    /// missing comma) leaves both substrings in place. This asserts the endpoint, the compactness
    /// and then PARSES the batch, so the values have to come back out of a valid document
    /// (test-validity audit row 8).
    /// </summary>
    private static async Task AssertSeqBatchAsync(FakeHttpHandler handler, params string[] messages)
    {
        await Assert.That(handler.LastMethod).IsEqualTo(HttpMethod.Post);
        await Assert.That(handler.LastRequestUri?.AbsoluteUri).IsEqualTo(SeqEndpoint);

        var body = handler.LastBody;
        await Assert.That(body).IsNotNull();

        // Compact: one line, and no insignificant whitespace between tokens.
        await Assert
            .That(body!.Contains('\n'))
            .IsFalse()
            .Because("the batch is compact JSON, one line, not pretty-printed");
        await Assert
            .That(body)
            .DoesNotContain(": ")
            .Because("compact means no space after a member name");
        await Assert
            .That(body.StartsWith("{\"Events\":[{\"@t\":\"", StringComparison.Ordinal))
            .IsTrue()
            .Because("the envelope and its first member are emitted without padding");

        // ...and the batch has to PARSE: a missing comma keeps every substring and loses the JSON.
        using var doc = JsonDocument.Parse(body);
        var events = doc.RootElement.GetProperty("Events");
        await Assert.That(events.GetArrayLength()).IsEqualTo(messages.Length);
        for (var i = 0; i < messages.Length; i++)
        {
            await Assert.That(events[i].GetProperty("@mt").GetString()).IsEqualTo(messages[i]);
            await Assert.That(events[i].TryGetProperty("@t", out _)).IsTrue();
        }
    }

    [Test]
    public async Task WriteBatchAsync_SendsCompactJson_ToCorrectEndpoint()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
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

        await AssertSeqBatchAsync(handler, "Hello Seq");
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
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5341") };
        var sink = new SeqSink(httpClient);

        await sink.WriteAsync(new LogEntry { Message = "one" });
        await sink.WriteAsync(new LogEntry { Message = "two" });
        await sink.FlushAsync();

        await AssertSeqBatchAsync(handler, "one", "two");
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
        var signal = new TaskCompletionSource();
        var handler = new FakeHttpHandler(_ =>
        {
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
        await AssertSeqBatchAsync(handler, "periodic-flush");

        await sink.DisposeAsync();
    }

    [Test]
    public async Task DisposeAsync_FlushesRemainingEntries()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5341") };
        var sink = new SeqSink(httpClient);

        await sink.WriteAsync(new LogEntry { Message = "dispose-test" });
        await sink.DisposeAsync();

        await AssertSeqBatchAsync(handler, "dispose-test");
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
