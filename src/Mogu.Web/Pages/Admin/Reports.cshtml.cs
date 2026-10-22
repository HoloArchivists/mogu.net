using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Mogu.Core.Data;
using Mogu.Core.Entities;
using Mogu.Web.Infrastructure;
using Mogu.Web.Services;

namespace Mogu.Web.Pages.Admin;

public sealed class ReportsModel(MoguDbContext db, ModerationService moderation) : MoguPageModel
{
    /// <summary>Choices of the old <c>ReportActionForm.action</c> select.</summary>
    public static IReadOnlyList<(string Value, string Label)> Actions { get; } =
        [("close", "Close"), ("hide", "Hide"), ("delete", "Delete")];

    public PagedList<Report> Reports { get; private set; } = null!;

    public Task<IActionResult> OnGetAsync(int p = 1) => RenderAsync(p);

    public async Task<IActionResult> OnPostAsync(
        [FromForm(Name = "action")] string? action,
        [FromForm(Name = "torrent")] int? torrent,
        [FromForm(Name = "report")] int? report,
        int p = 1)
    {
        ReportAction? parsed = action switch
        {
            "close" => ReportAction.Close,
            "hide" => ReportAction.Hide,
            "delete" => ReportAction.Delete,
            _ => null,
        };
        if (parsed is null)
        {
            ModelState.AddModelError("action", "Not a valid choice");
            return await RenderAsync(p);
        }

        if (torrent is not { } torrentId || report is not { } reportId
            || !await moderation.ReviewReportAsync(reportId, torrentId, parsed.Value, CurrentUser.User!, HttpContext.RequestAborted))
        {
            return NotFound();
        }

        Flash(FlashLevel.Success, $"Closed report #{reportId}");
        return Redirect("/admin/reports");
    }

    private async Task<IActionResult> RenderAsync(int p)
    {
        var page = await db.Reports
            .AsNoTracking()
            .Include(r => r.User)
            .Include(r => r.Torrent).ThenInclude(t => t.Uploader)
            .Where(r => r.Status == ReportStatus.InReview)
            .OrderBy(r => r.Id)
            .ToPageAsync(p, HttpContext.RequestAborted);
        if (page is null)
        {
            return NotFound();
        }

        Reports = page;
        return Page();
    }
}
