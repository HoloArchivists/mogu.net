using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mogu.Core.Data;
using Mogu.Core.Options;
using Mogu.Web.Infrastructure;
using Mogu.Web.Services;

namespace Mogu.Web.Pages;

/// <summary>Front page listing and search (old <c>main.home</c>), also served as RSS at <c>/rss</c> or <c>?page=rss</c>.</summary>
public sealed partial class IndexModel(MoguDbContext db, TorrentSearch search, IOptionsSnapshot<MoguOptions> options) : MoguPageModel
{
    public const string HashRedirectMessage = "You were redirected here because the given hash matched this torrent.";

    public SearchResultsModel Results { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(string? feed, CancellationToken cancellationToken)
    {
        var listing = ListingQuery.FromQuery(Request.Query);
        var rss = feed is not null || listing.RssRequested;

        int? userId = null;
        if (!string.IsNullOrEmpty(listing.UserName))
        {
            userId = await FindUserIdAsync(listing.UserName, cancellationToken);
            if (userId is null)
            {
                return NotFound();
            }
        }

        // Advanced features for searches, but not RSS or user searches.
        string? firstWordUser = null;
        string? querySansUser = null;
        var term = listing.Term;
        if (!string.IsNullOrEmpty(term) && !rss && userId is null)
        {
            var userWord = FirstWord().Match(term);
            if (userWord.Success)
            {
                var name = userWord.Groups[1].Value;
                firstWordUser = await db.Users.Where(u => u.Username == name).Select(u => u.Username).FirstOrDefaultAsync(cancellationToken);
                querySansUser = userWord.Groups[2].Value;
            }

            if (InfoHashSearch.TryParse(term) is { } infoHash)
            {
                var torrentId = await db.Torrents.Where(t => t.InfoHash == infoHash).Select(t => (int?)t.Id).FirstOrDefaultAsync(cancellationToken);
                if (torrentId is not null)
                {
                    Flash(FlashLevel.Info, HashRedirectMessage);
                    return Redirect($"/view/{torrentId}");
                }
            }
        }

        var result = await search.SearchAsync(listing.ToRequest(userId, CurrentUser.User, rss), cancellationToken);
        if (!result.Succeeded)
        {
            return result.ToErrorResult();
        }

        if (rss)
        {
            var xml = RssFeedWriter.Write(result.Torrents.Items, options.Value, Request.AbsoluteUrl("/"), "Home", listing.MagnetLinks);
            Response.Headers.CacheControl = "max-age=300";
            return File(xml, "application/xml");
        }

        Search = listing.ToSearchState();
        RssFilter = listing.ToRssFilter(listing.UserName);
        Results = new SearchResultsModel(result.Torrents, Search, firstWordUser, querySansUser);
        return Page();
    }

    private Task<int?> FindUserIdAsync(string name, CancellationToken cancellationToken) =>
        db.Users.Where(u => u.Username == name).Select(u => (int?)u.Id).FirstOrDefaultAsync(cancellationToken);

    [GeneratedRegex("^([a-zA-Z0-9_-]+) *(.*|$)", RegexOptions.CultureInvariant)]
    private static partial Regex FirstWord();
}
