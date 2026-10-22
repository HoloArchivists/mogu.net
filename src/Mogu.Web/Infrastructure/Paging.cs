namespace Mogu.Web.Infrastructure;

/// <summary>One page of results plus what <c>_Pagination.cshtml</c> needs to render page links.</summary>
public sealed class PagedList<T>(IReadOnlyList<T> items, int page, int pageSize, int totalCount)
{
    public IReadOnlyList<T> Items { get; } = items;

    public int Page { get; } = page;

    public int PageSize { get; } = pageSize;

    /// <summary>Total number of results, possibly capped by the page limit.</summary>
    public int TotalCount { get; } = totalCount;

    public int PageCount => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));

    public bool HasPrevious => Page > 1;

    public bool HasNext => Page < PageCount;

    /// <summary>
    /// Page numbers to link, with <c>null</c> marking an ellipsis. Mirrors Flask-SQLAlchemy's
    /// <c>iter_pages(left_edge=2, left_current=6, right_current=6, right_edge=0)</c>.
    /// </summary>
    public IEnumerable<int?> PageLinks(int leftEdge = 2, int leftCurrent = 6, int rightCurrent = 6, int rightEdge = 0)
    {
        var last = 0;
        for (var num = 1; num <= PageCount; num++)
        {
            if (num <= leftEdge
                || (num > Page - leftCurrent - 1 && num < Page + rightCurrent)
                || num > PageCount - rightEdge)
            {
                if (last + 1 != num)
                {
                    yield return null;
                }

                yield return num;
                last = num;
            }
        }
    }
}
