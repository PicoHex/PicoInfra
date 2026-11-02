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
        // Set while the document is inside a [[table]] block. Array tables carry no end token, so
        // the block ends when a token reports a different table.
        string? arrayTableScope = null;

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case TokenType.PropertyName:
                {
                    var key = Encoding.UTF8.GetString(reader.KeySpan);
                    if (arrayTableScope is not null && Table(reader) != arrayTableScope)
                        arrayTableScope = null;
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
                    result[Join(scope, prefix, key)] = Encoding.UTF8.GetString(reader.ValueSpan);
                    pendingKey = null;
                    break;
                }

                case TokenType.ArrayStart when reader.IsArrayTable:
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
                    arrays.Push(new ArrayFrame(scope));
                    scope = Join(scope, Table(reader), pendingKey ?? "");
                    pendingKey = null;
                    break;

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
