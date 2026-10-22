using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;

namespace Mogu.Web.Torrents;

/// <summary>Form field names of the upload form (same as the old WTForms ids, which main.js relies on).</summary>
public static class UploadFields
{
    public const string TorrentFile = "torrent_file";
    public const string DisplayName = "display_name";
    public const string Category = "category";
    public const string Information = "information";
    public const string Description = "description";
    public const string IsHidden = "is_hidden";
    public const string IsRemake = "is_remake";
    public const string IsAnonymous = "is_anonymous";
    public const string IsComplete = "is_complete";
    public const string IsTrusted = "is_trusted";
    public const string IsCommentLocked = "is_comment_locked";

    /// <summary>Pseudo field for upload rate limit errors.</summary>
    public const string RateLimit = "ratelimit";

    /// <summary>Pseudo field for raid mode and range ban errors.</summary>
    public const string RangeBanned = "rangebanned";

    /// <summary>Field name of each <see cref="UploadInput"/> property, for mapping validation results.</summary>
    public static readonly IReadOnlyDictionary<string, string> ByProperty = new Dictionary<string, string>
    {
        [nameof(UploadInput.TorrentFile)] = TorrentFile,
        [nameof(UploadInput.DisplayName)] = DisplayName,
        [nameof(UploadInput.Category)] = Category,
        [nameof(UploadInput.Information)] = Information,
        [nameof(UploadInput.Description)] = Description,
        [nameof(UploadInput.IsHidden)] = IsHidden,
        [nameof(UploadInput.IsRemake)] = IsRemake,
        [nameof(UploadInput.IsAnonymous)] = IsAnonymous,
        [nameof(UploadInput.IsComplete)] = IsComplete,
        [nameof(UploadInput.IsTrusted)] = IsTrusted,
        [nameof(UploadInput.IsCommentLocked)] = IsCommentLocked,
    };
}

/// <summary>The upload form (old <c>UploadForm</c>), bound by the upload page and filled from JSON by the API.</summary>
public sealed class UploadInput
{
    public const string RequiredMessage = "This field is required.";

    [ModelBinder(Name = UploadFields.TorrentFile)]
    [Required(ErrorMessage = RequiredMessage)]
    public IFormFile? TorrentFile { get; set; }

    [ModelBinder(Name = UploadFields.DisplayName)]
    [StringLength(255, MinimumLength = 3, ErrorMessage = "Torrent display name must be at least {2} characters long and {1} at most.")]
    public string? DisplayName { get; set; }

    [ModelBinder(Name = UploadFields.Category)]
    [Required(ErrorMessage = "Please select a category")]
    [RegularExpression(@"^\d+_\d+$", ErrorMessage = "Please select a category")]
    public string? Category { get; set; }

    [ModelBinder(Name = UploadFields.Information)]
    [StringLength(255, ErrorMessage = "Information must be at most {1} characters long.")]
    public string? Information { get; set; }

    [ModelBinder(Name = UploadFields.Description)]
    [StringLength(10 * 1024, ErrorMessage = "Description must be at most {1} characters long.")]
    public string? Description { get; set; }

    [ModelBinder(Name = UploadFields.IsHidden)]
    public bool IsHidden { get; set; }

    [ModelBinder(Name = UploadFields.IsRemake)]
    public bool IsRemake { get; set; }

    [ModelBinder(Name = UploadFields.IsAnonymous)]
    public bool IsAnonymous { get; set; }

    [ModelBinder(Name = UploadFields.IsComplete)]
    public bool IsComplete { get; set; }

    [ModelBinder(Name = UploadFields.IsTrusted)]
    public bool IsTrusted { get; set; }

    [ModelBinder(Name = UploadFields.IsCommentLocked)]
    public bool IsCommentLocked { get; set; }

    /// <summary>Validates the DataAnnotations outside MVC model binding (for the API), keyed by form field name.</summary>
    public UploadErrors Validate()
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(this, new ValidationContext(this), results, validateAllProperties: true);
        var errors = new UploadErrors();
        foreach (var result in results)
        {
            foreach (var member in result.MemberNames)
            {
                errors.Add(UploadFields.ByProperty.GetValueOrDefault(member, member), result.ErrorMessage ?? "Invalid value.");
            }
        }

        return errors;
    }

    public async Task<UploadRequest> ToRequestAsync(CancellationToken cancellationToken)
    {
        byte[]? data = null;
        if (TorrentFile is not null)
        {
            using var buffer = new MemoryStream();
            await TorrentFile.CopyToAsync(buffer, cancellationToken);
            data = buffer.ToArray();
        }

        return new UploadRequest
        {
            FileName = TorrentFile?.FileName ?? "",
            TorrentData = data,
            DisplayName = DisplayName,
            Category = Category ?? "",
            Information = Information,
            Description = Description,
            IsHidden = IsHidden,
            IsRemake = IsRemake,
            IsAnonymous = IsAnonymous,
            IsComplete = IsComplete,
            IsTrusted = IsTrusted,
            IsCommentLocked = IsCommentLocked,
        };
    }
}

/// <summary>What <see cref="TorrentUploadService"/> needs to store an upload.</summary>
public sealed record UploadRequest
{
    public required string FileName { get; init; }

    /// <summary>The raw .torrent; null when no file was sent (the caller reports that).</summary>
    public required byte[]? TorrentData { get; init; }

    public string? DisplayName { get; init; }

    public string Category { get; init; } = "";

    public string? Information { get; init; }

    public string? Description { get; init; }

    public bool IsHidden { get; init; }

    public bool IsRemake { get; init; }

    public bool IsAnonymous { get; init; }

    public bool IsComplete { get; init; }

    public bool IsTrusted { get; init; }

    public bool IsCommentLocked { get; init; }
}

/// <summary>Validation errors keyed by form field name (<see cref="UploadFields"/>).</summary>
public sealed class UploadErrors : IEnumerable<KeyValuePair<string, List<string>>>
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public bool HasErrors => _errors.Count > 0;

    public IReadOnlyList<string> this[string field] => _errors.TryGetValue(field, out var list) ? list : [];

    public void Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var list))
        {
            _errors[field] = list = [];
        }

        list.Add(message);
    }

    public void AddRange(UploadErrors other)
    {
        foreach (var (field, messages) in other)
        {
            foreach (var message in messages)
            {
                Add(field, message);
            }
        }
    }

    public IEnumerator<KeyValuePair<string, List<string>>> GetEnumerator() => _errors.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
