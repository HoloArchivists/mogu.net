using System.Globalization;
using System.Net;

namespace Mogu.LegacyImport;

/// <summary>Value conversions from the legacy (SQLAlchemy) representation.</summary>
public static class LegacyConvert
{
    private static readonly string[] DateTimeFormats =
    [
        "yyyy-MM-dd HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd",
    ];

    /// <summary>Discord epoch (2015-01-01) in Unix milliseconds.</summary>
    private const long DiscordEpochMs = 1420070400000;

    /// <summary>Parses SQLAlchemy's naive UTC DATETIME text, e.g. <c>2022-02-16 11:32:58.135478</c>.</summary>
    public static DateTime? ParseDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTime.ParseExact(
            value.Trim(),
            DateTimeFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }

    /// <summary>Packed 4/16 byte address to canonical string; null if absent or malformed.</summary>
    public static string? IpFromBlob(byte[]? packed) =>
        packed is { Length: 4 or 16 } ? new IPAddress(packed).ToString() : null;

    /// <summary>Creation time encoded in a Discord snowflake.</summary>
    public static DateTime SnowflakeTime(long snowflake) =>
        DateTime.UnixEpoch.AddMilliseconds((snowflake >> 22) + DiscordEpochMs);

    /// <summary>Lower-cases ASCII letters only, matching SQLite's NOCASE collation.</summary>
    public static string NoCaseKey(string value) =>
        string.Create(value.Length, value, static (span, s) =>
        {
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                span[i] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
            }
        });
}

/// <summary>Legacy <c>TorrentFlags</c> bitfield.</summary>
[Flags]
#pragma warning disable CA1711 // Mirrors the legacy TorrentFlags name.
public enum LegacyTorrentFlags
{
    None = 0,
    Anonymous = 1,
    Hidden = 2,
    Trusted = 4,
    Remake = 8,
    Complete = 16,
    Deleted = 32,
    Banned = 64,
    CommentLocked = 128,
}
#pragma warning restore CA1711
