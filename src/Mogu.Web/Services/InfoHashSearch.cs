using System.Text.RegularExpressions;

namespace Mogu.Web.Services;

/// <summary>Recognises search terms that are a torrent info hash (40 hex chars or 32 base32 chars).</summary>
public static partial class InfoHashSearch
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>The 20 byte info hash the term spells, or null if it isn't one.</summary>
    public static byte[]? TryParse(string? term)
    {
        if (string.IsNullOrEmpty(term))
        {
            return null;
        }

        if (HexHash().IsMatch(term))
        {
            return Convert.FromHexString(term);
        }

        return Base32Hash().IsMatch(term) ? DecodeBase32(term) : null;
    }

    /// <summary>RFC 4648 base32 without padding; null when the text contains characters outside the alphabet.</summary>
    internal static byte[]? DecodeBase32(string text)
    {
        var output = new byte[text.Length * 5 / 8];
        var buffer = 0;
        var bits = 0;
        var index = 0;
        foreach (var c in text)
        {
            var value = Base32Alphabet.IndexOf(char.ToUpperInvariant(c), StringComparison.Ordinal);
            if (value < 0)
            {
                return null;
            }

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output[index++] = (byte)(buffer >> bits);
                buffer &= (1 << bits) - 1;
            }
        }

        return output;
    }

    [GeneratedRegex(@"^[a-f0-9]{40}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HexHash();

    [GeneratedRegex(@"^[a-z0-9]{32}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Base32Hash();
}
