namespace Mogu.Core.Entities;

public class Torrent
{
    public int Id { get; set; }

    /// <summary>20-byte SHA-1 of the bencoded info dictionary.</summary>
    public required byte[] InfoHash { get; set; }

    public required string DisplayName { get; set; }

    /// <summary>Original file name of the uploaded .torrent.</summary>
    public required string TorrentName { get; set; }

    public string Information { get; set; } = "";

    public string Description { get; set; } = "";

    public long FileSize { get; set; }

    public string Encoding { get; set; } = "utf-8";

    public bool IsAnonymous { get; set; }

    public bool IsHidden { get; set; }

    public bool IsTrusted { get; set; }

    public bool IsRemake { get; set; }

    public bool IsComplete { get; set; }

    public bool IsDeleted { get; set; }

    public bool IsBanned { get; set; }

    public bool IsCommentLocked { get; set; }

    /// <summary>Whether a bencoded info dict is stored for this torrent (so a .torrent can be served).</summary>
    public bool HasTorrent { get; set; }

    public int? UploaderId { get; set; }

    public User? Uploader { get; set; }

    public string? UploaderIp { get; set; }

    public int MainCategoryId { get; set; }

    public int SubCategoryId { get; set; }

    public MainCategory MainCategory { get; set; } = null!;

    public SubCategory SubCategory { get; set; } = null!;

    public int CommentCount { get; set; }

    public int SeedCount { get; set; }

    public int LeechCount { get; set; }

    public int DownloadCount { get; set; }

    public DateTime? StatsUpdatedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public TorrentFileList? FileList { get; set; }

    public List<TorrentTracker> Trackers { get; set; } = [];

    public List<Comment> Comments { get; set; } = [];

    public string InfoHashHex => Convert.ToHexStringLower(InfoHash);

    public string CategoryCode => $"{MainCategoryId}_{SubCategoryId}";
}

/// <summary>JSON file tree of a torrent, kept apart from <see cref="Torrent"/> so listings stay light.</summary>
public class TorrentFileList
{
    public int TorrentId { get; set; }

    /// <summary>Nested JSON object: directories map to objects, files map to their size in bytes.</summary>
    public required string Json { get; set; }
}

public class Tracker
{
    public int Id { get; set; }

    public required string Uri { get; set; }

    public bool IsWebSeed { get; set; }

    public bool IsDisabled { get; set; }
}

public class TorrentTracker
{
    public int TorrentId { get; set; }

    public int TrackerId { get; set; }

    public int Order { get; set; }

    public Tracker Tracker { get; set; } = null!;
}
