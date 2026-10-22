using System.Text;
using Mogu.Core.Torrents;
using static Mogu.Tests.Torrents.TestTorrents;

namespace Mogu.Tests.Torrents;

[TestClass]
public sealed class TorrentFileTests
{
    [TestMethod]
    public void MagnetMatchesTheOldFormat()
    {
        var magnet = MagnetUri.Create(
            "[Group] Show - 01 (1080p).mkv",
            "0123456789abcdef0123456789abcdef01234567",
            ["udp://main.example:1337/announce", "http://b.example/announce?x=1&y", "wss://c.example"],
            maxTrackers: 2);

        Assert.AreEqual(
            "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567"
            + "&dn=%5BGroup%5D%20Show%20-%2001%20%281080p%29.mkv"
            + "&tr=udp%3A%2F%2Fmain.example%3A1337%2Fannounce"
            + "&tr=http%3A%2F%2Fb.example%2Fannounce%3Fx%3D1%26y",
            magnet);
    }

    [TestMethod]
    public void MagnetEncodesUnicodeAsUtf8()
    {
        Assert.AreEqual("magnet:?xt=urn:btih:00&dn=%C3%A9~", MagnetUri.Create("\u00e9~", "00", [], 5));
    }

    [TestMethod]
    public void TrackerListPutsMainFirstWithoutDuplicates()
    {
        var trackers = TrackerLists.Combine("udp://main", ["http://own", "udp://main"], ["http://own", "udp://default"]);

        Assert.AreEqual(Joined("udp://main", "http://own", "udp://default"), Joined(trackers));
    }

    [TestMethod]
    public void GeneratedTorrentKeepsTheInfoHash()
    {
        var upload = TorrentMetadata.Parse(Encode(MultiFile("Show", ("a.mkv", 5_000_000), ("b/c.txt", 10))));
        var createdAt = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var metadata = new TorrentFileMetadata(
            createdAt,
            "https://mogu.example/view/7",
            "utf-8",
            ["udp://main", "http://own"],
            ["https://seed.example/"]);

        var file = TorrentFileBuilder.Build(upload.InfoDictionary, metadata);

        // The result is canonical bencode (re-encoding gives the same bytes) and parses as an upload.
        var decoded = (BencodeDictionary)Bencode.Decode(file);
        CollectionAssert.AreEqual(file, Bencode.Encode(decoded));
        var reparsed = TorrentMetadata.Parse(file);
        Assert.AreEqual(upload.InfoHashHex, reparsed.InfoHashHex);

        Assert.AreEqual("Mogu", Text(decoded["created by"]));
        Assert.AreEqual(new DateTimeOffset(createdAt).ToUnixTimeSeconds(), decoded["creation date"]);
        Assert.AreEqual("https://mogu.example/view/7", Text(decoded["comment"]));
        Assert.AreEqual("utf-8", Text(decoded["encoding"]));
        Assert.AreEqual("udp://main", Text(decoded["announce"]));
        var announceList = ((List<object>)decoded["announce-list"]).Select(tier => Text(((List<object>)tier).Single())).ToArray();
        Assert.AreEqual(Joined("udp://main", "http://own"), Joined(announceList));
        Assert.AreEqual(Joined("https://seed.example/"), Joined(((List<object>)decoded["url-list"]).Select(Text)));
    }

    [TestMethod]
    public void SingleTrackerHasNoAnnounceList()
    {
        var upload = TorrentMetadata.Parse(Encode(SingleFile("a", 1)));
        var file = TorrentFileBuilder.Build(upload.InfoDictionary, new TorrentFileMetadata(DateTime.UnixEpoch, "c", "utf-8", ["udp://main"], []));

        var decoded = (BencodeDictionary)Bencode.Decode(file);

        Assert.IsFalse(decoded.ContainsKey("announce-list"));
        Assert.IsFalse(decoded.ContainsKey("url-list"));
        Assert.AreEqual("udp://main", Text(decoded["announce"]));
    }

    private static string Text(object value) => Encoding.UTF8.GetString((byte[])value);

    private static string Joined(params IEnumerable<object> items) => string.Join(" | ", items);
}
