using System.Security.Cryptography;
using System.Text;
using Mogu.Core.Torrents;
using static Mogu.Tests.Torrents.TestTorrents;

namespace Mogu.Tests.Torrents;

[TestClass]
public sealed class TorrentMetadataTests
{
    private static string Error(BencodeDictionary torrent) => Error(Encode(torrent));

    private static string Error(byte[] data) =>
        Assert.ThrowsExactly<TorrentValidationException>(() => TorrentMetadata.Parse(data)).Message;

    [TestMethod]
    public void InfoHashIsSha1OfTheInfoDict()
    {
        // Written out by hand, so the hash doesn't depend on our encoder.
        const string info = "d6:lengthi1048576e4:name8:test.mkv12:piece lengthi262144e6:pieces20:aaaaaaaaaaaaaaaaaaaae";
        var data = Encoding.ASCII.GetBytes("d8:announce35:udp://tracker.example:1337/announce4:info" + info + "e");

        var metadata = TorrentMetadata.Parse(data);

#pragma warning disable CA5350 // BitTorrent info hashes are SHA-1.
        var expected = Convert.ToHexStringLower(SHA1.HashData(Encoding.ASCII.GetBytes(info)));
#pragma warning restore CA5350
        Assert.AreEqual(expected, metadata.InfoHashHex);
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes(info), metadata.InfoDictionary);
        Assert.AreEqual("test.mkv", metadata.Name);
        Assert.AreEqual(1048576L, metadata.TotalSize);
        Assert.AreEqual("""{"test.mkv":1048576}""", metadata.FileTree.ToJson());
        Assert.AreEqual(Joined(MainTracker), Joined(metadata.Trackers));
    }

    [TestMethod]
    public void UnsortedInfoDictIsHashedSorted()
    {
        const string unsorted = "d4:name8:test.mkv6:lengthi1e12:piece lengthi1e6:pieces20:aaaaaaaaaaaaaaaaaaaae";
        const string sorted = "d6:lengthi1e4:name8:test.mkv12:piece lengthi1e6:pieces20:aaaaaaaaaaaaaaaaaaaae";
        var data = Encoding.ASCII.GetBytes("d8:announce3:x:y4:info" + unsorted + "e");

        var metadata = TorrentMetadata.Parse(data);

        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes(sorted), metadata.InfoDictionary);
    }

    [TestMethod]
    public void BuildsSortedFileTreeWithDirectoriesFirst()
    {
        var torrent = MultiFile("Root",
            ("b.txt", 2),
            ("a.txt", 1),
            ("z/2.mkv", 20),
            ("z/1.mkv", 10),
            ("m/deep/x", 5),
            ("empty/", 0));

        var metadata = TorrentMetadata.Parse(Encode(torrent));

        Assert.AreEqual(
            """{"Root":{"empty":{},"m":{"deep":{"x":5}},"z":{"1.mkv":10,"2.mkv":20},"a.txt":1,"b.txt":2}}""",
            metadata.FileTree.ToJson());
        Assert.AreEqual(38L, metadata.TotalSize);
    }

    [TestMethod]
    public void CollectsTrackersAndWebSeeds()
    {
        var torrent = SingleFile("a", 1);
        torrent["announce-list"] = new List<object>
        {
            new List<object> { Bytes(MainTracker), Bytes("http://ignored.example/announce") },
            new List<object> { Bytes("http://second.example/announce") },
            new List<object>(),
        };
        torrent["url-list"] = Bytes("https://seed.example/file"); // qBittorrent's bare string

        var metadata = TorrentMetadata.Parse(Encode(torrent));

        Assert.AreEqual(Joined(MainTracker, "http://second.example/announce"), Joined(metadata.Trackers));
        Assert.AreEqual(Joined("https://seed.example/file"), Joined(metadata.WebSeeds));
        Assert.IsTrue(metadata.HasTracker(MainTracker.ToUpperInvariant()));
    }

    [TestMethod]
    public void EmptyUrlListMeansNoWebSeeds()
    {
        var torrent = SingleFile("a", 1);
        torrent["url-list"] = Array.Empty<byte>();

        Assert.IsEmpty(TorrentMetadata.Parse(Encode(torrent)).WebSeeds);
    }

    [TestMethod]
    public void FoldsBitCometUtf8Keys()
    {
        var torrent = MultiFile("legacy name", ("a.txt", 1));
        var info = Info(torrent);
        info["name.utf-8"] = Bytes("Proper name");
        var originalHash = Convert.ToHexStringLower(SHA256.HashData(Bencode.Encode(info)));

        var metadata = TorrentMetadata.Parse(Encode(torrent));

        Assert.AreEqual("Proper name", metadata.Name);
        Assert.AreEqual("""{"Proper name":{"a.txt":1}}""", metadata.FileTree.ToJson());

        // The stored and hashed info dict is still what was uploaded.
        Assert.AreEqual(originalHash, Convert.ToHexStringLower(SHA256.HashData(metadata.InfoDictionary)));
    }

    [TestMethod]
    public void DecodesNamesInTheDeclaredEncoding()
    {
        var torrent = SingleFile("x", 1);
        torrent["encoding"] = Bytes("shift_jis");
        System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Info(torrent)["name"] = System.Text.Encoding.GetEncoding("shift_jis").GetBytes("\u3042.mkv");

        var metadata = TorrentMetadata.Parse(Encode(torrent));

        Assert.AreEqual("\u3042.mkv", metadata.Name);
        Assert.AreEqual("shift_jis", metadata.Encoding);
    }

    [TestMethod]
    public void RejectsGarbage()
    {
        Assert.AreEqual("Malformed torrent file", Error(Encoding.ASCII.GetBytes("not a torrent")));
    }

    [TestMethod]
    public void RejectsNonDictionary()
    {
        Assert.AreEqual("Malformed torrent metadata (torrent metadata is not a dict)", Error(Bencode.Encode(new List<object>())));
    }

    [TestMethod]
    public void RejectsMissingInfo()
    {
        var torrent = SingleFile("a", 1);
        torrent.Remove("info");

        Assert.AreEqual("Malformed torrent metadata (no info_dict in torrent)", Error(torrent));
    }

    [TestMethod]
    public void RejectsBadPieces()
    {
        var torrent = SingleFile("a", 1);
        Info(torrent)["pieces"] = new byte[30];

        Assert.AreEqual("Malformed torrent metadata (pieces length is not a multiple of 20)", Error(torrent));
    }

    [TestMethod]
    public void RejectsNonPositivePieceLength()
    {
        var torrent = SingleFile("a", 1);
        Info(torrent)["piece length"] = 0L;

        Assert.AreEqual("Malformed torrent metadata (piece length is not positive)", Error(torrent));
    }

    [TestMethod]
    public void RejectsEmptySingleFile()
    {
        Assert.AreEqual("Malformed torrent metadata (length is not positive)", Error(SingleFile("a", 0)));
    }

    [TestMethod]
    public void RejectsNegativeFileLength()
    {
        Assert.AreEqual("Malformed torrent metadata (file length is less than 0)", Error(MultiFile("a", ("x", -1))));
    }

    [TestMethod]
    public void RejectsEmptyDirectoryNames()
    {
        Assert.AreEqual("Malformed torrent metadata (path part is empty)", Error(MultiFile("a", ("/x", 1))));
    }

    [TestMethod]
    public void RejectsUndecodableNames()
    {
        var torrent = SingleFile("a", 1);
        Info(torrent)["name"] = new byte[] { 0xFF, 0xFE };

        Assert.AreEqual("Malformed torrent metadata (name could not be decoded from 'utf-8')", Error(torrent));
    }

    [TestMethod]
    public void RejectsUnknownEncodings()
    {
        var torrent = SingleFile("a", 1);
        torrent["encoding"] = Bytes("klingon");

        Assert.AreEqual("Malformed torrent metadata (encoding 'klingon' is unknown)", Error(torrent));
    }

    [TestMethod]
    public void RejectsBadWebSeeds()
    {
        var torrent = SingleFile("a", 1);
        torrent["url-list"] = new List<object> { 5L };

        Assert.AreEqual("Malformed torrent metadata (url-list item is not bytes)", Error(torrent));
    }

    [TestMethod]
    public void RejectsMissingTracker()
    {
        var torrent = SingleFile("a", 1);
        torrent.Remove("announce");

        Assert.AreEqual("Malformed torrent trackers (no tracker in torrent)", Error(torrent));
    }

    [TestMethod]
    public void RejectsBadAnnounceList()
    {
        var torrent = SingleFile("a", 1);
        torrent["announce-list"] = new List<object> { Bytes("udp://x") };

        Assert.AreEqual("Malformed torrent trackers (announce-list item is not a list)", Error(torrent));
    }

    [TestMethod]
    public void PrivateTorrentsMustAnnounceToOurTracker()
    {
        var torrent = SingleFile("a", 1);
        Info(torrent)["private"] = 1L;
        torrent["announce"] = Bytes("udp://elsewhere.example/announce");

        var metadata = TorrentMetadata.Parse(Encode(torrent));

        Assert.AreEqual($"Private torrent: please set {MainTracker} as the main tracker", metadata.CheckTrackerPolicy(MainTracker, false));
    }

    [TestMethod]
    public void EnforcedMainTrackerMustBeIncluded()
    {
        var torrent = SingleFile("a", 1);
        torrent["announce"] = Bytes("udp://elsewhere.example/announce");
        var metadata = TorrentMetadata.Parse(Encode(torrent));

        Assert.IsNull(metadata.CheckTrackerPolicy(MainTracker, false));
        Assert.AreEqual($"Please include {MainTracker} in the trackers of the torrent", metadata.CheckTrackerPolicy(MainTracker, true));
        Assert.IsNull(TorrentMetadata.Parse(Encode(SingleFile("a", 1))).CheckTrackerPolicy(MainTracker, true));
    }

    private static string Joined(params IEnumerable<object> items) => string.Join(" | ", items);
}
