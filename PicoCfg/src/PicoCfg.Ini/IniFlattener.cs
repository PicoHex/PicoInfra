using PicoIni;
using PicoSerDe.Core;

namespace PicoCfg.Ini;

/// <summary>
/// Flattens an INI document into the <c>section:key</c> dictionary PicoCfg binds against,
/// reading through <see cref="IniReader"/> instead of splitting lines.
/// </summary>
/// <remarks>
/// <para>
/// INI has no nesting and no arrays, so this rewrite changes no semantics on purpose: the point is
/// that all four format backends now go through the format's own reader instead of each carrying
/// its own line splitter. That also makes quoting, comments and escapes the reader's problem.
/// </para>
/// <para>
/// <b>The one ambiguity INI has, and the rule that removes it</b>: a row is <c>section:key</c>, and
/// a section nests with <c>'.'</c> (<c>[Parent.Child]</c> → <c>Parent:Child</c>), so INI puts two
/// characters in competition for the same job and <c>':'</c> loses: <c>':'</c> is PicoCfg's path
/// separator and may not appear in a section name or a key. A name that carries one is rejected at
/// its own position — <c>[A:C]</c> is not a second way of spelling <c>[A.C]</c>, it is not a name at
/// all. Keeping <c>'.'</c> as the nesting rule is what makes this a fix rather than a reshuffle:
/// every <c>[Parent.Child]</c> document keeps the keys it has today, and the path this flattener
/// publishes becomes injective — one spelling per key — so no document can self-collide.
/// </para>
/// <para>
/// For completeness the flattener still runs through <see cref="CfgFlatKeyMap"/>, which is what the
/// other two backends need, so a row that reaches an already-published key from a different path
/// would throw rather than overwrite.
/// </para>
/// </remarks>
internal static class IniFlattener
{
    /// <param name="ini">UTF-8 INI bytes.</param>
    /// <param name="document">
    /// File name used in collision messages, or <see langword="null"/> for an in-memory document.
    /// </param>
    public static Dictionary<string, string> Flatten(
        ReadOnlySpan<byte> ini,
        string? document = null
    )
    {
        var map = new CfgFlatKeyMap("INI", document);
        if (ini.IsEmpty)
            return map.Values;

        var reader = new IniReader(ini);
        var section = "";
        // The section as it was SPELLED: '.' is what nests, so the raw text is what a rejection
        // quotes back to the reader of the error ("[A:C]").
        var sectionSpelling = "";
        string? pendingKey = null;
        long pendingKeyOffset = 0;

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case TokenType.ObjectStart:
                {
                    sectionSpelling = Encoding.UTF8.GetString(reader.GetStringRaw());
                    section = sectionSpelling.Replace('.', ':');
                    var offset = HeaderOffset(ini, reader.TokenStart, sectionSpelling);

                    // ':' inside a section name is the separator, so [A:C] addresses the section
                    // path [A.C] already addresses; it is not a name of its own. The check is on the
                    // SPELLING: the '.' in [A.C] has already become the ':' above.
                    if (sectionSpelling.IndexOf(':') >= 0)
                    {
                        throw map.RejectName(
                            ini,
                            "section name",
                            section,
                            $"[{sectionSpelling}]",
                            offset,
                            map.TryGetSection(section, out var spelled, out var line)
                                ? CfgKeyCollisionException.Position(line, $"[{spelled}]")
                                : null
                        );
                    }

                    if (section.Length > 0)
                        map.DeclareSection(ini, section, sectionSpelling, offset);
                    pendingKey = null;
                    break;
                }

                case TokenType.PropertyName:
                    pendingKey = Encoding.UTF8.GetString(reader.GetStringRaw());
                    pendingKeyOffset = reader.TokenStart;
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
                    if (pendingKey is null)
                        break;

                    var key = section.Length > 0 ? $"{section}:{pendingKey}" : pendingKey;

                    // Same rule on the key half: [A]\nB:C=1 addresses 'A:B:C', which [A.B]\nC=1
                    // already addresses, so 'B:C' is not a key name.
                    if (pendingKey.IndexOf(':') >= 0)
                    {
                        throw map.RejectName(
                            ini,
                            "key",
                            key,
                            sectionSpelling.Length > 0
                                ? $"[{sectionSpelling}] {pendingKey}"
                                : pendingKey,
                            pendingKeyOffset,
                            map.TryGetPosition(key, out var published) ? published : null
                        );
                    }

                    // An empty INI value stays an empty string (it is a value, not a missing key).
                    map.Set(
                        ini,
                        key,
                        Encoding.UTF8.GetString(reader.GetStringRaw()),
                        CfgFlatKeyMap.Origin(sectionSpelling, pendingKey),
                        pendingKeyOffset
                    );
                    pendingKey = null;
                    break;
                }
            }
        }

        return map.Values;
    }

    /// <summary>
    /// Where the section header really is. The reader reports a section token's <c>TokenStart</c> at
    /// the row BELOW the header for every section but the first (measured on
    /// <c>[A.C]</c>/<c>k=1</c>/<c>[A:C]</c>/<c>k=2</c>: 0 for the first header, the 'k' of the
    /// following line for the second), so the header is located by searching backwards for its own
    /// text — which also lands on the nearest one when two headers share a name.
    /// </summary>
    private static long HeaderOffset(
        ReadOnlySpan<byte> ini,
        long tokenStart,
        string sectionSpelling
    )
    {
        var limit = (int)Math.Clamp(tokenStart, 0, ini.Length);
        var header = Encoding.UTF8.GetBytes(string.Concat("[", sectionSpelling, "]"));
        var index = ini[..limit].LastIndexOf(header);
        return index >= 0 ? index : limit;
    }
}
