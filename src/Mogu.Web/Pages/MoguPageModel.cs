using Microsoft.AspNetCore.Mvc.RazorPages;
using Mogu.Web.Infrastructure;

namespace Mogu.Web.Pages;

/// <summary>Base for pages using <c>_Layout.cshtml</c>; carries the state the navbar search needs.</summary>
public abstract class MoguPageModel : PageModel
{
    /// <summary>Current search, used to pre-fill the navbar search form. Null when not on a listing.</summary>
    public SearchState? Search { get; protected set; }

    /// <summary>Set on user pages so the navbar searches that user's torrents.</summary>
    public string? UserPageName { get; protected set; }

    /// <summary>Query parameters for the RSS link of the current listing (q, c, f, u).</summary>
    public IReadOnlyDictionary<string, string?>? RssFilter { get; protected set; }

    protected CurrentUser CurrentUser => HttpContext.CurrentUser();

    protected void Flash(FlashLevel level, string text, bool isHtml = false) => TempData.AddFlash(level, text, isHtml);
}

/// <summary>Search parameters of a torrent listing as given in the query string.</summary>
public sealed record SearchState(string Term, string Category, string QualityFilter, string Sort, string Order);
