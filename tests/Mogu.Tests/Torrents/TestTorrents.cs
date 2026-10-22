using System.Text;
using Mogu.Core.Torrents;

namespace Mogu.Tests.Torrents;

/// <summary>Builds .torrent files for tests.</summary>
internal static class TestTorrents
{
    public const string MainTracker = "udp://tracker.example:1337/announce";

    public static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    /// <summary>A multi-file torrent; <paramref name="files"/> are (path, length) with '/' separated paths.</summary>
    public static BencodeDictionary MultiFile(string name, params (string Path, long Length)[] files)
    {
        var info = new BencodeDictionary
        {
            ["name"] = Bytes(name),
            ["piece length"] = 262144L,
            ["pieces"] = Pieces(name),
            ["files"] = files.Select(f => (object)new BencodeDictionary
            {
                ["length"] = f.Length,
                ["path"] = f.Path.Split('/').Select(p => (object)Bytes(p)).ToList(),
            }).ToList(),
        };
        return new BencodeDictionary
        {
            ["announce"] = Bytes(MainTracker),
            ["info"] = info,
        };
    }

    public static BencodeDictionary SingleFile(string name, long length)
    {
        return new BencodeDictionary
        {
            ["announce"] = Bytes(MainTracker),
            ["info"] = new BencodeDictionary
            {
                ["name"] = Bytes(name),
                ["piece length"] = 262144L,
                ["pieces"] = Pieces(name),
                ["length"] = length,
            },
        };
    }

    public static byte[] Encode(BencodeDictionary torrent) => Bencode.Encode(torrent);

    public static BencodeDictionary Info(BencodeDictionary torrent) => (BencodeDictionary)torrent["info"];

    /// <summary>Deterministic, name dependent piece hashes so different names give different info hashes.</summary>
    private static byte[] Pieces(string name)
    {
        var pieces = new byte[40];
        var seed = Encoding.UTF8.GetBytes(name);
        for (var i = 0; i < pieces.Length; i++)
        {
            pieces[i] = (byte)(seed[i % seed.Length] + i);
        }

        return pieces;
    }
}
