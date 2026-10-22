using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mogu.Core.Data;
using Mogu.Core.Options;
using Mogu.Web.Infrastructure;
using Mogu.Web.Services;

namespace Mogu.Web.Pages.Torrents;

/// <summary>Editing and deleting torrent comments, same URLs and responses as the old views.</summary>
public sealed class CommentModel(
    MoguDbContext db,
    TimeProvider time,
    IOptionsSnapshot<MoguOptions> options,
    ModerationService moderation) : MoguPageModel
{
    [BindProperty(Name = "comment")]
    [Required(ErrorMessage = ViewTorrentModel.CommentRequiredMessage)]
    [StringLength(2048, MinimumLength = 3, ErrorMessage = ViewTorrentModel.CommentLengthMessage)]
    public string? CommentText { get; set; }

    public IActionResult OnGet() => NotFound();

    /// <summary>Returns <c>{"comment": text}</c>, or <c>{"error": message}</c> with a 400.</summary>
    public async Task<IActionResult> OnPostEditAsync(int id, int commentId)
    {
        var user = CurrentUser.User;
        if (user is null)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var torrent = await db.Torrents.FirstOrDefaultAsync(t => t.Id == id, HttpContext.RequestAborted);
        var comment = torrent is null
            ? null
            : await db.Comments.FirstOrDefaultAsync(c => c.Id == commentId && c.TorrentId == id, HttpContext.RequestAborted);
        if (torrent is null || comment is null)
        {
            return NotFound();
        }

        if (CommentRules.EditDenied(user, torrent, comment) is { } status)
        {
            return StatusCode(status);
        }

        var now = time.GetUtcNow().UtcDateTime;
        if (CommentRules.EditingLimitExceeded(comment, now, options.Value.EditingTimeLimit))
        {
            return BadRequest(new { error = "Editing time limit exceeded." });
        }

        if (!ModelState.IsValid)
        {
            return BadRequest(new { error = string.Join(" ", ModelState.ErrorsFor("comment")) });
        }

        comment.Text = CommentText!.Trim();
        comment.EditedAt = now;
        await db.SaveChangesAsync(HttpContext.RequestAborted);

        return new JsonResult(new { comment = comment.Text });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id, int commentId)
    {
        var user = CurrentUser.User;
        if (user is null)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var torrent = await db.Torrents.FirstOrDefaultAsync(t => t.Id == id, HttpContext.RequestAborted);
        var comment = torrent is null
            ? null
            : await db.Comments.FirstOrDefaultAsync(c => c.Id == commentId, HttpContext.RequestAborted);
        if (torrent is null || comment is null)
        {
            return NotFound();
        }

        var exceeded = CommentRules.EditingLimitExceeded(comment, time.GetUtcNow().UtcDateTime, options.Value.EditingTimeLimit);
        if (CommentRules.DeleteDenied(user, torrent, comment, exceeded) is { } status)
        {
            return StatusCode(status);
        }

        db.Comments.Remove(comment);
        await db.SaveChangesAsync(HttpContext.RequestAborted);
        torrent.CommentCount = await db.Comments.CountAsync(c => c.TorrentId == torrent.Id, HttpContext.RequestAborted);

        var url = ModerationService.TorrentUrl(torrent.Id);
        if (user.IsModerator)
        {
            moderation.AddAdminLog(user, $"Comment deleted on torrent [#{torrent.Id}]({url})");
        }

        await db.SaveChangesAsync(HttpContext.RequestAborted);

        Flash(FlashLevel.Success, "Comment successfully deleted.");
        return Redirect(url);
    }
}
