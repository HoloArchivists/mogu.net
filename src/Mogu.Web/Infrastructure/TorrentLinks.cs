using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Mogu.Core.Entities;
using Mogu.Core.Options;
using Mogu.Core.Torrents;
using Mogu.Web.Tracking;

namespace Mogu.Web.Infrastructure;

/// <summary>
/// Single source of truth for which trackers go into magnet links and generated .torrent files:
/// our main announce URL first (<see cref="MoguOptions.MainAnnounceUrl"/>, else the built-in tracker),
/// then the torrent's own trackers (for .torrent files), then <see cref="MoguOptions.DefaultTrackers"/>,
/// never anything in <see cref="MoguOptions.TrackerBlacklist"/>. Magnets are capped at
/// <see cref="MoguOptions.MagnetMaxTrackers"/> and cached, since listings build the same links repeatedly.
/// </summary>
public sealed class TorrentLinks : IDisposable
{
    private const int MaxCacheEntries = 4096;

    private readonly IOptionsMonitor<MoguOptions> _options;
    private readonly IOptionsMonitor<TrackerOptions> _trackerOptions;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IDisposable? _changeSubscription;
    private readonly ConcurrentDictionary<(string? Main, string DisplayName, string InfoHashHex), string> _cache = new();

    public TorrentLinks(IOptionsMonitor<MoguOptions> options, IOptionsMonitor<TrackerOptions> trackerOptions, IHttpContextAccessor httpContextAccessor)
    {
        _options = options;
        _trackerOptions = trackerOptions;
        _httpContextAccessor = httpContextAccessor;
        _changeSubscription = options.OnChange(_ => _cache.Clear());
    }

    /// <summary>
    /// The announce URL generated torrents lead with. When neither <see cref="MoguOptions.MainAnnounceUrl"/>
    /// nor <see cref="TrackerOptions.AnnounceUrl"/> is set, it's derived from the current request's host.
    /// </summary>
    public string? MainAnnounceUrl
    {
        get
        {
            var mogu = _options.CurrentValue;
            var tracker = _trackerOptions.CurrentValue;
            if (_httpContextAccessor.HttpContext?.Request is { } request)
            {
                return TrackerAnnounceUrl.GetMainAnnounceUrl(request, mogu, tracker);
            }

            if (!string.IsNullOrWhiteSpace(mogu.MainAnnounceUrl))
            {
                return mogu.MainAnnounceUrl;
            }

            return tracker.Enabled && !string.IsNullOrWhiteSpace(tracker.AnnounceUrl) ? tracker.AnnounceUrl : null;
        }
    }

    /// <summary>Main announce URL followed by the default trackers, without duplicates or blacklisted trackers.</summary>
    public IReadOnlyList<string> DefaultTrackers => TrackersFor([]);

    /// <summary>Announce list for a generated .torrent with the torrent's own <paramref name="torrentTrackers"/>.</summary>
    public IReadOnlyList<string> TrackersFor(IEnumerable<string> torrentTrackers)
    {
        var options = _options.CurrentValue;
        return TrackerLists.Combine(MainAnnounceUrl, torrentTrackers, options.DefaultTrackers)
            .Where(t => !options.IsBlacklistedTracker(t))
            .ToList();
    }

    public string Magnet(Torrent torrent) => Magnet(torrent.DisplayName, torrent.InfoHash);

    public string Magnet(string displayName, byte[] infoHash) => Magnet(displayName, Convert.ToHexStringLower(infoHash));

    public string Magnet(string displayName, string infoHashHex)
    {
        var key = (MainAnnounceUrl, displayName, infoHashHex);
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
