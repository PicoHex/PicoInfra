namespace PicoCfg.Abs;

/// <summary>
/// The flat-key dictionary ONE document flattens into, plus the guard that makes an ambiguous
/// flattening throw (<see cref="CfgKeyCollisionException"/>) instead of silently overwriting.
/// </summary>
/// <remarks>
/// <para>
/// A write is identified by its <b>origin</b>: the raw, pre-rewrite path segments it came from,
/// joined with <see cref="SegmentSeparator"/> (a YAML <c>a:\n  b: 1</c> is <c>a␟b</c>, the key
/// <c>"a:b"</c> is <c>a:b</c>). Two writes that produce the same flat key <i>and</i> the same origin
/// are the same mapping written twice — last wins, which is the long-standing contract for
/// <c>a: 1\na: 2</c>. Two writes that produce the same flat key from <i>different</i> origins are a
/// collision and throw.
/// </para>
/// <para>
/// Section headers are remembered separately (<see cref="DeclareSection"/>): they publish no value,
/// but a rejected name has to be able to say whether another spelling of the same path exists.
/// </para>
/// <para>
/// Line numbers are computed incrementally from the byte offset of the token that produced the
/// write (PicoYaml/PicoIni/PicoToml all expose <c>TokenStart</c>), so the common path costs one byte
/// scan per document — not per key — and only the text of a collision is ever formatted.
/// </para>
/// </remarks>
internal sealed class CfgFlatKeyMap(string format, string? documentName)
{
    /// <summary>
    /// Separator between two raw path segments inside an origin. Cannot appear in a real key without
    /// the key containing a control character, which is what makes the origin unambiguous.
    /// </summary>
    public const char SegmentSeparator = '\u001F';

    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Section> _sections = new(StringComparer.Ordinal);
    private long _scanned;
    private int _line = 1;

    /// <summary>The flattened document, in token order.</summary>
    public Dictionary<string, string> Values => _values;

    /// <summary>Builds an origin from the raw (pre-rewrite) segments of one write.</summary>
    public static string Origin(IReadOnlyList<string> segments)
    {
        var origin = new StringBuilder();
        foreach (var segment in segments)
        {
            if (string.IsNullOrEmpty(segment))
                continue;
            if (origin.Length > 0)
                origin.Append(SegmentSeparator);
            origin.Append(segment);
        }
        return origin.ToString();
    }

    /// <summary>Builds an origin from up to three raw segments, skipping the empty ones.</summary>
    public static string Origin(params string[] segments) =>
        Origin((IReadOnlyList<string>)segments);

    /// <summary>
    /// Writes <paramref name="value"/> under <paramref name="key"/>, throwing when an earlier write
    /// produced the same key from a different origin.
    /// </summary>
    /// <param name="document">The document being flattened, used for line numbers.</param>
    /// <param name="key">The flat key the caller is about to publish.</param>
    /// <param name="value">The value to publish.</param>
    /// <param name="origin">The raw segments the key was built from (see <see cref="Origin(string[])"/>).</param>
    /// <param name="offset">Byte offset of the token that produced the write.</param>
    public void Set(
        ReadOnlySpan<byte> document,
        string key,
        string value,
        string origin,
        long offset
    )
    {
        var line = LineOf(document, offset);

        if (_entries.TryGetValue(key, out var existing))
        {
            if (!string.Equals(existing.Origin, origin, StringComparison.Ordinal))
            {
                // Only a collision ever formats a position, so the common path allocates one string
                // (the origin) per key and nothing else.
                throw CfgKeyCollisionException.Collision(
                    format,
                    documentName,
                    key,
                    CfgKeyCollisionException.Position(existing.Line, Describe(existing.Origin)),
                    CfgKeyCollisionException.Position(line, Describe(origin))
                );
            }

            // The same mapping, written twice: last wins, and the position follows the winner.
            _entries[key] = existing with
            {
                Line = line,
            };
        }
        else
        {
            _entries[key] = new Entry(origin, line);
        }

        _values[key] = value;
    }

    /// <summary>
    /// Remembers a section header that published no value, so a later name that addresses the same
    /// path can name this position.
    /// </summary>
    /// <param name="document">The document being flattened, used for line numbers.</param>
    /// <param name="path">The normalised section path, e.g. <c>A:C</c> for <c>[A.C]</c>.</param>
    /// <param name="spelling">The header text as the document writes it, e.g. <c>A.C</c>.</param>
    /// <param name="offset">Byte offset of the header.</param>
    public void DeclareSection(
        ReadOnlySpan<byte> document,
        string path,
        string spelling,
        long offset
    ) => _sections[path] = new Section(spelling, LineOf(document, offset));

    /// <summary>
    /// Builds the error for a name that carries the separator itself, which a format only reaches
    /// when the separator is also a reserved character (INI).
    /// </summary>
    /// <param name="document">The document being flattened, used for line numbers.</param>
    /// <param name="what">What was rejected, e.g. <c>section name</c> or <c>key</c>.</param>
    /// <param name="key">The flat key the rejected name addresses.</param>
    /// <param name="written">The rejected name as the document writes it.</param>
    /// <param name="offset">Byte offset of the rejected name.</param>
    /// <param name="existingPosition">
    /// The position that already addresses <paramref name="key"/>, when the document has one.
    /// </param>
    public CfgKeyCollisionException RejectName(
        ReadOnlySpan<byte> document,
        string what,
        string key,
        string written,
        long offset,
        string? existingPosition
    ) =>
        CfgKeyCollisionException.NameContainsSeparator(
            format,
            documentName,
            what,
            key,
            existingPosition,
            CfgKeyCollisionException.Position(LineOf(document, offset), written)
        );

    /// <summary>How an earlier header spelled <paramref name="path"/>, and where it is.</summary>
    public bool TryGetSection(string path, out string spelling, out int line)
    {
        if (_sections.TryGetValue(path, out var section))
        {
            spelling = section.Spelling;
            line = section.Line;
            return true;
        }

        spelling = "";
        line = 0;
        return false;
    }

    /// <summary>The origin an already-published key was written from.</summary>
    public bool TryGetOrigin(string key, out string origin)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            origin = entry.Origin;
            return true;
        }

        origin = "";
        return false;
    }

    /// <summary>Where an already-published key was written, e.g. <c>line 4: a → b</c>.</summary>
    public bool TryGetPosition(string key, out string position)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            position = CfgKeyCollisionException.Position(entry.Line, Describe(entry.Origin));
            return true;
        }

        position = "";
        return false;
    }

    /// <summary>Gives back an entry a reader published and then re-read as something else.</summary>
    public void Remove(string key)
    {
        _values.Remove(key);
        _entries.Remove(key);
    }

    private int LineOf(ReadOnlySpan<byte> document, long offset)
    {
        if (offset < _scanned)
        {
            // Behind the cursor: an INI section header is located a few bytes above the row that
            // reported it, so the difference is counted over that short range instead of rescanning
            // the document from the top (which would make a section-rich file quadratic).
            var lines = 0;
            for (var i = offset; i < _scanned && i < document.Length; i++)
            {
                if (document[(int)i] == (byte)'\n')
                    lines++;
            }

            return Math.Max(1, _line - lines);
        }

        while (_scanned < offset && _scanned < document.Length)
        {
            if (document[(int)_scanned] == (byte)'\n')
                _line++;
            _scanned++;
        }

        return _line;
    }

    private static string Describe(string origin) =>
        origin.Replace(SegmentSeparator.ToString(), " → ", StringComparison.Ordinal);

    private readonly record struct Section(string Spelling, int Line);

    private readonly record struct Entry(string Origin, int Line);
}
