using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Mogu.Core.Data;

namespace Mogu.Web.Infrastructure;

public static class UrlHelpers
{
    /// <summary>All categories as (code, dropdown label, title), in navbar order.</summary>
    public static IReadOnlyList<(string Code, string Label, string Title)> CategoryChoices { get; } =
        CategorySeed.MainCategories.SelectMany(main =>
            new[] { (main.Code, main.Name, main.Name) }.Concat(
                CategorySeed.SubCategories
                    .Where(sub => sub.MainCategoryId == main.Id)
                    .Select(sub => (sub.Code, "- " + sub.Name, main.Name + " - " + sub.Name))))
        .ToList();

    /// <summary>"Unarchived - Karaoke" for "1_1"; "???" for unknown codes.</summary>
    public static string CategoryName(string code)
    {
        var parts = code.Split('_');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var main) || !int.TryParse(parts[1], out var sub))
        {
            return "???";
        }

        var mainCategory = CategorySeed.MainCategories.FirstOrDefault(c => c.Id == main);
        if (mainCategory is null)
        {
            return "???";
        }

        if (sub == 0)
        {
            return mainCategory.Name;
        }

        var subCategory = CategorySeed.SubCategories.FirstOrDefault(c => c.MainCategoryId == main && c.Id == sub);
        return subCategory is null ? "???" : mainCategory.Name + " - " + subCategory.Name;
    }

    /// <summary>Link to the RSS feed of a listing; <paramref name="filter"/> holds q/c/f/u.</summary>
    public static string RssUrl(this IUrlHelper url, IReadOnlyDictionary<string, string?>? filter, bool absolute = false)
    {
        var query = new QueryBuilder { { "page", "rss" } };
        foreach (var (key, value) in filter ?? new Dictionary<string, string?>())
        {
            if (!string.IsNullOrEmpty(value))
            {
                query.Add(key, value);
            }
        }

        var path = "/" + query.ToQueryString();
        return absolute ? url.ActionContext.HttpContext.Request.AbsoluteUrl(path) : path;
    }

    /// <summary>Current path and query with the given parameters replaced and the page (<c>p</c>) dropped.</summary>
    public static string ModifyQuery(this HttpRequest request, params (string Key, string? Value)[] values)
    {
        var query = request.Query
            .Where(kv => kv.Key != "p" && !values.Any(v => v.Key == kv.Key))
            .SelectMany(kv => kv.Value.Select(v => new KeyValuePair<string, string>(kv.Key, v ?? "")))
            .ToList();
        query.AddRange(values.Where(v => v.Value is not null).Select(v => new KeyValuePair<string, string>(v.Key, v.Value!)));
        return request.PathBase + request.Path + new QueryBuilder(query).ToQueryString();
    }

    /// <summary>Current path and query with only the page (<c>p</c>) replaced.</summary>
    public static string WithPage(this HttpRequest request, int page)
    {
        var query = request.Query
            .Where(kv => kv.Key != "p")
            .SelectMany(kv => kv.Value.Select(v => new KeyValuePair<string, string>(kv.Key, v ?? "")))
            .Append(new KeyValuePair<string, string>("p", page.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return request.PathBase + request.Path + new QueryBuilder(query).ToQueryString();
    }

    public static string AbsoluteUrl(this HttpRequest request, string pathAndQuery) =>
        UriHelper.BuildAbsolute(request.Scheme, request.Host, request.PathBase) + pathAndQuery.TrimStart('/');
}
