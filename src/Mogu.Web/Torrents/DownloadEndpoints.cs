using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mogu.Core.Data;
using Mogu.Core.Entities;
using Mogu.Core.Options;
using Mogu.Core.Torrents;
using Mogu.Web.Infrastructure;

namespace Mogu.Web.Torrents;

/// <summary>Builds .torrent files from stored info dicts.</summary>
public sealed class TorrentFileService(DataPaths paths, TorrentLinks links)
{
    /// <summary>
    /// The .torrent for <paramref name="torrent"/>, whose <see cref="Torrent.Trackers"/> (with
    /// <see cref="TorrentTracker.Tracker"/>) must be loaded. Null if the info dict is missing.
    /// </summary>
    public async Task<byte[]?> CreateAsync(Torrent torrent, string torrentPageUrl, CancellationToken cancellationToken)
    {
        var path = paths.InfoDictPath(torrent.InfoHashHex);
        if (!File.Exists(path))
        {
            return null;
        }

        var info = await File.ReadAllBytesAsync(path, cancellationToken);
        var own = torrent.Trackers.OrderBy(t => t.Order).Select(t => t.Tracker).ToList();
        var metadata = new TorrentFileMetadata(
            torrent.CreatedAt,
            torrentPageUrl,
            torrent.Encoding,
            links.TrackersFor(own.Where(t => !t.IsWebSeed).Select(t => t.Uri)),
            own.Where(t => t.IsWebSeed).Select(t => t.Uri).Distinct(StringComparer.Ordinal).ToList());
        return TorrentFileBuilder.Build(info, metadata);
    }
}

/// <summary><c>/view/{id}/torrent</c>, <c>/download/{id}.torrent</c> and <c>/view/{id}/magnet</c>.</summary>
public static class DownloadEndpoints
{
    public static IEndpointRouteBuilder MapDownloadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/view/{id:int}/torrent", DownloadAsync);
        endpoints.MapGet("/download/{id:int}.torrent", DownloadAsync);
        endpoints.MapGet("/view/{id:int}/magnet", MagnetAsync);
        return endpoints;
    }

    /// <summary>Content-Disposition as the old site sent it: the UTF-8 percent-encoded name in both parameters.</summary>
    public static string ContentDisposition(string torrentName)
    {
        var quoted = UploadRules.Quote(torrentName);
        return $"inline; filename=\"{quoted}\"; filename*=UTF-8''{quoted}";
    }

    private static async Task<IResult> DownloadAsync(
        int id, HttpContext context, MoguDbContext db, CurrentUser currentUser, TorrentFileService files, CancellationToken cancellationToken)
    {
        var torrent = await db.Torrents.AsNoTracking()
            .Include(t => t.Trackers).ThenInclude(tt => tt.Tracker)
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (torrent is null || !torrent.HasTorrent || (torrent.IsDeleted && !currentUser.IsModerator))
        {
            return Results.NotFound();
        }

        var data = await files.CreateAsync(torrent, context.Request.AbsoluteUrl($"/view/{torrent.Id}"), cancellationToken);
        if (data is null)
        {
            return Results.NotFound();
        }

        context.Response.Headers.ContentDisposition = ContentDisposition(torrent.TorrentName);
        return Results.Bytes(data, "application/x-bittorrent");
    }

    private static async Task<IResult> MagnetAsync(int id, MoguDbContext db, CurrentUser currentUser, TorrentLinks links, CancellationToken cancellationToken)
    {
        var torrent = await db.Torrents.AsNoTracking()
            .Where(t => t.Id == id)
            .Select(t => new { t.DisplayName, t.InfoHash, t.IsDeleted })
            .FirstOrDefaultAsync(cancellationToken);
        if (torrent is null || (torrent.IsDeleted && !currentUser.IsModerator))
        {
            return Results.NotFound();
        }

        return Results.Redirect(links.Magnet(torrent.DisplayName, torrent.InfoHash));
    }
}
