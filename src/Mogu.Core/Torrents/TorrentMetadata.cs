using System.Security.Cryptography;
using System.Text;

namespace Mogu.Core.Torrents;

/// <summary>An uploaded .torrent was rejected; <see cref="Exception.Message"/> is shown to the uploader.</summary>
public sealed class TorrentValidationException : Exception
{
    public TorrentValidationException()
    {
    }

    public TorrentValidationException(string message)
        : base(message)
    {
    }

    public TorrentValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A decoded and validated .torrent (the old <c>UploadForm.validate_torrent_file</c> and the
/// parsing half of <c>handle_torrent_upload</c>). See https://wiki.theory.org/BitTorrentSpecification#Metainfo_File_Structure.
/// </summary>
public sealed class TorrentMetadata
{
    static TorrentMetadata()
    {
        // Legacy code pages (shift_jis, gbk, ...) that some torrents declare as their encoding.
        System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>Limit on path parts per file, so the file tree stays serialisable.</summary>
    public const int MaxPathDepth = 128;

    private TorrentMetadata(BencodeDictionary torrent, byte[] infoDictionary)
    {
        Torrent = torrent;
        InfoDictionary = infoDictionary;
#pragma warning disable CA5350 // BitTorrent v1 info hashes are SHA-1 by definition.
        InfoHash = SHA1.HashData(infoDictionary);
#pragma warning restore CA5350
    }

    /// <summary>The decoded torrent, with BitComet style <c>*.utf-8</c> keys folded into their plain keys.</summary>
    public BencodeDictionary Torrent { get; }

    /// <summary>
    /// The info dict re-encoded with sorted keys (exactly what is hashed and stored). A torrent
    /// whose keys were not sorted gets a different hash, but that is its client's fault.
    /// </summary>
    public byte[] InfoDictionary { get; }

    /// <summary>SHA-1 of <see cref="InfoDictionary"/>.</summary>
    public byte[] InfoHash { get; }

    public string InfoHashHex => Convert.ToHexStringLower(InfoHash);

    /// <summary>The torrent's <c>name</c>, used as display name when the uploader gives none.</summary>
    public string Name { get; private set; } = "";

    /// <summary>The torrent's declared <c>encoding</c>, <c>utf-8</c> if absent.</summary>
    public string Encoding { get; private set; } = "utf-8";

    public long TotalSize { get; private set; }

    public bool IsPrivate { get; private set; }

    /// <summary>Announce URLs in order: <c>announce</c>, then the first URL of each <c>announce-list</c> tier. Distinct.</summary>
    public IReadOnlyList<string> Trackers { get; private set; } = [];

    /// <summary>BEP 19 web seeds from <c>url-list</c>. Distinct.</summary>
    public IReadOnlyList<string> WebSeeds { get; private set; } = [];

    public TorrentFileTree FileTree { get; private set; } = new();

    /// <summary>Decodes and validates an uploaded .torrent.</summary>
    /// <exception cref="TorrentValidationException">With the same messages as the old upload form.</exception>
    public static TorrentMetadata Parse(ReadOnlySpan<byte> data)
    {
        object decoded;
        try
        {
            // The old decoder ignored trailing garbage; keep accepting such files.
            decoded = Bencode.Decode(data, allowTrailingData: true);
        }
        catch (BencodeException e)
        {
            throw new TorrentValidationException("Malformed torrent file", e);
        }

        try
        {
            ValidateMetadata(decoded);
        }
        catch (AssertionException e)
        {
            throw new TorrentValidationException($"Malformed torrent metadata ({e.Message})", e);
        }

        var torrent = (BencodeDictionary)decoded;
        try
        {
            ValidateTrackers(torrent);
        }
        catch (AssertionException e)
        {
            throw new TorrentValidationException($"Malformed torrent trackers ({e.Message})", e);
        }

        var info = (BencodeDictionary)torrent["info"];
        var metadata = new TorrentMetadata(torrent, Bencode.Encode(info))
        {
            IsPrivate = info.GetValueOrDefault("private") is 1L,
            Encoding = System.Text.Encoding.UTF8.GetString((byte[])(torrent.GetValueOrDefault("encoding") ?? "utf-8"u8.ToArray())),
        };

        // Fold *.utf-8 keys only after hashing: the hash is over what the uploader sent.
        var changedToUtf8 = ReplaceUtf8Values(torrent);
        try
        {
            metadata.ReadContents(info, changedToUtf8);
        }
        catch (AssertionException e)
        {
            throw new TorrentValidationException($"Malformed torrent metadata ({e.Message})", e);
        }

        return metadata;
    }

    /// <summary>Whether <paramref name="trackerUrl"/> is one of the torrent's announce URLs (case-insensitive).</summary>
    public bool HasTracker(string trackerUrl) =>
        Trackers.Contains(trackerUrl, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Private torrents must announce to our tracker first; with <paramref name="enforceMainAnnounceUrl"/>
    /// every torrent must include it. Returns the error message, or null if acceptable.
    /// </summary>
    public string? CheckTrackerPolicy(string? mainAnnounceUrl, bool enforceMainAnnounceUrl)
    {
        if (IsPrivate)
        {
            var announce = System.Text.Encoding.UTF8.GetString((byte[])Torrent["announce"]);
            return announce == mainAnnounceUrl ? null : $"Private torrent: please set {mainAnnounceUrl} as the main tracker";
        }

        if (enforceMainAnnounceUrl && (string.IsNullOrEmpty(mainAnnounceUrl) || !HasTracker(mainAnnounceUrl)))
        {
            return $"Please include {mainAnnounceUrl} in the trackers of the torrent";
        }

        return null;
    }

    private void ReadContents(BencodeDictionary info, bool changedToUtf8)
    {
        var pathEncoding = changedToUtf8 ? "utf-8" : Encoding;
        var decoder = GetDecoder(pathEncoding);
        string DecodePart(object? value, string name) =>
            Decode(AssertBytes(value, name, checkEmpty: false), decoder, name, pathEncoding);

        Name = DecodePart(info.GetValueOrDefault("name"), "name");

        var files = info.GetValueOrDefault("files") as List<object>;
        TotalSize = info.GetValueOrDefault("length") is long length and not 0
            ? length
            : files?.Sum(f => f is BencodeDictionary file && file.GetValueOrDefault("length") is long l ? l : 0) ?? 0;

        var tree = new TorrentFileTree();
        if (files is null)
        {
            // Single-file torrent: the file sits at the root.
            tree.AddFile([Name], TotalSize);
        }
        else
        {
            tree.AddDirectory(Name);
            foreach (var entry in files)
            {
                var file = AssertDictionary(entry, "file");
                var path = file.GetValueOrDefault("path") as List<object> ?? [];
                var parts = path.Select(part => DecodePart(part, "path part")).Prepend(Name).ToList();
                if (parts.Count > 1 && file.GetValueOrDefault("length") is long fileLength)
                {
                    tree.AddFile(parts, fileLength);
                }
            }
        }

        FileTree = tree;

        var trackers = new List<string>();
        if (Torrent.GetValueOrDefault("announce") is byte[] announce && announce.Length > 0)
        {
            trackers.Add(System.Text.Encoding.UTF8.GetString(announce));
        }

        if (Torrent.GetValueOrDefault("announce-list") is List<object> announceList)
        {
            trackers.AddRange(announceList
                .OfType<List<object>>()
                .Where(tier => tier.Count > 0 && tier[0] is byte[])
                .Select(tier => System.Text.Encoding.UTF8.GetString((byte[])tier[0])));
        }

        Trackers = trackers.Distinct(StringComparer.Ordinal).ToList();

        // qBittorrent writes an empty string for no web seeds and a bare string for a single one.
        var webSeeds = Torrent.GetValueOrDefault("url-list") switch
        {
            byte[] { Length: > 0 } single => [single],
            List<object> list => list.OfType<byte[]>(),
            _ => [],
        };
        WebSeeds = webSeeds.Select(System.Text.Encoding.UTF8.GetString).Distinct(StringComparer.Ordinal).ToList();
    }

    private static void ValidateMetadata(object decoded)
    {
        var torrent = AssertDictionary(decoded, "torrent metadata");

        var infoValue = torrent.GetValueOrDefault("info");
        Assert(infoValue is not null, "no info_dict in torrent");
        var info = AssertDictionary(infoValue, "info");

        var encodingName = AssertBytesDecodable(torrent.GetValueOrDefault("encoding") ?? "utf-8"u8.ToArray(), "encoding", "utf-8")
            .ToLowerInvariant();
        var decoder = GetDecoder(encodingName);

        AssertBytesDecodable(info.GetValueOrDefault("name"), "name", encodingName, decoder);

        AssertNumber(info.GetValueOrDefault("piece length"), "piece length", positive: true);

        var pieces = AssertBytes(info.GetValueOrDefault("pieces"), "pieces");
        Assert(pieces.Length % 20 == 0, "pieces length is not a multiple of 20");

        var files = info.GetValueOrDefault("files");
        if (files is not null)
        {
            foreach (var entry in AssertList(files, "filelist"))
            {
                var file = AssertDictionary(entry, "file");
                AssertNumber(file.GetValueOrDefault("length"), "file length", positive: false);

                var path = AssertList(file.GetValueOrDefault("path"), "path");
                Assert(path.Count > 0, "path is empty");
                Assert(path.Count <= MaxPathDepth, "path is too deep");

                // Directory names must not be empty; the file name may be (BitComet's empty directories).
                foreach (var part in path.Take(path.Count - 1))
                {
                    AssertBytesDecodable(part, "path part", encodingName, decoder);
                }

                AssertBytesDecodable(path[^1], "filename", encodingName, decoder, checkEmpty: false);
            }
        }
        else
        {
            AssertNumber(info.GetValueOrDefault("length"), "length", positive: true);
        }

        // http://www.bittorrent.org/beps/bep_0019.html
        var webSeeds = torrent.GetValueOrDefault("url-list");
        if (webSeeds is byte[] single)
        {
            webSeeds = single.Length > 0 ? new List<object> { single } : null;
        }

        if (IsTruthy(webSeeds))
        {
            foreach (var url in AssertList(webSeeds, "url-list"))
            {
                AssertBytesDecodable(url, "url-list item", "utf-8");
            }
        }
    }

    private static void ValidateTrackers(BencodeDictionary torrent)
    {
        var announce = torrent.GetValueOrDefault("announce");
        Assert(announce is not null, "no tracker in torrent");
        AssertBytesDecodable(announce, "announce", "utf-8");

        var announceList = torrent.GetValueOrDefault("announce-list");
        if (announceList is not null)
        {
            foreach (var tier in AssertList(announceList, "announce-list"))
            {
                var urls = AssertList(tier, "announce-list item");
                if (urls.Count > 0)
                {
                    AssertBytesDecodable(urls[0], "announce-list item url", "utf-8");
                }
            }
        }
    }

    /// <summary>
    /// Replaces <c>property</c> with <c>property.utf-8</c> throughout (thanks, BitComet).
    /// Returns whether anything changed.
    /// </summary>
    internal static bool ReplaceUtf8Values(object value)
    {
        var changed = false;
        if (value is BencodeDictionary dictionary)
        {
            foreach (var key in dictionary.Keys.Where(k => k.EndsWith(".utf-8", StringComparison.Ordinal)).ToList())
            {
                dictionary.Remove(key, out var item);
                dictionary[key.Replace(".utf-8", "", StringComparison.Ordinal)] = item!;
                changed = true;
            }

            foreach (var item in dictionary.Values)
            {
                changed = ReplaceUtf8Values(item) || changed;
            }
        }
        else if (value is List<object> list)
        {
            foreach (var item in list)
            {
                changed = ReplaceUtf8Values(item) || changed;
            }
        }

        return changed;
    }

    private static bool IsTruthy(object? value) => value switch
    {
        null => false,
        long number => number != 0,
        byte[] bytes => bytes.Length > 0,
        List<object> list => list.Count > 0,
        BencodeDictionary dictionary => dictionary.Count > 0,
        _ => true,
    };

    /// <summary>A strict decoder for a Python codec name such as <c>utf-8</c>, <c>utf8</c>, <c>shift_jis</c> or <c>gbk</c>.</summary>
    private static System.Text.Encoding GetDecoder(string name)
    {
        var normalized = name.Trim().ToLowerInvariant();
        string[] candidates = normalized.Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal) == "utf8"
            ? ["utf-8"]
            : [normalized, normalized.Replace('_', '-'), normalized.Replace('-', '_')];
        foreach (var candidate in candidates)
        {
            try
            {
                var encoding = System.Text.Encoding.GetEncoding(candidate);
                return System.Text.Encoding.GetEncoding(encoding.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            }
            catch (ArgumentException)
            {
                // Try the next spelling.
            }
        }

        throw new AssertionException($"encoding '{name}' is unknown");
    }

    private static string Decode(byte[] bytes, System.Text.Encoding decoder, string name, string encodingName)
    {
        try
        {
            return decoder.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new AssertionException($"{name} could not be decoded from '{encodingName}'");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new AssertionException(message);
        }
    }

    private static BencodeDictionary AssertDictionary(object? value, string name) =>
        value as BencodeDictionary ?? throw new AssertionException(name + " is not a dict");

    private static List<object> AssertList(object? value, string name) =>
        value as List<object> ?? throw new AssertionException(name + " is not a list");

    private static void AssertNumber(object? value, string name, bool positive)
    {
        if (value is not long number)
        {
            throw new AssertionException(name + " is not an int");
        }

        if (positive)
        {
            Assert(number > 0, name + " is not positive");
        }
        else
        {
            Assert(number >= 0, name + " is less than 0");
        }
    }

    private static byte[] AssertBytes(object? value, string name, bool checkEmpty = true)
    {
        if (value is not byte[] bytes)
        {
            throw new AssertionException(name + " is not bytes");
        }

        if (checkEmpty)
        {
            Assert(bytes.Length > 0, name + " is empty");
        }

        return bytes;
    }

    private static string AssertBytesDecodable(object? value, string name, string encodingName, System.Text.Encoding? decoder = null, bool checkEmpty = true)
    {
        var bytes = AssertBytes(value, name, checkEmpty);
        return Decode(bytes, decoder ?? GetDecoder(encodingName), name, encodingName);
    }

    /// <summary>Internal control flow for validation failures, mirroring the old <c>assert</c>s.</summary>
    private sealed class AssertionException(string message) : Exception(message);
}
