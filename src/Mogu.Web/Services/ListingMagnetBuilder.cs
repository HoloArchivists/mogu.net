using System.Text;
using Mogu.Core.Options;

namespace Mogu.Web.Services;

/// <summary>
/// Magnet links for listings and RSS (old <c>torrents._create_magnet</c>): the info hash, the
/// display name and the default trackers (main announce first, de-duplicated) capped at
/// <see cref="MoguOptions.MagnetMaxTrackers"/>.
/// </summary>
/// <remarks>
/// Kept separate from the torrent page's magnet service so the browsing pages don't depend on it;
/// both produce the same format and can be merged.
/// </remarks>
public static class ListingMagnetBuilder
{
    public static string Create(string displayName, string infoHashHex, MoguOptions options)
    {
        var builder = new StringBuilder("magnet:?xt=urn:btih:")
            .Append(infoHashHex)
            .Append("&dn=")
            .Append(Uri.EscapeDataString(displayName));

        foreach (var tracker in DefaultTrackers(options).Take(options.MagnetMaxTrackers))
        {
            builder.Append("&tr=").Append(Uri.EscapeDataString(tracker));
        }

        return builder.ToString();
    }

    /// <summary>Main announce URL followed by the configured tracker list, without duplicates.</summary>
    public static IEnumerable<string> DefaultTrackers(MoguOptions options)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(options.MainAnnounceUrl) && seen.Add(options.MainAnnounceUrl))
        {
            yield return options.MainAnnounceUrl;
        }

        foreach (var tracker in options.DefaultTrackers)
        {
            if (!string.IsNullOrWhiteSpace(tracker) && seen.Add(tracker))
            {
                yield return tracker;
            }
        }
    }
}
