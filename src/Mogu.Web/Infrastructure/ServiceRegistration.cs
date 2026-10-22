using Mogu.Web.Services;
using Mogu.Web.Tracker;

namespace Mogu.Web.Infrastructure;

/// <summary>Registration points for application services and non-Razor endpoints.</summary>
public static class ServiceRegistration
{
    public static IServiceCollection AddMoguServices(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddScoped<ModerationService>();
        services.AddScoped<TorrentFileListRenderer>();
        services.AddMoguTracker();
        return services;
    }

    public static IEndpointRouteBuilder MapMoguEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapTrackerEndpoints();
        return endpoints;
    }
}
