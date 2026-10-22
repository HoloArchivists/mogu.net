using Mogu.Web.Api;
using Mogu.Web.Services;
using Mogu.Web.Torrents;
using Mogu.Web.Tracking;

namespace Mogu.Web.Infrastructure;

/// <summary>Registration points for application services and non-Razor endpoints.</summary>
public static class ServiceRegistration
{
    public static IServiceCollection AddMoguServices(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddHttpContextAccessor();

        // Torrent pipeline: magnet links, .torrent downloads, uploads and the API.
        services.AddSingleton<TorrentLinks>();
        services.AddScoped<TorrentFileService>();
        services.AddScoped<TorrentUploadService>();
        services.AddScoped<ApiTokenService>();

        services.AddScoped<TorrentSearch>();
        services.AddScoped<ModerationService>();
        services.AddScoped<TorrentFileListRenderer>();
        services.AddMoguTracker();
        return services;
    }

    public static IEndpointRouteBuilder MapMoguEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapDownloadEndpoints();
        endpoints.MapApiEndpoints();
        endpoints.MapTrackerEndpoints();
        return endpoints;
    }
}
