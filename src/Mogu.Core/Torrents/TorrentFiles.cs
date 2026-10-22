using System.Text;

namespace Mogu.Core.Torrents;

/// <summary>Magnet URIs, as the old <c>torrents._create_magnet</c> built them.</summary>
public static class MagnetUri
{
    /// <summary><c>magnet:?xt=urn:btih:&lt;hex&gt;&amp;dn=&lt;name&gt;&amp;tr=&lt;tracker&gt;...</c> with at most <paramref name="maxTrackers"/> trackers.</summary>
    public static string Create(string displayName, string infoHashHex, IEnumerable<string> trackers, int maxTrackers)
    {
        var builder = new StringBuilder("magnet:?xt=urn:btih:")
            .Append(infoHashHex)
            .Append("&dn=")
            .Append(UploadRules.Quote(displayName, safe: ""));
        foreach (var tracker in trackers.Take(maxTrackers))
        {
            builder.Append("&tr=").Append(UploadRules.Quote(tracker, safe: ""));
        }

        return builder.ToString();
    }
}

public static class TrackerLists
{
    /// <summary>
    /// Announce list for generated torrents and magnets: our main tracker first, then the
    /// torrent's own trackers, then the default trackers, without duplicates.
    /// </summary>
    public static IReadOnlyList<string> Combine(string? mainAnnounceUrl, IEnumerable<string> torrentTrackers, IEnumerable<string> defaultTrackers)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var tracker in new[] { mainAnnounceUrl }.Concat(torrentTrackers).Concat(defaultTrackers))
        {
            if (!string.IsNullOrEmpty(tracker) && seen.Add(tracker))
            {
                result.Add(tracker);
            }
        }

        return result;
    }
}

/// <summary>Everything besides the info dict that goes into a generated .torrent.</summary>
/// <param name="Comment">Absolute URL of the torrent's page.</param>
/// <param name="Trackers">Announce URLs, main tracker first (see <see cref="TrackerLists.Combine"/>).</param>
public sealed record TorrentFileMetadata(
    DateTime CreatedAt,
    string Comment,
    string Encoding,
    IReadOnlyList<string> Trackers,
    IReadOnlyList<string> WebSeeds,
    string CreatedBy = "Mogu");

/// <summary>Builds .torrent files from stored info dicts (the old <c>create_bencoded_torrent</c>).</summary>
public static class TorrentFileBuilder
{
    /// <summary>
    /// Splices <paramref name="bencodedInfo"/> verbatim between the metadata keys sorting before
    /// and after <c>info</c>, so the info hash is exactly that of the stored dict.
    /// </summary>
    public static byte[] Build(ReadOnlySpan<byte> bencodedInfo, TorrentFileMetadata metadata)
    {
        var root = new BencodeDictionary
        {
            ["created by"] = metadata.CreatedBy,
            ["creation date"] = new DateTimeOffset(DateTime.SpecifyKind(metadata.CreatedAt, DateTimeKind.Utc)).ToUnixTimeSeconds(),
            ["comment"] = metadata.Comment,
            ["encoding"] = metadata.Encoding,
        };

        if (metadata.Trackers.Count > 0)
        {
            root["announce"] = metadata.Trackers[0];
        }

        if (metadata.Trackers.Count > 1)
        {
            // Yes, a list of single-element lists.
            root["announce-list"] = metadata.Trackers.Select(t => (object)new List<object> { t }).ToList();
        }

        if (metadata.WebSeeds.Count > 0)
        {
            root["url-list"] = metadata.WebSeeds.Cast<object>().ToList();
        }

        var prefixed = new BencodeDictionary();
        var suffixed = new BencodeDictionary();
        foreach (var (key, value) in root)
        {
            var target = Utf8OrdinalComparer.Instance.Compare(key, "info") < 0 ? prefixed : suffixed;
            target[key] = value;
        }

        var prefix = Bencode.Encode(prefixed);
        var suffix = Bencode.Encode(suffixed);

        var result = new byte[prefix.Length - 1 + 6 + bencodedInfo.Length + suffix.Length - 1];
        var span = result.AsSpan();
        prefix.AsSpan(0, prefix.Length - 1).CopyTo(span);
        span = span[(prefix.Length - 1)..];
        "4:info"u8.CopyTo(span);
        span = span[6..];
        bencodedInfo.CopyTo(span);
        span = span[bencodedInfo.Length..];
        suffix.AsSpan(1).CopyTo(span);
        return result;
    }
}
