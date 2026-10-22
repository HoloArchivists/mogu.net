using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mogu.Core.Data;
using Mogu.Core.Options;

namespace Mogu.Web.Infrastructure;

/// <summary>Rejects writes while in maintenance mode and from banned IPs.</summary>
public sealed class RequestGuardMiddleware(RequestDelegate next, IOptionsMonitor<MoguOptions> options)
{
    public async Task InvokeAsync(HttpContext context, MoguDbContext db, ITempDataDictionaryFactory tempData)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await next(context);
            return;
        }

        var settings = options.CurrentValue;
        if (settings.MaintenanceMode && !(settings.MaintenanceModeLogins && IsLoginRequest(context)))
        {
            const string message = "Site is currently in maintenance mode.";
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                await context.Response.WriteAsync(message);
                return;
            }

            tempData.GetTempData(context).AddFlash(FlashLevel.Danger, message);
            var referer = context.Request.Headers.Referer.ToString();
            context.Response.Redirect(Uri.TryCreate(referer, UriKind.Absolute, out var uri) && uri.Host == context.Request.Host.Host
                ? referer
                : "/");
            return;
        }

        var ip = context.GetClientIp();
        if (ip is not null && await db.Bans.AnyAsync(b => b.UserIp == ip, context.RequestAborted))
        {
            if (context.User.Identity?.IsAuthenticated == true)
            {
                await context.SignOutAsync();
                context.Response.Redirect("/");
                return;
            }

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("You are banned.");
            return;
        }

        await next(context);
    }

    // The OAuth callback is a GET, so the only "login" write is none; kept for parity with the old config flag.
    private static bool IsLoginRequest(HttpContext context) =>
        context.Request.Path.StartsWithSegments("/login");
}
