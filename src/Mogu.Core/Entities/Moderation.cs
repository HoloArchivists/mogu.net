using System.Net;

namespace Mogu.Core.Entities;

public class Comment
{
    public int Id { get; set; }

    public int TorrentId { get; set; }

    public Torrent Torrent { get; set; } = null!;

    public int UserId { get; set; }

    public User User { get; set; } = null!;

    public required string Text { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? EditedAt { get; set; }
}

public class Report
{
    public int Id { get; set; }

    public int TorrentId { get; set; }

    public Torrent Torrent { get; set; } = null!;

    public int? UserId { get; set; }

    public User? User { get; set; }

    public required string Reason { get; set; }

    public ReportStatus Status { get; set; } = ReportStatus.InReview;

    public DateTime CreatedAt { get; set; }
}

public class AdminLogEntry
{
    public int Id { get; set; }

    public int AdminId { get; set; }

    public User Admin { get; set; } = null!;

    /// <summary>Markdown text, rendered client side.</summary>
    public required string Message { get; set; }

    public DateTime CreatedAt { get; set; }
}

public class Ban
{
    public int Id { get; set; }

    public int AdminId { get; set; }

    public User Admin { get; set; } = null!;

    public int? UserId { get; set; }

    public User? User { get; set; }

    /// <summary>Banned IP address in canonical string form.</summary>
    public string? UserIp { get; set; }

    public required string Reason { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>An IPv4 CIDR range that may not upload anonymously.</summary>
public class RangeBan
{
    public int Id { get; set; }

    public required string Cidr { get; set; }

    /// <summary>Network address as a big-endian integer.</summary>
    public long MaskedAddress { get; set; }

    public long Mask { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>Creation time for temporary bans that may be cleared; null for permanent bans.</summary>
    public DateTime? TemporarySince { get; set; }

    public static RangeBan FromCidr(string cidr)
    {
        var network = IPNetwork.Parse(cidr);
        var prefix = network.PrefixLength;
        var mask = prefix == 0 ? 0L : (long)(uint.MaxValue << (32 - prefix));
        return new RangeBan
        {
            Cidr = network.ToString(),
            Mask = mask,
            MaskedAddress = ToInt64(network.BaseAddress) & mask,
        };
    }

    public static long ToInt64(IPAddress address)
    {
        var bytes = address.MapToIPv4().GetAddressBytes();
        return ((long)bytes[0] << 24) | ((long)bytes[1] << 16) | ((long)bytes[2] << 8) | bytes[3];
    }
}

public enum TrackerWhitelistAction
{
    Insert = 0,
    Remove = 1,
}

/// <summary>Queue of info hash changes for an external tracker whitelist.</summary>
public class TrackerWhitelistEvent
{
    public int Id { get; set; }

    public required byte[] InfoHash { get; set; }

    public TrackerWhitelistAction Action { get; set; }
}
