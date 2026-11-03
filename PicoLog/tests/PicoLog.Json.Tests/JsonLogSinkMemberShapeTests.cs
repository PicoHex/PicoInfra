using System.Text;

namespace PicoLog.Json.Tests;

/// <summary>
/// The wire shape of one NDJSON record, pinned from the outside.
/// </summary>
/// <remarks>
/// These tests exist because the writer calls inside <c>WriteJsonEntry</c> are being moved from
/// the two-call helper (<c>WritePropertyName</c> + <c>WriteX</c>, <c>ref JsonWriter</c>) to the
/// fused <c>Write*Property</c> overloads. A fused call that is one byte off — a lost comma, a
/// member written outside its object, a value staged in the writer's span cache and never
/// committed — still produces a substring-correct line, so <c>Contains()</c> assertions cannot
/// see it: the exact-bytes test below is the contract, and each of the others isolates one
/// property of the fused family (null, escaping, long payloads that force a span refetch).
/// </remarks>
public class JsonLogSinkMemberShapeTests
{
    /// <summary>
    /// Every member the sink can emit, in emission order, compared byte for byte.
    /// </summary>
    [Test]
    public async Task WriteEntry_FullyPopulated_PinsExactNdjsonLine()
    {
        var file = TempFile();
        try
        {
            var sink = new JsonLogSink(file);
            await sink.WriteAsync(
                new LogEntry
                {
                    Timestamp = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero),
                    Level = LogLevel.Warning,
                    Category = "Cat",
                    Message = "Hello",
                    MessageTemplate = "Hello {Name}",
                    EventId = new EventId(7, "E7"),
                    Exception = new FixedStackException("boom", "at Sink"),
                    Properties = [new("p", "v")],
                    ScopeProperties = [new("s", "w")],
                }
            );
            await sink.DisposeAsync();

            // Member order is the order WriteJsonEntry emits them in; a fused call that
            // reorders or drops one is a byte difference here even when the line still parses.
            const string Expected =
                "{\"Timestamp\":\"2025-01-02T03:04:05.0000000+00:00\",\"Level\":\"Warning\","
                + "\"Category\":\"Cat\",\"Message\":\"Hello\",\"MessageTemplate\":\"Hello {Name}\","
                + "\"EventId\":\"E7\",\"ExceptionType\":\"PicoLog.Json.Tests.JsonLogSinkMemberShapeTests+FixedStackException\","
                + "\"Exception\":\"boom\",\"StackTrace\":\"at Sink\","
                + "\"Properties\":{\"p\":\"v\"},\"ScopeProperties\":{\"s\":\"w\"}}";

            var line = (await File.ReadAllTextAsync(file)).TrimEnd('\n');
            await Assert.That(line).IsEqualTo(Expected);
        }
        finally
        {
            Delete(file);
        }
    }

    /// <summary>
    /// A dynamic property whose value is <c>null</c> must be JSON <c>null</c>, not the empty
    /// string: <c>WriteStringProperty(name, null)</c> is <c>""</c> (a null string converts to an
    /// empty <see cref="ReadOnlySpan{T}"/>), so the null branch is still an explicit decision.
    /// </summary>
    [Test]
    public async Task WriteEntry_PropertyWithNullValue_WritesJsonNull()
    {
        var file = TempFile();
        try
        {
            var sink = new JsonLogSink(file);
            await sink.WriteAsync(
                new LogEntry
                {
                    Timestamp = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    Level = LogLevel.Info,
                    Properties = [new("nil", null)],
                    ScopeProperties = [new("scopeNil", null)],
                }
            );
            await sink.DisposeAsync();

            var text = await File.ReadAllTextAsync(file);
            await Assert.That(text).Contains("\"nil\":null");
            await Assert.That(text).Contains("\"scopeNil\":null");
            await Assert.That(text).DoesNotContain("\"nil\":\"\"");

            var root = PicoDocument.Parse(Encoding.UTF8.GetBytes(text.TrimEnd('\n'))).RootElement;
            await Assert.That(root.TryGetProperty("Properties", out var props)).IsTrue();
            await Assert.That(props.TryGetProperty("nil", out var nil)).IsTrue();
            await Assert.That(nil.ValueKind).IsEqualTo(PicoValueKind.Null);
            await Assert.That(root.TryGetProperty("ScopeProperties", out var scope)).IsTrue();
            await Assert.That(scope.TryGetProperty("scopeNil", out var scopeNil)).IsTrue();
            await Assert.That(scopeNil.ValueKind).IsEqualTo(PicoValueKind.Null);
        }
        finally
        {
            Delete(file);
        }
    }

    /// <summary>
    /// Escaping through a fused call has to match what <c>WritePropertyName</c> + <c>WriteString</c>
    /// produced: quotes, backslashes, the short control forms and <c>\u00XX</c> for the rest,
    /// with a surrogate pair passing through untouched — and the line still parses back to the
    /// original string.
    /// </summary>
    [Test]
    public async Task WriteEntry_LiteralFieldsWithControlCharacters_EscapeAndRoundTrip()
    {
        var file = TempFile();
        try
        {
            var nasty = "q\"b\\s\nn\tt\rc\u0001\u001f emoji \U0001F600 end";
            var sink = new JsonLogSink(file);
            await sink.WriteAsync(
                new LogEntry
                {
                    Timestamp = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    Level = LogLevel.Info,
                    Category = nasty,
                    Message = nasty,
                    MessageTemplate = nasty,
                }
            );
            await sink.DisposeAsync();

            var text = await File.ReadAllTextAsync(file);
            // The raw control characters must not reach the file at all.
            await Assert.That(text).DoesNotContain("\u0001");
            await Assert.That(text).DoesNotContain("\u001f");
            // JsonWriter emits the canonical RFC 8259 uppercase form (\u001F); SeqSink's own
            // hand-rolled escaper uses lowercase (\u001f). Both are legal JSON — the point here
            // is only that this sink escapes through the writer, not through SeqSink's table.
            await Assert.That(text).Contains("\\u0001");
            await Assert.That(text).Contains("\\u001F");
            await Assert.That(text).Contains("\\n");
            await Assert.That(text).Contains("\\\"");

            var root = PicoDocument.Parse(Encoding.UTF8.GetBytes(text.TrimEnd('\n'))).RootElement;
            foreach (var name in (string[])["Category", "Message", "MessageTemplate"])
            {
                await Assert.That(root.TryGetProperty(name, out var value)).IsTrue();
                await Assert.That(value.GetString()).IsEqualTo(nasty).Because($"member {name}");
            }
        }
        finally
        {
            Delete(file);
        }
    }

    /// <summary>
    /// Payloads longer than the writer's 256-byte span cache, written through both the fused calls
    /// and the <c>ref</c> helper, in one document.
    /// </summary>
    /// <remarks>
    /// A long token makes <c>WriteRaw</c> flush the span cache and hand the bytes straight to the
    /// buffer, so the very next member is staged in a <em>fresh</em> span. If any call in the mix
    /// were a by-value copy of the writer, its bytes (and the comma state) would die in the copy
    /// and the line would lose members or separators — precisely the failure the helper's
    /// <c>ref</c> exists to prevent.
    /// </remarks>
    [Test]
    public async Task WriteEntry_LongMembersAcrossSpanRefetch_KeepEveryMemberAndSeparator()
    {
        var file = TempFile();
        try
        {
            var longLiteral = new string('L', 600);
            var longDynamic = new string('D', 600);
            var sink = new JsonLogSink(file);
            await sink.WriteAsync(
                new LogEntry
                {
                    Timestamp = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    Level = LogLevel.Info,
                    Category = longLiteral,
                    Message = longLiteral,
                    Properties = [new("dyn", longDynamic)],
                }
            );
            await sink.DisposeAsync();

            var root = PicoDocument
                .Parse(Encoding.UTF8.GetBytes((await File.ReadAllTextAsync(file)).TrimEnd('\n')))
                .RootElement;

            await Assert.That(root.TryGetProperty("Category", out var cat)).IsTrue();
            await Assert.That(cat.GetString()).IsEqualTo(longLiteral);
            await Assert.That(root.TryGetProperty("Message", out var msg)).IsTrue();
            await Assert.That(msg.GetString()).IsEqualTo(longLiteral);
            await Assert.That(root.TryGetProperty("Properties", out var props)).IsTrue();
            await Assert.That(props.TryGetProperty("dyn", out var dyn)).IsTrue();
            await Assert.That(dyn.GetString()).IsEqualTo(longDynamic);
        }
        finally
        {
            Delete(file);
        }
    }

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"picolog-shape-{Guid.NewGuid():N}.jsonl");

    private static void Delete(string file)
    {
        if (File.Exists(file))
            File.Delete(file);
    }

    /// <summary>
    /// An exception whose stack trace is a fixed string: the real one carries absolute build
    /// paths, which would make the exact-bytes assertion environment-dependent.
    /// </summary>
    private sealed class FixedStackException(string message, string stackTrace) : Exception(message)
    {
        public override string? StackTrace => stackTrace;
    }
}
