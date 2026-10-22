using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Mogu.Core.Data;
using Mogu.Core.Entities;
using Mogu.Web.Infrastructure;
using Mogu.Web.Torrents;

namespace Mogu.Web.Api;

/// <summary>
/// The JSON API used by upload scripts (see the old <c>utils/api_uploader_v2.py</c>). Not antiforgery
/// protected: uploads authenticate with an API token only, never the login cookie.
/// </summary>
public static partial class ApiEndpoints
{
    /// <summary>Upload form fields whose API key differs (old <c>UPLOAD_API_FORM_KEYMAP</c>).</summary>
    private static readonly Dictionary<string, string> ApiKeyByField = new(StringComparer.Ordinal)
    {
        [UploadFields.TorrentFile] = "torrent",
        [UploadFields.DisplayName] = "name",
        [UploadFields.IsAnonymous] = "anonymous",
        [UploadFields.IsHidden] = "hidden",
        [UploadFields.IsComplete] = "complete",
        [UploadFields.IsRemake] = "remake",
        [UploadFields.IsTrusted] = "trusted",
    };

    public static IEndpointRouteBuilder MapApiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api").DisableAntiforgery();
        api.MapPost("/upload", UploadAsync);
        api.MapPost("/v2/upload", UploadAsync);
        api.MapGet("/info/{torrentIdOrHash}", InfoAsync);
        return endpoints;
    }

    private static IResult Errors(object errors, int status = StatusCodes.Status400BadRequest) =>
        Results.Json(new { errors }, statusCode: status);

    private static IResult Error(string message, int status = StatusCodes.Status400BadRequest) =>
        Errors(new[] { message }, status);

    private static IResult BadAuthorization() => Error("Bad authorization", StatusCodes.Status403Forbidden);

    private static async Task<IResult> UploadAsync(
        HttpContext context, ApiTokenService tokens, TorrentUploadService uploads, TorrentLinks links, CancellationToken cancellationToken)
    {
        var user = await tokens.AuthenticateAsync(context.Request, cancellationToken);
        if (user is null)
        {
            return BadAuthorization();
        }

        var form = context.Request.HasFormContentType ? await context.Request.ReadFormAsync(cancellationToken) : null;
        var torrentData = form?["torrent_data"].ToString();
        if (form is null || !form.ContainsKey("torrent_data"))
        {
            return Error("missing torrent_data field");
        }

        JsonElement data;
        try
        {
            using var document = JsonDocument.Parse(torrentData!);
            data = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Error("unable to parse valid JSON in torrent_data");
        }

        if (data.ValueKind != JsonValueKind.Object)
        {
            return Error("unable to parse valid JSON in torrent_data");
        }

        // Same keys and defaults as the old UPLOAD_API_DEFAULTS.
        var input = new UploadInput
        {
            TorrentFile = form.Files.GetFile("torrent"),
            DisplayName = GetString(data, "name"),
            Category = GetString(data, "category"),
            Information = GetString(data, "information"),
            Description = GetString(data, "description"),
            IsAnonymous = GetBool(data, "anonymous", false),
            IsHidden = GetBool(data, "hidden", false),
            IsComplete = GetBool(data, "complete", false),
            IsRemake = GetBool(data, "remake", false),
            IsTrusted = GetBool(data, "trusted", true),
        };

        var errors = input.Validate();
        var request = await input.ToRequestAsync(cancellationToken);
        var result = await uploads.UploadAsync(request, user, context.GetClientIp(), !errors.HasErrors, cancellationToken);
        if (result.Succeeded)
        {
            var torrent = result.Torrent;
            return Results.Json(new
            {
                url = context.Request.AbsoluteUrl($"/view/{torrent.Id}"),
                id = torrent.Id,
                name = torrent.DisplayName,
                hash = torrent.InfoHashHex,
                magnet = links.Magnet(torrent),
            });
        }

        errors.AddRange(result.Errors);
        return Errors(errors.ToDictionary(e => ApiKeyByField.GetValueOrDefault(e.Key, e.Key), e => e.Value));
    }

    private static async Task<IResult> InfoAsync(
        string torrentIdOrHash, HttpContext context, ApiTokenService tokens, CurrentUser currentUser, MoguDbContext db, TorrentLinks links, CancellationToken cancellationToken)
    {
        // Token auth, or the login cookie for read-only requests.
        var viewer = await tokens.AuthenticateAsync(context.Request, cancellationToken) ?? currentUser.User;
        if (viewer is null)
        {
            return BadAuthorization();
        }

        var query = torrentIdOrHash.Trim().ToLowerInvariant();
        IQueryable<Torrent> torrents = db.Torrents.AsNoTracking()
            .Include(t => t.Uploader)
            .Include(t => t.MainCategory)
            .Include(t => t.SubCategory)
            .Include(t => t.FileList);
        if (IdPattern().IsMatch(query) && int.TryParse(query, out var id))
        {
            torrents = torrents.Where(t => t.Id == id);
        }
        else if (InfoHashPattern().IsMatch(query))
        {
            var hash = Convert.FromHexString(query);
            torrents = torrents.Where(t => t.InfoHash == hash);
        }
        else
        {
            return NotFound();
        }

        var torrent = await torrents.AsSplitQuery().FirstOrDefaultAsync(cancellationToken);

        // Only administrators see deleted torrents.
        if (torrent is null || (torrent.IsDeleted && !viewer.IsAdministrator))
        {
            return NotFound();
        }

        string? submitter = null;
        if (torrent.Uploader is { } uploader && (!torrent.IsAnonymous || viewer.Id == uploader.Id || viewer.IsModerator))
        {
            submitter = uploader.Username;
        }

        JsonElement files;
        using (var document = JsonDocument.Parse(torrent.FileList?.Json ?? "{}"))
        {
            files = document.RootElement.Clone();
        }

        return Results.Json(new
        {
            submitter,
            url = context.Request.AbsoluteUrl($"/view/{torrent.Id}"),
            id = torrent.Id,
            name = torrent.DisplayName,
            creation_date = Format.MinutesUtc(torrent.CreatedAt),
            hash_b32 = Base32(torrent.InfoHash),
            hash_hex = torrent.InfoHashHex,
            magnet = links.Magnet(torrent),
            main_category = torrent.MainCategory.Name,
            main_category_id = torrent.MainCategoryId,
            sub_category = torrent.SubCategory.Name,
            sub_category_id = torrent.SubCategoryId,
            information = torrent.Information,
            description = torrent.Description,
            stats = new
            {
                seeders = torrent.SeedCount,
                leechers = torrent.LeechCount,
                downloads = torrent.DownloadCount,
            },
            filesize = torrent.FileSize,
            files,
            is_trusted = torrent.IsTrusted,
            is_complete = torrent.IsComplete,
            is_remake = torrent.IsRemake,
        });

        static IResult NotFound() => Error("Query was not a valid id or hash.");
    }

    /// <summary>
    /// A string field; missing, null and blank become null (like MVC form binding, so "" counts as
    /// not given), other JSON types their raw text.
    /// </summary>
    private static string? GetString(JsonElement data, string key)
    {
        var text = data.TryGetProperty(key, out var value) ? value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => value.GetString(),
            _ => value.GetRawText(),
        }
        : null;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>A flag with Python truthiness, as the old <c>BooleanField</c> applied it.</summary>
    private static bool GetBool(JsonElement data, string key, bool defaultValue) =>
        data.TryGetProperty(key, out var value) ? value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => defaultValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.GetDouble() != 0,
            JsonValueKind.String => value.GetString()!.Length > 0,
            JsonValueKind.Array => value.GetArrayLength() > 0,
            _ => value.EnumerateObject().Any(),
        }
        : defaultValue;

    /// <summary>RFC 4648 base32, as used in old style magnet links.</summary>
    internal static string Base32(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var builder = new StringBuilder((data.Length + 4) / 5 * 8);
        var buffer = 0;
        var bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                builder.Append(alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }

            buffer &= (1 << bits) - 1;
        }

        if (bits > 0)
        {
            builder.Append(alphabet[(buffer << (5 - bits)) & 31]);
        }

        while (builder.Length % 8 != 0)
        {
            builder.Append('=');
        }

        return builder.ToString();
    }

    [GeneratedRegex("^[0-9]+$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex InfoHashPattern();
}
