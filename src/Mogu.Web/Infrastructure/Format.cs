using System.Diagnostics;
using System.Globalization;

namespace Mogu.Web.Infrastructure;

/// <summary>Display helpers matching the formatting of the original Jinja templates.</summary>
public static class Format
{
    private static readonly string[] BinaryPrefixes = ["KiB", "MiB", "GiB", "TiB", "PiB", "EiB", "ZiB", "YiB"];

    /// <summary>Same output as Jinja's <c>filesizeformat(binary=True)</c>, e.g. <c>1.5 GiB</c>.</summary>
    public static string FileSize(long value)
    {
        double bytes = value;
        if (bytes == 1)
        {
            return "1 Byte";
        }

        if (bytes < 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(long)bytes} Bytes");
        }

        for (var i = 0; i < BinaryPrefixes.Length; i++)
        {
            var unit = Math.Pow(1024, i + 2);
            if (bytes < unit || i == BinaryPrefixes.Length - 1)
            {
                return (1024 * bytes / unit).ToString("0.0", CultureInfo.InvariantCulture) + " " + BinaryPrefixes[i];
            }
        }

        throw new UnreachableException();
    }

    /// <summary>Unix timestamp in seconds, used by <c>data-timestamp</c> attributes for client-side localisation.</summary>
    public static long UnixSeconds(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    /// <summary><c>2024-01-31 13:37</c></summary>
    public static string Minutes(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary><c>2024-01-31 13:37 UTC</c></summary>
    public static string MinutesUtc(DateTime utc) => Minutes(utc) + " UTC";

    /// <summary>RFC 822 date as used in RSS <c>pubDate</c>.</summary>
    public static string Rfc822(DateTime utc) => utc.ToString("ddd, dd MMM yyyy HH:mm:ss '-0000'", CultureInfo.InvariantCulture);

    /// <summary>Possessive form used in the search placeholder: "kitsune's", "chris'".</summary>
    public static string Possessive(string name) => name.EndsWith('s') ? name + "'" : name + "'s";
}
