using System.Text;

namespace Mogu.Core.Text;

public static class TextSanitizer
{
    /// <summary>
    /// Replaces characters that are illegal in XML (C0 controls other than tab/LF/CR, lone surrogates,
    /// U+FFFE and U+FFFF) with <paramref name="replacement"/>, like the old <c>backend.sanitize_string</c>.
    /// Valid surrogate pairs (emoji etc.) are kept.
    /// </summary>
    public static string SanitizeString(string value, char replacement = '�')
    {
        ArgumentNullException.ThrowIfNull(value);

        StringBuilder? builder = null;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                builder?.Append(c).Append(value[i + 1]);
                i++;
                continue;
            }

            var illegal = (c < '\x20' && c is not '\t' and not '\n' and not '\r')
                || char.IsSurrogate(c)
                || c is '￾' or '￿';

            if (illegal && builder is null)
            {
                builder = new StringBuilder(value.Length).Append(value, 0, i);
            }

            builder?.Append(illegal ? replacement : c);
        }

        return builder?.ToString() ?? value;
    }
}
