using Microsoft.Extensions.Options;
using Mogu.Core.Options;

namespace Mogu.Web.Tracker;

/// <summary>Works out which announce URL generated .torrent files and magnet links should lead with.</summary>
public static class TrackerAnnounceUrl
{
    /// <summary>
    /// <see cref="MoguOptions.MainAnnounceUrl"/> when configured; otherwise this site's built-in tracker
    /// (<see cref="TrackerOptions.AnnounceUrl"/>, or <c>&lt;request base&gt;/announce</c>) when it is enabled; else null.
    /// </summary>
    public static string? GetMainAnnounceUrl(HttpRequest request, MoguOptions mogu, TrackerOptions tracker)
    {
        if (!string.IsNullOrWhiteSpace(mogu.MainAnnounceUrl))
        {
            return mogu.MainAnnounceUrl;
        }

        if (!tracker.Enabled)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(tracker.AnnounceUrl)
            ? $"{request.Scheme}://{request.Host}{request.PathBase}/announce"
            : tracker.AnnounceUrl;
    }

    public static string? GetMainAnnounceUrl(this HttpContext context) =>
        GetMainAnnounceUrl(
            context.Request,
            context.RequestServices.GetRequiredService<IOptions<MoguOptions>>().Value,
            context.RequestServices.GetRequiredService<IOptions<TrackerOptions>>().Value);
}
