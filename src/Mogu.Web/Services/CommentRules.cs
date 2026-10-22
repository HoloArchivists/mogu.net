using Mogu.Core.Entities;

namespace Mogu.Web.Services;

/// <summary>Who may post, edit and delete torrent comments; mirrors view.html and the old comment views.</summary>
public static class CommentRules
{
    /// <summary>Old <c>Comment.editing_limit_exceeded</c>; a zero limit disables it.</summary>
    public static bool EditingLimitExceeded(Comment comment, DateTime utcNow, TimeSpan limit)
    {
        ArgumentNullException.ThrowIfNull(comment);
        return limit > TimeSpan.Zero && utcNow - comment.CreatedAt >= limit;
    }

    /// <summary>Whether the comment form is shown (and comments may be posted or edited).</summary>
    public static bool CanComment(User? user, Torrent torrent)
    {
        ArgumentNullException.ThrowIfNull(torrent);
        return user is not null && (!torrent.IsCommentLocked || user.IsModerator);
    }

    /// <summary>Edit button: own comment, within the time limit, comments open (or moderator).</summary>
    public static bool ShowEditButton(User? user, Torrent torrent, Comment comment, bool limitExceeded)
    {
        ArgumentNullException.ThrowIfNull(comment);
        return user?.Id == comment.UserId && !limitExceeded && CanComment(user, torrent);
    }

    /// <summary>Delete button: administrators always, authors while comments are open and within the time limit.</summary>
    public static bool ShowDeleteButton(User? user, Torrent torrent, Comment comment, bool limitExceeded)
    {
        ArgumentNullException.ThrowIfNull(torrent);
        ArgumentNullException.ThrowIfNull(comment);
        return user is { IsAdministrator: true }
            || (user?.Id == comment.UserId && !torrent.IsCommentLocked && !limitExceeded);
    }

    /// <summary>Status code the edit endpoint refuses with (403), or null if allowed. The time limit is checked separately (400).</summary>
    public static int? EditDenied(User user, Torrent torrent, Comment comment)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(torrent);
        ArgumentNullException.ThrowIfNull(comment);
        if (comment.UserId != user.Id || (torrent.IsCommentLocked && !user.IsModerator))
        {
            return StatusCodes.Status403Forbidden;
        }

        return null;
    }

    /// <summary>Status code the delete endpoint refuses with, or null if allowed.</summary>
    public static int? DeleteDenied(User user, Torrent torrent, Comment comment, bool limitExceeded)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(torrent);
        ArgumentNullException.ThrowIfNull(comment);
        if (comment.UserId != user.Id && !user.IsAdministrator)
        {
            return StatusCodes.Status403Forbidden;
        }

        if (comment.TorrentId != torrent.Id)
        {
            return StatusCodes.Status400BadRequest;
        }

        if ((torrent.IsCommentLocked && !user.IsModerator) || (limitExceeded && !user.IsAdministrator))
        {
            return StatusCodes.Status403Forbidden;
        }

        return null;
    }
}
