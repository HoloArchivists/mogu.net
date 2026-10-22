using Mogu.Web.Api;
using Mogu.Web.Torrents;

namespace Mogu.Web.Infrastructure;

/// <summary>Registration points for application services and non-Razor endpoints.</summary>
public static class ServiceRegistration
{
    public static IServiceCollection AddMoguServices(this IServiceCollection services)
    {
        // Torrent pipeline: magnet links, .torrent downloads, uploads and the API.
        services.AddSingleton<TorrentLinks>();
        services.AddScoped<TorrentFileService>();
        services.AddScoped<TorrentUploadService>();
        services.AddScoped<ApiTokenService>();

        return services;
    }

    public static IEndpointRouteBuilder MapMoguEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapDownloadEndpoints();
        endpoints.MapApiEndpoints();

        return endpoints;
    }
}
