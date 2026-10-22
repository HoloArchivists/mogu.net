using System.Net;
using System.Text;
using System.Web;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mogu.Core.Data;
using Mogu.Core.Entities;
using Mogu.Web.Infrastructure;

namespace Mogu.Tests.Auth;

/// <summary>Drives the full Discord OAuth2 round trip against a stubbed Discord API.</summary>
[TestClass]
public sealed class DiscordLoginTests
{
    private const string Guild = "4242";
    private const string LoginRole = "100";
    private const string ModeratorRole = "200";
    private const long DiscordId = 123456789012345678;

    [TestMethod]
    public async Task MemberWithLoginRoleIsSignedInAndCreated()
    {
        using var factory = new DiscordSiteFactory(new FakeDiscord(InGuild: true, Roles: [LoginRole, ModeratorRole]));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var callback = await LoginAsync(client);

        Assert.AreEqual(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.AreEqual("/upload", callback.Headers.Location?.OriginalString);
        var user = await factory.WithDbAsync(db => db.Users.SingleAsync(u => u.DiscordId == DiscordId));
        Assert.AreEqual("pekora", user.Username);
        Assert.AreEqual(UserLevel.Moderator, user.Level);

        var home = await client.GetStringAsync(new Uri("/", UriKind.Relative));
        StringAssert.Contains(home, "Logged in as pekora");
    }

    [TestMethod]
    public async Task NonMemberIsSentToErrorPageAndNotSignedIn()
    {
        using var factory = new DiscordSiteFactory(new FakeDiscord(InGuild: false, Roles: []));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var callback = await LoginAsync(client);

        Assert.AreEqual(HttpStatusCode.Redirect, callback.StatusCode);
        StringAssert.StartsWith(callback.Headers.Location?.OriginalString, "/Error/login?message=Please%20join");
        Assert.IsFalse(callback.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(c => c.StartsWith("mogu_session=", StringComparison.Ordinal) && !c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual(0, await factory.WithDbAsync(db => db.Users.CountAsync()));
    }

    [TestMethod]
    public async Task MissingLoginRoleIsRejected()
    {
        using var factory = new DiscordSiteFactory(new FakeDiscord(InGuild: true, Roles: ["999"]));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var callback = await LoginAsync(client);

        StringAssert.StartsWith(callback.Headers.Location?.OriginalString, "/Error/login?message=You%20do%20not%20have");
    }

    [TestMethod]
    public async Task BannedUserIsRejected()
    {
        using var factory = new DiscordSiteFactory(new FakeDiscord(InGuild: true, Roles: [LoginRole]));
        await factory.WithDbAsync(async db =>
        {
            db.Users.Add(new User { DiscordId = DiscordId, Username = "pekora", Status = UserStatus.Banned, CreatedAt = DateTime.UtcNow });
            return await db.SaveChangesAsync();
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var callback = await LoginAsync(client);

        StringAssert.StartsWith(callback.Headers.Location?.OriginalString, "/Error/login?message=Your%20account%20is%20banned");
    }

    /// <summary>GET /login, then hit the callback with the state Discord would echo back.</summary>
    private static async Task<HttpResponseMessage> LoginAsync(HttpClient client)
    {
        using var challenge = await client.GetAsync(new Uri("/login?next=/upload", UriKind.Relative));
        Assert.AreEqual(HttpStatusCode.Redirect, challenge.StatusCode);
        var authorize = challenge.Headers.Location!;
        StringAssert.StartsWith(authorize.ToString(), "https://discord.com/oauth2/authorize");
        var state = HttpUtility.ParseQueryString(authorize.Query)["state"];

        var request = new HttpRequestMessage(HttpMethod.Get, $"/discord/oauth2/callback?code=abc&state={Uri.EscapeDataString(state!)}");
        foreach (var cookie in challenge.Headers.GetValues("Set-Cookie"))
        {
            request.Headers.Add("Cookie", cookie.Split(';')[0]);
        }

        return await client.SendAsync(request);
    }

    private sealed record FakeDiscord(bool InGuild, string[] Roles);

    private sealed class FakeDiscordHandler(FakeDiscord discord) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var response = path switch
            {
                "/api/oauth2/token" => Json("""{"access_token":"token","token_type":"Bearer","expires_in":3600}"""),
                "/api/users/@me" => Json($$"""{"id":"{{DiscordId}}","username":"pekora"}"""),
                _ when path == $"/api/users/@me/guilds/{Guild}/member" => discord.InGuild
                    ? Json($$"""{"roles":[{{string.Join(',', discord.Roles.Select(r => $"\"{r}\""))}}]}""")
                    : new HttpResponseMessage(HttpStatusCode.NotFound),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
            return Task.FromResult(response);
        }

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed class DiscordSiteFactory(FakeDiscord discord) : WebApplicationFactory<CurrentUser>
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "mogu-tests-" + Guid.NewGuid().ToString("N"));

        public async Task<T> WithDbAsync<T>(Func<MoguDbContext, Task<T>> action)
        {
            using var scope = Services.CreateScope();
            return await action(scope.ServiceProvider.GetRequiredService<MoguDbContext>());
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(directory);
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Mogu", $"Data Source={Path.Combine(directory, "mogu.db")}");
            builder.UseSetting("Mogu:DataDirectory", directory);
            builder.UseSetting("Discord:GuildId", Guild);
            builder.UseSetting("Discord:LoginRoles:0", LoginRole);
            builder.UseSetting("Discord:ModeratorRoles:0", ModeratorRole);
            builder.ConfigureServices(services =>
                services.PostConfigure<OAuthOptions>(DiscordAuthentication.Scheme, options =>
                    options.Backchannel = new HttpClient(new FakeDiscordHandler(discord))));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
