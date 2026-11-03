namespace PicoCfg.Abs;

/// <summary>
/// Thrown when one document cannot be flattened into a single <c>':'</c>-separated key space: two
/// of its positions address the same flat key, so one of them would silently overwrite the other.
/// </summary>
/// <remarks>
/// <para>
/// Every text backend publishes its document as ONE <c>a:b:c</c> dictionary, so the separator
/// <c>':'</c> cannot appear inside a path segment without becoming ambiguous: YAML
/// <c>a:\n  b: 1</c> and <c>"a:b": 2</c> are both <c>a:b</c>; TOML <c>[db.ssl]\non = false</c> and
/// <c>[db]\n"ssl:on" = true</c> are both <c>db:ssl:on</c>. Before this exception existed the later
/// value silently overwrote the earlier one and neither the winner nor the loser was reported.
/// </para>
/// <para>
/// The contract is deliberately <b>throw, not escape</b>: an escaping scheme would rename every
/// existing key and break <c>CfgBind</c> path matching, whereas a document that flattens ambiguously
/// is a document that cannot be read unambiguously. Duplicate keys <i>inside the same mapping</i> are
/// not a collision — that is last-wins and stays that way.
/// </para>
/// <para>
/// INI has one case the other formats do not: it already uses <c>'.'</c> as the way a section nests
/// (<c>[Parent.Child]</c> → <c>Parent:Child</c>), so a <c>':'</c> inside a section name or a key is
/// not a legal name at all — it would be a second spelling of a path the format already spells with
/// <c>'.'</c>. That is reported by this same exception, with <see cref="ExistingPosition"/> naming
/// the <c>'.'</c>-spelled position when the document contains one and <see langword="null"/> when it
/// does not (nothing else produced the key; the name itself is unaddressable).
/// </para>
/// <para>
/// The error is raised while the snapshot is built (i.e. from <c>CfgBuilder.BuildAsync</c> or when a
/// watched source reopens), never from a later lookup.
/// </para>
/// </remarks>
public sealed class CfgKeyCollisionException : InvalidOperationException
{
    /// <summary>The backend that detected the collision, e.g. <c>YAML</c> or <c>INI</c>.</summary>
    public string Format { get; }

    /// <summary>The flat key both positions address (for INI sections, the normalised section path).</summary>
    public string Key { get; }

    /// <summary>
    /// Human-readable position of the occurrence that was already there — a line number plus the
    /// raw path it came from, e.g. <c>line 1: a → b</c> — or <see langword="null"/> when nothing else
    /// produced <see cref="Key"/> and the rejected name is unusable on its own (INI only).
    /// </summary>
    public string? ExistingPosition { get; }

    /// <summary>Human-readable position of the occurrence that collided with it.</summary>
    public string IncomingPosition { get; }

    private CfgKeyCollisionException(
        string format,
        string key,
        string? existingPosition,
        string incomingPosition,
        string message
    )
        : base(message)
    {
        Format = format;
        Key = key;
        ExistingPosition = existingPosition;
        IncomingPosition = incomingPosition;
    }

    /// <summary>
    /// Two positions of one document flatten onto the same key. <paramref name="document"/> names the
    /// file, or is <see langword="null"/> for an in-memory document.
    /// </summary>
    internal static CfgKeyCollisionException Collision(
        string format,
        string? document,
        string key,
        string existingPosition,
        string incomingPosition
    ) =>
        new(
            format,
            key,
            existingPosition,
            incomingPosition,
            $"PicoCfg.{format}: two different positions in {Where(document)} flatten to the same key"
                + $" '{key}'."
                + $"\n  earlier: {existingPosition}"
                + $"\n  later:   {incomingPosition}"
                + "\nPicoCfg flattens a document into one ':'-separated key space, so a ':' inside a"
                + " name — and any '.' a format reads as a path separator — cannot be told apart"
                + " from real nesting. Without this error the later value would silently overwrite"
                + " the earlier one. Rename one of the two positions, or move one of them into a"
                + " separate configuration source."
        );

    /// <summary>
    /// A name carries the separator itself, so it cannot be written down unambiguously
    /// (<paramref name="what"/> is the part of the document that was rejected, e.g. <c>section</c>).
    /// <paramref name="existingPosition"/> is set when another spelling of the same path exists.
    /// </summary>
    internal static CfgKeyCollisionException NameContainsSeparator(
        string format,
        string? document,
        string what,
        string key,
        string? existingPosition,
        string incomingPosition
    ) =>
        new(
            format,
            key,
            existingPosition,
            incomingPosition,
            $"PicoCfg.{format}: the {what} at {incomingPosition}{In(document)} contains ':',"
                + " which is the separator"
                + $" PicoCfg joins path segments with, so it cannot be part of a name: it addresses"
                + $" '{key}'."
                + (
                    existingPosition is null
                        ? ""
                        : $"\n  already: {existingPosition} — '.' already produces '{key}', so the"
                            + " two spellings cannot be told apart."
                )
                + "\nINI spells nesting with '.' ([Parent.Child] → 'Parent:Child'), so drop the ':'"
                + " from the name — spell the section [Parent.Child], or rename the key — instead of"
                + " relying on it. Every key that does not carry a ':' keeps the value it has today."
        );

    /// <summary>A position in the document, as every collision reports it.</summary>
    internal static string Position(int line, string what) => $"line {line}: {what}";

    private static string Where(string? document) =>
        document is null ? "the same document" : $"'{document}'";

    private static string In(string? document) => document is null ? "" : $" in '{document}'";
}
