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

        // FileShare.ReadWrite, not Read: the live file stays openable by another process that
        // shares it (a rotator, an external tool). Windows sharing is bilateral, so a reader
        // still has to share write itself — File.ReadAllText fails on any open write handle,
        // whatever this sink shares.
        _stream = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        // UTF8Encoding(false): a StreamWriter over an empty append target emits the preamble
        // (it only suppresses it when the seekable stream is already positioned), so record 0
        // was literally "U+FEFF{...}", which strict readers (JSON.parse, jq) reject.
        // NewLine: NDJSON is one JSON value per LF-terminated line, and StreamWriter.NewLine
        // would be CRLF on Windows.
        _writer = new StreamWriter(
            _stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        )
        {
            AutoFlush = false,
            NewLine = "\n",
        };
    }

    private static void WriteJsonEntry(IBufferWriter<byte> writer, LogEntry entry)
    {
        // NOT `using var`: the two dynamic-key loops further down still hand the writer to the
        // ref-taking helper below, and `ref` of a `using`-declared local is CS1657 (a using
        // variable is readonly). The scope hook `using` would run is therefore run by hand at the
        // end of this method, so the bytes no longer depend on the implicit "closing the
        // outermost container commits" inside WriteEndObject.
        var jw = new JsonWriter(writer);
        jw.WriteStartObject();

        // The literal names go through the fused Write*Property overloads: one call that cannot be
        // split between the name and the value, so the span-cache-copy bug the ref helper exists
        // to dodge cannot be reintroduced here by a future edit. The fused call delegates to the
        // two-call form, so the bytes are identical: same separator, same escaping, same values.
        // Every one of these nine is a string on the wire; the family's integer/bool/null
        // overloads stay unused because changing a member's JSON type is a format change, not a
        // refactor.
        jw.WriteStringProperty("Timestamp"u8, entry.Timestamp.ToString("O"));
        jw.WriteStringProperty("Level"u8, entry.Level.ToString());
        if (entry.Category is not null)
            jw.WriteStringProperty("Category"u8, entry.Category);
        if (entry.Message is not null)
            jw.WriteStringProperty("Message"u8, entry.Message);
        if (entry.MessageTemplate is not null)
            jw.WriteStringProperty("MessageTemplate"u8, entry.MessageTemplate);
        if (entry.EventId != default && entry.EventId.Name is not null)
            jw.WriteStringProperty("EventId"u8, entry.EventId.Name);

        if (entry.Exception is not null)
        {
            jw.WriteStringProperty("ExceptionType"u8, entry.Exception.GetType().FullName!);
            jw.WriteStringProperty("Exception"u8, entry.Exception.Message);
            if (entry.Exception.StackTrace is not null)
                jw.WriteStringProperty("StackTrace"u8, entry.Exception.StackTrace);
        }

        if (entry.Properties is { Count: > 0 })
        {
            jw.WritePropertyName("Properties");
            jw.WriteStartObject();
            foreach (var prop in entry.Properties)
                WriteProperty(ref jw, prop.Key, FormatValue(prop.Value));
            jw.WriteEndObject();
        }

        if (entry.ScopeProperties is { Count: > 0 })
        {
            jw.WritePropertyName("ScopeProperties");
            jw.WriteStartObject();
            foreach (var prop in entry.ScopeProperties)
                WriteProperty(ref jw, prop.Key, FormatValue(prop.Value));
            jw.WriteEndObject();
        }

        jw.WriteEndObject();

        // Explicit commit on the non-`using` path. WriteEndObject flushes only when depth reaches
        // 0, which glued "the document is complete" to "the bytes reached the buffer" through a
        // side effect; Dispose() is the same hook `using` would run (Flush, idempotent, never
        // throws), so the tail is handed over once, on purpose, and stays handed over if anyone
        // ever appends a member below this line.
        jw.Dispose();
    }

    // Values are stringified for JSON, so the format has to be culture-stable: object.ToString()
    // is CurrentCulture, which turned a double 1.5 into "1,5" and a DateTime into
    // "02.01.2025 03:04:05" under de-DE — the same binary emitting different bytes per locale,
    // with the value type unrecoverable for the consumer. Everything else here is already
    // culture-stable ("O" timestamps, Level.ToString()); the escaping itself is unchanged
    // because the result still goes through JsonWriter.WriteString.
    private static string? FormatValue(object? value) =>
        value is null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);

    // BY REF, because JsonWriter is a ref struct: a by-value parameter is a COPY, and every
    // byte (plus the "property name is pending" flag) it writes dies with the callee. Until
    // the PicoSerDe 2026.12.0 bump this was invisible rather than harmless — 2026.10.0 got the
    // bytes into the shared IBufferWriter and produced `{"Message":"x""Level":"Info"}` (invalid
    // JSON, commas lost with the copy's comma state), and the new span cache in 2026.11+ keeps
    // them in the copy entirely, so the line became `{}`. The test that should have caught it
    // asserted Contains() instead of parsing the line; JsonLogSinkTests now parses it.
    //
    // KEPT for exactly two call sites, both dynamic keys: `prop.Key` in the Properties and
    // ScopeProperties loops. The fused Write*Property overloads take the name as
    // `ReadOnlySpan<byte>`, so a string key would have to be encoded first — either
    // Encoding.UTF8.GetBytes (one byte[] per property per event, the +32 B measured earlier) or
    // a stackalloc dance at every call site. The helper hands the string to WritePropertyName,
    // which already encodes internally into a stack buffer while a name's worst case is <= 256
    // UTF-8 bytes, so the dynamic path stays allocation-free for ordinary keys; the nine literal
    // names are `u8` literals and allocate nothing at all.
    //
    // It is also the only reason WriteJsonEntry cannot declare `using var jw`: CS1657 forbids
    // passing a using variable by ref, which is why that method ends with an explicit
    // jw.Dispose() instead of a scope hook.
    private static void WriteProperty(ref JsonWriter writer, string name, string? value)
    {
        writer.WritePropertyName(name);
        if (value is null)
            writer.WriteNull();
        else
            writer.WriteString(value);
    }
}
