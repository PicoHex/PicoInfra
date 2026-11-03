using System.Globalization;
using PicoSerDe.Core;
using PicoYaml;

namespace PicoCfg.Yaml;

/// <summary>
/// Flattens a YAML document into the <c>key:sub:leaf</c> dictionary PicoCfg binds against,
/// reading through <see cref="YamlReader"/> instead of splitting lines.
/// </summary>
/// <remarks>
/// <para>
/// The line splitter this replaces was blind in exactly the places YAML differs from INI: a
/// sequence item (<c>- a</c>) has no ':', so it was skipped; a block scalar's body has no ':',
/// so it was skipped too (leaving <c>Script</c> = <c>"|"</c>); a mapping response that YAML
/// fuses onto one line was cut at its first ':' with no notion of indentation depth. Every one
/// of those was silent — the document parsed, values just were not there.
/// </para>
/// <para>
/// <b>What the reader gives us</b> (measured against PicoYaml 2026.12.0): mappings arrive as
/// <c>PropertyName</c> tokens carrying the key in <see cref="YamlReader.KeySpan"/> and a fused
/// scalar in <see cref="YamlReader.ValueSpan"/> (empty when a container follows); sequences
/// arrive as an <c>ObjectStart</c> whose children are BARE value tokens with no key. So "bare
/// value token" means "sequence item" — that is the only signal, because KeySpan keeps the
/// previous key on item tokens.
/// </para>
/// <para>
/// <b>Known reader gap, compensated nowhere</b>: YAML's first inline member of a sequence item
/// (<c>- name: x</c>) is handed over as one raw string (<c>name: x</c>) and its continuation
/// arrives as a separate nested object, so a sequence of mappings flattens as
/// <c>Items:0 = "name: x"</c>, <c>Items:0:port = "1"</c> with <c>flattenArrays: true</c>, and a
/// nested sequence (<c>- - 1</c>) as <c>"- 1"</c>. Splitting those would put a hand-written
/// YAML parser back in this file, which is the bug being fixed; the tokenizer is the thing that
/// has to carry them.
/// </para>
/// </remarks>
internal static class YamlFlattener
{
    /// <param name="yaml">UTF-8 YAML bytes.</param>
    /// <param name="flattenArrays">
    /// When <see langword="true"/>, sequence elements are indexed into the path
    /// (<c>Servers:0</c>, <c>Items:0</c>). Defaults to <see langword="false"/> — sequences are
    /// skipped, which is the same contract <c>AddJson</c> has.
    /// </param>
    public static Dictionary<string, string> Flatten(
        ReadOnlySpan<byte> yaml,
        bool flattenArrays = false
    )
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (yaml.IsEmpty)
            return result;

        var reader = new YamlReader(yaml);
        // The implicit document root. Every container pushes a frame; a frame whose Key is a
        // mapping member prefixes its children, and a frame's NextIndex names sequence items.
        var frames = new List<Frame> { new("") };
        string? pendingKey = null;
        // Set when a sequence-item scalar was just consumed: an ObjectStart right after it is the
        // continuation of that same item (YAML fuses the item's first inline member into the
        // scalar, so the rest of the mapping arrives as a nested object).
        string? pendingItemIndex = null;

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case TokenType.PropertyName:
                {
                    var key = Encoding.UTF8.GetString(reader.KeySpan);
                    if (reader.ValueSpan.IsEmpty)
                    {
                        // Either a container follows or the member is null; the next token decides.
                        pendingKey = key;
                        break;
                    }

                    // With flattenArrays off the whole indexed subtree is skipped, not just the
                    // sequence's own scalars: a sequence-of-mappings item is handed over as a fused
                    // scalar plus a continuation object, and every member of that object is still
                    // part of the sequence (Items:0:port used to leak with the flag off).
                    if (!flattenArrays && frames[^1].UnderIndex)
                        break;

                    result[Path(frames, key)] = Encoding.UTF8.GetString(reader.ValueSpan);
                    pendingKey = null;
                    break;
                }

                // Mappings open with ObjectStart and sequences with ArrayStart (PicoSerDe
                // 2026.12.1: "a sequence's container token is an array, not an object"). Both
                // carry the owning key on the preceding PropertyName, and both close with their
                // own kind, so one frame per container serves either.
                case TokenType.ObjectStart:
                case TokenType.ArrayStart:
                    // A container keyed by an item index, or any container inside one, sits under
                    // an index segment; that is what the flattenArrays gate below keys off.
                    frames.Add(
                        new Frame(
                            pendingItemIndex ?? pendingKey ?? "",
                            pendingItemIndex is not null || frames[^1].UnderIndex
                        )
                    );
                    pendingKey = null;
                    pendingItemIndex = null;
                    break;

                case TokenType.ObjectEnd:
                case TokenType.ArrayEnd:
                    // The reader emits one trailing ObjectEnd past the last close (measured), so
                    // the pop has to be guarded rather than trusted.
                    if (frames.Count > 1)
                        frames.RemoveAt(frames.Count - 1);
                    pendingKey = null;
                    break;

                case TokenType.String:
                case TokenType.Int32:
                case TokenType.Int64:
                case TokenType.UInt32:
                case TokenType.UInt64:
                case TokenType.Float32:
                case TokenType.Float64:
                case TokenType.Bool:
                case TokenType.Null:
                {
                    // A bare value token is a sequence item: no key was written for it.
                    if (reader.TokenType == TokenType.Null)
                        break;

                    var current = frames[^1];
                    var index = current.NextIndex++;
                    if (flattenArrays)
                        result[$"{Path(frames, null)}:{index}"] = Encoding.UTF8.GetString(
                            reader.ValueSpan
                        );
                    pendingItemIndex = index.ToString(CultureInfo.InvariantCulture);
                    break;
                }
            }
        }

        return result;
    }

    private static string Path(List<Frame> frames, string? leaf)
    {
        var parts = new List<string>(frames.Count);
        for (var i = 1; i < frames.Count; i++)
            if (frames[i].Key.Length > 0)
                parts.Add(frames[i].Key);
        if (!string.IsNullOrEmpty(leaf))
            parts.Add(leaf!);
        return string.Join(':', parts);
    }

    // underIndex: this frame's path already runs through a sequence index segment - the item
    // itself, or anything nested inside it.
    private sealed class Frame(string key, bool underIndex = false)
    {
        public string Key { get; } = key;
        public bool UnderIndex { get; } = underIndex;
        public int NextIndex { get; set; }
    }
}
