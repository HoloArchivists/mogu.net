using System.Buffers;
using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace Mogu.Web.Tracking;

/// <summary>
/// Minimal forward-only bencode writer for tracker responses. Callers must write dictionary keys in
/// sorted (raw byte) order themselves.
/// </summary>
internal sealed class TrackerBencodeWriter
{
    private readonly ArrayBufferWriter<byte> buffer = new(256);

    public TrackerBencodeWriter BeginDictionary() => WriteRaw("d");

    public TrackerBencodeWriter BeginList() => WriteRaw("l");

    public TrackerBencodeWriter End() => WriteRaw("e");

    public TrackerBencodeWriter Integer(long value) =>
        WriteRaw(string.Create(CultureInfo.InvariantCulture, $"i{value}e"));

    public TrackerBencodeWriter String(ReadOnlySpan<byte> value)
    {
        WriteRaw(string.Create(CultureInfo.InvariantCulture, $"{value.Length}:"));
        buffer.Write(value);
        return this;
    }

    public TrackerBencodeWriter String(string value) => String(Encoding.UTF8.GetBytes(value));

    public byte[] ToArray() => buffer.WrittenSpan.ToArray();

    private TrackerBencodeWriter WriteRaw(string ascii)
    {
        var span = buffer.GetSpan(ascii.Length);
        var written = Encoding.ASCII.GetBytes(ascii, span);
        buffer.Advance(written);
        return this;
    }
}

/// <summary>Bencoded tracker response bodies (BEP 3, 7, 23, 48).</summary>
internal static class TrackerResponse
{
    public const string ContentType = "text/plain";

    public static byte[] Failure(string reason) =>
        new TrackerBencodeWriter()
            .BeginDictionary()
            .String("failure reason").String(reason)
            .End()
            .ToArray();

    public static byte[] Announce(TrackerAnnounceResult result, bool compact, bool noPeerId)
    {
        var writer = new TrackerBencodeWriter()
            .BeginDictionary()
            .String("complete").Integer(result.Complete)
            .String("incomplete").Integer(result.Incomplete)
            .String("interval").Integer((long)result.Interval.TotalSeconds)
            .String("min interval").Integer((long)result.MinInterval.TotalSeconds);

        if (compact)
        {
            writer.String("peers").String(CompactPeers(result.Peers, AddressFamily.InterNetwork, 4));
            var peers6 = CompactPeers(result.Peers, AddressFamily.InterNetworkV6, 16);
            if (peers6.Length > 0)
            {
                writer.String("peers6").String(peers6);
            }
        }
        else
        {
            writer.String("peers").BeginList();
            foreach (var peer in result.Peers)
            {
                writer.BeginDictionary().String("ip").String(peer.Address.ToString());
                if (!noPeerId)
                {
                    writer.String("peer id").String(peer.PeerId);
                }

                writer.String("port").Integer(peer.Port).End();
            }

            writer.End();
        }

        return writer.End().ToArray();
    }

    public static byte[] Scrape(IEnumerable<TrackerScrapeEntry> entries)
    {
        var writer = new TrackerBencodeWriter().BeginDictionary().String("files").BeginDictionary();
        foreach (var entry in entries.OrderBy(e => Convert.ToHexStringLower(e.InfoHash), StringComparer.Ordinal))
        {
            writer.String(entry.InfoHash)
                .BeginDictionary()
                .String("complete").Integer(entry.Complete)
                .String("downloaded").Integer(entry.Downloaded)
                .String("incomplete").Integer(entry.Incomplete)
                .End();
        }

        return writer.End().End().ToArray();
    }

    private static byte[] CompactPeers(IReadOnlyList<TrackerPeer> peers, AddressFamily family, int addressLength)
    {
        var matching = peers.Where(p => p.Address.AddressFamily == family).ToList();
        var bytes = new byte[matching.Count * (addressLength + 2)];
        var offset = 0;
        foreach (var peer in matching)
        {
            peer.Address.TryWriteBytes(bytes.AsSpan(offset, addressLength), out _);
            bytes[offset + addressLength] = (byte)(peer.Port >> 8);
            bytes[offset + addressLength + 1] = (byte)peer.Port;
            offset += addressLength + 2;
        }

        return bytes;
    }
}
