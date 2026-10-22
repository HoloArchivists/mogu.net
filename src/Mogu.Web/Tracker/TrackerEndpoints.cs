using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mogu.Core.Data;
using Mogu.Core.Options;

namespace Mogu.Web.Tracker;

public static class TrackerEndpoints
{
    public const string UnregisteredTorrent = "Unregistered torrent";

    public static IServiceCollection AddMoguTracker(this IServiceCollection services)
    {
        services.AddOptions<TrackerOptions>()
            .BindConfiguration(TrackerOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<ITrackerPeerStore, InMemoryTrackerPeerStore>();
        services.AddSingleton<TrackerWhitelist>();
        services.AddSingleton<ITrackerWhitelist>(sp => sp.GetRequiredService<TrackerWhitelist>());
        services.AddSingleton<TrackerStatsWriter>();
        services.AddHostedService<TrackerWhitelistSyncService>();
        services.AddHostedService<TrackerPeerPruningService>();
        services.AddHostedService<TrackerStatsFlushService>();
        return services;
    }

    /// <summary>
    /// Maps <c>GET /announce</c> and <c>GET /scrape</c>. They're anonymous GETs with bencoded bodies: no antiforgery,
    /// no auth, not output cached. When the tracker is disabled they answer 404.
    /// </summary>
    public static IEndpointRouteBuilder MapTrackerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/announce", AnnounceAsync).ExcludeFromDescription().AllowAnonymous();
        endpoints.MapGet("/scrape", ScrapeAsync).ExcludeFromDescription().AllowAnonymous();
        return endpoints;
    }

    internal static async Task<IResult> AnnounceAsync(
        HttpContext context,
        IOptions<TrackerOptions> options,
        ITrackerWhitelist whitelist,
        ITrackerPeerStore peerStore)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return Results.NotFound();
        }

        var clientAddress = context.Connection.RemoteIpAddress;
        if (clientAddress is null)
        {
            return Failure(context, "Unable to determine client address");
        }

        if (!TrackerQueryParser.TryParseAnnounce(context.Request.QueryString.Value, clientAddress, settings, out var request, out var failure))
        {
            return Failure(context, failure);
        }

        if (!settings.AllowUnregisteredTorrents && !await whitelist.IsAllowedAsync(request.InfoHash, context.RequestAborted))
        {
            return Failure(context, UnregisteredTorrent);
        }

        var result = peerStore.Announce(request);
        return Bencoded(context, TrackerResponse.Announce(result, request.Compact, request.NoPeerId));
    }

    internal static async Task<IResult> ScrapeAsync(
        HttpContext context,
        IOptions<TrackerOptions> options,
        ITrackerPeerStore peerStore,
        MoguDbContext db)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return Results.NotFound();
        }

        if (!TrackerQueryParser.TryParseScrape(context.Request.QueryString.Value, settings, out var infoHashes, out var failure))
        {
            return Failure(context, failure);
        }

        var requested = infoHashes.DistinctBy(Convert.ToHexStringLower).ToList();
        var downloads = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var infoHash in requested)
        {
            // One indexed lookup per hash; scrapes rarely carry more than a few.
            var torrent = await db.Torrents.AsNoTracking()
                .Where(t => t.InfoHash == infoHash && !t.IsBanned)
                .Select(t => new { t.DownloadCount })
                .FirstOrDefaultAsync(context.RequestAborted);
            if (torrent is not null)
            {
                downloads[Convert.ToHexStringLower(infoHash)] = torrent.DownloadCount;
            }
        }

        var entries = new List<TrackerScrapeEntry>(requested.Count);
        foreach (var infoHash in requested)
        {
            var hex = Convert.ToHexStringLower(infoHash);
            var known = downloads.TryGetValue(hex, out var stored);
            if (!known && !settings.AllowUnregisteredTorrents)
            {
                continue;
            }

            var stats = peerStore.GetStats(hex);
            entries.Add(new TrackerScrapeEntry(infoHash, stats.Seeders, stats.Leechers, (long)stored + stats.PendingDownloads));
        }

        return Bencoded(context, TrackerResponse.Scrape(entries));
    }

    private static IResult Failure(HttpContext context, string reason) =>
        Bencoded(context, TrackerResponse.Failure(reason));

    private static IResult Bencoded(HttpContext context, byte[] body)
    {
        context.Response.Headers.CacheControl = "no-store";
        return Results.Bytes(body, TrackerResponse.ContentType);
    }
}
