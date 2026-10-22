using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mogu.Core.Data;
using Mogu.Core.Entities;
using Mogu.Core.Options;
using Mogu.Web.Infrastructure;

namespace Mogu.Web.Services;

/// <summary>A torrent as shown in listings and RSS feeds.</summary>
public sealed record TorrentRow(
    int Id,
    string DisplayName,
    byte[] InfoHash,
    int MainCategoryId,
    int SubCategoryId,
    long FileSize,
    DateTime CreatedAt,
    int CommentCount,
    int SeedCount,
    int LeechCount,
    int DownloadCount,
    bool IsDeleted,
    bool IsHidden,
    bool IsRemake,
    bool IsTrusted,
    bool HasTorrent)
{
    public string CategoryCode => $"{MainCategoryId}_{SubCategoryId}";

    public string InfoHashHex => Convert.ToHexStringLower(InfoHash);

    /// <summary>Bootstrap row class, same precedence as the old template.</summary>
    public string RowClass => IsDeleted ? "deleted" : IsHidden ? "warning" : IsRemake ? "danger" : IsTrusted ? "success" : "default";
}

/// <summary>Raw listing parameters, defaults already applied (see <see cref="ListingQuery"/>).</summary>
public sealed record TorrentSearchRequest
{
    public string Term { get; init; } = "";

    /// <summary>Restrict to torrents uploaded by this user (the /user/{name} view).</summary>
    public int? UserId { get; init; }

    public string Sort { get; init; } = "id";

    public string Order { get; init; } = "desc";

    public string Category { get; init; } = "0_0";

    public string QualityFilter { get; init; } = "0";

    public long Page { get; init; } = 1;

    public bool Rss { get; init; }

    /// <summary>The logged in user, if any. Moderators see everything ("god mode").</summary>
    public User? Viewer { get; init; }
}

public sealed class TorrentSearchResult
{
    private TorrentSearchResult(int statusCode, string? message, PagedList<TorrentRow>? torrents, int actualCount)
    {
        StatusCode = statusCode;
        Message = message;
        Torrents = torrents ?? new PagedList<TorrentRow>([], 1, 1, 0);
        ActualCount = actualCount;
    }

    /// <summary>200 on success, otherwise the HTTP status the old site aborted with.</summary>
    public int StatusCode { get; }

    /// <summary>Plain text body for errors that had one (the page limit).</summary>
    public string? Message { get; }

    public bool Succeeded => StatusCode == StatusCodes.Status200OK;

    public PagedList<TorrentRow> Torrents { get; }

    /// <summary>Number of matches before the page limit cap; 0 for RSS.</summary>
    public int ActualCount { get; }

    internal static TorrentSearchResult Success(PagedList<TorrentRow> torrents, int actualCount) =>
        new(StatusCodes.Status200OK, null, torrents, actualCount);

    internal static TorrentSearchResult Error(int statusCode, string? message = null) => new(statusCode, message, null, 0);

    public IActionResult ToErrorResult() => Message is null
        ? new StatusCodeResult(StatusCode)
        : new ContentResult { StatusCode = StatusCode, Content = Message, ContentType = "text/plain; charset=utf-8" };
}

/// <summary>
/// Database torrent search, a port of <c>nyaa.search.search_db</c> (Elasticsearch is gone).
/// MySQL FULLTEXT is replaced by LIKE matching, see <see cref="SearchTerms"/>.
/// </summary>
public sealed partial class TorrentSearch(MoguDbContext db, IOptionsSnapshot<MoguOptions> options)
{
    public const string MaxPagesMessage = "You've exceeded the maximum number of pages. Please make your search query less broad.";

    private static readonly MethodInfo LikeMethod = typeof(DbFunctionsExtensions).GetMethod(
        nameof(DbFunctionsExtensions.Like),
        [typeof(DbFunctions), typeof(string), typeof(string), typeof(string)])!;

    public async Task<TorrentSearchResult> SearchAsync(TorrentSearchRequest request, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var perPage = settings.ResultsPerPage;
        var page = request.Page;
        if (page > uint.MaxValue)
        {
            return TorrentSearchResult.Error(StatusCodes.Status404NotFound);
        }

        var viewer = request.Viewer;
        var admin = viewer?.IsModerator == true;
        var sameUser = viewer is not null && request.UserId == viewer.Id;

        // Logged in users should always be able to view their full listing.
        var maxPages = sameUser || admin ? 0 : settings.MaxPages;
        if (maxPages > 0 && page > maxPages)
        {
            return TorrentSearchResult.Error(StatusCodes.Status403Forbidden, MaxPagesMessage);
        }

        var sort = request.Sort.ToLowerInvariant();
        if (sort is not ("id" or "size" or "comments" or "seeders" or "leechers" or "downloads"))
        {
            return TorrentSearchResult.Error(StatusCodes.Status400BadRequest);
        }

        var order = request.Order.ToLowerInvariant();
        if (order is not ("asc" or "desc"))
        {
            return TorrentSearchResult.Error(StatusCodes.Status400BadRequest);
        }

        var filter = request.QualityFilter.ToLowerInvariant();
        if (filter is not ("0" or "1" or "2" or "3"))
        {
            return TorrentSearchResult.Error(StatusCodes.Status400BadRequest);
        }

        var categoryMatch = CategoryPattern().Match(request.Category);
        if (!categoryMatch.Success)
        {
            return TorrentSearchResult.Error(StatusCodes.Status400BadRequest);
        }

        IQueryable<Torrent> query = db.Torrents.AsNoTracking();

        if (request.UserId is int userId)
        {
            // User view (/user/username)
            query = query.Where(t => t.UploaderId == userId);
            if (!admin)
            {
                query = query.Where(t => !t.IsDeleted);

                // Others (and RSS, always) only see the user's public, non-anonymous torrents.
                if (!sameUser || request.Rss)
                {
                    query = query.Where(t => !t.IsHidden && !t.IsAnonymous);
                }
            }
        }
        else if (!admin)
        {
            // General view: never deleted; hidden only to their uploader, and never in RSS.
            query = query.Where(t => !t.IsDeleted);
            if (viewer is not null && !request.Rss)
            {
                var viewerId = viewer.Id;
                query = query.Where(t => !t.IsHidden || t.UploaderId == viewerId);
            }
            else
            {
                query = query.Where(t => !t.IsHidden);
            }
        }

        query = ApplyCategory(query, categoryMatch.Groups[1].Value, categoryMatch.Groups[2].Value);

        query = filter switch
        {
            "1" => query.Where(t => !t.IsRemake),
            "2" => query.Where(t => t.IsTrusted),
            "3" => query.Where(t => t.IsComplete),
            _ => query,
        };

        query = ApplyTerm(query, request.Term);

        if (request.Rss)
        {
            sort = "id";
            order = "desc";
        }

        var ordered = ApplySort(query, sort, order == "desc");

        if (request.Rss)
        {
            var feed = await Project(ordered.Take(perPage)).ToListAsync(cancellationToken);
            return TorrentSearchResult.Success(new PagedList<TorrentRow>(feed, 1, perPage, feed.Count), 0);
        }

        var offset = (page - 1) * perPage;
        if (offset > int.MaxValue)
        {
            return TorrentSearchResult.Error(StatusCodes.Status404NotFound);
        }

        var actualCount = await query.CountAsync(cancellationToken);
        var items = await Project(ordered.Skip((int)offset).Take(perPage)).ToListAsync(cancellationToken);
        if (items.Count == 0 && page != 1)
        {
            return TorrentSearchResult.Error(StatusCodes.Status404NotFound);
        }

        var total = maxPages > 0 ? (int)Math.Min(actualCount, (long)maxPages * perPage) : actualCount;
        return TorrentSearchResult.Success(new PagedList<TorrentRow>(items, (int)page, perPage, total), actualCount);
    }

    /// <summary>
    /// Like search_db, an unknown category id silently means "no category filter"; only a
    /// malformed value is a 400.
    /// </summary>
    private static IQueryable<Torrent> ApplyCategory(IQueryable<Torrent> query, string mainText, string subText)
    {
        if (!int.TryParse(mainText, NumberStyles.None, CultureInfo.InvariantCulture, out var mainId)
            || !int.TryParse(subText, NumberStyles.None, CultureInfo.InvariantCulture, out var subId)
            || mainId <= 0)
        {
            return query;
        }

        if (subId > 0)
        {
            return CategorySeed.SubCategories.Any(c => c.MainCategoryId == mainId && c.Id == subId)
                ? query.Where(t => t.MainCategoryId == mainId && t.SubCategoryId == subId)
                : query;
        }

        return CategorySeed.MainCategories.Any(c => c.Id == mainId)
            ? query.Where(t => t.MainCategoryId == mainId)
            : query;
    }

    private static IQueryable<Torrent> ApplyTerm(IQueryable<Torrent> query, string term)
    {
        var torrent = Expression.Parameter(typeof(Torrent), "t");
        var name = Expression.Property(torrent, nameof(Torrent.DisplayName));
        var functions = Expression.Constant(EF.Functions);
        var escape = Expression.Constant("\\");

        foreach (var group in SearchTerms.Parse(term))
        {
            Expression? any = null;
            foreach (var alternative in group.Alternatives)
            {
                var like = Expression.Call(LikeMethod, functions, name, Parameter("%" + SearchTerms.EscapeLike(alternative) + "%"), escape);
                any = any is null ? like : Expression.OrElse(any, like);
            }

            var body = group.Exclude ? Expression.Not(any!) : any!;
            query = query.Where(Expression.Lambda<Func<Torrent, bool>>(body, torrent));
        }

        return query;
    }

    /// <summary>A closure member access, so EF sends the value as a SQL parameter instead of inlining it.</summary>
    private static Expression Parameter(string value)
    {
        Expression<Func<string>> capture = () => value;
        return capture.Body;
    }

    /// <summary>Sorts with the torrent id (same direction) as tie breaker so paging is stable.</summary>
    private static IOrderedQueryable<Torrent> ApplySort(IQueryable<Torrent> query, string sort, bool descending)
    {
        IOrderedQueryable<Torrent> By<TKey>(Expression<Func<Torrent, TKey>> key) =>
            descending ? query.OrderByDescending(key) : query.OrderBy(key);

        var ordered = sort switch
        {
            "size" => By(t => t.FileSize),
            "comments" => By(t => t.CommentCount),
            "seeders" => By(t => t.SeedCount),
            "leechers" => By(t => t.LeechCount),
            "downloads" => By(t => t.DownloadCount),
            _ => null,
        };

        if (ordered is null)
        {
            return By(t => t.Id);
        }

        return descending ? ordered.ThenByDescending(t => t.Id) : ordered.ThenBy(t => t.Id);
    }

    private static IQueryable<TorrentRow> Project(IQueryable<Torrent> query) => query.Select(t => new TorrentRow(
        t.Id,
        t.DisplayName,
        t.InfoHash,
        t.MainCategoryId,
        t.SubCategoryId,
        t.FileSize,
        t.CreatedAt,
        t.CommentCount,
        t.SeedCount,
        t.LeechCount,
        t.DownloadCount,
        t.IsDeleted,
        t.IsHidden,
        t.IsRemake,
        t.IsTrusted,
        t.HasTorrent));

    [GeneratedRegex(@"^(\d+)_(\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex CategoryPattern();
}
