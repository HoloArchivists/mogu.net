using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Mogu.Core.Data;
using Mogu.Core.Entities;
using Mogu.Web.Infrastructure;
using Mogu.Web.Services;

namespace Mogu.Web.Pages.Admin;

public sealed class BansModel(MoguDbContext db, ModerationService moderation) : MoguPageModel
{
    public PagedList<Ban> Bans { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(int p = 1)
    {
        var page = await db.Bans
            .AsNoTracking()
            .Include(b => b.Admin)
            .Include(b => b.User)
            .OrderByDescending(b => b.CreatedAt)
            .ThenByDescending(b => b.Id)
            .ToPageAsync(p, HttpContext.RequestAborted);
        if (page is null)
        {
            return NotFound();
        }

        Bans = page;
        return Page();
    }

    /// <summary>The "Unban" button carries the ban id as its value (old <c>StringSubmitForm</c>).</summary>
    public async Task<IActionResult> OnPostAsync([FromForm(Name = "submit")] int? banId)
    {
        if (banId is not { } id || !await moderation.LiftBanAsync(id, CurrentUser.User!, HttpContext.RequestAborted))
        {
            return NotFound();
        }

        Flash(FlashLevel.Success, $"Unbanned ban #{id}");
        return Redirect(Request.Path + Request.QueryString);
    }
}
