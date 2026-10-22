using Microsoft.EntityFrameworkCore;
using Mogu.Core.Data;
using Mogu.Core.Entities;
using Mogu.Web.Infrastructure;

namespace Mogu.Web.Services;

/// <summary>Buttons of the edit page's "Danger Zone" (old <c>DeleteForm</c> and <c>BanForm</c>).</summary>
public enum TorrentModerationAction
{
    Delete,
    Ban,
    Undelete,
    Unban,
    BanUser,
    BanUserIp,
}

/// <param name="CanBanUploader">Whether the editor got the ban form (moderator with a higher level than the uploader).</param>
/// <param name="Reason">Ban reason, for <see cref="TorrentModerationAction.BanUser"/> and <see cref="TorrentModerationAction.BanUserIp"/>.</param>
/// <param name="SiteName">Prefix of the ban reason link, the old "Nyaa"/"Sukebei" flavor.</param>
/// <param name="AbsoluteTorrentUrl">External URL of the torrent page, linked from the ban reason.</param>
public sealed record TorrentModerationRequest(
    TorrentModerationAction Action,
    bool CanBanUploader,
    string? Reason,
    string SiteName,
    string AbsoluteTorrentUrl);

public sealed record TorrentModerationResult(IReadOnlyList<FlashMessage> Flashes, string RedirectUrl);

public enum ReportAction
{
    Close,
    Hide,
    Delete,
}

/// <summary>
/// Moderator actions shared by the torrent edit page and the admin pages: admin log entries, bans
/// and tracker whitelist events. Nothing is saved unless a method says so.
/// </summary>
public sealed class ModerationService(MoguDbContext db, TimeProvider time)
{
    private const string NonsenseMessage = "What the fuck are you doing?";

    public static string TorrentUrl(int torrentId) => $"/view/{torrentId}";

    public static string UserUrl(string username) => "/user/" + Uri.EscapeDataString(username);

    /// <summary>Queues an admin log entry (markdown, rendered client side).</summary>
    public void AddAdminLog(User admin, string message) =>
        db.AdminLog.Add(new AdminLogEntry { AdminId = admin.Id, Message = message, CreatedAt = Now });

    /// <summary>Queues an info hash change for the external tracker whitelist (old <c>TrackerApi</c>).</summary>
    public void QueueTrackerWhitelist(byte[] infoHash, TrackerWhitelistAction action) =>
        db.TrackerWhitelistEvents.Add(new TrackerWhitelistEvent { InfoHash = infoHash, Action = action });

    public Task<bool> IsIpBannedAsync(string ip, CancellationToken cancellationToken = default) =>
        db.Bans.AnyAsync(b => b.UserIp == ip, cancellationToken);

    /// <summary>
    /// The old <c>ipbanned</c> flag: both the upload IP and the uploader's last IP are banned. A missing
    /// IP (or uploader) counts as banned since there is nothing left to ban.
    /// </summary>
    public async Task<bool> IsUploaderIpBannedAsync(Torrent torrent, CancellationToken cancellationToken = default)
    {
        var (torrentIpBanned, userIpBanned) = await UploaderIpBansAsync(torrent, cancellationToken);
        return torrentIpBanned && userIpBanned;
    }

    /// <summary>
    /// Port of the old <c>_delete_torrent</c>, including its quirks. <paramref name="torrent"/> must have
    /// <see cref="Torrent.Uploader"/> loaded. Saves changes.
    /// </summary>
    public async Task<TorrentModerationResult> ModerateTorrentAsync(
        Torrent torrent, User editor, TorrentModerationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(torrent);
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(request);

        var flashes = new List<FlashMessage>();
        var uploader = torrent.Uploader;
        var viewUrl = TorrentUrl(torrent.Id);
        var editUrl = viewUrl + "/edit";
        var url = "/";

        var banUploader = request.CanBanUploader
            && request.Action is TorrentModerationAction.BanUser or TorrentModerationAction.BanUserIp;
        var banTorrent = request.Action == TorrentModerationAction.Ban || banUploader;

        string? action = null;
        if (request.Action == TorrentModerationAction.Delete && !torrent.IsDeleted)
        {
            action = "deleted";
            torrent.IsDeleted = true;
        }
        else if (banTorrent && !torrent.IsBanned && editor.IsModerator)
        {
            action = "banned";
            torrent.IsBanned = true;
            if (!torrent.IsDeleted)
            {
                torrent.IsDeleted = true;
                action = "deleted and banned";
            }

            QueueTrackerWhitelist(torrent.InfoHash, TrackerWhitelistAction.Remove);
            torrent.SeedCount = 0;
            torrent.LeechCount = 0;
        }
        else if (request.Action == TorrentModerationAction.Undelete && torrent.IsDeleted)
        {
            action = "undeleted";
            torrent.IsDeleted = false;
            if (torrent.IsBanned)
            {
                action = "undeleted and unbanned";
                torrent.IsBanned = false;
                QueueTrackerWhitelist(torrent.InfoHash, TrackerWhitelistAction.Insert);
            }
        }
        else if (request.Action == TorrentModerationAction.Unban && torrent.IsBanned)
        {
            action = "unbanned";
            torrent.IsBanned = false;
            QueueTrackerWhitelist(torrent.InfoHash, TrackerWhitelistAction.Insert);
        }

        if (action is null && !banTorrent)
        {
            flashes.Add(new FlashMessage(FlashLevel.Danger, NonsenseMessage));
            return new TorrentModerationResult(flashes, editUrl);
        }

        if (action is not null && editor.IsModerator)
        {
            url = viewUrl;
            if (editor.Id != uploader?.Id)
            {
                AddAdminLog(editor, $"Torrent [#{torrent.Id}]({viewUrl}) has been {action}");
            }
        }

        if (action is not null)
        {
            torrent.UpdatedAt = Now;
            await db.SaveChangesAsync(cancellationToken);
            flashes.Add(new FlashMessage(FlashLevel.Success, $"Torrent has been successfully {action}."));
        }

        if (!banUploader)
        {
            return new TorrentModerationResult(flashes, url);
        }

        var banIp = request.Action == TorrentModerationAction.BanUserIp;
        var (torrentIpBanned, userIpBanned) = await UploaderIpBansAsync(torrent, cancellationToken);
        if ((!banIp && uploader is not { IsBanned: false }) || (banIp && torrentIpBanned && userIpBanned))
        {
            flashes.Add(new FlashMessage(FlashLevel.Danger, NonsenseMessage));
            return new TorrentModerationResult(flashes, editUrl);
        }

        var reason = $"[{request.SiteName}#{torrent.Id}]({request.AbsoluteTorrentUrl}) {request.Reason}";
        var ban1 = new Ban { AdminId = editor.Id, Reason = reason, CreatedAt = Now };
        Ban? ban2 = null;
        db.Bans.Add(ban1);

        if (uploader is not null)
        {
            uploader.Status = UserStatus.Banned;
            ban1.UserId = uploader.Id;
        }

        if (banIp)
        {
            if (!userIpBanned)
            {
                ban1.UserIp = uploader!.LastLoginIp;
                if (!torrentIpBanned && ban1.UserIp != torrent.UploaderIp)
                {
                    ban2 = new Ban { AdminId = editor.Id, UserId = uploader.Id, UserIp = torrent.UploaderIp, Reason = reason, CreatedAt = Now };
                    db.Bans.Add(ban2);
                }
            }
            else
            {
                ban1.UserIp = torrent.UploaderIp;
            }
        }

        var uploaderText = uploader is null ? "Anonymous" : $"[{uploader.Username}]({UserUrl(uploader.Username)})";
        if (ban1.UserIp is not null)
        {
            uploaderText += $" IP({ban1.UserIp})";
        }

        if (ban2?.UserIp is not null)
        {
            uploaderText += $" IP({ban2.UserIp})";
        }

        AddAdminLog(editor, $"Uploader {uploaderText} of torrent [#{torrent.Id}]({viewUrl}) has been banned.");
        await db.SaveChangesAsync(cancellationToken);

        flashes.Add(new FlashMessage(FlashLevel.Success, "Uploader has been successfully banned."));
        return new TorrentModerationResult(flashes, url);
    }

    /// <summary>Deletes a ban and reactivates its user. Returns false if there is no such ban. Saves changes.</summary>
    public async Task<bool> LiftBanAsync(int banId, User admin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admin);

        var ban = await db.Bans.Include(b => b.User).FirstOrDefaultAsync(b => b.Id == banId, cancellationToken);
        if (ban is null)
        {
            return false;
        }

        var log = $"Unbanned ban #{ban.Id}";
        if (ban.User is not null)
        {
            log += " " + ban.User.Username;
            ban.User.Status = UserStatus.Active;
        }

        if (ban.UserIp is not null)
        {
            log += $" IP({ban.UserIp})";
        }

        AddAdminLog(admin, log);
        db.Bans.Remove(ban);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Reviews an open report, then drops the other open reports for the same torrent (old
    /// <c>Report.remove_reviewed</c>). Returns false if the report or torrent doesn't exist or the report
    /// was already reviewed. Saves changes.
    /// </summary>
    public async Task<bool> ReviewReportAsync(int reportId, int torrentId, ReportAction action, User admin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admin);

        var report = await db.Reports
            .Include(r => r.Torrent)
            .Include(r => r.User)
            .FirstOrDefaultAsync(r => r.Id == reportId && r.TorrentId == torrentId, cancellationToken);
        if (report is null || report.Status != ReportStatus.InReview)
        {
            return false;
        }

        var torrent = report.Torrent;
        string verb;
        switch (action)
        {
            case ReportAction.Delete:
                torrent.IsDeleted = true;
                report.Status = ReportStatus.Valid;
                verb = "Deleted";
                break;
            case ReportAction.Hide:
                torrent.IsHidden = true;
                report.Status = ReportStatus.Valid;
                verb = "Hid";
                break;
            default:
                report.Status = ReportStatus.Invalid;
                verb = "Closed";
                break;
        }

        var reporter = report.User is null ? "deleted user" : $"[{report.User.Username}]({UserUrl(report.User.Username)})";
        AddAdminLog(admin, $"Report #{report.Id}: {verb} [#{torrent.Id}]({TorrentUrl(torrent.Id)}), reported by {reporter}");

        var otherOpenReports = await db.Reports
            .Where(r => r.TorrentId == torrent.Id && r.Status == ReportStatus.InReview && r.Id != report.Id)
            .ToListAsync(cancellationToken);
        db.Reports.RemoveRange(otherOpenReports);

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private async Task<(bool TorrentIpBanned, bool UserIpBanned)> UploaderIpBansAsync(Torrent torrent, CancellationToken cancellationToken)
    {
        var torrentIpBanned = torrent.UploaderIp is not { } uploadIp || await IsIpBannedAsync(uploadIp, cancellationToken);
        var userIpBanned = torrent.Uploader?.LastLoginIp is not { } lastIp || await IsIpBannedAsync(lastIp, cancellationToken);
        return (torrentIpBanned, userIpBanned);
    }
}
