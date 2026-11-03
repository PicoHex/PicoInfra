namespace PicoLog.Json.Tests;

public class JsonLogSinkTests
{
    [Test]
    public async Task WriteEntry_WritesValidNdjson()
    {
        var file = Path.GetTempFileName();
        try
        {
            var sink = new JsonLogSink(file);
            var entry = new LogEntry
            {
                Timestamp = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
                Level = LogLevel.Info,
                Category = "Test",
                Message = "Hello, world!",
            };

            await sink.WriteAsync(entry);
            await sink.DisposeAsync();

            var lines = await File.ReadAllLinesAsync(file);
            await Assert.That(lines.Length).IsEqualTo(1);
            await Assert.That(lines[0]).Contains("Hello, world!");
            await Assert.That(lines[0]).Contains("Test");
            await Assert.That(lines[0]).Contains("Info");

            // Contains() is not the contract: this file is NDJSON, so every line has to PARSE.
            // The old writer misuse produced `{"Message":"x""Level":"Info"}` — substrings all
            // present, commas gone — and this test stayed green on it for months.
            var doc = PicoDocument.Parse(System.Text.Encoding.UTF8.GetBytes(lines[0]));
            var root = doc.RootElement;
            await Assert.That(root.TryGetProperty("Message", out var message)).IsTrue();
            await Assert.That(message.GetString()).IsEqualTo("Hello, world!");
            await Assert.That(root.TryGetProperty("Level", out var level)).IsTrue();
            await Assert.That(level.GetString()).IsEqualTo("Info");
            await Assert.That(root.TryGetProperty("Category", out var category)).IsTrue();
            await Assert.That(category.GetString()).IsEqualTo("Test");
        }
        finally
        {
            if (File.Exists(file))
                File.Delete(file);
        }
    }

    [Test]
    public async Task WriteEntry_WithException_WritesExceptionInfo()
    {
        var file = Path.GetTempFileName();
        try
        {
            var sink = new JsonLogSink(file);
            var entry = new LogEntry
            {
                Timestamp = DateTimeOffset.UtcNow,
                Level = LogLevel.Error,
                Category = "Test",
                Message = "Error occurred",
                Exception = new InvalidOperationException("Something went wrong"),
            };

            await sink.WriteAsync(entry);
            await sink.DisposeAsync();

            // Contains() is not the contract here either: the exception branch used to be the
            // one path no parse test reached, so emitting the triple as raw text with a missing
            // comma (`"ExceptionType":"…""Exception":"…"`) left this test green — both
            // substrings present, the line unparseable (review-2026-11-02 test-validity
            // finding 5). The line is NDJSON, so it has to PARSE, with the exception members
            // carrying the exception's data.
            var lines = await File.ReadAllLinesAsync(file);
            await Assert.That(lines.Length).IsEqualTo(1).Because("one entry, one NDJSON record");

            var doc = PicoDocument.Parse(System.Text.Encoding.UTF8.GetBytes(lines[0]));
            var root = doc.RootElement;

            await Assert.That(root.TryGetProperty("ExceptionType", out var exceptionType)).IsTrue();
            await Assert
                .That(exceptionType.GetString())
                .IsEqualTo("System.InvalidOperationException")
                .Because("the record must carry the exception's full type name");
            await Assert.That(root.TryGetProperty("Exception", out var exception)).IsTrue();
            await Assert
                .That(exception.GetString())
                .IsEqualTo("Something went wrong")
                .Because("the record must carry the exception's message");

            // ...and the rest of the record is still a well-formed entry.
            await Assert.That(root.TryGetProperty("Message", out var message)).IsTrue();
            await Assert.That(message.GetString()).IsEqualTo("Error occurred");
            await Assert.That(root.TryGetProperty("Level", out var level)).IsTrue();
            await Assert.That(level.GetString()).IsEqualTo("Error");
        }
        finally
        {
            if (File.Exists(file))
                File.Delete(file);
        }
    }

    /// <summary>
    /// NDJSON is one JSON value per LF-terminated line. <c>File.ReadAllLines</c> cannot see
    /// the defect this guards: it strips a UTF-8 BOM for you, so a file nobody else can
    /// parse reads back fine. This test reads the RAW BYTES: no BOM in front of record 0,
    /// no CR anywhere, and line 0 must be a parseable JSON object as it sits on disk.
    /// </summary>
    [Test]
    public async Task WrittenFile_StartsWithNoBomAndLinesEndWithLfOnly()
    {
        const byte CR = 13;
        const byte LF = 10;
        var file = Path.Combine(Path.GetTempPath(), $"picolog-raw-{Guid.NewGuid():N}.jsonl");
        try
        {
            var sink = new JsonLogSink(file);
            for (var i = 0; i < 2; i++)
            {
                await sink.WriteAsync(
                    new LogEntry
                    {
                        Timestamp = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
                        Level = LogLevel.Info,
                        Category = "Test",
                        Message = $"raw line {i}",
                    }
                );
            }

            await sink.DisposeAsync();

            var raw = await File.ReadAllBytesAsync(file);
            await Assert.That(raw.Length).IsGreaterThan(0).Because("the sink wrote nothing");

            var firstThree = $"{raw[0]:X2} {raw[1]:X2} {raw[2]:X2}";
            await Assert
                .That(firstThree)
                .IsNotEqualTo("EF BB BF")
                .Because(
                    $"record 0 must not be prefixed by a UTF-8 BOM (first3={firstThree}); "
                        + "0xEF is an invalid start of a JSON value for jq/JSON.parse"
                );

            var crCount = raw.Count(b => b == (byte)CR);
            await Assert
                .That(crCount)
                .IsEqualTo(0)
                .Because($"NDJSON lines are LF-terminated; found {crCount} CR byte(s)");

            await Assert.That(raw[^1]).IsEqualTo((byte)LF).Because("every record ends with LF");

            var lfIndex = Array.IndexOf(raw, (byte)LF);
            await Assert.That(lfIndex).IsGreaterThan(0).Because("line 0 must be LF-terminated");

            var line0 = raw[..lfIndex];
            var doc = PicoDocument.Parse(line0);
            var root = doc.RootElement;
            await Assert.That(root.TryGetProperty("Message", out var message)).IsTrue();
            await Assert.That(message.GetString()).IsEqualTo("raw line 0");
        }
        finally
        {
            if (File.Exists(file))
                File.Delete(file);
        }
    }

    /// <summary>
    /// A machine log must emit the same bytes under every locale. Serialising property and
    /// scope values through <c>object.ToString()</c> uses CurrentCulture, so under de-DE a
    /// double 1.5 became the JSON string "1,5" and a DateTime became "02.01.2025 03:04:05" —
    /// the value type is then unrecoverable for the consumer.
    /// </summary>
    [Test]
    public async Task WriteEntry_FormatsPropertyAndScopeValuesWithInvariantCulture()
    {
        var file = Path.Combine(Path.GetTempPath(), $"picolog-culture-{Guid.NewGuid():N}.jsonl");
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");

            var sink = new JsonLogSink(file);
            var when = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            await sink.WriteAsync(
                new LogEntry
                {
                    Timestamp = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero),
                    Level = LogLevel.Info,
                    Category = "Test",
                    Message = "culture",
                    Properties = [new("dbl", 1.5), new("date", when)],
                    ScopeProperties = [new("scopeDbl", 1.5)],
                }
            );
            await sink.DisposeAsync();

            var text = await File.ReadAllTextAsync(file);
            await Assert.That(text).Contains("1.5").Because("invariant double formatting is 1.5");
            await Assert.That(text).DoesNotContain("1,5").Because("de-DE would emit 1,5");
            await Assert
                .That(text)
                .DoesNotContain("02.01.2025 03:04:05")
                .Because("de-DE would emit the DateTime as 02.01.2025 03:04:05");
            await Assert
                .That(text)
                .Contains("01/02/2025 03:04:05")
                .Because("invariant DateTime.ToString() is 01/02/2025 03:04:05");

            // Values are still JSON strings (escaping unchanged), and the line still parses
            // as JSON — only the culture of the formatting changed.
            var doc = PicoDocument.Parse(
                System.Text.Encoding.UTF8.GetBytes(text.TrimEnd((char)10))
            );
            var root = doc.RootElement;
            await Assert.That(root.TryGetProperty("Properties", out var props)).IsTrue();
            await Assert.That(props.TryGetProperty("dbl", out var dbl)).IsTrue();
            await Assert.That(dbl.GetString()).IsEqualTo("1.5");
            await Assert.That(props.TryGetProperty("date", out var date)).IsTrue();
            await Assert.That(date.GetString()).IsEqualTo("01/02/2025 03:04:05");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
            if (File.Exists(file))
                File.Delete(file);
        }
    }

    [Test]
    public async Task Flush_WritesBufferedContentToDisk()
    {
        var file = Path.Combine(Path.GetTempPath(), $"picolog-flush-{Guid.NewGuid():N}.log");
        JsonLogSink? sink = null;
        try
        {
            sink = new JsonLogSink(file);
            await sink.WriteAsync(
                new LogEntry
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    Level = LogLevel.Info,
                    Category = "Test",
                    Message = "flushed",
                }
            );
            await sink.FlushAsync();

            // Read BEFORE disposing: DisposeAsync flushes as well, so disposing first made this
            // test pass with FlushAsync() gutted. Nothing but FlushAsync() has run here, and the
            // row must already be on disk. The read goes through a tailer-style handle because
            // File.ReadAllText cannot open a live log at all: Windows sharing is bilateral, so a
            // reader that shares only Read trips over the writer's access regardless of what the
            // sink shares (see LiveFile_AllowsAnotherWriteHandleWhileSinkIsOpen for that half).
            var content = await ReadLiveAsync(file);
            await Assert.That(content).Contains("flushed");
        }
        finally
        {
            if (sink is not null)
                await sink.DisposeAsync();
            if (File.Exists(file))
                File.Delete(file);
        }
    }

    /// <summary>
    /// The sink opens its append handle with FileShare.ReadWrite, which is what lets another
    /// process open the live file for write (a rotator, an external tool). Under the old
    /// FileShare.Read this second open fails with IOException "being used by another process".
    /// </summary>
    [Test]
    public async Task LiveFile_AllowsAnotherWriteHandleWhileSinkIsOpen()
    {
        var file = Path.Combine(Path.GetTempPath(), $"picolog-share-{Guid.NewGuid():N}.log");
        var sink = new JsonLogSink(file);
        try
        {
            await sink.WriteAsync(
                new LogEntry
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    Level = LogLevel.Info,
                    Category = "Test",
                    Message = "shared",
                }
            );

            Exception? openFailure = null;
            FileStream? second = null;
            try
            {
                second = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            }
            catch (Exception ex)
            {
                openFailure = ex;
            }
            finally
            {
                second?.Dispose();
            }

            await Assert
                .That(openFailure)
                .IsNull()
                .Because($"a live log must stay openable by other processes: {openFailure}");
        }
        finally
        {
            await sink.DisposeAsync();
            if (File.Exists(file))
                File.Delete(file);
        }
    }

    /// <summary>
    /// Reads a file the sink still holds open, the way a tailer does (FileShare.ReadWrite).
    /// File.ReadAllText would throw IOException here: its own FileShare.Read does not permit
    /// the writer's access.
    /// </summary>
    private static async Task<string> ReadLiveAsync(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete
        );
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
