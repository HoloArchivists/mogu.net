using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Mogu.Web.Infrastructure;

namespace Mogu.Web.Pages.Admin;

/// <summary>Flask-SQLAlchemy style <c>paginate(page, per_page=20)</c> for the admin tables.</summary>
internal static class AdminPaging
{
    public const int PageSize = 20;

    /// <summary>Old <c>DATETIME</c> rendering of the admin tables (Python's <c>str(datetime)</c> without microseconds).</summary>
    public static string Timestamp(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Returns null when the page is out of range (the old views 404'd).</summary>
    public static async Task<PagedList<T>?> ToPageAsync<T>(this IQueryable<T> query, int page, CancellationToken cancellationToken)
    {
        if (page < 1)
        {
            return null;
        }

        var total = await query.CountAsync(cancellationToken);
        if (page > 1 && (page - 1) * PageSize >= total)
        {
            return null;
        }

        var items = await query.Skip((page - 1) * PageSize).Take(PageSize).ToListAsync(cancellationToken);
        return new PagedList<T>(items, page, PageSize, total);
    }
}
