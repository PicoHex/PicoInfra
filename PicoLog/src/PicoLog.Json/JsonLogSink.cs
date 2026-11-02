namespace PicoLog.Json;

/// <summary>
/// Writes log entries as newline-delimited JSON (NDJSON) to a file.
/// Uses PicoJetson for AOT-compatible zero-reflection JSON serialization.
/// </summary>
public sealed class JsonLogSink : ILogSink, IFlushableLogSink
{
    private readonly string _filePath;
    private readonly Lock _writeLock = new();
    private FileStream? _stream;
    private StreamWriter? _writer;
    private int _disposed;

    public JsonLogSink(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
    }

    /// <summary>
    /// Serializes the entry to JSON outside the lock, then writes under
    /// a single lock acquisition — eliminating the race window between
    /// EnsureWriter and the actual write that existed in the previous
    /// double-lock pattern. Also checks disposed state under lock to
    /// prevent silent writes after disposal.
    /// </summary>
    public Task WriteAsync(LogEntry entry, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Serialize JSON outside the lock to minimize contention.
        // The rented writer is released even on serialization failure.
        var jsonWriter = SerializerExtensions.RentWriter();
        string jsonLine;
        try
        {
            WriteJsonEntry(jsonWriter, entry);
            var bytes = jsonWriter.WrittenSpan;
            jsonLine = Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            jsonWriter.Clear();
        }

        lock (_writeLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            EnsureWriterUnderLock();
            _writer!.WriteLine(jsonLine);
        }

        return Task.CompletedTask;
    }

    public ValueTask FlushAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_writeLock)
        {
            _writer?.Flush();
            _stream?.Flush();
            _stream?.Flush(true);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return ValueTask.CompletedTask;

        lock (_writeLock)
        {
            _writer?.Dispose();
            _stream?.Dispose();
            _writer = null;
            _stream = null;
        }

        return ValueTask.CompletedTask;
    }

    private void EnsureWriterUnderLock()
    {
        if (_writer is not null)
            return;

        var dir = Path.GetDirectoryName(_filePath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _stream = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(_stream, Encoding.UTF8) { AutoFlush = false };
    }

    private static void WriteJsonEntry(IBufferWriter<byte> writer, LogEntry entry)
    {
        var jw = new JsonWriter(writer);
        jw.WriteStartObject();

        WriteProperty(ref jw, "Timestamp", entry.Timestamp.ToString("O"));
        WriteProperty(ref jw, "Level", entry.Level.ToString());
        if (entry.Category is not null)
            WriteProperty(ref jw, "Category", entry.Category);
        if (entry.Message is not null)
            WriteProperty(ref jw, "Message", entry.Message);
        if (entry.MessageTemplate is not null)
            WriteProperty(ref jw, "MessageTemplate", entry.MessageTemplate);
        if (entry.EventId != default && entry.EventId.Name is not null)
            WriteProperty(ref jw, "EventId", entry.EventId.Name);

        if (entry.Exception is not null)
        {
            WriteProperty(ref jw, "ExceptionType", entry.Exception.GetType().FullName!);
            WriteProperty(ref jw, "Exception", entry.Exception.Message);
            if (entry.Exception.StackTrace is not null)
                WriteProperty(ref jw, "StackTrace", entry.Exception.StackTrace);
        }

        if (entry.Properties is { Count: > 0 })
        {
            jw.WritePropertyName("Properties");
            jw.WriteStartObject();
            foreach (var prop in entry.Properties)
                WriteProperty(ref jw, prop.Key, prop.Value?.ToString());
            jw.WriteEndObject();
        }

        if (entry.ScopeProperties is { Count: > 0 })
        {
            jw.WritePropertyName("ScopeProperties");
            jw.WriteStartObject();
            foreach (var prop in entry.ScopeProperties)
                WriteProperty(ref jw, prop.Key, prop.Value?.ToString());
            jw.WriteEndObject();
        }

        jw.WriteEndObject();
    }

    // BY REF, because JsonWriter is a ref struct: a by-value parameter is a COPY, and every
    // byte (plus the "property name is pending" flag) it writes dies with the callee. Until
    // the PicoSerDe 2026.12.0 bump this was invisible rather than harmless — 2026.10.0 got the
    // bytes into the shared IBufferWriter and produced `{"Message":"x""Level":"Info"}` (invalid
    // JSON, commas lost with the copy's comma state), and the new span cache in 2026.11+ keeps
    // them in the copy entirely, so the line became `{}`. The test that should have caught it
    // asserted Contains() instead of parsing the line; JsonLogSinkTests now parses it.
    private static void WriteProperty(ref JsonWriter writer, string name, string? value)
    {
        writer.WritePropertyName(name);
        if (value is null)
            writer.WriteNull();
        else
            writer.WriteString(value);
    }
}
