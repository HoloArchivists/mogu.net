using System.Net;

namespace Mogu.Web.Tracker;

public enum TrackerEvent
{
    None,
    Started,
    Stopped,
    Completed,
}

/// <summary>A parsed <c>/announce</c> request.</summary>
/// <param name="InfoHash">Raw 20-byte info hash.</param>
/// <param name="PeerId">Raw 20-byte peer id.</param>
/// <param name="Address">Peer address: the client's connection address (IPv4-mapped addresses unwrapped).</param>
/// <param name="NumWant">Requested peer count, already clamped to the configured maximum.</param>
public sealed record TrackerAnnounceRequest(
    byte[] InfoHash,
    byte[] PeerId,
    IPAddress Address,
    int Port,
    long Uploaded,
    long Downloaded,
    long Left,
    TrackerEvent Event,
    bool Compact,
    bool NoPeerId,
    int NumWant)
{
    public string InfoHashHex { get; } = Convert.ToHexStringLower(InfoHash);

    public bool IsSeeder => Left == 0;
}

public sealed record TrackerPeer(byte[] PeerId, IPAddress Address, int Port);

public sealed record TrackerAnnounceResult(
    IReadOnlyList<TrackerPeer> Peers,
    int Complete,
    int Incomplete,
    TimeSpan Interval,
    TimeSpan MinInterval);

/// <summary>Live swarm counts plus completions not yet written to the database.</summary>
public readonly record struct TrackerSwarmStats(int Seeders, int Leechers, int PendingDownloads);

/// <summary>Scrape entry for one info hash; <paramref name="Downloaded"/> includes database and pending completions.</summary>
public sealed record TrackerScrapeEntry(byte[] InfoHash, int Complete, int Incomplete, long Downloaded);

/// <summary>Counts to persist for one torrent, taken from the peer store by the stats writer.</summary>
public sealed record TrackerStatsChange(string InfoHashHex, int Seeders, int Leechers, int DownloadsDelta);

public readonly record struct TrackerPruneResult(int RemovedPeers, int RemovedSwarms);
