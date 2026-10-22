using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Mogu.Core.Data;
using Mogu.Core.Entities;
using Mogu.Web.Infrastructure;

namespace Mogu.Web.Pages.Users;

/// <summary>All comments of a user, newest first; moderators only (old <c>users.view_user_comments</c>).</summary>
public sealed class UserCommentsModel(MoguDbContext db) : MoguPageModel
{
    public const int CommentsPerPage = 100;

    public User Profile { get; private set; } = null!;

    public PagedList<UserComment> Comments { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(string username, string? p, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Username == username, cancellationToken);
        if (user is null)
        {
            return NotFound();
        }

        if (!CurrentUser.IsModerator)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var page = int.TryParse(p, out var parsed) ? Math.Max(1, parsed) : 1;
        if ((long)(page - 1) * CommentsPerPage > int.MaxValue)
        {
            return NotFound();
        }

        Profile = user;
        var query = db.Comments.Where(c => c.UserId == user.Id);
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id)
            .Skip((page - 1) * CommentsPerPage)
            .Take(CommentsPerPage)
            .Select(c => new UserComment(c.Id, c.TorrentId, c.Torrent.DisplayName, c.Text, c.CreatedAt, c.EditedAt))
            .ToListAsync(cancellationToken);
        if (items.Count == 0 && page != 1)
        {
            return NotFound();
        }

        Comments = new PagedList<UserComment>(items, page, CommentsPerPage, total);
        return Page();
    }
}

public sealed record UserComment(int Id, int TorrentId, string TorrentName, string Text, DateTime CreatedAt, DateTime? EditedAt);
