using PicoIni;
using PicoSerDe.Core;

namespace PicoCfg.Ini;

/// <summary>
/// Flattens an INI document into the <c>section:key</c> dictionary PicoCfg binds against,
/// reading through <see cref="IniReader"/> instead of splitting lines.
/// </summary>
/// <remarks>
/// INI has no nesting and no arrays, so this rewrite changes no semantics on purpose: the point is
/// that all four format backends now go through the format's own reader instead of each carrying
/// its own line splitter. That also makes quoting, comments and escapes the reader's problem —
/// <c>[A.C]</c> sections are still flattened with ':' as the separator, as before.
/// </remarks>
internal static class IniFlattener
{
    public static Dictionary<string, string> Flatten(ReadOnlySpan<byte> ini)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ini.IsEmpty)
            return result;

        var reader = new IniReader(ini);
        var section = "";
        string? pendingKey = null;

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case TokenType.ObjectStart:
                    section = Encoding.UTF8.GetString(reader.GetStringRaw()).Replace('.', ':');
                    pendingKey = null;
                    break;

                case TokenType.PropertyName:
                    pendingKey = Encoding.UTF8.GetString(reader.GetStringRaw());
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
                    // An empty INI value stays an empty string (it is a value, not a missing key).
                    result[key] = Encoding.UTF8.GetString(reader.GetStringRaw());
                    pendingKey = null;
                    break;
                }
            }
        }

        return result;
    }
}
