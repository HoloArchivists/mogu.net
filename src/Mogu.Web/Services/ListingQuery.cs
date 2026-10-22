using System.Globalization;
using Mogu.Core.Entities;
using Mogu.Web.Pages;

namespace Mogu.Web.Services;

/// <summary>
/// Listing parameters as given in the query string, with the old aliases
/// (<c>q</c>/<c>term</c>, <c>c</c>/<c>cats</c>, <c>f</c>/<c>filter</c>, <c>u</c>/<c>user</c>, <c>p</c>/<c>page</c>/<c>offset</c>).
/// </summary>
public sealed record ListingQuery(
    string? Term,
    string? Sort,
    string? Order,
    string? Category,
    string? QualityFilter,
    string? UserName,
    long Page,
    bool MagnetLinks,
    bool RssRequested)
{
    public static ListingQuery FromQuery(IQueryCollection query) => new(
        Term: First(query, "q", "term"),
        Sort: First(query, "s"),
        Order: First(query, "o"),
        Category: First(query, "c", "cats"),
        QualityFilter: First(query, "f", "filter"),
        UserName: First(query, "u", "user"),
        Page: ParsePage(First(query, "p", "page", "offset")),
        MagnetLinks: query.ContainsKey("magnets") || query.ContainsKey("m"),
        RssRequested: First(query, "page") == "rss");

    /// <summary>Search arguments with defaults applied, as passed to <see cref="TorrentSearch"/>.</summary>
    public TorrentSearchRequest ToRequest(int? userId, User? viewer, bool rss) => new()
    {
        Term = Term ?? "",
        UserId = userId,
        Sort = string.IsNullOrEmpty(Sort) ? "id" : Sort,
        Order = string.IsNullOrEmpty(Order) ? "desc" : Order,
        Category = string.IsNullOrEmpty(Category) ? "0_0" : Category,
        QualityFilter = string.IsNullOrEmpty(QualityFilter) ? "0" : QualityFilter,
        Page = Page,
        Rss = rss,
        Viewer = viewer,
    };

    /// <summary>State for the navbar search form and the sortable column headers.</summary>
    public SearchState ToSearchState()
    {
        var request = ToRequest(null, null, false);
        return new SearchState(request.Term, request.Category, request.QualityFilter, request.Sort, request.Order);
    }

    /// <summary>Query parameters for the RSS link of this listing (old <c>_generate_query_string</c>).</summary>
    public IReadOnlyDictionary<string, string?> ToRssFilter(string? userName) => new Dictionary<string, string?>
    {
        ["q"] = Term,
        ["c"] = Category,
        ["f"] = QualityFilter,
        ["u"] = userName,
    };

    /// <summary>First value of the first key that is present, like the old <c>chain_get</c>.</summary>
    private static string? First(IQueryCollection query, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (query.TryGetValue(key, out var values))
            {
                return values.Count > 0 ? values[0] ?? "" : "";
            }
        }

        return null;
    }

    /// <summary><c>max(1, int(p))</c>, falling back to 1 for anything unparsable.</summary>
    private static long ParsePage(string? value)
    {
        if (!long.TryParse(value?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var page))
        {
            // Absurdly large numbers still parse in Python and end up as a 404.
            return value is not null && value.Trim().Length > 0 && value.Trim().All(char.IsAsciiDigit) ? long.MaxValue : 1;
        }

        return Math.Max(1, page);
    }
}
