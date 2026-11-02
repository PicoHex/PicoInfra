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

            var content = await File.ReadAllTextAsync(file);
            await Assert.That(content).Contains("Something went wrong");
            await Assert.That(content).Contains("InvalidOperationException");
        }
        finally
        {
            if (File.Exists(file))
                File.Delete(file);
        }
    }

    [Test]
    public async Task Flush_WritesBufferedContentToDisk()
    {
        var file = Path.GetTempFileName();
        try
        {
            var sink = new JsonLogSink(file);
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
            // Force writer to release the file so it's readable
            await sink.DisposeAsync();

            var content = await File.ReadAllTextAsync(file);
            await Assert.That(content).Contains("flushed");
        }
        finally
        {
            if (File.Exists(file))
                File.Delete(file);
        }
    }
}
