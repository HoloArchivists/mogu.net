using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Mogu.Web.Infrastructure;

/// <summary>What <c>_Pagination.cshtml</c> needs; implemented by <see cref="PagedList{T}"/>.</summary>
public interface IPageInfo
{
    int Page { get; }

    bool HasPrevious { get; }

    bool HasNext { get; }

    IEnumerable<int?> PageLinks(int leftEdge = 2, int leftCurrent = 6, int rightCurrent = 6, int rightEdge = 0);
}

public sealed record MarkdownEditorModel(string Name, string Label, string? Value, IReadOnlyList<string> Errors);

public static class ModelStateExtensions
{
    /// <summary>Error messages for one field, for <c>_FieldErrors.cshtml</c>.</summary>
    public static IReadOnlyList<string> ErrorsFor(this ModelStateDictionary modelState, string key) =>
        modelState.TryGetValue(key, out var entry)
            ? entry.Errors.Select(e => e.ErrorMessage).ToList()
            : [];
}
