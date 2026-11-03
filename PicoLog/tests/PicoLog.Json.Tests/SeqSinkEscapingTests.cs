namespace PicoLog.Json.Tests;

/// <summary>
/// SeqSink hand-rolls its JSON (PicoLog references only PicoLog.Abs — no serializer),
/// so the wire format has to be proved to PARSE, not to "contain" the payload.
/// Substring assertions were exactly the trap that hid the PicoLog.Json writer defect
/// (<c>{"Message":"x""Level":"Info"}</c> kept every substring and lost every comma).
/// These tests therefore feed payloads through the real sink, capture the bytes that
/// would hit <c>/api/events/raw</c>, and parse them back.
/// </summary>
/// <remarks>
/// System.Text.Json is used <b>in this test project only</b> as an independent JSON
/// oracle. The sink's escaping is production code and must stay dependency-free;
/// see the note in SeqSink's EscapeJson.
/// </remarks>
public sealed class SeqSinkEscapingTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public byte[]? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            Body = await request.Content!.ReadAsByteArrayAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.Created);
        }
    }

    private static async Task<byte[]> EmitAsync(params LogEntry[] entries)
    {
        var handler = new CapturingHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5341") };
        var sink = new SeqSink(httpClient, flushInterval: TimeSpan.FromMinutes(5));

        foreach (var entry in entries)
            await sink.WriteAsync(entry);
        await sink.FlushAsync();
        await sink.DisposeAsync();

        await Assert.That(handler.Body).IsNotNull();
        return handler.Body!;
    }

    private static Task<byte[]> EmitMessageAsync(string message) =>
        EmitAsync(
            new LogEntry
            {
                Timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                Level = LogLevel.Info,
                Message = message,
            }
        );

    /// <summary>Renders control characters as visible hex so a failure report is readable.</summary>
    private static string Show(string? value)
    {
        if (value is null)
            return "<null>";

        var sb = new StringBuilder("\"");
        foreach (var c in value)
        {
            if (c < 0x20 || c == 0x7F || char.IsSurrogate(c))
                sb.Append($"\\x{(int)c:X4}");
            else
                sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    // Every input the audit measured (U+0000, U+0008, U+000C, U+001F) plus the escape
    // classes and non-ASCII shapes a hand-rolled escaper also has to get right.
    // RoundTrip is what Seq must read back; it differs from Input only where the value
    // cannot survive UTF-8 (an unpaired surrogate).
    private static readonly (string Label, string Input, string RoundTrip)[] Samples =
    [
        ("U+0000 NUL", "pre\u0000post", "pre\u0000post"),
        ("U+0008 BS", "pre\bpost", "pre\bpost"),
        ("U+000C FF", "pre\fpost", "pre\fpost"),
        ("U+001F US", "pre\u001fpost", "pre\u001fpost"),
        ("U+0001 SOH", "pre\u0001post", "pre\u0001post"),
        ("U+000B VT (no short form)", "pre\u000bpost", "pre\u000bpost"),
        ("U+001F + all four audit chars", "\u0000\b\f\u001f", "\u0000\b\f\u001f"),
        ("quote", "pre\"post", "pre\"post"),
        ("backslash", "pre\\post", "pre\\post"),
        ("LF", "pre\npost", "pre\npost"),
        ("CR", "pre\rpost", "pre\rpost"),
        ("TAB", "pre\tpost", "pre\tpost"),
        ("backslash-then-quote", "\\\"", "\\\""),
        ("U+2028 line separator", "pre\u2028post", "pre\u2028post"),
        ("U+2029 paragraph separator", "pre\u2029post", "pre\u2029post"),
        ("emoji (surrogate pair)", "pre\U0001F600post", "pre\U0001F600post"),
        ("lone high surrogate", "pre\ud800post", "pre\ufffdpost"),
        ("lone low surrogate", "pre\udc00post", "pre\ufffdpost"),
    ];

    [Test]
    public async Task EmittedMessage_ParsesAsJson_ForEveryControlCharacterAndEscape()
    {
        var failures = new List<string>();

        foreach (var (label, input, roundTrip) in Samples)
        {
            var body = await EmitMessageAsync(input);
            try
            {
                using var doc = JsonDocument.Parse(body);
                var actual = doc
                    .RootElement.GetProperty("Events")[0]
                    .GetProperty("@mt")
                    .GetString();
                if (actual != roundTrip)
                    failures.Add(
                        $"{label}: parsed @mt = {Show(actual)}, expected {Show(roundTrip)}"
                    );
            }
            catch (JsonException ex)
            {
                failures.Add(
                    $"{label}: INVALID JSON — {ex.Message} | wire = "
                        + Show(Encoding.UTF8.GetString(body))
                );
            }
        }

        var report = failures.Count == 0 ? null : string.Join("\n", failures);
        await Assert.That(report).IsNull().Because("every emitted Seq line must parse as JSON");
    }

    [Test]
    public async Task EmittedPropertyKeysAndExceptionText_ParseAsJson()
    {
        var failures = new List<string>();

        var entries = new LogEntry[]
        {
            new()
            {
                Timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                Level = LogLevel.Info,
                Message = "key test",
                Properties =
                [
                    new KeyValuePair<string, object?>("key\u0000zero", "value\u0008back"),
                    new KeyValuePair<string, object?>("k\"q", "v\\b"),
                ],
            },
            new()
            {
                Timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                Level = LogLevel.Error,
                Message = "exception test",
                Exception = new InvalidOperationException("thrown\u0000by\u001fchar"),
            },
        };

        var body = await EmitAsync(entries);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var first = doc.RootElement.GetProperty("Events")[0];
            var names = first.EnumerateObject().Select(p => p.Name).ToList();
            await Assert.That(names).Contains("key\u0000zero");
            await Assert.That(names).Contains("k\"q");
            await Assert
                .That(first.GetProperty("key\u0000zero").GetString())
                .IsEqualTo("value\bback");
            await Assert.That(first.GetProperty("k\"q").GetString()).IsEqualTo("v\\b");

            var second = doc.RootElement.GetProperty("Events")[1];
            await Assert
                .That(second.GetProperty("@x").GetString()!)
                .Contains("thrown\u0000by\u001fchar");
        }
        catch (JsonException ex)
        {
            failures.Add(
                $"INVALID JSON — {ex.Message} | wire = {Show(Encoding.UTF8.GetString(body))}"
            );
        }

        var report = failures.Count == 0 ? null : string.Join("\n", failures);
        await Assert
            .That(report)
            .IsNull()
            .Because("property keys and exception text must escape too");
    }

    [Test]
    public async Task Escaping_UsesTheDocumentedForms_Exactly()
    {
        // input char -> emitted sequence. Short forms where JSON defines one,
        // \u00XX (lowercase hex, 4 digits) for every other C0 control.
        var table = new (string Label, char Input, string Form)[]
        {
            ("quote", '"', "\\\""),
            ("backslash", '\\', "\\\\"),
            ("backspace", '\b', "\\b"),
            ("formfeed", '\f', "\\f"),
            ("linefeed", '\n', "\\n"),
            ("carriage return", '\r', "\\r"),
            ("tab", '\t', "\\t"),
            ("U+0000", '\u0000', "\\u0000"),
            ("U+0001", '\u0001', "\\u0001"),
            ("U+000B", '\u000b', "\\u000b"),
            ("U+001F", '\u001f', "\\u001f"),
        };

        foreach (var (label, input, form) in table)
        {
            var body = Encoding.UTF8.GetString(await EmitMessageAsync($"x{input}y"));
            await Assert.That(body).Contains($"x{form}y").Because($"{label} must emit {form}");
        }

        // U+2028/U+2029 and astral pairs stay raw: valid JSON and cheaper than escaping.
        var separator = Encoding.UTF8.GetString(await EmitMessageAsync("x\u2028y"));
        await Assert.That(separator).Contains("x\u2028y");
        var emoji = Encoding.UTF8.GetString(await EmitMessageAsync("x\U0001F600y"));
        await Assert.That(emoji).Contains("x\U0001F600y");
    }
}
