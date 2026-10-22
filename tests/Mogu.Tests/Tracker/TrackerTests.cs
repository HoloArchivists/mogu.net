using System.Net;
using Microsoft.Extensions.Options;
using Mogu.Core.Options;
using Mogu.Web.Tracking;
using static Mogu.Tests.Tracker.TrackerTestBencode;

namespace Mogu.Tests.Tracker;

[TestClass]
public sealed class TrackerTests
{
    private static readonly byte[] InfoHash = [.. Enumerable.Range(0, 20).Select(i => (byte)(i * 13))];
    private static readonly IPAddress Client = IPAddress.Parse("203.0.113.7");

    [TestMethod]
    public void ParseAnnouncePreservesRawBinaryInfoHashAndPeerId()
    {
        byte[] infoHash = [0x00, 0xFF, 0x80, 0x25, 0x26, 0x3D, 0x2B, 0x20, (byte)'a', (byte)'Z', 0x7F, 0xC3, 0xA9, 1, 2, 3, 4, 5, 6, 7];
        // Typical client encoding: unreserved characters stay literal, everything else is %XX (any case).
        var peerIdQuery = "-qB4650-%00%ffab%2Fcd~._-1";
        var query = $"?info_hash={PercentEncode(infoHash).ToLowerInvariant()}&peer_id={peerIdQuery}&port=6881&uploaded=1&downloaded=2&left=3&event=started";

        Assert.IsTrue(TrackerQueryParser.TryParseAnnounce(query, Client, new TrackerOptions(), out var request, out var failure), failure);

        CollectionAssert.AreEqual(infoHash, request.InfoHash);
        CollectionAssert.AreEqual("-qB4650-\0\u00FFab/cd~._-1".Select(c => (byte)c).ToArray(), request.PeerId);
        Assert.AreEqual(6881, request.Port);
        Assert.AreEqual(1, request.Uploaded);
        Assert.AreEqual(2, request.Downloaded);
        Assert.AreEqual(3, request.Left);
        Assert.AreEqual(TrackerEvent.Started, request.Event);
        Assert.IsTrue(request.Compact);
    }

    [TestMethod]
    [DataRow("info_hash=%00%01&peer_id={peer}&port=1&left=0", "Invalid info_hash")]
    [DataRow("peer_id={peer}&port=1&left=0", "Invalid info_hash")]
    [DataRow("info_hash={hash}&peer_id=short&port=1&left=0", "Invalid peer_id")]
    [DataRow("info_hash={hash}&peer_id={peer}&port=0&left=0", "Invalid port")]
    [DataRow("info_hash={hash}&peer_id={peer}&port=70000&left=0", "Invalid port")]
    [DataRow("info_hash={hash}&peer_id={peer}&port=1", "Invalid uploaded, downloaded or left")]
    [DataRow("info_hash={hash}&peer_id={peer}&port=1&left=-1", "uploaded, downloaded and left must not be negative")]
    [DataRow("info_hash=%zz&peer_id={peer}&port=1&left=0", "Invalid percent encoding in query string")]
    public void ParseAnnounceRejectsInvalidQueries(string query, string expected)
    {
        query = query.Replace("{hash}", PercentEncode(InfoHash), StringComparison.Ordinal)
            .Replace("{peer}", PercentEncode(PeerId(1)), StringComparison.Ordinal);

        Assert.IsFalse(TrackerQueryParser.TryParseAnnounce(query, Client, new TrackerOptions(), out _, out var failure));
        Assert.AreEqual(expected, failure);
    }

    [TestMethod]
    [DataRow(null, 50)]
    [DataRow("10", 10)]
    [DataRow("0", 0)]
    [DataRow("5000", 200)]
    [DataRow("-1", 50)]
    [DataRow("lots", 50)]
    public void ParseAnnounceDefaultsAndCapsNumWant(string? numWant, int expected)
    {
        var query = AnnounceQuery(PeerId(1)) + (numWant is null ? "" : "&numwant=" + numWant);

        Assert.IsTrue(TrackerQueryParser.TryParseAnnounce(query, Client, new TrackerOptions(), out var request, out var failure), failure);
        Assert.AreEqual(expected, request.NumWant);
    }

    [TestMethod]
    public void ParseAnnounceIgnoresIpParameterAndUnwrapsMappedAddresses()
    {
        var query = AnnounceQuery(PeerId(1)) + "&ip=198.51.100.1&compact=0&no_peer_id=1";

        Assert.IsTrue(TrackerQueryParser.TryParseAnnounce(query, IPAddress.Parse("::ffff:192.0.2.5"), new TrackerOptions(), out var request, out _));
        Assert.AreEqual(IPAddress.Parse("192.0.2.5"), request.Address);
        Assert.IsFalse(request.Compact);
        Assert.IsTrue(request.NoPeerId);
    }

    [TestMethod]
    public void ParseScrapeReadsRepeatedInfoHashes()
    {
        byte[] second = [.. Enumerable.Repeat((byte)0xAB, 20)];
        var query = $"?info_hash={PercentEncode(InfoHash)}&info_hash={PercentEncode(second)}";

        Assert.IsTrue(TrackerQueryParser.TryParseScrape(query, new TrackerOptions(), out var hashes, out var failure), failure);
        Assert.HasCount(2, hashes);
        CollectionAssert.AreEqual(InfoHash, hashes[0]);
        CollectionAssert.AreEqual(second, hashes[1]);

        Assert.IsFalse(TrackerQueryParser.TryParseScrape("", new TrackerOptions(), out _, out failure));
        Assert.AreEqual("info_hash is required", failure);
        Assert.IsFalse(TrackerQueryParser.TryParseScrape(query, new TrackerOptions { MaxScrapeInfoHashes = 1 }, out _, out _));
    }

    [TestMethod]
    public void AnnounceReturnsOtherPeersAndCounts()
    {
        var (store, _) = CreateStore();

        var first = store.Announce(Request(PeerId(1), "198.51.100.1", left: 100));
        Assert.IsEmpty(first.Peers);
        Assert.AreEqual(0, first.Complete);
        Assert.AreEqual(1, first.Incomplete);
        Assert.AreEqual(TimeSpan.FromMinutes(30), first.Interval);
        Assert.AreEqual(TimeSpan.FromMinutes(5), first.MinInterval);

        var second = store.Announce(Request(PeerId(2), "198.51.100.2", left: 0));
        Assert.HasCount(1, second.Peers);
        Assert.AreEqual(IPAddress.Parse("198.51.100.1"), second.Peers[0].Address);
        CollectionAssert.AreEqual(PeerId(1), second.Peers[0].PeerId);
        Assert.AreEqual(1, second.Complete);
        Assert.AreEqual(1, second.Incomplete);

        // Re-announcing updates the peer instead of adding a duplicate.
        var again = store.Announce(Request(PeerId(1), "198.51.100.1", left: 50));
        Assert.AreEqual(1, again.Incomplete);
        Assert.AreEqual(1, again.Complete);
    }

    [TestMethod]
    public void AnnounceSeedersOnlyGetLeechers()
    {
        var (store, _) = CreateStore();
        store.Announce(Request(PeerId(1), "198.51.100.1", left: 0));
        store.Announce(Request(PeerId(2), "198.51.100.2", left: 10));

        var seeder = store.Announce(Request(PeerId(3), "198.51.100.3", left: 0));

        Assert.HasCount(1, seeder.Peers);
        CollectionAssert.AreEqual(PeerId(2), seeder.Peers[0].PeerId);
    }

    [TestMethod]
    public void AnnounceHonoursNumWant()
    {
        var (store, _) = CreateStore();
        for (var i = 1; i <= 10; i++)
        {
            store.Announce(Request(PeerId(i), $"198.51.100.{i}", left: 10));
        }

        var result = store.Announce(Request(PeerId(11), "198.51.100.11", left: 10, numWant: 4));

        Assert.HasCount(4, result.Peers);
        Assert.AreEqual(4, result.Peers.Select(p => p.Address).Distinct().Count());
        Assert.IsEmpty(store.Announce(Request(PeerId(12), "198.51.100.12", left: 10, numWant: 0)).Peers);
        Assert.AreEqual(12, store.GetStats(Convert.ToHexStringLower(InfoHash)).Leechers);
    }

    [TestMethod]
    public void AnnounceStoppedRemovesPeer()
    {
        var (store, _) = CreateStore();
        store.Announce(Request(PeerId(1), "198.51.100.1", left: 10));
        store.Announce(Request(PeerId(2), "198.51.100.2", left: 0));

        var stopped = store.Announce(Request(PeerId(1), "198.51.100.1", left: 10, trackerEvent: TrackerEvent.Stopped));

        Assert.IsEmpty(stopped.Peers);
        Assert.AreEqual(0, stopped.Incomplete);
        Assert.AreEqual(1, stopped.Complete);
        Assert.IsEmpty(store.Announce(Request(PeerId(3), "198.51.100.3", left: 0)).Peers);
    }

    [TestMethod]
    public void AnnounceStoppedFromAnotherAddressDoesNotRemovePeer()
    {
        var (store, _) = CreateStore();
        store.Announce(Request(PeerId(1), "198.51.100.1", left: 10));

        store.Announce(Request(PeerId(1), "198.51.100.66", left: 10, trackerEvent: TrackerEvent.Stopped));

        Assert.AreEqual(1, store.GetStats(Convert.ToHexStringLower(InfoHash)).Leechers);
    }

    [TestMethod]
    public void AnnounceCompletedCountsOncePerDownload()
    {
        var (store, _) = CreateStore();
        var hex = Convert.ToHexStringLower(InfoHash);
        store.Announce(Request(PeerId(1), "198.51.100.1", left: 10, trackerEvent: TrackerEvent.Started));
        store.Announce(Request(PeerId(1), "198.51.100.1", left: 0, trackerEvent: TrackerEvent.Completed));
        // A duplicate completed from a peer that already is a seeder doesn't count again.
        store.Announce(Request(PeerId(1), "198.51.100.1", left: 0, trackerEvent: TrackerEvent.Completed));
        store.Announce(Request(PeerId(2), "198.51.100.2", left: 0, trackerEvent: TrackerEvent.Completed));

        var stats = store.GetStats(hex);
        Assert.AreEqual(2, stats.PendingDownloads);
        Assert.AreEqual(2, stats.Seeders);
        Assert.AreEqual(0, stats.Leechers);
    }

    [TestMethod]
    public void StatsChangesReportDirtyTorrentsAndClearPendingDownloadsWhenPersisted()
    {
        var (store, _) = CreateStore();
        var hex = Convert.ToHexStringLower(InfoHash);
        store.Announce(Request(PeerId(1), "198.51.100.1", left: 0, trackerEvent: TrackerEvent.Completed));

        var changes = store.TakeStatsChanges();
        Assert.HasCount(1, changes);
        Assert.AreEqual(new TrackerStatsChange(hex, 1, 0, 1), changes[0]);

        // Failed writes are retried, and completions stay pending.
        store.CompleteStatsChanges(changes, persisted: false);
        changes = store.TakeStatsChanges();
        Assert.AreEqual(new TrackerStatsChange(hex, 1, 0, 1), changes[0]);

        // A completion arriving while the write is in flight survives it.
        store.Announce(Request(PeerId(2), "198.51.100.2", left: 0, trackerEvent: TrackerEvent.Completed));
        store.CompleteStatsChanges(changes, persisted: true);
        Assert.AreEqual(1, store.GetStats(hex).PendingDownloads);

        Assert.AreEqual(new TrackerStatsChange(hex, 2, 0, 1), store.TakeStatsChanges().Single());
        store.CompleteStatsChanges([new TrackerStatsChange(hex, 2, 0, 1)], persisted: true);

        // Unchanged swarms produce no work.
        store.Announce(Request(PeerId(2), "198.51.100.2", left: 0));
        Assert.IsEmpty(store.TakeStatsChanges());
        Assert.AreEqual(0, store.GetStats(hex).PendingDownloads);
    }

    [TestMethod]
    public void PruneExpiredDropsSilentPeersAndFlushesZero()
    {
        var (store, time) = CreateStore();
        var hex = Convert.ToHexStringLower(InfoHash);
        store.Announce(Request(PeerId(1), "198.51.100.1", left: 10));
        time.Advance(TimeSpan.FromMinutes(40));
        store.Announce(Request(PeerId(2), "198.51.100.2", left: 0));
        store.CompleteStatsChanges(store.TakeStatsChanges(), persisted: true);

        time.Advance(TimeSpan.FromMinutes(30));
        // Expired peers no longer count or get handed out, even before pruning.
        Assert.AreEqual(new TrackerSwarmStats(1, 0, 0), store.GetStats(hex));

        Assert.AreEqual(new TrackerPruneResult(1, 0), store.PruneExpired());
        Assert.AreEqual(new TrackerStatsChange(hex, 1, 0, 0), store.TakeStatsChanges().Single());

        time.Advance(TimeSpan.FromMinutes(60));
        Assert.AreEqual(new TrackerPruneResult(1, 1), store.PruneExpired());
        Assert.AreEqual(new TrackerStatsChange(hex, 0, 0, 0), store.TakeStatsChanges().Single());
        Assert.IsEmpty(store.GetActiveInfoHashes());
    }

    [TestMethod]
    public void AnnounceEvictsOldestPeerBeyondLimit()
    {
        var (store, time) = CreateStore(new TrackerOptions { MaxPeersPerTorrent = 2 });
        store.Announce(Request(PeerId(1), "198.51.100.1", left: 10));
        time.Advance(TimeSpan.FromSeconds(1));
        store.Announce(Request(PeerId(2), "198.51.100.2", left: 10));
        time.Advance(TimeSpan.FromSeconds(1));

        var result = store.Announce(Request(PeerId(3), "198.51.100.3", left: 10));

        Assert.AreEqual(2, result.Incomplete);
        CollectionAssert.AreEqual(PeerId(2), result.Peers.Single().PeerId);
    }

    [TestMethod]
    public void AnnounceResponseCompactSplitsIPv4AndIPv6Peers()
    {
        var result = new TrackerAnnounceResult(
            [
                new TrackerPeer(PeerId(1), IPAddress.Parse("10.1.2.3"), 6881),
                new TrackerPeer(PeerId(2), IPAddress.Parse("2001:db8::1"), 51413),
            ],
            Complete: 3,
            Incomplete: 4,
            TimeSpan.FromMinutes(30),
            TimeSpan.FromMinutes(5));

        var response = DecodeDictionary(TrackerResponse.Announce(result, compact: true, noPeerId: false));

        Assert.AreEqual(3L, response["complete"]);
        Assert.AreEqual(4L, response["incomplete"]);
        Assert.AreEqual(1800L, response["interval"]);
        Assert.AreEqual(300L, response["min interval"]);
        CollectionAssert.AreEqual(new byte[] { 10, 1, 2, 3, 0x1A, 0xE1 }, (byte[])response["peers"]);
        byte[] expected6 = [.. IPAddress.Parse("2001:db8::1").GetAddressBytes(), 0xC8, 0xD5];
        CollectionAssert.AreEqual(expected6, (byte[])response["peers6"]);
    }

    [TestMethod]
    public void AnnounceResponseNonCompactListsPeerDictionaries()
    {
        var result = new TrackerAnnounceResult(
            [new TrackerPeer(PeerId(1), IPAddress.Parse("2001:db8::1"), 6881)],
            1, 0, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(5));

        var peers = (List<object>)DecodeDictionary(TrackerResponse.Announce(result, compact: false, noPeerId: false))["peers"];
        var peer = (Dictionary<string, object>)peers.Single();
        Assert.AreEqual("2001:db8::1", Text(peer["ip"]));
        Assert.AreEqual(6881L, peer["port"]);
        CollectionAssert.AreEqual(PeerId(1), (byte[])peer["peer id"]);

        peers = (List<object>)DecodeDictionary(TrackerResponse.Announce(result, compact: false, noPeerId: true))["peers"];
        Assert.IsFalse(((Dictionary<string, object>)peers.Single()).ContainsKey("peer id"));
    }

    [TestMethod]
    public void ScrapeAndFailureResponses()
    {
        byte[] low = [.. Enumerable.Repeat((byte)0x01, 20)];
        byte[] high = [.. Enumerable.Repeat((byte)0xF0, 20)];

        var files = (Dictionary<string, object>)DecodeDictionary(TrackerResponse.Scrape(
            [new TrackerScrapeEntry(high, 1, 2, 3), new TrackerScrapeEntry(low, 4, 5, 6)]))["files"];

        var entry = (Dictionary<string, object>)files[System.Text.Encoding.Latin1.GetString(high)];
        Assert.AreEqual(1L, entry["complete"]);
        Assert.AreEqual(2L, entry["incomplete"]);
        Assert.AreEqual(3L, entry["downloaded"]);
        Assert.HasCount(2, files);

        Assert.AreEqual(
            "d14:failure reason20:Unregistered torrente",
            System.Text.Encoding.UTF8.GetString(TrackerResponse.Failure("Unregistered torrent")));
    }

    [TestMethod]
    public void OptionsValidateCrossFieldConstraints()
    {
        var context = new System.ComponentModel.DataAnnotations.ValidationContext(new object());
        Assert.IsEmpty(new TrackerOptions().Validate(context));
        var invalid = new TrackerOptions
        {
            MinAnnounceInterval = TimeSpan.FromHours(1),
            PeerTimeout = TimeSpan.FromMinutes(10),
            DefaultNumWant = 500,
            AnnounceUrl = "udp://example.com/announce",
        };
        Assert.HasCount(4, invalid.Validate(context).ToList());
    }

    private static (InMemoryTrackerPeerStore Store, TrackerTestTimeProvider Time) CreateStore(TrackerOptions? options = null)
    {
        var time = new TrackerTestTimeProvider();
        return (new InMemoryTrackerPeerStore(Options.Create(options ?? new TrackerOptions()), time), time);
    }

    private static TrackerAnnounceRequest Request(
        byte[] peerId,
        string address,
        long left,
        TrackerEvent trackerEvent = TrackerEvent.None,
        int numWant = 50) =>
        new(InfoHash, peerId, IPAddress.Parse(address), 6881, 0, 0, left, trackerEvent, Compact: true, NoPeerId: false, numWant);

    private static string AnnounceQuery(byte[] peerId) =>
        $"?info_hash={PercentEncode(InfoHash)}&peer_id={PercentEncode(peerId)}&port=6881&uploaded=0&downloaded=0&left=0";

    private static byte[] PeerId(int n) => [.. Enumerable.Repeat((byte)n, 20)];
}
