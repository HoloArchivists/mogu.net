using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Mogu.Core.Entities;
using Mogu.Core.Options;
using Mogu.Core.Torrents;

namespace Mogu.Web.Infrastructure;

/// <summary>
/// Magnet links for torrents: our main tracker first, then <see cref="MoguOptions.DefaultTrackers"/>,
/// at most <see cref="MoguOptions.MagnetMaxTrackers"/> of them. Singleton; results are cached
/// (listings build the same links over and over) and the cache resets when the options change.
/// </summary>
public sealed class TorrentLinks : IDisposable
{
    private const int MaxCacheEntries = 4096;

    private readonly IOptionsMonitor<MoguOptions> _options;
    private readonly IDisposable? _changeSubscription;
    private readonly ConcurrentDictionary<(string DisplayName, string InfoHashHex), string> _cache = new();

    public TorrentLinks(IOptionsMonitor<MoguOptions> options)
    {
        _options = options;
        _changeSubscription = options.OnChange(_ => _cache.Clear());
    }

    /// <summary>Main announce URL followed by the default trackers, without duplicates.</summary>
    public IReadOnlyList<string> DefaultTrackers =>
        TrackerLists.Combine(_options.CurrentValue.MainAnnounceUrl, [], _options.CurrentValue.DefaultTrackers);

    public string Magnet(Torrent torrent) => Magnet(torrent.DisplayName, torrent.InfoHash);

    public string Magnet(string displayName, byte[] infoHash) => Magnet(displayName, Convert.ToHexStringLower(infoHash));

    public string Magnet(string displayName, string infoHashHex)
    {
        var key = (displayName, infoHashHex);
        if (_cache.TryGetValue(key, out var magnet))
        {
            return magnet;
        }

        magnet = MagnetUri.Create(displayName, infoHashHex, DefaultTrackers, _options.CurrentValue.MagnetMaxTrackers);
        if (_cache.Count >= MaxCacheEntries)
        {
            _cache.Clear();
        }

        _cache[key] = magnet;
        return magnet;
    }

    /// <summary>Relative URL of the .torrent download.</summary>
    public static string Download(int torrentId) => $"/download/{torrentId}.torrent";

    public void Dispose() => _changeSubscription?.Dispose();
}
