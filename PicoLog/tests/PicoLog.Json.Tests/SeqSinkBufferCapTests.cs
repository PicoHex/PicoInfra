namespace PicoLog.Json.Tests;

/// <summary>
/// The hard cap on <c>SeqSink</c>'s buffer: drop-oldest, the policy the pipeline queue already has.
/// </summary>
/// <remarks>
/// <c>MaxBatchEntries</c> / <c>MaxBatchBytes</c> are TRIGGER thresholds — they start a drain, they do
/// not bound the buffer — and the only appender never drops and never refuses. A flush tick that
/// cannot run (a starved pool, a stalled HTTP call) therefore grew the sink's buffer without
/// bound: memory growth, and every entry in it lost if the process exits. Recorded in
/// <c>docs/release-notes/v2026.11.0.md</c> as the one consequence of the timer-shaped deadlines that
/// outlives a late tick.
/// </remarks>
public sealed class SeqSinkBufferCapTests
{
    /// <summary>The sink's hard entry cap is 20 batches of <c>MaxBatchEntries</c> = 100.</summary>
    private const int BufferCap = 100 * 20;

    private const string SeqEndpoint = "http://localhost:5341/api/events/raw";

    [Test]
    public async Task Buffer_WithAStalledFlush_DropsTheOldestAtTheHardCap()
    {
        var handler = new StallingRecordingHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5341") };
        var sink = new SeqSink(httpClient, flushInterval: TimeSpan.FromHours(1));

        try
        {
            // The first batch trips the trigger, and its send stalls: nothing drains after it.
            for (var i = 0; i < 100; i++)
                await sink.WriteAsync(new LogEntry { Message = $"entry-{i}" });
            await handler.Entered.WaitAsync(TimeSpan.FromSeconds(5));

            const int Overflow = 2500; // past the 2000-entry cap
            for (var i = 0; i < Overflow; i++)
                await sink.WriteAsync(new LogEntry { Message = $"stalled-{i}" });

            await Assert
                .That(sink.DroppedByCap)
                .IsEqualTo(Overflow - BufferCap)
                .Because(
                    "every entry past the cap is dropped, and they are counted rather than silently "
                        + "lost — this is what a caller observes when the cap trips"
                );

            handler.Release();
            await sink.DisposeAsync();

            var body = handler.LastBody ?? string.Empty;
            await Assert.That(handler.LastRequestUri).IsEqualTo(SeqEndpoint);
            await Assert
                .That(body)
                .Contains($"stalled-{Overflow - 1}")
                .Because("the newest entries are the ones kept");
            await Assert
                .That(body)
                .DoesNotContain("stalled-0\"")
                .Because("the oldest entries are the ones dropped");
        }
        finally
        {
            handler.Release();
        }
    }

    /// <summary>A handler that records the body, signals, and then parks until released.</summary>
    private sealed class StallingRecordingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public readonly SemaphoreSlim Entered = new(0);
        public volatile string? LastBody;
        public volatile string? LastRequestUri;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            LastRequestUri = request.RequestUri?.AbsoluteUri;
            LastBody = await request.Content!.ReadAsStringAsync(ct).ConfigureAwait(false);
            Entered.Release();
            await _release.Task.WaitAsync(ct).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.Created);
        }

        public void Release() => _release.TrySetResult();
    }
}
