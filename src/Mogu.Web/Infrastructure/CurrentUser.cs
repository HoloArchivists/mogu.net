using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Mogu.Core.Data;
using Mogu.Core.Entities;

namespace Mogu.Web.Infrastructure;

/// <summary>The logged in <see cref="User"/> for the current request, loaded once by <see cref="CurrentUserMiddleware"/>.</summary>
public sealed class CurrentUser
{
    public User? User { get; internal set; }

    public bool IsAuthenticated => User is not null;

    public bool IsTrusted => User?.IsTrusted == true;

    public bool IsModerator => User?.IsModerator == true;

    public bool IsAdministrator => User?.IsAdministrator == true;

    public int? Id => User?.Id;
}

/// <summary>
/// Resolves the cookie principal to a database user. Users that vanished or are no longer
/// active get signed out, and the last seen IP is kept up to date.
/// </summary>
public sealed class CurrentUserMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, CurrentUser currentUser, MoguDbContext db, Microsoft.Extensions.Options.IOptions<Core.Options.MoguOptions> options)
    {
        if (context.User.Identity?.IsAuthenticated == true
            && int.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, context.RequestAborted);
            if (user is null || user.Status != UserStatus.Active)
            {
                await context.SignOutAsync();
                context.User = new ClaimsPrincipal(new ClaimsIdentity());
            }
            else
            {
                currentUser.User = user;
                var ip = context.GetClientIp();
                if (!options.Value.MaintenanceMode && ip is not null && user.LastLoginIp != ip)
                {
                    user.LastLoginIp = ip;
                    await db.SaveChangesAsync(context.RequestAborted);
                }
            }
        }

        await next(context);
    }
}

public static class HttpContextExtensions
{
    /// <summary>Client IP in canonical form (IPv4-mapped addresses are unwrapped). Honours forwarded headers.</summary>
    public static string? GetClientIp(this HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null)
        {
            return null;
        }

        return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    }

    public static CurrentUser CurrentUser(this HttpContext context) =>
        context.RequestServices.GetRequiredService<CurrentUser>();
}
