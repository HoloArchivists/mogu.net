using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mogu.Core.Data;
using Mogu.Core.Entities;
using Mogu.Core.Options;
using Mogu.Web.Infrastructure;
using Mogu.Web.Services;

namespace Mogu.Web.Pages.Torrents;

/// <summary>Old <c>submit_report</c>: accounts older than <see cref="MoguOptions.RateLimitAccountAge"/> may report torrents.</summary>
public sealed class ReportModel(MoguDbContext db, TimeProvider time, IOptionsSnapshot<MoguOptions> options) : MoguPageModel
{
    [BindProperty(Name = "reason")]
    [Required(ErrorMessage = "You must provide a valid report reason.")]
    [StringLength(255, MinimumLength = 3, ErrorMessage = "Report reason must be at least 3 characters long and 255 at most.")]
    public string? Reason { get; set; }

    public IActionResult OnGet() => NotFound();

    public async Task<IActionResult> OnPostAsync(int id)
    {
        var user = CurrentUser.User;
        var now = time.GetUtcNow().UtcDateTime;
        if (user is null || user.Age(now) < options.Value.RateLimitAccountAge)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var torrent = await db.Torrents.FirstOrDefaultAsync(t => t.Id == id, HttpContext.RequestAborted);
        if (torrent is null)
        {
            return NotFound();
        }

        if (torrent.IsBanned)
        {
            Flash(FlashLevel.Danger, "The torrent you've tried to report is already banned.");
            return NotFound();
        }

        if (ModelState.IsValid)
        {
            db.Reports.Add(new Report { TorrentId = torrent.Id, UserId = user.Id, Reason = Reason!, CreatedAt = now });
            await db.SaveChangesAsync(HttpContext.RequestAborted);
            Flash(FlashLevel.Success, "Successfully reported torrent!");
        }
        else if (string.IsNullOrEmpty(Reason))
        {
            Flash(FlashLevel.Danger, "Please give a report reason!");
        }

        return Redirect(ModerationService.TorrentUrl(torrent.Id));
    }
}
