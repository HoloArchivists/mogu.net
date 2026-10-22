using System.Net;
using Microsoft.Extensions.Options;
using Mogu.Core.Options;

namespace Mogu.Web.Tracking;

public interface ITrackerPeerStore
{
    /// <summary>Records the announce and returns peers for the client.</summary>
    TrackerAnnounceResult Announce(TrackerAnnounceRequest request);

    TrackerSwarmStats GetStats(string infoHashHex);

    /// <summary>Drops all peers of a torrent (e.g. it was banned). Its counts get flushed as zero.</summary>
    void RemoveSwarm(string infoHashHex);

    TrackerPruneResult PruneExpired();

    /// <summary>Info hashes that currently have peers.</summary>
    IReadOnlySet<string> GetActiveInfoHashes();

    /// <summary>
    /// Returns counts of torrents that changed since the last call, plus pending completions. Pass the result to
    /// <see cref="CompleteStatsChanges"/> once it is persisted (or failed to be).
    /// </summary>
    IReadOnlyList<TrackerStatsChange> TakeStatsChanges();

    void CompleteStatsChanges(IReadOnlyList<TrackerStatsChange> changes, bool persisted);
}

/// <summary>Thread-safe in-memory swarm state; a single instance per process. Peers are lost on restart.</summary>
public sealed class InMemoryTrackerPeerStore(IOptions<TrackerOptions> options, TimeProvider timeProvider) : ITrackerPeerStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, Dictionary<string, PeerEntry>> swarms = new(StringComparer.Ordinal);

    // Completions not yet added to Torrent.DownloadCount.
    private readonly Dictionary<string, int> pendingDownloads = new(StringComparer.Ordinal);

    // Torrents whose seeders/leechers may differ from what was last persisted.
    private readonly HashSet<string> dirty = new(StringComparer.Ordinal);

    public TrackerAnnounceResult Announce(TrackerAnnounceRequest request)
    {
        var settings = options.Value;
        var now = timeProvider.GetUtcNow();
        var cutoff = now - settings.PeerTimeout;
        var hash = request.InfoHashHex;
        var key = PeerKey(request.PeerId, request.Address);

        lock (gate)
        {
            swarms.TryGetValue(hash, out var peers);
            var selected = new List<TrackerPeer>();

            if (request.Event == TrackerEvent.Stopped)
            {
                if (peers is not null && peers.Remove(key))
                {
                    dirty.Add(hash);
                    if (peers.Count == 0)
                    {
                        swarms.Remove(hash);
                    }
                }
            }
            else
            {
                if (peers is null)
                {
                    peers = new Dictionary<string, PeerEntry>(StringComparer.Ordinal);
                    swarms[hash] = peers;
                }

                var isNew = !peers.TryGetValue(key, out var peer) || peer.LastAnnounce < cutoff;
                if (request.Event == TrackerEvent.Completed && (peer is null || !peer.IsSeeder))
                {
                    pendingDownloads[hash] = pendingDownloads.GetValueOrDefault(hash) + 1;
                }

                if (isNew || peer!.IsSeeder != request.IsSeeder)
                {
                    dirty.Add(hash);
                }

                if (peer is null)
                {
                    if (peers.Count >= settings.MaxPeersPerTorrent)
                    {
                        EvictOldest(peers);
                    }

                    peer = new PeerEntry(request.PeerId, request.Address);
                    peers[key] = peer;
                }

                peer.Port = request.Port;
                peer.IsSeeder = request.IsSeeder;
                peer.LastAnnounce = now;

                SelectPeers(peers, peer, cutoff, request.NumWant, selected);
            }

            var (seeders, leechers) = peers is null ? (0, 0) : Count(peers, cutoff);
            return new TrackerAnnounceResult(selected, seeders, leechers, settings.AnnounceInterval, settings.MinAnnounceInterval);
        }
    }

    public TrackerSwarmStats GetStats(string infoHashHex)
    {
        var cutoff = timeProvider.GetUtcNow() - options.Value.PeerTimeout;
        lock (gate)
        {
            var (seeders, leechers) = swarms.TryGetValue(infoHashHex, out var peers) ? Count(peers, cutoff) : (0, 0);
            return new TrackerSwarmStats(seeders, leechers, pendingDownloads.GetValueOrDefault(infoHashHex));
        }
    }

    public void RemoveSwarm(string infoHashHex)
    {
        lock (gate)
        {
            if (swarms.Remove(infoHashHex))
            {
                dirty.Add(infoHashHex);
            }
        }
    }

    public TrackerPruneResult PruneExpired()
    {
        var cutoff = timeProvider.GetUtcNow() - options.Value.PeerTimeout;
        var removedPeers = 0;
        var removedSwarms = 0;
        lock (gate)
        {
            List<string>? empty = null;
            foreach (var (hash, peers) in swarms)
            {
                var removed = RemoveExpired(peers, cutoff);
                if (removed > 0)
                {
                    removedPeers += removed;
                    dirty.Add(hash);
                }

                if (peers.Count == 0)
                {
                    (empty ??= []).Add(hash);
                }
            }

            foreach (var hash in empty ?? [])
            {
                swarms.Remove(hash);
                removedSwarms++;
            }
        }

        return new TrackerPruneResult(removedPeers, removedSwarms);
    }

    public IReadOnlySet<string> GetActiveInfoHashes()
    {
        lock (gate)
        {
            return swarms.Keys.ToHashSet(StringComparer.Ordinal);
        }
    }

    public IReadOnlyList<TrackerStatsChange> TakeStatsChanges()
    {
        var cutoff = timeProvider.GetUtcNow() - options.Value.PeerTimeout;
        lock (gate)
        {
            var changes = new List<TrackerStatsChange>(dirty.Count + pendingDownloads.Count);
            foreach (var hash in dirty.Union(pendingDownloads.Keys))
            {
                var (seeders, leechers) = swarms.TryGetValue(hash, out var peers) ? Count(peers, cutoff) : (0, 0);
                changes.Add(new TrackerStatsChange(hash, seeders, leechers, pendingDownloads.GetValueOrDefault(hash)));
            }

            dirty.Clear();
            return changes;
        }
    }

    public void CompleteStatsChanges(IReadOnlyList<TrackerStatsChange> changes, bool persisted)
    {
        lock (gate)
        {
            foreach (var change in changes)
            {
                if (!persisted)
                {
                    dirty.Add(change.InfoHashHex);
                }
                else if (change.DownloadsDelta > 0 && pendingDownloads.TryGetValue(change.InfoHashHex, out var pending))
                {
                    var remaining = pending - change.DownloadsDelta;
                    if (remaining > 0)
                    {
                        pendingDownloads[change.InfoHashHex] = remaining;
                    }
                    else
                    {
                        pendingDownloads.Remove(change.InfoHashHex);
                    }
                }
            }
        }
    }

    private static void SelectPeers(Dictionary<string, PeerEntry> peers, PeerEntry self, DateTimeOffset cutoff, int numWant, List<TrackerPeer> selected)
    {
        if (numWant <= 0)
        {
            return;
        }

        // Seeders only need leechers.
        var candidates = new List<PeerEntry>(Math.Min(peers.Count, 1024));
        foreach (var peer in peers.Values)
        {
            if (!ReferenceEquals(peer, self) && peer.LastAnnounce >= cutoff && !(self.IsSeeder && peer.IsSeeder))
            {
                candidates.Add(peer);
            }
        }

        // Partial Fisher-Yates so every peer has the same chance to be handed out.
        var take = Math.Min(numWant, candidates.Count);
        for (var i = 0; i < take; i++)
        {
            var j = Random.Shared.Next(i, candidates.Count);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
            selected.Add(new TrackerPeer(candidates[i].PeerId, candidates[i].Address, candidates[i].Port));
        }
    }

    private static (int Seeders, int Leechers) Count(Dictionary<string, PeerEntry> peers, DateTimeOffset cutoff)
    {
        int seeders = 0, leechers = 0;
        foreach (var peer in peers.Values)
        {
            if (peer.LastAnnounce < cutoff)
            {
                continue;
            }

            if (peer.IsSeeder)
            {
                seeders++;
            }
            else
            {
                leechers++;
            }
        }

        return (seeders, leechers);
    }

    private static int RemoveExpired(Dictionary<string, PeerEntry> peers, DateTimeOffset cutoff)
    {
        var removed = 0;
        foreach (var (key, peer) in peers)
        {
            // Removing while enumerating is allowed for Dictionary<TKey, TValue>.
            if (peer.LastAnnounce < cutoff && peers.Remove(key))
            {
                removed++;
            }
        }

        return removed;
    }

    private static void EvictOldest(Dictionary<string, PeerEntry> peers)
    {
        var oldest = peers.MinBy(p => p.Value.LastAnnounce).Key;
        peers.Remove(oldest);
    }

    // A peer is identified by its peer id and address, so another host can't stop or overwrite it.
    private static string PeerKey(byte[] peerId, IPAddress address) =>
        $"{Convert.ToHexStringLower(peerId)}@{address}";

    private sealed class PeerEntry(byte[] peerId, IPAddress address)
    {
        public byte[] PeerId { get; } = peerId;

        public IPAddress Address { get; } = address;

        public int Port { get; set; }

        public bool IsSeeder { get; set; }

        public DateTimeOffset LastAnnounce { get; set; }
    }
}
