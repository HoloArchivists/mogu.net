using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mogu.Core.Data;
using Mogu.Core.Options;
using Mogu.Web.Infrastructure;
using Mogu.Web.Torrents;

namespace Mogu.Web.Pages.Torrents;

/// <summary>A category option; main categories are shown but can't be selected (old <c>DisabledSelectField</c>).</summary>
public sealed record CategoryOption(string Code, string Label, bool IsDisabled);

public sealed class UploadModel(TorrentUploadService uploads, MoguDbContext db, IOptionsSnapshot<MoguOptions> options) : MoguPageModel
{
    // Bound without prefix so fields keep their old names (torrent_file, display_name, ...).
    [BindProperty(Name = "")]
    public UploadInput Input { get; set; } = new();

    public IReadOnlyList<CategoryOption> Categories { get; private set; } = [];

    public bool ShowRateLimit { get; private set; }

    public UploadRateLimit? RateLimit { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        // Trusted users mark their uploads trusted unless they opt out.
        Input.IsTrusted = true;
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);

        var request = await Input.ToRequestAsync(cancellationToken);
        var result = await uploads.UploadAsync(request, CurrentUser.User, HttpContext.GetClientIp(), ModelState.IsValid, cancellationToken);
        if (result.Succeeded)
        {
            return Redirect($"/view/{result.Torrent.Id}");
        }

        foreach (var (field, messages) in result.Errors)
        {
            foreach (var message in messages)
            {
                ModelState.AddModelError(field, message);
            }
        }

        // A rate limit error always comes with the rate limit alert.
        ShowRateLimit |= ModelState.ErrorsFor(UploadFields.RateLimit).Count > 0;
        Response.StatusCode = StatusCodes.Status400BadRequest;
        return Page();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var mains = await db.MainCategories.AsNoTracking()
            .Include(c => c.SubCategories)
            .ToListAsync(cancellationToken);

        // Sorted by code like the old _create_upload_category_choices, main categories disabled.
        Categories = mains
            .SelectMany(main => main.SubCategories
                .Select(sub => new CategoryOption(sub.Code, main.Name + " - " + sub.Name, false))
                .Prepend(new CategoryOption(main.Code, main.Name, true)))
            .OrderBy(c => c.Code, StringComparer.Ordinal)
            .ToList();

        if (uploads.IsSubjectToRateLimit(CurrentUser.User))
        {
            RateLimit = await uploads.GetRateLimitAsync(CurrentUser.User, HttpContext.GetClientIp(), cancellationToken);
            ShowRateLimit = RateLimit.Count >= options.Value.MaxUploadBurst;
        }
    }
}
