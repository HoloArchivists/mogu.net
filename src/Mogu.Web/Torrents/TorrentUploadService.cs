using Mogu.Core.Text;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mogu.Core.Data;
using Mogu.Core.Entities;
using Mogu.Core.Options;
using Mogu.Core.Torrents;
using Mogu.Web.Infrastructure;

namespace Mogu.Web.Torrents;

/// <summary>Upload rate limit state of a user or IP (old <c>check_uploader_ratelimit</c>).</summary>
/// <param name="Count">Torrents uploaded within <see cref="MoguOptions.UploadBurstDuration"/>.</param>
/// <param name="NextAllowed">When the next upload is allowed; null if allowed now.</param>
public sealed record UploadRateLimit(DateTime Now, int Count, DateTime? NextAllowed)
{
    public bool IsLimited => NextAllowed is not null;
}

public sealed class UploadResult
{
    private UploadResult(Torrent? torrent, UploadErrors errors)
    {
        Torrent = torrent;
        Errors = errors;
    }

    public Torrent? Torrent { get; }

    public UploadErrors Errors { get; }

    [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(true, nameof(Torrent))]
    public bool Succeeded => Torrent is not null;

    public static UploadResult Success(Torrent torrent) => new(torrent, new UploadErrors());

    public static UploadResult Failure(UploadErrors errors) => new(null, errors);

    public static UploadResult Failure(string field, string message)
    {
        var errors = new UploadErrors();
        errors.Add(field, message);
        return new(null, errors);
    }
}

/// <summary>Validates and stores uploaded torrents (old <c>validate_torrent_file</c> + <c>handle_torrent_upload</c>).</summary>
public sealed partial class TorrentUploadService(
    MoguDbContext db,
    DataPaths paths,
    TimeProvider time,
    IOptionsSnapshot<MoguOptions> options,
    TorrentLinks links,
    ILogger<TorrentUploadService> logger)
{
    private MoguOptions Settings => options.Value;

    /// <summary>Anonymous uploaders and young untrusted accounts are rate limited when <see cref="MoguOptions.RateLimitUploads"/> is on.</summary>
    public bool IsSubjectToRateLimit(User? user) =>
        Settings.RateLimitUploads
        && (user is null || (user.Age(time.GetUtcNow().UtcDateTime) < Settings.RateLimitAccountAge && !user.IsTrusted));

    public async Task<UploadRateLimit> GetRateLimitAsync(User? user, string? ip, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var userId = user?.Id;
        var uploads = db.Torrents.Where(t => (userId != null && t.UploaderId == userId) || (ip != null && t.UploaderIp == ip));

        var since = now - Settings.UploadBurstDuration;
        var count = await uploads.CountAsync(t => t.CreatedAt >= since, cancellationToken);
        if (count < Settings.MaxUploadBurst)
        {
            return new UploadRateLimit(now, count, null);
        }

        var last = await uploads.MaxAsync(t => t.CreatedAt, cancellationToken);
        var afterTimeout = last + Settings.UploadTimeout;
        return new UploadRateLimit(now, count, now < afterTimeout ? afterTimeout : null);
    }

    /// <summary>
    /// Validates the upload and stores it. When <paramref name="inputIsValid"/> is false (the caller
    /// already found field errors) only the torrent file and category are checked, so all form errors
    /// can be shown at once, and nothing is stored.
    /// </summary>
    public async Task<UploadResult> UploadAsync(UploadRequest request, User? uploader, string? ip, bool inputIsValid, CancellationToken cancellationToken)
    {
        // Form level validation: category and the torrent file itself.
        var errors = new UploadErrors();
        var category = await ValidateCategoryAsync(request.Category, errors, cancellationToken);
        var (metadata, replacesTorrentId) = await ValidateTorrentFileAsync(request.TorrentData, errors, cancellationToken);
        if (!inputIsValid || errors.HasErrors || metadata is null || category is null)
        {
            return UploadResult.Failure(errors);
        }

        if (IsSubjectToRateLimit(uploader) && (await GetRateLimitAsync(uploader, ip, cancellationToken)).IsLimited)
        {
            return UploadResult.Failure(UploadFields.RateLimit, "You've gone over the upload ratelimit.");
        }

        if (uploader is null)
        {
            if (Settings.RaidModeLimitUploads)
            {
                return UploadResult.Failure(UploadFields.RangeBanned, Settings.RaidModeUploadsMessage);
            }

            if (await IsRangeBannedAsync(ip, cancellationToken))
            {
                return UploadResult.Failure(UploadFields.RangeBanned, "Your IP is banned from uploading anonymously.");
            }
        }

        // Checks that used to run just before the commit (validate_torrent_post_upload).
        if (uploader is null && metadata.TotalSize < Settings.MinimumAnonymousTorrentSize)
        {
            errors.Add(UploadFields.TorrentFile, "Torrent too small for an anonymous uploader");
        }

        if (!UploadRules.AreFilenamesAllowed(metadata.FileTree.AllNames()))
        {
            errors.Add(UploadFields.TorrentFile, "Torrent has forbidden characters in filenames");
        }

        if (errors.HasErrors)
        {
            return UploadResult.Failure(errors);
        }

        var torrent = CreateTorrent(request, metadata, category, uploader, ip, replacesTorrentId);
        try
        {
            await StoreAsync(torrent, metadata, request, replacesTorrentId, cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Most likely lost a race against an identical upload (unique info hash).
            db.ChangeTracker.Clear();
            var existing = await db.Torrents.AsNoTracking()
                .Where(t => t.InfoHash == metadata.InfoHash)
                .Select(t => (int?)t.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (existing is null)
            {
                throw;
            }

            return UploadResult.Failure(UploadFields.TorrentFile, $"This torrent already exists (#{existing})");
        }

        LogUploaded(torrent.Id, torrent.InfoHashHex, uploader?.Username ?? "anonymous", ip);
        return UploadResult.Success(torrent);
    }

    private async Task<SubCategory?> ValidateCategoryAsync(string code, UploadErrors errors, CancellationToken cancellationToken)
    {
        var match = CategoryPattern().Match(code);
        if (!match.Success)
        {
            errors.Add(UploadFields.Category, "Please select a category");
            return null;
        }

        SubCategory? category = null;
        if (int.TryParse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture, out var mainId)
            && int.TryParse(match.Groups[2].ValueSpan, CultureInfo.InvariantCulture, out var subId))
        {
            category = await db.SubCategories.AsNoTracking()
                .FirstOrDefaultAsync(c => c.MainCategoryId == mainId && c.Id == subId, cancellationToken);
        }

        if (category is null)
        {
            errors.Add(UploadFields.Category, "Please select a proper category");
        }

        return category;
    }

    private async Task<(TorrentMetadata? Metadata, int? ReplacesTorrentId)> ValidateTorrentFileAsync(byte[]? data, UploadErrors errors, CancellationToken cancellationToken)
    {
        if (data is null)
        {
            return (null, null);
        }

        TorrentMetadata metadata;
        try
        {
            metadata = TorrentMetadata.Parse(data);
        }
        catch (TorrentValidationException e)
        {
            errors.Add(UploadFields.TorrentFile, e.Message);
            return (null, null);
        }

        if (metadata.CheckTrackerPolicy(links.MainAnnounceUrl, Settings.EnforceMainAnnounceUrl) is { } trackerError)
        {
            errors.Add(UploadFields.TorrentFile, trackerError);
            return (null, null);
        }

        var existing = await db.Torrents.AsNoTracking()
            .Where(t => t.InfoHash == metadata.InfoHash)
            .Select(t => new { t.Id, t.IsDeleted, t.IsBanned })
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is null)
        {
            return (metadata, null);
        }

        if (!existing.IsDeleted)
        {
            errors.Add(UploadFields.TorrentFile, $"This torrent already exists (#{existing.Id})");
            return (null, null);
        }

        if (existing.IsBanned)
        {
            errors.Add(UploadFields.TorrentFile, "This torrent is banned");
            return (null, null);
        }

        // Deleted but not banned: the new upload replaces it, keeping its id.
        return (metadata, existing.Id);
    }

    private async Task<bool> IsRangeBannedAsync(string? ip, CancellationToken cancellationToken)
    {
        if (ip is null || !IPAddress.TryParse(ip, out var address)
            || (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork && !address.IsIPv4MappedToIPv6))
        {
            // Range bans are IPv4 only.
            return false;
        }

        var value = RangeBan.ToInt64(address);
        return await db.RangeBans.AnyAsync(r => r.IsEnabled && (r.Mask & value) == r.MaskedAddress, cancellationToken);
    }

    private Torrent CreateTorrent(UploadRequest request, TorrentMetadata metadata, SubCategory category, User? uploader, string? ip, int? id)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var displayName = request.DisplayName?.Trim();
        return new Torrent
        {
            Id = id ?? 0,
            InfoHash = metadata.InfoHash,
            DisplayName = TextSanitizer.SanitizeString(string.IsNullOrEmpty(displayName) ? metadata.Name.Trim() : displayName),
            TorrentName = UploadRules.BaseName(request.FileName),
            Information = TextSanitizer.SanitizeString((request.Information ?? "").Trim()),
            Description = TextSanitizer.SanitizeString((request.Description ?? "").Trim()),
            Encoding = metadata.Encoding,
            FileSize = metadata.TotalSize,
            UploaderId = uploader?.Id,
            UploaderIp = ip,
            HasTorrent = true,
            IsAnonymous = uploader is null || request.IsAnonymous,
            IsHidden = request.IsHidden,
            IsRemake = request.IsRemake,
            IsComplete = request.IsComplete,
            IsTrusted = uploader?.IsTrusted == true && request.IsTrusted,
            IsCommentLocked = uploader?.IsModerator == true && request.IsCommentLocked,
            MainCategoryId = category.MainCategoryId,
            SubCategoryId = category.Id,
            CreatedAt = now,
            UpdatedAt = now,
            FileList = new TorrentFileList { Json = metadata.FileTree.ToJson() },
        };
    }

    /// <summary>Saves the torrent with its trackers in one transaction; the info dict and backup are written before the commit and removed if it fails.</summary>
    private async Task StoreAsync(Torrent torrent, TorrentMetadata metadata, UploadRequest request, int? replacesTorrentId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (replacesTorrentId is { } oldId)
        {
            // Comments, reports, trackers and the file list go with it (ON DELETE CASCADE).
            await db.Torrents.Where(t => t.Id == oldId).ExecuteDeleteAsync(cancellationToken);
        }

        await AttachTrackersAsync(torrent, metadata, cancellationToken);
        db.Torrents.Add(torrent);
        db.TrackerWhitelistEvents.Add(new TrackerWhitelistEvent { InfoHash = torrent.InfoHash, Action = TrackerWhitelistAction.Insert });
        await db.SaveChangesAsync(cancellationToken);

        var infoDictPath = paths.InfoDictPath(torrent.InfoHashHex);
        var infoDictExisted = File.Exists(infoDictPath);
        var backupName = UploadRules.SecureFilename(torrent.TorrentName);
        var backupPath = Path.Combine(paths.TorrentBackupDirectory, $"{torrent.Id}.{(backupName.Length > 0 ? backupName : "upload.torrent")}");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(infoDictPath)!);
            await File.WriteAllBytesAsync(infoDictPath, metadata.InfoDictionary, cancellationToken);
            Directory.CreateDirectory(paths.TorrentBackupDirectory);
            await File.WriteAllBytesAsync(backupPath, request.TorrentData!, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            TryDelete(backupPath);
            if (!infoDictExisted)
            {
                TryDelete(infoDictPath);
            }

            throw;
        }
    }

    /// <summary>Links the torrent's announce URLs and web seeds, creating <see cref="Tracker"/> rows as needed.</summary>
    private async Task AttachTrackersAsync(Torrent torrent, TorrentMetadata metadata, CancellationToken cancellationToken)
    {
        // Dead trackers are dropped from uploads entirely.
        var announces = metadata.Trackers.Where(t => !Settings.IsBlacklistedTracker(t)).ToList();
        var uris = announces.Concat(metadata.WebSeeds).Distinct(StringComparer.Ordinal).ToList();
        var known = new Dictionary<string, Tracker>(StringComparer.OrdinalIgnoreCase);
        if (uris.Count > 0)
        {
            foreach (var tracker in await db.Trackers.Where(t => uris.Contains(t.Uri)).ToListAsync(cancellationToken))
            {
                known[tracker.Uri] = tracker;
            }
        }

        var linked = new List<Tracker>();
        foreach (var uri in announces)
        {
            if (!known.TryGetValue(uri, out var tracker))
            {
                known[uri] = tracker = new Tracker { Uri = uri };
                db.Trackers.Add(tracker);
            }
            else if (tracker.IsWebSeed)
            {
                // An announce URL marked as web seed (user error or malice): better a bad announce than a hidden one.
                tracker.IsWebSeed = false;
            }

            if (!linked.Contains(tracker))
            {
                linked.Add(tracker);
            }
        }

        foreach (var uri in metadata.WebSeeds)
        {
            if (!known.TryGetValue(uri, out var webSeed))
            {
                known[uri] = webSeed = new Tracker { Uri = uri, IsWebSeed = true };
                db.Trackers.Add(webSeed);
            }

            // Never list a tracker as web seed.
            if (webSeed.IsWebSeed && !linked.Contains(webSeed))
            {
                linked.Add(webSeed);
            }
        }

        for (var order = 0; order < linked.Count; order++)
        {
            torrent.Trackers.Add(new TorrentTracker { Tracker = linked[order], Order = order });
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException e)
        {
            logger.LogWarning(e, "Could not clean up {Path}", path);
        }
        catch (UnauthorizedAccessException e)
        {
            logger.LogWarning(e, "Could not clean up {Path}", path);
        }
    }

    [GeneratedRegex(@"^(\d+)_(\d+)$")]
    private static partial Regex CategoryPattern();

    [LoggerMessage(LogLevel.Information, "Torrent #{Id} ({InfoHash}) uploaded by {Uploader} from {Ip}")]
    private partial void LogUploaded(int id, string infoHash, string uploader, string? ip);
}
