using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text;
using Mogu.Core.Options;

namespace Mogu.Web.Tracker;

/// <summary>
/// Parses tracker query strings from the raw (still percent-encoded) query, because <c>info_hash</c> and
/// <c>peer_id</c> are arbitrary bytes that the decoded <see cref="IQueryCollection"/> would mangle as UTF-8.
/// </summary>
public static class TrackerQueryParser
{
    public const int HashLength = 20;

    /// <summary>
    /// Parses an announce. The peer address is always <paramref name="clientAddress"/>; the client supplied
    /// <c>ip</c> parameter is ignored so nobody can register (or stop) peers on behalf of other hosts.
    /// </summary>
    public static bool TryParseAnnounce(
        string? rawQuery,
        IPAddress clientAddress,
        TrackerOptions options,
        [NotNullWhen(true)] out TrackerAnnounceRequest? request,
        out string failureReason)
    {
        request = null;
        failureReason = "";

        if (!TryParseRaw(rawQuery, out var parameters, out failureReason))
        {
            return false;
        }

        if (!TryGet(parameters, "info_hash", out var infoHash) || infoHash.Length != HashLength)
        {
            failureReason = "Invalid info_hash";
            return false;
        }

        if (!TryGet(parameters, "peer_id", out var peerId) || peerId.Length != HashLength)
        {
            failureReason = "Invalid peer_id";
            return false;
        }

        if (!TryGetInt64(parameters, "port", out var port) || port is < 1 or > ushort.MaxValue)
        {
            failureReason = "Invalid port";
            return false;
        }

        // Some clients omit the transfer counters on stopped announces; only "left" is required.
        if (!TryGetInt64(parameters, "left", out var left)
            || !TryGetOptionalInt64(parameters, "uploaded", out var uploaded)
            || !TryGetOptionalInt64(parameters, "downloaded", out var downloaded))
        {
            failureReason = "Invalid uploaded, downloaded or left";
            return false;
        }

        if (uploaded < 0 || downloaded < 0 || left < 0)
        {
            failureReason = "uploaded, downloaded and left must not be negative";
            return false;
        }

        var trackerEvent = TryGetString(parameters, "event", out var eventName)
            ? eventName.ToUpperInvariant() switch
            {
                "STARTED" => TrackerEvent.Started,
                "STOPPED" => TrackerEvent.Stopped,
                "COMPLETED" => TrackerEvent.Completed,
                _ => TrackerEvent.None, // "" and "empty" are both used for regular announces
            }
            : TrackerEvent.None;

        // Practically every client asks for compact peers; default to it like most trackers do.
        var compact = !(TryGetString(parameters, "compact", out var compactValue) && compactValue == "0");
        var noPeerId = TryGetString(parameters, "no_peer_id", out var noPeerIdValue) && noPeerIdValue == "1";

        var numWant = TryGetInt64(parameters, "numwant", out var requested) && requested >= 0
            ? (int)Math.Min(requested, options.MaxNumWant)
            : Math.Min(options.DefaultNumWant, options.MaxNumWant);

        request = new TrackerAnnounceRequest(
            infoHash,
            peerId,
            Normalize(clientAddress),
            (int)port,
            uploaded,
            downloaded,
            left,
            trackerEvent,
            compact,
            noPeerId,
            numWant);
        return true;
    }

    /// <summary>Parses the (possibly repeated) <c>info_hash</c> parameters of a scrape.</summary>
    public static bool TryParseScrape(
        string? rawQuery,
        TrackerOptions options,
        out IReadOnlyList<byte[]> infoHashes,
        out string failureReason)
    {
        infoHashes = [];

        if (!TryParseRaw(rawQuery, out var parameters, out failureReason))
        {
            return false;
        }

        // Full scrapes (no info_hash) would publish the whole torrent list and aren't supported.
        if (!parameters.TryGetValue("info_hash", out var values) || values.Count == 0)
        {
            failureReason = "info_hash is required";
            return false;
        }

        if (values.Exists(v => v.Length != HashLength))
        {
            failureReason = "Invalid info_hash";
            return false;
        }

        if (values.Count > options.MaxScrapeInfoHashes)
        {
            failureReason = $"Too many info_hash values (maximum {options.MaxScrapeInfoHashes})";
            return false;
        }

        infoHashes = values;
        return true;
    }

    public static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool TryParseRaw(string? rawQuery, out Dictionary<string, List<byte[]>> parameters, out string failureReason)
    {
        failureReason = "";
        try
        {
            parameters = ParseRawQuery(rawQuery ?? "");
            return true;
        }
        catch (FormatException ex)
        {
            parameters = [];
            failureReason = ex.Message;
            return false;
        }
    }

    internal static Dictionary<string, List<byte[]>> ParseRawQuery(string rawQuery)
    {
        var result = new Dictionary<string, List<byte[]>>(StringComparer.Ordinal);
        var query = rawQuery.StartsWith('?') ? rawQuery[1..] : rawQuery;

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            var rawKey = equals >= 0 ? pair[..equals] : pair;
            var rawValue = equals >= 0 ? pair[(equals + 1)..] : "";
            var key = Encoding.ASCII.GetString(PercentDecode(rawKey)).ToLowerInvariant();

            if (!result.TryGetValue(key, out var values))
            {
                values = [];
                result[key] = values;
            }

            values.Add(PercentDecode(rawValue));
        }

        return result;
    }

    internal static byte[] PercentDecode(string value)
    {
        var buffer = new byte[value.Length];
        var length = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '%')
            {
                if (i + 2 >= value.Length
                    || !byte.TryParse(value.AsSpan(i + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var decoded))
                {
                    throw new FormatException("Invalid percent encoding in query string");
                }

                buffer[length++] = decoded;
                i += 2;
            }
            else if (c == '+')
            {
                buffer[length++] = (byte)' ';
            }
            else if (c > 0x7F)
            {
                throw new FormatException("Invalid character in query string");
            }
            else
            {
                buffer[length++] = (byte)c;
            }
        }

        return buffer[..length];
    }

    private static bool TryGet(Dictionary<string, List<byte[]>> parameters, string key, out byte[] value)
    {
        if (parameters.TryGetValue(key, out var values) && values.Count > 0)
        {
            value = values[0];
            return true;
        }

        value = [];
        return false;
    }

    private static bool TryGetString(Dictionary<string, List<byte[]>> parameters, string key, out string value)
    {
        value = TryGet(parameters, key, out var bytes) ? Encoding.ASCII.GetString(bytes) : "";
        return parameters.ContainsKey(key);
    }

    private static bool TryGetInt64(Dictionary<string, List<byte[]>> parameters, string key, out long value)
    {
        value = 0;
        return TryGetString(parameters, key, out var text)
            && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetOptionalInt64(Dictionary<string, List<byte[]>> parameters, string key, out long value)
    {
        if (!parameters.ContainsKey(key))
        {
            value = 0;
            return true;
        }

        return TryGetInt64(parameters, key, out value);
    }
}
