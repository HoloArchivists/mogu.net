using Mogu.Web.Services;

namespace Mogu.Web.Infrastructure;

/// <summary>Registration points for application services and non-Razor endpoints.</summary>
public static class ServiceRegistration
{
    public static IServiceCollection AddMoguServices(this IServiceCollection services)
    {
        // Torrent page, comments, moderation (WP3).
        services.AddMemoryCache();
        services.AddScoped<ModerationService>();
        services.AddScoped<TorrentFileListRenderer>();

        return services;
    }

    public static IEndpointRouteBuilder MapMoguEndpoints(this IEndpointRouteBuilder endpoints)
    {
        return endpoints;
    }
}
