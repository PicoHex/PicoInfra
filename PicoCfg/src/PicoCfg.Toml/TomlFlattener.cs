using System.Globalization;
using PicoSerDe.Core;
using PicoToml;

namespace PicoCfg.Toml;

/// <summary>
/// Flattens a TOML document into the <c>key:sub:leaf</c> dictionary PicoCfg binds against,
/// reading through <see cref="TomlReader"/> instead of splitting lines.
/// </summary>
/// <remarks>
/// <para>
/// The line splitter this replaces treated any line starting with '[' as a table header: it cut
/// <c>[[servers]]</c> into the header <c>[servers]</c>, so an array of tables flattened into the
/// single key <c>[servers]:name</c> with the bracket text inside the key and every row but the
/// last silently overwritten. Inline arrays became the literal string <c>"[1, 2, 3]"</c>, and a
/// dotted key (<c>a.b = 1</c>) produced <c>a:b</c> only by luck.
/// </para>
/// <para>
/// <b>What the reader gives us</b> (measured against PicoToml 2026.12.0): every token carries the
/// enclosing table in <see cref="TomlReader.TablePath"/> (dotted), scalars arrive fused on a
/// <c>PropertyName</c> token (<see cref="TomlReader.KeySpan"/> + <see cref="TomlReader.ValueSpan"/>),
/// <c>[[table]]</c> starts an <c>ArrayStart</c> with <see cref="TomlReader.IsArrayTable"/> set —
/// one per element, with NO matching ArrayEnd — and a value array is a plain
/// <c>ArrayStart</c>/<c>ArrayEnd</c> pair whose elements are bare value tokens.
/// </para>
/// </remarks>
internal static class TomlFlattener
{
    /// <param name="toml">UTF-8 TOML bytes.</param>
    /// <param name="flattenArrays">
    /// When <see langword="true"/>, array elements are indexed into the path — <c>servers:0:name</c>
    /// for an array of tables, <c>ports:0</c> for a value array — matching the list binding format
    /// used by CfgBind. Defaults to <see langword="false"/> (arrays are skipped), the same contract
    /// <c>AddJson</c> has.
    /// </param>
    public static Dictionary<string, string> Flatten(
        ReadOnlySpan<byte> toml,
        bool flattenArrays = false
    )
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (toml.IsEmpty)
            return result;

        var reader = new TomlReader(toml);
        var arrayTables = new Dictionary<string, int>(StringComparer.Ordinal);
        var arrays = new Stack<ArrayFrame>();
        var scope = ""; // prefix PropertyName tokens attach to ("" = document root)
        string? pendingKey = null;
        // The path the previous token published. An inline-array value arrives as a PropertyName
        // carrying the PREVIOUS key's value in ValueSpan (the reader keeps the old span for an
        // array value - measured against PicoToml 2026.12.1), so that PropertyName is not
        // recognised as the array's key and the entry it wrote has to be given back.
        string? written = null;
        // Set while the document is inside a [[table]] block. Array tables carry no end token, so
        // the block ends when a token reports a different table.
        string? arrayTableScope = null;

        while (reader.Read())
        {
            var publishedByPreviousToken = written;
            written = null;

            switch (reader.TokenType)
            {
                case TokenType.PropertyName:
                {
                    var key = Encoding.UTF8.GetString(reader.KeySpan);
                    if (arrayTableScope is not null && Table(reader) != arrayTableScope)
                    {
                        // The block ends with the token that reports another table, and the scope
                        // named ITS row - so the scope has to go with the block. Left in place it
                        // prefixed everything that followed: [z] below used to flatten b/c as
                        // s:0:b/s:0:c instead of z:b/z:c.
                        arrayTableScope = null;
                        scope = "";
                    }
                    if (reader.ValueSpan.IsEmpty)
                    {
                        pendingKey = key; // a value array follows
                        break;
                    }

                    // Inside an open array (or array table) the scope already names the element,
                    // so the table path must NOT be prepended a second time. And with
                    // flattenArrays off, an array's members are skipped — the same contract
                    // AddJson has ("arrays are skipped"), rather than flattened or dropped by luck.
                    if (arrayTableScope is not null && !flattenArrays)
                        break;

                    var prefix = scope.Length > 0 ? "" : Table(reader);
                    var path = Join(scope, prefix, key);
                    result[path] = Encoding.UTF8.GetString(reader.ValueSpan);
                    written = path;
                    pendingKey = null;
                    break;
                }

                // No [[table]] header can appear inside an open value array, so a bracket token
                // there is a nested array whatever the reader's flag says.
                case TokenType.ArrayStart
                    when arrays.Count == 0 && IsArrayTableHeader(reader, toml):
                {
                    // One ArrayStart per [[table]] element, with no ArrayEnd to balance it, so the
                    // element index has to come from a per-table counter rather than a stack.
                    var table = Table(reader);
                    var index = arrayTables.TryGetValue(table, out var seen) ? seen : 0;
                    arrayTables[table] = index + 1;
                    scope = Join("", table, index.ToString(CultureInfo.InvariantCulture));
                    arrayTableScope = table;
                    pendingKey = null;
                    break;
                }

                case TokenType.ArrayStart:
                {
                    // A value array, named by the PropertyName that opened it. Inside a [[table]]
                    // block (or another array) the scope already names the element, so the table
                    // path must not be prepended a second time. When the opening PropertyName
                    // carried a stale ValueSpan instead of naming the array, the array's key is
                    // the entry that PropertyName just published - which is not the array, so it
                    // is dropped before the elements are indexed under it.
                    var openScope = pendingKey is not null
                        ? Join(scope, scope.Length > 0 ? "" : Table(reader), pendingKey)
                        : publishedByPreviousToken ?? Join(scope, Table(reader), "");
                    if (pendingKey is null && publishedByPreviousToken is not null)
                        result.Remove(publishedByPreviousToken);
                    arrays.Push(new ArrayFrame(scope));
                    scope = openScope;
                    pendingKey = null;
                    break;
                }

                case TokenType.ArrayEnd:
                    if (arrays.Count > 0)
                        scope = arrays.Pop().Scope;
                    break;

                case TokenType.String:
                case TokenType.Int32:
                case TokenType.Int64:
                case TokenType.UInt32:
                case TokenType.UInt64:
                case TokenType.Float32:
                case TokenType.Float64:
                case TokenType.Bool:
                {
                    // Bare value token: an element of the value array that is open right now.
                    if (reader.TokenType == TokenType.Null || arrays.Count == 0)
                        break;

                    var frame = arrays.Pop();
                    var index = frame.NextIndex;
                    arrays.Push(frame with { NextIndex = index + 1 });
                    if (flattenArrays)
                        result[Join(scope, null, index.ToString(CultureInfo.InvariantCulture))] =
                            Encoding.UTF8.GetString(reader.ValueSpan);
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Distinguishes the <c>[[table]]</c> header from a value array inside a <c>[[table]]</c>
    /// block: both arrive as an <c>ArrayStart</c>, and both report <c>IsArrayTable</c> - the flag
    /// stays set until the next header is read (measured against PicoToml 2026.12.1), so it cannot
    /// tell them apart. The source can: a header is the line that begins with <c>[</c>, while a
    /// value array's <c>ArrayStart</c> starts past its own bracket, at the first element or at the
    /// closing <c>]</c>. The token start can sit on the blank line above its header (measured),
    /// which is why the scan steps over blanks before asking where the line begins. Byte values are
    /// written out here (9 = tab, 10 = LF, 13 = CR) to keep the arithmetic obvious.
    /// </summary>
    private static bool IsArrayTableHeader(TomlReader reader, ReadOnlySpan<byte> toml)
    {
        if (!reader.IsArrayTable)
            return false;

        var bracket = (int)reader.TokenStart;
        if (bracket < 0 || bracket >= toml.Length)
            return false;

        while (bracket < toml.Length && toml[bracket] is (byte)' ' or 9 or 10 or 13)
            bracket++;
        if (bracket >= toml.Length || toml[bracket] != (byte)'[')
            return false;

        // Indentation only between the line start and the bracket, or it is not a header.
        var lineStart = bracket;
        while (lineStart > 0 && toml[lineStart - 1] is (byte)' ' or 9)
            lineStart--;

        return lineStart == 0 || toml[lineStart - 1] is 10 or 13;
    }

    private static string Table(TomlReader reader) =>
        reader.TablePath.IsEmpty ? "" : Encoding.UTF8.GetString(reader.TablePath).Replace('.', ':');

    private static string Join(string scope, string? table, string leaf)
    {
        var parts = new List<string>(3);
        if (scope.Length > 0)
            parts.Add(scope);
        if (!string.IsNullOrEmpty(table))
            parts.Add(table!);
        if (leaf.Length > 0)
            parts.Add(leaf);
        return string.Join(':', parts);
    }

    private readonly record struct ArrayFrame(string Scope, int NextIndex = 0);
}
