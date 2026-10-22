using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mogu.Core.Data;
using Mogu.Core.Entities;
using Mogu.Core.Options;
using Mogu.Web.Infrastructure;
using Mogu.Web.Services;

namespace Mogu.Web.Pages.Torrents;

/// <summary>A comment as shown on the torrent page, with its anchor number and the buttons the viewer gets.</summary>
public sealed record CommentItem(Comment Comment, int Number, bool ShowEdit, bool ShowDelete, bool ShowEditBox, long? EditableUntil);

public sealed class ViewTorrentModel(
    MoguDbContext db,
    TimeProvider time,
    IOptionsSnapshot<MoguOptions> options,
    TorrentFileListRenderer fileLists) : MoguPageModel
{
    public const string CommentLengthMessage = "Comment must be at least 3 characters long and 2048 at most.";
    public const string CommentRequiredMessage = "Comment must not be empty.";

    public Torrent Torrent { get; private set; } = null!;

    public IReadOnlyList<CommentItem> Comments { get; private set; } = [];

    public IHtmlContent FileList { get; private set; } = HtmlString.Empty;

    public bool CanEdit { get; private set; }

    public bool CanComment { get; private set; }

    public bool CanReport { get; private set; }

    [BindProperty(Name = "comment")]
    [Required(ErrorMessage = CommentRequiredMessage)]
    [StringLength(2048, MinimumLength = 3, ErrorMessage = CommentLengthMessage)]
    public string? CommentText { get; set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        if (!await LoadTorrentAsync(id))
        {
            return NotFound();
        }

        await LoadPageAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        if (!await LoadTorrentAsync(id))
        {
            return NotFound();
        }

        if (!CanComment)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        if (!ModelState.IsValid)
        {
            await LoadPageAsync();
            return Page();
        }

        db.Comments.Add(new Comment
        {
            TorrentId = Torrent.Id,
            UserId = CurrentUser.User!.Id,
            Text = CommentText!.Trim(),
            CreatedAt = time.GetUtcNow().UtcDateTime,
        });
        await db.SaveChangesAsync(HttpContext.RequestAborted);

        Torrent.CommentCount = await db.Comments.CountAsync(c => c.TorrentId == Torrent.Id, HttpContext.RequestAborted);
        await db.SaveChangesAsync(HttpContext.RequestAborted);

        Flash(FlashLevel.Success, "Comment successfully posted.");
        return Redirect($"{ModerationService.TorrentUrl(Torrent.Id)}#com-{Torrent.CommentCount}");
    }

    private async Task<bool> LoadTorrentAsync(int id)
    {
        var torrent = await db.Torrents
            .Include(t => t.Uploader)
            .Include(t => t.MainCategory)
            .Include(t => t.SubCategory)
            .FirstOrDefaultAsync(t => t.Id == id, HttpContext.RequestAborted);

        // Only moderators see deleted torrents.
        if (torrent is null || (torrent.IsDeleted && !CurrentUser.IsModerator))
        {
            return false;
        }

        Torrent = torrent;
        CanComment = CommentRules.CanComment(CurrentUser.User, torrent);
        return true;
    }

    private async Task LoadPageAsync()
    {
        var user = CurrentUser.User;
        var settings = options.Value;
        var now = time.GetUtcNow().UtcDateTime;

        CanEdit = user is not null && (user.Id == Torrent.UploaderId || user.IsModerator);
        CanReport = user is not null && user.Age(now) > settings.RateLimitAccountAge;

        var comments = await db.Comments
            .AsNoTracking()
            .Include(c => c.User)
            .Where(c => c.TorrentId == Torrent.Id)
            .OrderBy(c => c.Id)
            .ToListAsync(HttpContext.RequestAborted);

        var limit = settings.EditingTimeLimit;
        Comments = comments.Select((comment, index) =>
        {
            var exceeded = CommentRules.EditingLimitExceeded(comment, now, limit);
            return new CommentItem(
                comment,
                index + 1,
                CommentRules.ShowEditButton(user, Torrent, comment, exceeded),
                CommentRules.ShowDeleteButton(user, Torrent, comment, exceeded),
                user?.Id == comment.UserId && CanComment,
                limit > TimeSpan.Zero ? Format.UnixSeconds(comment.CreatedAt + limit) : null);
        }).ToList();

        FileList = await fileLists.RenderAsync(Torrent.Id, Torrent.InfoHashHex, HttpContext.RequestAborted);
    }
}
