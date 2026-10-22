using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mogu.Core.Data;
using Mogu.Core.Entities;
using Mogu.Core.Options;

namespace Mogu.Web.Infrastructure;

/// <summary>
/// Cookie sessions backed by Discord OAuth2. On every login the user's guild roles are
/// re-read and mapped to a <see cref="UserLevel"/>, so Discord stays the source of truth.
/// </summary>
public static class DiscordAuthentication
{
    public const string Scheme = "Discord";

    private const string ApiBase = "https://discord.com/api/";

    public static IServiceCollection AddMoguAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var discord = configuration.GetSection(DiscordOptions.SectionName).Get<DiscordOptions>() ?? new DiscordOptions();

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "mogu_session";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromDays(7);
                options.SlidingExpiration = true;
                options.LoginPath = "/login";
                options.LogoutPath = "/logout";
                options.AccessDeniedPath = "/Error/403";
                options.ReturnUrlParameter = "next";
            })
            .AddOAuth(Scheme, options =>
            {
                options.ClientId = discord.ClientId;
                options.ClientSecret = discord.ClientSecret;
                options.CallbackPath = discord.CallbackPath;
                options.AuthorizationEndpoint = "https://discord.com/oauth2/authorize";
                options.TokenEndpoint = ApiBase + "oauth2/token";
                options.UserInformationEndpoint = ApiBase + "users/@me";
                options.UsePkce = true;
                options.SaveTokens = false;
                options.Scope.Add("identify");
                options.Scope.Add("guilds.members.read");
                options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.Events = new OAuthEvents
                {
                    OnCreatingTicket = OnCreatingTicketAsync,
                    OnRemoteFailure = context =>
                    {
                        var message = context.Failure?.Message ?? "Login failed.";
                        context.Response.Redirect("/Error/login?message=" + Uri.EscapeDataString(message));
                        context.HandleResponse();
                        return Task.CompletedTask;
                    },
                };
            });

        return services;
    }

    private static async Task OnCreatingTicketAsync(OAuthCreatingTicketContext context)
    {
        var services = context.HttpContext.RequestServices;
        var discord = services.GetRequiredService<IOptions<DiscordOptions>>().Value;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DiscordAuthentication));

        using var me = await GetJsonAsync(context, context.Options.UserInformationEndpoint);
        if (me is null)
        {
            throw new AuthenticationFailureException("Failed to get user data from Discord.");
        }

        var discordId = long.Parse(me.RootElement.GetProperty("id").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        var username = me.RootElement.GetProperty("username").GetString()!;

        // 404s when the user isn't in the guild.
        using var member = await GetJsonAsync(context, $"{ApiBase}users/@me/guilds/{discord.GuildId}/member");
        if (member is null)
        {
            throw new AuthenticationFailureException("Please join the discord server in the Help page and try again.");
        }

        var roles = member.RootElement.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!).ToHashSet();
        if (!roles.Overlaps(discord.LoginRoles))
        {
            throw new AuthenticationFailureException("You do not have the sufficient permissions to login. Contact an admin for more info.");
        }

        var level = roles.Overlaps(discord.AdminRoles) ? UserLevel.Administrator
            : roles.Overlaps(discord.ModeratorRoles) ? UserLevel.Moderator
            : roles.Overlaps(discord.TrustedRoles) ? UserLevel.Trusted
            : UserLevel.Regular;

        var db = services.GetRequiredService<MoguDbContext>();
        var time = services.GetRequiredService<TimeProvider>();
        var now = time.GetUtcNow().UtcDateTime;
        var ip = context.HttpContext.GetClientIp();

        var user = await db.Users.FirstOrDefaultAsync(u => u.DiscordId == discordId, context.HttpContext.RequestAborted);
        if (user is null)
        {
            user = new User
            {
                DiscordId = discordId,
                Username = await UniqueUsernameAsync(db, username, context.HttpContext.RequestAborted),
                CreatedAt = now,
                RegistrationIp = ip,
            };
            db.Users.Add(user);
        }

        if (user.IsBanned)
        {
            throw new AuthenticationFailureException("Your account is banned.");
        }

        user.Status = UserStatus.Active;
        user.Level = level;
        user.LastLoginAt = now;
        user.LastLoginIp = ip;
        await db.SaveChangesAsync(context.HttpContext.RequestAborted);

        logger.LogInformation("User {UserId} ({Username}) logged in as {Level}", user.Id, user.Username, level);

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Name, user.Username),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);
        context.Principal = new ClaimsPrincipal(identity);
        context.Properties.IsPersistent = true;
    }

    private static async Task<JsonDocument?> GetJsonAsync(OAuthCreatingTicketContext context, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(context.HttpContext.RequestAborted));
    }

    /// <summary>Discord names can collide with existing (legacy) usernames, so suffix a number if needed.</summary>
    private static async Task<string> UniqueUsernameAsync(MoguDbContext db, string username, CancellationToken cancellationToken)
    {
        var candidate = username.Length > 32 ? username[..32] : username;
        for (var i = 2; await db.Users.AnyAsync(u => u.Username == candidate, cancellationToken); i++)
        {
            var suffix = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            candidate = (username.Length + suffix.Length > 32 ? username[..(32 - suffix.Length)] : username) + suffix;
        }

        return candidate;
    }
}
