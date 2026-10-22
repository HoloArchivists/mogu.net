using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mogu.Core.Data;
using Mogu.Core.Entities;
using Mogu.Core.Options;
using Mogu.Core.Text;
using Mogu.Web.Infrastructure;
using Mogu.Web.Services;

namespace Mogu.Web.Pages.Torrents;

/// <summary>The old <c>EditForm</c>; field names match the original form.</summary>
public sealed class EditTorrentForm
{
    private const string DisplayNameMessage = "Torrent display name must be at least 3 characters long and 255 at most.";

    [ModelBinder(Name = "display_name")]
    [Required(ErrorMessage = DisplayNameMessage)]
    [StringLength(255, MinimumLength = 3, ErrorMessage = DisplayNameMessage)]
    public string? DisplayName { get; set; }

    [ModelBinder(Name = "category")]
    public string? Category { get; set; }

    [ModelBinder(Name = "information")]
    [StringLength(255, ErrorMessage = "Information must be at most 255 characters long.")]
    public string? Information { get; set; }

    [ModelBinder(Name = "description")]
    [StringLength(10 * 1024, ErrorMessage = "Description must be at most 10240 characters long.")]
    public string? Description { get; set; }

    [ModelBinder(Name = "is_hidden")]
    public bool IsHidden { get; set; }

    [ModelBinder(Name = "is_remake")]
    public bool IsRemake { get; set; }

    [ModelBinder(Name = "is_anonymous")]
    public bool IsAnonymous { get; set; }

    [ModelBinder(Name = "is_complete")]
    public bool IsComplete { get; set; }

    [ModelBinder(Name = "is_trusted")]
    public bool IsTrusted { get; set; }

    [ModelBinder(Name = "is_comment_locked")]
    public bool IsCommentLocked { get; set; }

    public static EditTorrentForm From(Torrent torrent) => new()
    {
        DisplayName = torrent.DisplayName,
        Category = torrent.CategoryCode,
        Information = torrent.Information,
        Description = torrent.Description,
        IsHidden = torrent.IsHidden,
        IsRemake = torrent.IsRemake,
        IsAnonymous = torrent.IsAnonymous,
        IsComplete = torrent.IsComplete,
        IsTrusted = torrent.IsTrusted,
        IsCommentLocked = torrent.IsCommentLocked,
    };
}

public sealed partial class EditTorrentModel(
    MoguDbContext db,
    TimeProvider time,
    IOptionsSnapshot<MoguOptions> options,
    ModerationService moderation) : MoguPageModel
{
    public const string BanReasonKey = "reason";

    public Torrent Torrent { get; private set; } = null!;

    public EditTorrentForm Input { get; private set; } = new();

    /// <summary>Whether the ban form is shown: the editor is a moderator above the uploader's level.</summary>
    public bool CanBanUploader { get; private set; }

    /// <summary>Old <c>ipbanned</c>; only computed for moderators.</summary>
    public bool IpBanned { get; private set; }

    public string? BanReason { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        if (await LoadAsync(id) is { } denied)
        {
            return denied;
        }

        Input = EditTorrentForm.From(Torrent);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id, EditTorrentForm input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (await LoadAsync(id) is { } denied)
        {
            return denied;
        }

        Input = input;
        var category = await ParseCategoryAsync(input.Category);
        if (!ModelState.IsValid || category is null)
        {
            return Page();
        }

        var editor = CurrentUser.User!;
        Torrent.MainCategoryId = category.MainCategoryId;
        Torrent.SubCategoryId = category.Id;
        Torrent.DisplayName = TextSanitizer.SanitizeString((input.DisplayName ?? "").Trim());
        Torrent.Information = TextSanitizer.SanitizeString((input.Information ?? "").Trim());
        Torrent.Description = TextSanitizer.SanitizeString((input.Description ?? "").Trim());
        Torrent.IsHidden = input.IsHidden;
        Torrent.IsRemake = input.IsRemake;
        Torrent.IsComplete = input.IsComplete;

        // The anonymous toggle is only shown for torrents with an uploader.
        if (Torrent.UploaderId is not null)
        {
            Torrent.IsAnonymous = input.IsAnonymous;
        }

        if (editor.IsTrusted)
        {
            Torrent.IsTrusted = input.IsTrusted;
        }

        if (editor.IsModerator && Torrent.IsCommentLocked != input.IsCommentLocked)
        {
            Torrent.IsCommentLocked = input.IsCommentLocked;
            var state = Torrent.IsCommentLocked ? "comments locked" : "comments unlocked";
            moderation.AddAdminLog(editor, $"Torrent [#{Torrent.Id}]({ModerationService.TorrentUrl(Torrent.Id)}) marked as {state}");
        }

        Torrent.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(HttpContext.RequestAborted);

        Flash(FlashLevel.Success, "Torrent has been successfully edited! Changes might take a few minutes to show up.");
        return Redirect(ModerationService.TorrentUrl(Torrent.Id));
    }

    public Task<IActionResult> OnPostDeleteAsync(int id, [FromForm(Name = BanReasonKey)] string? reason) =>
        ModerateAsync(id, TorrentModerationAction.Delete, reason);

    public Task<IActionResult> OnPostBanAsync(int id, [FromForm(Name = BanReasonKey)] string? reason) =>
        ModerateAsync(id, TorrentModerationAction.Ban, reason);

    public Task<IActionResult> OnPostUndeleteAsync(int id, [FromForm(Name = BanReasonKey)] string? reason) =>
        ModerateAsync(id, TorrentModerationAction.Undelete, reason);

    public Task<IActionResult> OnPostUnbanAsync(int id, [FromForm(Name = BanReasonKey)] string? reason) =>
        ModerateAsync(id, TorrentModerationAction.Unban, reason);

    public Task<IActionResult> OnPostBanUserAsync(int id, [FromForm(Name = BanReasonKey)] string? reason) =>
        ModerateAsync(id, TorrentModerationAction.BanUser, reason);

    public Task<IActionResult> OnPostBanUserIpAsync(int id, [FromForm(Name = BanReasonKey)] string? reason) =>
        ModerateAsync(id, TorrentModerationAction.BanUserIp, reason);

    private async Task<IActionResult> ModerateAsync(int id, TorrentModerationAction action, string? reason)
    {
        if (await LoadAsync(id) is { } denied)
        {
            return denied;
        }

        // The old BanForm validated whenever it was present, whichever button was pressed.
        if (CanBanUploader)
        {
            if (action is TorrentModerationAction.BanUser or TorrentModerationAction.BanUserIp && string.IsNullOrWhiteSpace(reason))
            {
                ModelState.AddModelError(BanReasonKey, "This field is required.");
            }

            if (reason?.Length > 1024)
            {
                ModelState.AddModelError(BanReasonKey, "Reason must be at most 1024 characters long.");
            }

            if (ModelState.ErrorCount > 0)
            {
                Input = EditTorrentForm.From(Torrent);
                BanReason = reason;
                return Page();
            }
        }

        var request = new TorrentModerationRequest(
            action,
            CanBanUploader,
            reason,
            options.Value.SiteName,
            Request.AbsoluteUrl(ModerationService.TorrentUrl(Torrent.Id)));
        var result = await moderation.ModerateTorrentAsync(Torrent, CurrentUser.User!, request, HttpContext.RequestAborted);
        foreach (var flash in result.Flashes)
        {
            Flash(flash.Level, flash.Text);
        }

        return Redirect(result.RedirectUrl);
    }

    /// <summary>Loads the torrent and checks access; returns the refusal, if any.</summary>
    private async Task<IActionResult?> LoadAsync(int id)
    {
        var torrent = await db.Torrents
            .Include(t => t.Uploader)
            .FirstOrDefaultAsync(t => t.Id == id, HttpContext.RequestAborted);
        var editor = CurrentUser.User;

        // Only moderators may edit deleted torrents, only owners and moderators may edit at all.
        if (torrent is null || (torrent.IsDeleted && !CurrentUser.IsModerator))
        {
            return NotFound();
        }

        if (editor is null || (editor.Id != torrent.UploaderId && !editor.IsModerator))
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        Torrent = torrent;
        CanBanUploader = editor.IsModerator && (torrent.Uploader is null || editor.Level > torrent.Uploader.Level);
        IpBanned = editor.IsModerator && await moderation.IsUploaderIpBannedAsync(torrent, HttpContext.RequestAborted);
        return null;
    }

    private async Task<SubCategory?> ParseCategoryAsync(string? code)
    {
        var match = CategoryRegex().Match(code ?? "");
        if (!match.Success)
        {
            ModelState.AddModelError("category", "Please select a category");
            return null;
        }

        var mainId = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var subId = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var category = await db.SubCategories.AsNoTracking()
            .FirstOrDefaultAsync(c => c.MainCategoryId == mainId && c.Id == subId, HttpContext.RequestAborted);
        if (category is null)
        {
            ModelState.AddModelError("category", "Please select a proper category");
        }

        return category;
    }

    [GeneratedRegex(@"^(\d{1,9})_(\d{1,9})\z")]
    private static partial Regex CategoryRegex();
}
