using Mogu.Web.Infrastructure;
using Mogu.Web.Services;

namespace Mogu.Web.Pages;

/// <summary>Model of <c>_SearchResults.cshtml</c> (old <c>search_results.html</c>).</summary>
/// <param name="FirstWordUserName">Set when the first word of the search is a username, for the "only results uploaded by" hint.</param>
/// <param name="QuerySansUser">The search without that first word.</param>
public sealed record SearchResultsModel(
    PagedList<TorrentRow> Torrents,
    SearchState Search,
    string? FirstWordUserName = null,
    string? QuerySansUser = null);
