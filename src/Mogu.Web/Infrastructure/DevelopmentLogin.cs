using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Mogu.Core.Data;
using Mogu.Core.Entities;

namespace Mogu.Web.Infrastructure;

/// <summary>
/// Development-only <c>/dev/login/{username}?level=Moderator</c>: creates the user if needed and
/// signs in without Discord. Never mapped outside the Development environment.
/// </summary>
public static class DevelopmentLogin
{
    public static IEndpointRouteBuilder MapDevelopmentLogin(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/dev/login/{username}", async (string username, UserLevel? level, HttpContext context, MoguDbContext db, TimeProvider time) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username);
            if (user is null)
            {
                user = new User
                {
                    Username = username,
                    DiscordId = Random.Shared.NextInt64(1, long.MaxValue),
                    CreatedAt = time.GetUtcNow().UtcDateTime - TimeSpan.FromDays(30),
                };
                db.Users.Add(user);
            }

            user.Level = level ?? user.Level;
            user.Status = UserStatus.Active;
            await db.SaveChangesAsync();

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, user.Id.ToString(CultureInfo.InvariantCulture)),
                    new Claim(ClaimTypes.Name, user.Username),
                ],
                CookieAuthenticationDefaults.AuthenticationScheme);
            await context.SignInAsync(new ClaimsPrincipal(identity));
            return Results.LocalRedirect("/");
        });

        return endpoints;
    }
}
