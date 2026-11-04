namespace PicoLog.Tests;

/// <summary>
/// Byte-level assertions on what <see cref="FileSink"/> puts on disk.
/// <para>
/// These deliberately do <b>not</b> use <c>File.ReadAllLines</c> / <c>File.ReadAllText</c>: the
/// former strips a UTF-8 preamble for you, so a log every other tool sees as
/// "ÿ»¿\[2025-…" reads back perfectly in a test that trusts it. The defect being pinned down
/// lives in the raw bytes (a BOM at offset 0, CR before every LF), so the assertions read
/// <see cref="File.ReadAllBytes(System.String)"/>.
/// </para>
/// </summary>
public class FileSinkEncodingTests
{
    /// <summary>Returns the message verbatim, so a test can compute the expected file bytes
    /// exactly — no timestamp or level noise between the assertion and the encoder.</summary>
    private sealed class VerbatimFormatter : ILogFormatter
    {
        public string Format(LogEntry entry) => entry.Message ?? string.Empty;
    }

    private static string GetTempFilePath() =>
        Path.Combine(Path.GetTempPath(), $"pico-encoding-{Guid.NewGuid():N}.log");

    private static LogEntry CreateEntry(string message) =>
        new()
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level = LogLevel.Info,
            Category = "Test",
            Message = message,
        };

    /// <summary>Writes the messages, shuts the sink down (which flushes and closes the handle),
    /// then returns the file's raw bytes.</summary>
    private static async Task<byte[]> WriteAndReadRawBytesAsync(params string[] messages)
    {
        var filePath = GetTempFilePath();
        var sink = new FileSink(
            new VerbatimFormatter(),
            new FileSinkOptions { FilePath = filePath, BatchSize = 1 }
        );
        try
        {
            foreach (var message in messages)
                await sink.WriteAsync(CreateEntry(message));

            await sink.DisposeAsync();
            return await File.ReadAllBytesAsync(filePath);
        }
        finally
        {
            TryDelete(filePath);
        }
    }

    /// <summary>
    /// A UTF-8 BOM is not part of a log line: it makes the first line unparseable for tailers,
    /// grep and every reader that does not transparently drop it.
    /// </summary>
    [Test]
    public async Task WrittenFile_StartsWithNoUtf8Bom()
    {
        var raw = await WriteAndReadRawBytesAsync("bom-check");
        await Assert.That(raw.Length).IsGreaterThanOrEqualTo(3).Because("the sink wrote nothing");

        var firstThree = $"{raw[0]:X2} {raw[1]:X2} {raw[2]:X2}";
        await Assert
            .That(firstThree)
            .IsNotEqualTo("EF BB BF")
            .Because(
                $"the file must not begin with a UTF-8 BOM (first3={firstThree}); "
                    + "StreamWriter over a fresh FileStream emits the preamble unless the "
                    + "encoding is constructed with encoderShouldEmitUTF8Identifier: false"
            );

        // Stronger than "not a BOM": offset 0 is the first character the caller logged.
        await Assert.That(raw[0]).IsEqualTo((byte)'b').Because($"first3={firstThree}");
    }

    /// <summary>
    /// Log lines are LF-terminated on every platform. CRLF would be invisible to
    /// <c>File.ReadAllLines</c> but breaks line counting on tools that split on LF and leaves a
    /// stray CR in every value on readers that split on LF only.
    /// </summary>
    [Test]
    public async Task WrittenFile_EndsLinesWithLfAndContainsNoCarriageReturn()
    {
        const byte CR = 13;
        const byte LF = 10;

        var raw = await WriteAndReadRawBytesAsync("first line", "second line");
        await Assert.That(raw.Length).IsGreaterThan(0).Because("the sink wrote nothing");

        var crCount = raw.Count(b => b == CR);
        await Assert
            .That(crCount)
            .IsEqualTo(0)
            .Because(
                $"lines must be LF-terminated; found {crCount} CR byte(s) — StreamWriter.NewLine "
                    + "defaults to Environment.NewLine, which is CRLF on Windows"
            );

        await Assert.That(raw[^1]).IsEqualTo((byte)LF).Because("the last line ends with LF");

        var lfCount = raw.Count(b => b == LF);
        await Assert
            .That(lfCount)
            .IsEqualTo(2)
            .Because($"two lines, so two LF bytes; got {lfCount}");
    }

    /// <summary>
    /// A message carrying a newline, a double quote and non-ASCII text must survive the trip to
    /// disk byte-for-byte. This is the assertion that catches an encoding that re-encodes or
    /// escapes, and it fails on a BOM too (U+FEFF changes the decoded string).
    /// </summary>
    [Test]
    public async Task WrittenFile_RoundTripsNewlineQuoteAndNonAsciiVerbatim()
    {
        const string message = "line one\nline \"two\" — héllo 日本語 ✓";

        var raw = await WriteAndReadRawBytesAsync(message);

        // Encoding.UTF8.GetString does not strip a BOM, so a preamble shows up here as U+FEFF.
        var decoded = Encoding.UTF8.GetString(raw);
        await Assert
            .That(decoded)
            .IsEqualTo(message + "\n")
            .Because($"raw={Convert.ToHexString(raw)}");

        // And the exact bytes: UTF-8 of the message plus a single LF terminator, nothing else.
        var expected = Encoding.UTF8.GetBytes(message + "\n");
        await Assert
            .That(raw.SequenceEqual(expected))
            .IsTrue()
            .Because(
                $"expected {Convert.ToHexString(expected)} but got {Convert.ToHexString(raw)}"
            );
    }

    /// <summary>
    /// The sharing half of the defect. The sink used to open with <c>FileShare.Read</c>, which
    /// denies every other handle that asks for <b>write</b> access: a rotator, a truncating log
    /// shipper or a second writer got IOException "being used by another process" for as long as
    /// the sink lived. <c>FileShare.ReadWrite</c> is what <c>JsonLogSink</c> already uses.
    /// </summary>
    [Test]
    public async Task LiveFile_AllowsAnotherWriteHandleWhileSinkIsOpen()
    {
        var filePath = GetTempFilePath();
        var sink = new FileSink(
            new VerbatimFormatter(),
            new FileSinkOptions { FilePath = filePath, BatchSize = 1 }
        );
        try
        {
            await sink.WriteAsync(CreateEntry("shared"));
            await sink.FlushAsync();

            Exception? openFailure = null;
            FileStream? second = null;
            try
            {
                second = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.ReadWrite
                );
            }
            catch (Exception ex)
            {
                openFailure = ex;
            }
            finally
            {
                if (second is not null)
                    await second.DisposeAsync();
            }

            await Assert
                .That(openFailure)
                .IsNull()
                .Because($"a live log must stay openable for write: {openFailure}");
        }
        finally
        {
            await sink.DisposeAsync();
            TryDelete(filePath);
        }
    }

    /// <summary>
    /// The tailing half, stated as literally as it can be: while the sink holds the file open, a
    /// reader that itself shares write (what <c>tail</c>, a log shipper or a test harness does)
    /// can open it and read what was flushed.
    /// <para>
    /// This assertion is <b>not</b> discriminating for the FileShare.Read → ReadWrite change on
    /// its own: Windows sharing is bilateral, so the reader's own FileShare.ReadWrite is what
    /// permits the sink's write access, and this open also succeeds while the sink shares only
    /// Read. It is here because it pins the contract — a regression to FileShare.None (or a
    /// reader that shares only Read) fails it. The change itself is pinned by
    /// <see cref="LiveFile_AllowsAnotherWriteHandleWhileSinkIsOpen"/>.
    /// </para>
    /// </summary>
    [Test]
    public async Task LiveFile_IsReadableThroughTailHandleThatSharesWrite()
    {
        var filePath = GetTempFilePath();
        var sink = new FileSink(
            new VerbatimFormatter(),
            new FileSinkOptions { FilePath = filePath, BatchSize = 1 }
        );
        try
        {
            await sink.WriteAsync(CreateEntry("tail-me"));
            await sink.FlushAsync();

            string content;
            await using (
                var tail = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete
                )
            )
            using (
                var reader = new StreamReader(
                    tail,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: false
                )
            )
            {
                content = await reader.ReadToEndAsync();
            }

            await Assert.That(content).Contains("tail-me");
        }
        finally
        {
            await sink.DisposeAsync();
            TryDelete(filePath);
        }
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
