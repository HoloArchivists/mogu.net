using System.Buffers.Text;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Mogu.Core.Torrents;

/// <summary>Raised for unsupported values when encoding and for malformed input when decoding.</summary>
public sealed class BencodeException : Exception
{
    public BencodeException()
    {
    }

    public BencodeException(string message)
        : base(message)
    {
    }

    public BencodeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A bencoded dictionary. Keys are UTF-8 strings (as they are in every real torrent);
/// on encode they are sorted by their UTF-8 bytes as the spec requires.
/// Values are <see cref="long"/>, <see cref="byte"/>[], <see cref="List{T}"/> of values or <see cref="BencodeDictionary"/>.
/// </summary>
public sealed class BencodeDictionary : IReadOnlyDictionary<string, object>
{
    private readonly Dictionary<string, object> _items = new(StringComparer.Ordinal);

    public int Count => _items.Count;

    public IEnumerable<string> Keys => _items.Keys;

    public IEnumerable<object> Values => _items.Values;

    public object this[string key]
    {
        get => _items[key];
        set => _items[key] = value ?? throw new ArgumentNullException(nameof(value));
    }

    public bool ContainsKey(string key) => _items.ContainsKey(key);

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out object value) => _items.TryGetValue(key, out value);

    public object? GetValueOrDefault(string key) => _items.GetValueOrDefault(key);

    public bool Remove(string key) => _items.Remove(key);

    public bool Remove(string key, [MaybeNullWhen(false)] out object value) => _items.Remove(key, out value);

    public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Bencoding as described in https://wiki.theory.org/BitTorrentSpecification#Bencoding.
/// Byte strings decode to <see cref="byte"/>[], integers to <see cref="long"/>, lists to
/// <see cref="List{T}"/> and dictionaries to <see cref="BencodeDictionary"/>.
/// </summary>
public static class Bencode
{
    /// <summary>Guards against stack exhaustion from hostile input; real torrents nest a few levels deep.</summary>
    public const int MaxDepth = 256;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Decodes a single bencoded value.</summary>
    /// <param name="allowTrailingData">Ignore bytes after the first value (the old Python decoder did).</param>
    /// <exception cref="BencodeException">The input is not valid bencode.</exception>
    public static object Decode(ReadOnlySpan<byte> data, bool allowTrailingData = false)
    {
        var reader = new Reader(data);
        var value = reader.ReadValue(0) ?? throw reader.Error("Unexpected end of list or dictionary");
        if (!allowTrailingData && reader.Position != data.Length)
        {
            throw reader.Error("Unexpected data after the end of the value");
        }

        return value;
    }

    /// <summary>Encodes <see cref="long"/>/<see cref="int"/>, <see cref="byte"/>[], <see cref="string"/> (as UTF-8), lists and dictionaries.</summary>
    /// <exception cref="BencodeException">A value of an unsupported type was found.</exception>
    public static byte[] Encode(object value)
    {
        using var buffer = new MemoryStream();
        Write(buffer, value, 0);
        return buffer.ToArray();
    }

    private static void Write(MemoryStream output, object value, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new BencodeException("Value nests too deeply");
        }

        switch (value)
        {
            case long number:
                WriteInteger(output, number);
                break;
            case int number:
                WriteInteger(output, number);
                break;
            case byte[] bytes:
                WriteBytes(output, bytes);
                break;
            case string text:
                WriteBytes(output, Encoding.UTF8.GetBytes(text));
                break;
            case BencodeDictionary dictionary:
                output.WriteByte((byte)'d');
                foreach (var (key, item) in dictionary.OrderBy(kv => kv.Key, Utf8OrdinalComparer.Instance))
                {
                    WriteBytes(output, Encoding.UTF8.GetBytes(key));
                    Write(output, item, depth + 1);
                }

                output.WriteByte((byte)'e');
                break;
            case IEnumerable list:
                output.WriteByte((byte)'l');
                foreach (var item in list)
                {
                    Write(output, item ?? throw new BencodeException("Unsupported type null"), depth + 1);
                }

                output.WriteByte((byte)'e');
                break;
            default:
                throw new BencodeException("Unsupported type " + value.GetType().Name);
        }
    }

    private static void WriteInteger(MemoryStream output, long number)
    {
        output.WriteByte((byte)'i');
        output.Write(Encoding.ASCII.GetBytes(number.ToString(CultureInfo.InvariantCulture)));
        output.WriteByte((byte)'e');
    }

    private static void WriteBytes(MemoryStream output, byte[] bytes)
    {
        output.Write(Encoding.ASCII.GetBytes(bytes.Length.ToString(CultureInfo.InvariantCulture)));
        output.WriteByte((byte)':');
        output.Write(bytes);
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;

        public int Position { get; private set; }

        public readonly BencodeException Error(string message) =>
            new(string.Create(CultureInfo.InvariantCulture, $"{message} at position {Position} (0x{Position:X2} hex)"));

        /// <summary>Reads one value; returns null for the <c>e</c> that closes a list or dictionary.</summary>
        public object? ReadValue(int depth)
        {
            if (depth > MaxDepth)
            {
                throw Error("Value nests too deeply");
            }

            if (Position >= _data.Length)
            {
                throw Error("EOF, expecting kind");
            }

            var kind = _data[Position++];
            switch (kind)
            {
                case (byte)'i':
                    return ReadInteger();
                case (byte)'l':
                    return ReadList(depth);
                case (byte)'d':
                    return ReadDictionary(depth);
                case (byte)'e':
                    return null;
                case >= (byte)'0' and <= (byte)'9':
                    return ReadBytes();
                default:
                    Position--;
                    throw Error(string.Create(CultureInfo.InvariantCulture, $"Unexpected data type (0x{kind:X2})"));
            }
        }

        private long ReadInteger()
        {
            var start = Position;
            while (true)
            {
                if (Position >= _data.Length)
                {
                    throw Error("EOF, expecting more integer");
                }

                var c = _data[Position];
                if (c == (byte)'e')
                {
                    var digits = _data[start..Position];
                    Position++;
                    if (!Utf8Parser.TryParse(digits, out long value, out var consumed) || consumed != digits.Length)
                    {
                        throw Error("Unable to parse int");
                    }

                    return value;
                }

                // Digits, with an optional leading minus sign.
                if (!(c is >= (byte)'0' and <= (byte)'9' || (c == (byte)'-' && Position == start)))
                {
                    throw Error(string.Create(CultureInfo.InvariantCulture, $"Unexpected input while reading an integer (0x{c:X2})"));
                }

                Position++;
            }
        }

        private List<object> ReadList(int depth)
        {
            var items = new List<object>();
            while (ReadValue(depth + 1) is { } item)
            {
                items.Add(item);
            }

            return items;
        }

        private BencodeDictionary ReadDictionary(int depth)
        {
            var dictionary = new BencodeDictionary();
            while (true)
            {
                var keyPosition = Position;
                var key = ReadValue(depth + 1);
                if (key is null)
                {
                    return dictionary;
                }

                if (key is not byte[] keyBytes)
                {
                    Position = keyPosition;
                    throw Error("Dictionary key is not a byte string");
                }

                string keyText;
                try
                {
                    keyText = StrictUtf8.GetString(keyBytes);
                }
                catch (DecoderFallbackException)
                {
                    Position = keyPosition;
                    throw Error("Dictionary key is not valid UTF-8");
                }

                var value = ReadValue(depth + 1) ?? throw new BencodeException("Uneven amount of key/value pairs");

                // Duplicate keys: the last one wins, like Python's dict().
                dictionary[keyText] = value;
            }
        }

        private byte[] ReadBytes()
        {
            var start = Position - 1;
            while (true)
            {
                if (Position >= _data.Length)
                {
                    throw Error("EOF, expecting more string len");
                }

                var c = _data[Position];
                if (c == (byte)':')
                {
                    break;
                }

                if (c is < (byte)'0' or > (byte)'9')
                {
                    throw Error(string.Create(CultureInfo.InvariantCulture, $"Unexpected input while reading string length (0x{c:X2})"));
                }

                Position++;
            }

            if (!Utf8Parser.TryParse(_data[start..Position], out int length, out _))
            {
                throw Error("Unable to parse bytestring length");
            }

            Position++;
            var available = _data.Length - Position;
            if (length > available)
            {
                throw Error(string.Create(CultureInfo.InvariantCulture, $"Read only {available} bytes, {length} wanted"));
            }

            var bytes = _data.Slice(Position, length).ToArray();
            Position += length;
            return bytes;
        }
    }
}

/// <summary>Orders strings by Unicode code point, which equals the byte order of their UTF-8 encoding.</summary>
public sealed class Utf8OrdinalComparer : IComparer<string>
{
    public static readonly Utf8OrdinalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var left = x.EnumerateRunes();
        var right = y.EnumerateRunes();
        while (true)
        {
            var hasLeft = left.MoveNext();
            var hasRight = right.MoveNext();
            if (!hasLeft || !hasRight)
            {
                return hasLeft.CompareTo(hasRight);
            }

            var result = left.Current.Value.CompareTo(right.Current.Value);
            if (result != 0)
            {
                return result;
            }
        }
    }
}
