using System.ComponentModel.DataAnnotations;

namespace Mogu.Core.Options;

/// <summary>Site settings, bound from the <c>Mogu</c> configuration section.</summary>
public sealed class MoguOptions
{
    public const string SectionName = "Mogu";

    [Required]
    public string SiteName { get; set; } = "Mogu";

    /// <summary>Root for info dicts and .torrent backups. Relative paths resolve against the content root.</summary>
    [Required]
    public string DataDirectory { get; set; } = "data";

    /// <summary>Read-only mode: POSTs are rejected (except logins when <see cref="MaintenanceModeLogins"/>).</summary>
    public bool MaintenanceMode { get; set; }

    public string MaintenanceModeMessage { get; set; } = "Site is currently in read-only maintenance mode.";

    public bool MaintenanceModeLogins { get; set; } = true;

    /// <summary>Block anonymous uploads completely.</summary>
    public bool RaidModeLimitUploads { get; set; }

    public string RaidModeUploadsMessage { get; set; } = "Anonymous uploads are currently disabled.";

    /// <summary>Show seeders/leechers/completions in listings and torrent pages.</summary>
    public bool EnableShowStats { get; set; }

    /// <summary>Above this many files the torrent page shows "Too many files to display."</summary>
    [Range(1, int.MaxValue)]
    public int MaxFilesView { get; set; } = 1000;

    /// <summary>Our tracker; always the first announce in generated torrents and magnets.</summary>
    public string? MainAnnounceUrl { get; set; }

    /// <summary>Require uploaded torrents to contain <see cref="MainAnnounceUrl"/>.</summary>
    public bool EnforceMainAnnounceUrl { get; set; }

    /// <summary>Extra trackers appended to every generated torrent and magnet link.</summary>
    public List<string> DefaultTrackers { get; set; } = [];

    /// <summary>
    /// Trackers that are never put in generated .torrent files or magnet links and are stripped from new
    /// uploads (case-insensitive exact match), e.g. dead trackers still referenced by old torrents.
    /// </summary>
    public List<string> TrackerBlacklist { get; set; } = [];

    /// <summary>True when <paramref name="uri"/> is in <see cref="TrackerBlacklist"/>.</summary>
    public bool IsBlacklistedTracker(string uri) =>
        TrackerBlacklist.Exists(t => string.Equals(t.Trim(), uri.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Maximum number of trackers put in magnet links.</summary>
    [Range(0, 100)]
    public int MagnetMaxTrackers { get; set; } = 5;

    public bool RateLimitUploads { get; set; }

    /// <summary>Accounts younger than this (and not trusted) are upload rate limited and can't report.</summary>
    public TimeSpan RateLimitAccountAge { get; set; } = TimeSpan.FromDays(7);

    /// <summary>After this many uploads within <see cref="UploadBurstDuration"/>, uploads must be <see cref="UploadTimeout"/> apart.</summary>
    [Range(1, int.MaxValue)]
    public int MaxUploadBurst { get; set; } = 5;

    public TimeSpan UploadBurstDuration { get; set; } = TimeSpan.FromMinutes(45);

    public TimeSpan UploadTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Minimum total size of anonymous uploads in bytes, 0 to disable.</summary>
    [Range(0, long.MaxValue)]
    public long MinimumAnonymousTorrentSize { get; set; } = 1024 * 1024;

    [Range(1, 1000)]
    public int ResultsPerPage { get; set; } = 75;

    /// <summary>Maximum page number for listings (moderators and users viewing themselves are exempt), 0 for no limit.</summary>
    [Range(0, int.MaxValue)]
    public int MaxPages { get; set; } = 100;

    /// <summary>How long after posting a comment may be edited or deleted by its author. Zero disables the limit.</summary>
    public TimeSpan EditingTimeLimit { get; set; } = TimeSpan.Zero;

    /// <summary>Torrents linked from the "Sticky Torrents" navbar dropdown.</summary>
    public List<StickyTorrent> StickyTorrents { get; set; } = [];

    /// <summary>Commit shown in the footer; links to the source repository.</summary>
    public string? CommitHash { get; set; }

    public string SourceRepositoryUrl { get; set; } = "https://github.com/HoloArchivists/mogu";

    /// <summary>Host of the work-safe site, named on the rules page (old <c>EXTERNAL_URLS['main']</c>).</summary>
    public string MainSiteHost { get; set; } = "mogu.holopirates.moe";

    /// <summary>Host of the non-work-safe sister site, named on the rules page (old <c>EXTERNAL_URLS['fap']</c>).</summary>
    public string NsfwSiteHost { get; set; } = "yabe.holopirates.moe";
}

public sealed class StickyTorrent
{
    [Required]
    public string Label { get; set; } = "";

    public int Id { get; set; }
}
