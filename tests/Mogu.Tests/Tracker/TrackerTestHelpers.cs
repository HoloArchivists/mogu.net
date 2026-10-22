using System.Globalization;
using System.Text;

namespace Mogu.Tests.Tracker;

/// <summary>Settable clock for tracker tests.</summary>
internal sealed class TrackerTestTimeProvider : TimeProvider
{
    private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan by) => now += by;
}

/// <summary>Tiny bencode decoder for asserting on tracker responses.</summary>
internal static class TrackerTestBencode
{
    public static object Decode(byte[] data)
    {
        var position = 0;
        var value = Read(data, ref position);
        Assert.AreEqual(data.Length, position, "Trailing data after bencoded value");
        return value;
    }

    public static Dictionary<string, object> DecodeDictionary(byte[] data) =>
        (Dictionary<string, object>)Decode(data);

    public static string Text(object value) => Encoding.UTF8.GetString((byte[])value);

    public static string PercentEncode(byte[] bytes) =>
        string.Concat(bytes.Select(b => "%" + b.ToString("X2", CultureInfo.InvariantCulture)));

    // Keys are decoded as Latin-1 so raw info hash keys survive the round trip.
    private static object Read(byte[] data, ref int position)
    {
        switch (data[position])
        {
            case (byte)'i':
            {
                var end = Array.IndexOf(data, (byte)'e', position);
                var value = long.Parse(Encoding.ASCII.GetString(data, position + 1, end - position - 1), CultureInfo.InvariantCulture);
                position = end + 1;
                return value;
            }
            case (byte)'l':
            {
                position++;
                var list = new List<object>();
                while (data[position] != (byte)'e')
                {
                    list.Add(Read(data, ref position));
                }

                position++;
                return list;
            }
            case (byte)'d':
            {
                position++;
                var dictionary = new Dictionary<string, object>(StringComparer.Ordinal);
                string? previous = null;
                while (data[position] != (byte)'e')
                {
                    var key = Encoding.Latin1.GetString((byte[])Read(data, ref position));
                    Assert.IsTrue(previous is null || string.CompareOrdinal(previous, key) < 0, $"Dictionary keys not sorted: {previous} >= {key}");
                    previous = key;
                    dictionary[key] = Read(data, ref position);
                }

                position++;
                return dictionary;
            }
            default:
            {
                var colon = Array.IndexOf(data, (byte)':', position);
                var length = int.Parse(Encoding.ASCII.GetString(data, position, colon - position), CultureInfo.InvariantCulture);
                var value = data.AsSpan(colon + 1, length).ToArray();
                position = colon + 1 + length;
                return value;
            }
        }
    }
}
