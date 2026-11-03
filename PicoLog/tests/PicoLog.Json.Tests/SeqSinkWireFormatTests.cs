namespace PicoLog.Json.Tests;

/// <summary>
/// Pins the SeqSink request body byte for byte, and the price of producing it.
/// </summary>
/// <remarks>
/// The escaping tests prove the body <em>parses</em>; this one proves it is <em>the same body</em>
/// the hand-rolled StringBuilder version emitted, member order and quoting included. That matters
/// because the writer swap is supposed to change how the bytes are produced, not what they say —
/// and a "looks right" assertion is exactly what hid the lost-comma defect this suite exists for.
/// </remarks>
public sealed class SeqSinkWireFormatTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public byte[]? Body { get; private set; }
        public string? ContentType { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            Body = await request.Content!.ReadAsByteArrayAsync(ct);
            ContentType = request.Content.Headers.ContentType?.ToString();
            return new HttpResponseMessage(HttpStatusCode.Created);
        }
    }

    private static readonly DateTimeOffset Stamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static async Task<CapturingHandler> EmitAsync(params LogEntry[] entries)
    {
        var handler = new CapturingHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5341") };
        var sink = new SeqSink(client, flushInterval: TimeSpan.FromMinutes(5));

        foreach (var entry in entries)
            await sink.WriteAsync(entry);
        await sink.FlushAsync();
        await sink.DisposeAsync();
        return handler;
    }

    [Test]
    public async Task BatchBody_IsTheExactCLEFDocumentTheSinkAlwaysSent()
    {
        var handler = await EmitAsync(
            new LogEntry
            {
                Timestamp = Stamp,
                Level = LogLevel.Info,
                Message = "Hello Seq",
            },
            new LogEntry
            {
                Timestamp = Stamp,
                Level = LogLevel.Error,
                Message = "boom",
                Properties =
                [
                    new KeyValuePair<string, object?>("k", "v"),
                    new KeyValuePair<string, object?>("n", null),
                ],
                Exception = new InvalidOperationException("bad"),
            }
        );

        const string Expected =
            "{\"Events\":["
            + "{\"@t\":\"2026-01-01T00:00:00.0000000+00:00\",\"@mt\":\"Hello Seq\",\"@l\":\"Info\"},"
            + "{\"@t\":\"2026-01-01T00:00:00.0000000+00:00\",\"@mt\":\"boom\",\"@l\":\"Error\","
            + "\"k\":\"v\",\"n\":\"null\",\"@x\":\"System.InvalidOperationException: bad\"}"
            + "]}";

        await Assert.That(Encoding.UTF8.GetString(handler.Body!)).IsEqualTo(Expected);
        // A null property value is the string "null", not a JSON null — that is the sink's
        // long-standing wire shape and the pin above says so on purpose.
    }

    [Test]
    public async Task Request_KeepsTheJsonContentTypeWithCharset()
    {
        var handler = await EmitAsync(
            new LogEntry
            {
                Timestamp = Stamp,
                Level = LogLevel.Info,
                Message = "content type",
            }
        );

        // ByteArrayContent does not set a media type, so the sink sets the one StringContent
        // used to set — a consumer-visible header that must not drift with the body plumbing.
        await Assert.That(handler.ContentType).IsEqualTo("application/json; charset=utf-8");
    }

    /// <summary>
    /// Guards the allocation class, not a number: one copy of the batch, not one
    /// <see cref="StringBuilder"/> per event.
    /// </summary>
    /// <remarks>
    /// Measured on this machine (JIT, Release, median of two runs, 100-entry batches,
    /// <c>GC.GetAllocatedBytesForCurrentThread</c>): the hand-rolled serializer this replaced
    /// allocated <b>1487 B/event</b>; <see cref="SeqSink.BuildBatchBody"/> allocates
    /// <b>221 B/event</b>, of which 109 B is the one unavoidable copy of the UTF-8 body and
    /// ~90 B is <c>DateTime.ToString("O")</c> — a per-event string the old path paid too.
    /// The ceiling below sits above the measurement and far below the old figure, so it fails if
    /// a per-event string builder (or a per-attempt re-serialization) comes back, and passes on
    /// any reasonable runtime. It is an allocation count, not a timing claim.
    /// </remarks>
    [Test]
    public async Task BatchBody_PaysOneCopyPerBatch_NotOneBuilderPerEntry()
    {
        var batch = Enumerable
            .Range(0, 100)
            .Select(i => new LogEntry
            {
                Timestamp = Stamp,
                Level = LogLevel.Info,
                Category = "App.Orders",
                Message = $"User Alice logged in from 10.0.0.1 (event {i})",
            })
            .ToList();

        for (var warmup = 0; warmup < 200; warmup++)
            _ = SeqSink.BuildBatchBody(batch);

        const int Iterations = 500;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Iterations; i++)
            _ = SeqSink.BuildBatchBody(batch);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        var perEvent = allocated / (double)(Iterations * batch.Count);
        await Assert
            .That(perEvent)
            .IsLessThan(400.0)
            .Because($"one copy of the batch, not a builder per event (was {perEvent:F1} B/event)");
    }
}
