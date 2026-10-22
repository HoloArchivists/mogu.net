using Mogu.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Mogu.Core.Data;
using Mogu.Core.Entities;
using Mogu.Web.Infrastructure;

namespace Mogu.Web.Pages.Users;

/// <summary>
/// The "nuke" buttons of the user page, at their old URLs <c>/user/{name}/nuke/torrents</c> and
/// <c>/user/{name}/nuke/comments</c> (the route's handler segment selects the handler). Administrators only.
/// </summary>
public sealed class NukeModel(MoguDbContext db, ModerationService moderation) : MoguPageModel
{
    public IActionResult OnGet() => NotFound();

    /// <summary>Deletes and bans all of the user's torrents and queues their removal from the tracker whitelist.</summary>
    public async Task<IActionResult> OnPostTorrentsAsync(string username, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Username == username, cancellationToken);
        if (user is null)
        {
            return NotFound();
        }

        if (!CurrentUser.IsAdministrator)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var torrents = db.Torrents.Where(t => t.UploaderId == user.Id);
        var infoHashes = await torrents.Select(t => t.InfoHash).ToListAsync(cancellationToken);
        await torrents.ExecuteUpdateAsync(
            s => s.SetProperty(t => t.IsDeleted, true)
                .SetProperty(t => t.IsBanned, true)
                .SetProperty(t => t.SeedCount, 0)
                .SetProperty(t => t.LeechCount, 0),
            cancellationToken);
        foreach (var infoHash in infoHashes)
        {
            moderation.QueueTrackerWhitelist(infoHash, TrackerWhitelistAction.Remove);
        }
        if (infoHashes.Count > 0)
        {
            Log($"Nuked {infoHashes.Count} torrents of [{user.Username}]({UserUrl(user)})");
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        Flash(FlashLevel.Success, $"Torrents of {user.Username} have been nuked.");
        return Redirect(UserUrl(user));
    }

    /// <summary>Deletes all of the user's comments and recounts the comments of the affected torrents.</summary>
    public async Task<IActionResult> OnPostCommentsAsync(string username, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Username == username, cancellationToken);
        if (user is null)
        {
            return NotFound();
        }

        if (!CurrentUser.IsAdministrator)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var comments = db.Comments.Where(c => c.UserId == user.Id);
        var torrentIds = await comments.Select(c => c.TorrentId).Distinct().ToListAsync(cancellationToken);
        var deleted = await comments.ExecuteDeleteAsync(cancellationToken);
        await db.Torrents.Where(t => torrentIds.Contains(t.Id)).ExecuteUpdateAsync(
            s => s.SetProperty(t => t.CommentCount, t => db.Comments.Count(c => c.TorrentId == t.Id)),
            cancellationToken);
        if (deleted > 0)
        {
            Log($"Nuked {deleted} comments of [{user.Username}]({UserUrl(user)})");
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        Flash(FlashLevel.Success, $"Comments of {user.Username} have been nuked.");
        return Redirect(UserUrl(user));
    }

    private static string UserUrl(User user) => $"/user/{Uri.EscapeDataString(user.Username)}";

    private void Log(string message) => moderation.AddAdminLog(CurrentUser.User!, message);
}
