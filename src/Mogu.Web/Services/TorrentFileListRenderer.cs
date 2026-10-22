using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Html;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Mogu.Core.Data;
using Mogu.Core.Options;
using Mogu.Web.Infrastructure;

namespace Mogu.Web.Services;

/// <summary>
/// Renders the "File list" panel of the torrent page from <see cref="Core.Entities.TorrentFileList.Json"/>.
/// File lists never change for an info hash, so the HTML is cached for a day like the old <c>{% cache %}</c> block.
/// </summary>
public sealed class TorrentFileListRenderer(MoguDbContext db, IMemoryCache cache, IOptionsSnapshot<MoguOptions> options)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(24);

    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 512 };

    /// <summary>Top-level folders with at most this many entries start expanded.</summary>
    private const int PreExpandLimit = 20;

    public async Task<IHtmlContent> RenderAsync(int torrentId, string infoHashHex, CancellationToken cancellationToken = default)
    {
        var key = "Mogu.FileList:" + infoHashHex;
        if (cache.TryGetValue(key, out string? html) && html is not null)
        {
            return new HtmlString(html);
        }

        var json = await db.TorrentFileLists
            .Where(f => f.TorrentId == torrentId)
            .Select(f => f.Json)
            .FirstOrDefaultAsync(cancellationToken);

        html = Render(json, options.Value.MaxFilesView);
        cache.Set(key, html, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheDuration, Size = html.Length });
        return new HtmlString(html);
    }

    internal static string Render(string? json, int maxFiles)
    {
        JsonDocument? document = null;
        try
        {
            if (!string.IsNullOrEmpty(json))
            {
                document = JsonDocument.Parse(json, JsonOptions);
            }
        }
        catch (JsonException)
        {
            document = null;
        }

        using (document)
        {
            var root = document?.RootElement;
            if (root is not { ValueKind: JsonValueKind.Object } tree || !tree.EnumerateObject().Any())
            {
                return CollapsedPanel("File list is not available for this torrent.");
            }

            if (CountFiles(tree) > maxFiles)
            {
                return CollapsedPanel("Too many files to display.");
            }

            var builder = new StringBuilder();
            builder.Append("<div class=\"panel panel-default\">\n")
                .Append("\t<div class=\"panel-heading\">\n")
                .Append("\t\t<h3 class=\"panel-title\">File list</h3>\n")
                .Append("\t</div>\n\n")
                .Append("\t<div class=\"torrent-file-list panel-body\">\n")
                .Append("\t\t<ul>\n");
            AppendItems(builder, tree, depth: 0);
            builder.Append("\t\t</ul>\n")
                .Append("\t</div>\n")
                .Append("</div><!--/.panel -->\n");
            return builder.ToString();
        }
    }

    private static void AppendItems(StringBuilder builder, JsonElement folder, int depth)
    {
        foreach (var entry in folder.EnumerateObject())
        {
            var name = TorrentHtml.Encode(entry.Name);
            if (entry.Value.ValueKind == JsonValueKind.Object)
            {
                var preExpanded = depth == 0 && entry.Value.EnumerateObject().Count() <= PreExpandLimit;
                builder.Append("<li>\n")
                    .Append("<a href=\"\" class=\"folder\"><i class=\"fa fa-folder").Append(preExpanded ? "-open" : "").Append("\"></i>")
                    .Append(name).Append("</a>\n")
                    .Append("<ul").Append(preExpanded ? " data-show=\"yes\"" : "").Append(">\n");
                AppendItems(builder, entry.Value, depth + 1);
                builder.Append("</ul>\n</li>\n");
            }
            else
            {
                var size = entry.Value.TryGetInt64(out var bytes) ? bytes : 0;
                builder.Append("<li><i class=\"fa fa-file\"></i>").Append(name)
                    .Append(" <span class=\"file-size\">(").Append(Format.FileSize(size)).Append(")</span></li>\n");
            }
        }
    }

    private static int CountFiles(JsonElement folder)
    {
        var count = 0;
        foreach (var entry in folder.EnumerateObject())
        {
            count += entry.Value.ValueKind == JsonValueKind.Object ? CountFiles(entry.Value) : 1;
        }

        return count;
    }

    private static string CollapsedPanel(string title) =>
        "<div class=\"panel panel-default\">\n"
        + "\t<div class=\"panel-heading panel-heading-collapse\">\n"
        + "\t\t<h3 class=\"panel-title\">\n"
        + "\t\t\t" + title + "\n"
        + "\t\t</h3>\n"
        + "\t</div>\n"
        + "</div>\n";
}
