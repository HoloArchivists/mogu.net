using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Mogu.Core.Data;
using Mogu.Core.Entities;
using Mogu.Web.Infrastructure;

namespace Mogu.Web.Pages.Admin;

public sealed partial class LogModel(MoguDbContext db) : MoguPageModel
{
    public PagedList<AdminLogEntry> Entries { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(int p = 1)
    {
        var page = await db.AdminLog
            .AsNoTracking()
            .Include(l => l.Admin)
            .OrderByDescending(l => l.CreatedAt)
            .ThenByDescending(l => l.Id)
            .ToPageAsync(p, HttpContext.RequestAborted);
        if (page is null)
        {
            return NotFound();
        }

        Entries = page;
        return Page();
    }

    /// <summary>Only administrators see the IP addresses in ban messages.</summary>
    public string DisplayMessage(AdminLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return CurrentUser.IsAdministrator ? entry.Message : IpRegex().Replace(entry.Message, "IP(hidden)");
    }

    [GeneratedRegex(@"IP\(.*?\)")]
    private static partial Regex IpRegex();
}
