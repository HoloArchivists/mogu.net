using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Mogu.Core.Data;
using Mogu.Core.Entities;
using Mogu.Web.Infrastructure;
using Mogu.Web.Services;

namespace Mogu.Web.Pages.Users;

/// <summary>
/// A user's torrents plus the moderator panel (old <c>users.view_user</c>, <c>nuke_user_torrents</c>
/// and <c>nuke_user_comments</c>). Each panel form posts to its own handler.
/// </summary>
public sealed class UserPageModel(MoguDbContext db, TorrentSearch search, TimeProvider time) : MoguPageModel
{
    public const string NonsenseMessage = "What the fuck are you doing?";

    private static readonly Dictionary<string, UserLevel> ClassLevels = new(StringComparer.Ordinal)
    {
        ["regular"] = UserLevel.Regular,
        ["trusted"] = UserLevel.Trusted,
        ["moderator"] = UserLevel.Moderator,
    };

    public User Profile { get; private set; } = null!;

    public SearchResultsModel Results { get; private set; } = null!;

    /// <summary>Number of torrents in the listing before the page limit cap.</summary>
    public int ActualCount { get; private set; }

    /// <summary>The viewer is a moderator of a higher level than this user and may use the panel forms.</summary>
    public bool CanManage { get; private set; }

    public IReadOnlyList<(string Value, string Label)> UserClassChoices { get; private set; } = [];

    /// <summary>Bans on this user's account or last login IP.</summary>
    public IReadOnlyList<Ban> Bans { get; private set; } = [];

    public bool IsIpBanned { get; private set; }

    [BindProperty(Name = "user_class")]
    public string? UserClass { get; set; }

    [BindProperty(Name = "activate_user")]
    public string? ActivateUser { get; set; }

    [BindProperty(Name = "reason")]
    [StringLength(1024, ErrorMessage = "Reason must be at most {1} characters long.")]
    public string? Reason { get; set; }

    [BindProperty(Name = "ban_user")]
    public string? BanUser { get; set; }

    [BindProperty(Name = "ban_userip")]
    public string? BanUserIp { get; set; }

    [BindProperty(Name = "unban")]
    public string? Unban { get; set; }

    private string ProfileUrl => $"/user/{Uri.EscapeDataString(Profile.Username)}";

    public async Task<IActionResult> OnGetAsync(string username, CancellationToken cancellationToken)
    {
        if (!await LoadUserAsync(username, cancellationToken))
        {
            return NotFound();
        }

        if (CanManage)
        {
            UserClass = DefaultUserClass(Profile);
        }

        return await RenderAsync(username, cancellationToken);
    }

    /// <remarks>
    /// Levels are re-synced from the user's Discord roles on every login, so a change made here
    /// only lasts until the user logs in again; permanent changes belong in Discord.
    /// </remarks>
    public async Task<IActionResult> OnPostUserClassAsync(string username, CancellationToken cancellationToken)
    {
        if (!await LoadUserAsync(username, cancellationToken))
        {
            return NotFound();
        }

        if (!CanManage)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        if (string.IsNullOrEmpty(UserClass))
        {
            ModelState.AddModelError("user_class", "Please select a proper user class");
        }
        else if (!UserClassChoices.Any(c => c.Value == UserClass))
        {
            ModelState.AddModelError("user_class", "Not a valid choice");
        }

        if (!ModelState.IsValid)
        {
            return await RenderAsync(username, cancellationToken);
        }

        var admin = CurrentUser.User!;
        var now = time.GetUtcNow().UtcDateTime;
        var level = ClassLevels[UserClass!];
        if (level != Profile.Level)
        {
            Profile.Level = level;
            Log(admin, $"[{Profile.Username}]({ProfileUrl}) changed to {UserClass} user", now);
        }

        if (ActivateUser is not null && !Profile.IsBanned && Profile.Status != UserStatus.Active)
        {
            Profile.Status = UserStatus.Active;
            Log(admin, $"[{Profile.Username}]({ProfileUrl}) was manually activated", now);
            Flash(FlashLevel.Success, $"{Profile.Username} was manually activated");
        }

        await db.SaveChangesAsync(cancellationToken);
        return Redirect(ProfileUrl);
    }

    public async Task<IActionResult> OnPostBanAsync(string username, CancellationToken cancellationToken)
    {
        if (!await LoadUserAsync(username, cancellationToken))
        {
            return NotFound();
        }

        if (!CanManage)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var banUser = BanUser is not null;
        var banIp = BanUserIp is not null;
        var unban = Unban is not null;
        if (!banUser && !banIp && !unban)
        {
            return BadRequest();
        }

        if ((banUser || banIp) && string.IsNullOrWhiteSpace(Reason))
        {
            ModelState.AddModelError("reason", "This field is required.");
        }

        if (!ModelState.IsValid)
        {
            UserClass = DefaultUserClass(Profile);
            return await RenderAsync(username, cancellationToken);
        }

        if ((banUser && Profile.IsBanned) || (banIp && IsIpBanned) || (unban && !Profile.IsBanned && Bans.Count == 0))
        {
            Flash(FlashLevel.Danger, NonsenseMessage);
            return Redirect(ProfileUrl);
        }

        if (banIp && Profile.LastLoginIp is null)
        {
            Flash(FlashLevel.Danger, "This user has no known IP address to ban.");
            return Redirect(ProfileUrl);
        }

        var admin = CurrentUser.User!;
        var now = time.GetUtcNow().UtcDateTime;
        var userText = $"[{Profile.Username}]({ProfileUrl})";
        string action;
        if (unban)
        {
            action = "unbanned";
            Profile.Status = UserStatus.Active;
            foreach (var ban in Bans)
            {
                if (ban.UserIp is not null)
                {
                    userText += $" IP({ban.UserIp})";
                }

                db.Bans.Remove(ban);
            }
        }
        else
        {
            action = "banned";
            Profile.Status = UserStatus.Banned;
            var ban = new Ban { AdminId = admin.Id, UserId = Profile.Id, Reason = Reason!, CreatedAt = now };
            if (banIp)
            {
                ban.UserIp = Profile.LastLoginIp;
                userText += $" IP({ban.UserIp})";
            }

            db.Bans.Add(ban);
        }

        Log(admin, $"User {userText} has been {action}.", now);
        await db.SaveChangesAsync(cancellationToken);

        Flash(FlashLevel.Success, $"User has been successfully {action}.");
        return Redirect(ProfileUrl);
    }

    /// <summary>Old <c>_create_user_class_choices</c>: moderators can grant trusted, administrators moderator.</summary>
    public static IReadOnlyList<(string Value, string Label)> CreateUserClassChoices(User? viewer)
    {
        var choices = new List<(string, string)> { ("regular", "Regular") };
        if (viewer?.IsModerator == true)
        {
            choices.Add(("trusted", "Trusted"));
        }

        if (viewer?.IsAdministrator == true)
        {
            choices.Add(("moderator", "Moderator"));
        }

        return choices;
    }

    public static string DefaultUserClass(User user) =>
        user.IsModerator ? "moderator" : user.IsTrusted ? "trusted" : user.IsBanned ? "banned" : "regular";

    private async Task<bool> LoadUserAsync(string username, CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username, cancellationToken);
        if (user is null)
        {
            return false;
        }

        Profile = user;
        var viewer = CurrentUser.User;
        CanManage = viewer is not null && viewer.IsModerator && viewer.Level > user.Level;
        if (CanManage)
        {
            UserClassChoices = CreateUserClassChoices(viewer);
            var ip = user.LastLoginIp;
            Bans = await db.Bans.Include(b => b.Admin)
                .Where(b => b.UserId == user.Id || (ip != null && b.UserIp == ip))
                .OrderBy(b => b.Id)
                .ToListAsync(cancellationToken);
            IsIpBanned = ip is not null && Bans.Any(b => b.UserIp == ip);
        }

        return true;
    }

    private async Task<IActionResult> RenderAsync(string username, CancellationToken cancellationToken)
    {
        var listing = ListingQuery.FromQuery(Request.Query);
        var result = await search.SearchAsync(listing.ToRequest(Profile.Id, CurrentUser.User, rss: false), cancellationToken);
        if (!result.Succeeded)
        {
            return result.ToErrorResult();
        }

        Search = listing.ToSearchState();
        UserPageName = Profile.Username;
        RssFilter = listing.ToRssFilter(username);
        ActualCount = result.ActualCount;
        Results = new SearchResultsModel(result.Torrents, Search);
        return Page();
    }

    private void Log(User admin, string message, DateTime now) =>
        db.AdminLog.Add(new AdminLogEntry { AdminId = admin.Id, Message = message, CreatedAt = now });
}
