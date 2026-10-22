using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mogu.Core.Data;
using Mogu.Core.Entities;
using Mogu.Web.Pages;

namespace Mogu.Tests.Browsing;

/// <remarks>Uses <see cref="IndexModel"/> as the assembly marker since LegacyImport also has a <c>Program</c>.</remarks>
internal sealed class BrowsingFactory : WebApplicationFactory<IndexModel>
{
    public TempDatabase Database { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Mogu", Database.ConnectionString);
        builder.UseSetting("Mogu:DataDirectory", Database.Directory);
        builder.UseSetting("Mogu:MaxPages", "100");
    }

    public HttpClient CreateBrowser() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public async Task<HttpClient> LoginAsync(string username, UserLevel level)
    {
        var client = CreateBrowser();
        var response = await client.GetAsync(new Uri($"/dev/login/{username}?level={level}", UriKind.Relative));
        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    public async Task<T> WithDbAsync<T>(Func<MoguDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<MoguDbContext>());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            Database.Dispose();
        }
    }
}

[TestClass]
public sealed partial class BrowsingIntegrationTests : IDisposable
{
    private BrowsingFactory factory = null!;

    [TestInitialize]
    public void Initialize() => factory = new BrowsingFactory();

    public void Dispose() => factory.Dispose();

    private Task<Torrent> AddTorrentAsync(string name, User? uploader = null) => factory.WithDbAsync(async db =>
    {
        if (uploader is not null)
        {
            db.Attach(uploader);
        }

        var torrent = Seed.Torrent(name, uploader);
        db.Torrents.Add(torrent);
        await db.SaveChangesAsync();
        return torrent;
    });

    private Task<User> AddUserAsync(string name, UserLevel level = UserLevel.Regular) => factory.WithDbAsync(async db =>
    {
        var user = Seed.User(name, level);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    });

    private static Uri Url(string path) => new(path, UriKind.Relative);

    [TestMethod]
    public async Task HexInfoHashRedirectsToTorrentWithFlash()
    {
        var torrent = await AddTorrentAsync("Hash me");
        var client = factory.CreateBrowser();

        var response = await client.GetAsync(Url("/?q=" + Convert.ToHexString(torrent.InfoHash)));

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.AreEqual($"/view/{torrent.Id}", response.Headers.Location?.OriginalString);
        var next = await client.GetStringAsync(Url("/"));
        StringAssert.Contains(next, IndexModel.HashRedirectMessage);
    }

    [TestMethod]
    public async Task Base32InfoHashRedirectsToTorrent()
    {
        var torrent = await AddTorrentAsync("Hash me too");
        var client = factory.CreateBrowser();

        var response = await client.GetAsync(Url("/?q=" + Base32(torrent.InfoHash).ToLowerInvariant()));

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.AreEqual($"/view/{torrent.Id}", response.Headers.Location?.OriginalString);
    }

    [TestMethod]
    public async Task UnknownHashIsANormalSearch()
    {
        await AddTorrentAsync("Something");
        var response = await factory.CreateBrowser().GetAsync(Url("/?q=" + new string('a', 40)));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "No results found");
    }

    [TestMethod]
    public async Task ListingRendersRowsAndSortLinks()
    {
        var torrent = await AddTorrentAsync("Pekora <Karaoke>");
        var html = await factory.CreateBrowser().GetStringAsync(Url("/?s=size&o=desc"));

        StringAssert.Contains(html, $"<a href=\"/view/{torrent.Id}\" title=\"Pekora &lt;Karaoke&gt;\">Pekora &lt;Karaoke&gt;</a>");
        StringAssert.Contains(html, "class=\"hdr-size sorting_desc text-center\"");
        StringAssert.Contains(html, "href=\"/?s=size&amp;o=asc\"");
        StringAssert.Contains(html, $"href=\"/download/{torrent.Id}.torrent\"");
        StringAssert.Contains(html, "magnet:?xt=urn:btih:" + torrent.InfoHashHex + "&amp;dn=Pekora%20%3CKaraoke%3E&amp;tr=udp%3A%2F%2Ftracker.holopirates.moe");
        StringAssert.Contains(html, "<title>Browse :: Mogu</title>");
    }

    [TestMethod]
    public async Task FirstWordUserShowsHint()
    {
        await AddUserAsync("pekora");
        var html = await factory.CreateBrowser().GetStringAsync(Url("/?q=pekora+karaoke&f=1"));
        StringAssert.Contains(html, "<a href=\"/user/pekora?f=1&amp;q=karaoke\">Click here to see only results uploaded by pekora</a>");
    }

    [TestMethod]
    [DataRow("/?s=bogus", HttpStatusCode.BadRequest)]
    [DataRow("/?c=1-2", HttpStatusCode.BadRequest)]
    [DataRow("/?f=7", HttpStatusCode.BadRequest)]
    [DataRow("/?u=nobody", HttpStatusCode.NotFound)]
    [DataRow("/?p=2", HttpStatusCode.NotFound)]
    [DataRow("/?p=101", HttpStatusCode.Forbidden)]
    [DataRow("/user/nobody", HttpStatusCode.NotFound)]
    [DataRow("/user/nobody/comments", HttpStatusCode.NotFound)]
    [DataRow("/rules", HttpStatusCode.OK)]
    [DataRow("/help", HttpStatusCode.OK)]
    [DataRow("/trusted", HttpStatusCode.OK)]
    [DataRow("/xmlns/nyaa", HttpStatusCode.OK)]
    [DataRow("/rss", HttpStatusCode.OK)]
    [DataRow("/?p=abc", HttpStatusCode.OK)]
    public async Task StatusCodes(string path, HttpStatusCode expected)
    {
        var response = await factory.CreateBrowser().GetAsync(Url(path));
        Assert.AreEqual(expected, response.StatusCode);
    }

    [TestMethod]
    public async Task MaxPagesReturnsPlainTextMessage()
    {
        var response = await factory.CreateBrowser().GetAsync(Url("/?p=500"));
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.AreEqual(Mogu.Web.Services.TorrentSearch.MaxPagesMessage, await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task TrustedPageHasNoRequestButton()
    {
        var html = await factory.CreateBrowser().GetStringAsync(Url("/trusted"));
        Assert.DoesNotContain("Request Trusted Status", html);
    }

    [TestMethod]
    public async Task RssIsWellFormedWithNyaaNamespace()
    {
        var torrent = await AddTorrentAsync("Bad \u0001 chars & <tags> ]]> end");
        await AddTorrentAsync("Second");

        var response = await factory.CreateBrowser().GetAsync(Url("/?page=rss"));

        Assert.AreEqual("application/xml", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(TimeSpan.FromSeconds(300), response.Headers.CacheControl?.MaxAge);
        var document = XDocument.Parse(await response.Content.ReadAsStringAsync());
        XNamespace nyaa = "http://localhost/xmlns/nyaa";
        var items = document.Root!.Element("channel")!.Elements("item").ToList();
        Assert.HasCount(2, items);
        Assert.AreEqual("Mogu - Home - Torrent File RSS", document.Root.Element("channel")!.Element("title")!.Value);

        var item = items.Single(i => i.Element("guid")!.Value == $"http://localhost/view/{torrent.Id}");
        Assert.AreEqual("Bad  chars & <tags> ]]> end", item.Element("title")!.Value);
        Assert.AreEqual($"http://localhost/download/{torrent.Id}.torrent", item.Element("link")!.Value);
        Assert.AreEqual(torrent.InfoHashHex, item.Element(nyaa + "infoHash")!.Value);
        Assert.AreEqual("1_1", item.Element(nyaa + "categoryId")!.Value);
        Assert.AreEqual("Unarchived - Karaoke", item.Element(nyaa + "category")!.Value);
        Assert.AreEqual("No", item.Element(nyaa + "trusted")!.Value);
        Assert.AreEqual("1000 Bytes", item.Element(nyaa + "size")!.Value);
        StringAssert.Contains(item.Element("description")!.Value, $"#{torrent.Id} | Bad  chars &amp; &lt;tags&gt; ]]&gt; end</a>");
        StringAssert.Contains(item.Element("description")!.Value, torrent.InfoHashHex.ToUpperInvariant());
    }

    [TestMethod]
    public async Task RssMagnetLinks()
    {
        var torrent = await AddTorrentAsync("Magnet");
        var xml = XDocument.Parse(await factory.CreateBrowser().GetStringAsync(Url("/rss?magnets")));
        var link = xml.Descendants("item").Single().Element("link")!.Value;
        Assert.StartsWith("magnet:?xt=urn:btih:" + torrent.InfoHashHex + "&dn=Magnet&tr=", link);
        StringAssert.Contains(xml.Root!.Element("channel")!.Element("title")!.Value, "Magnet URI RSS");
    }

    [TestMethod]
    public async Task UserPageModeratorPanelOnlyForModerators()
    {
        var alice = await AddUserAsync("alice");
        await AddTorrentAsync("alice upload", alice);

        var anonymous = await factory.CreateBrowser().GetStringAsync(Url("/user/alice"));
        Assert.DoesNotContain("User Information", anonymous);
        StringAssert.Contains(anonymous, "alice upload");
        StringAssert.Contains(anonymous, "placeholder=\"Search alice&#x27;s torrents...\"");

        var moderator = await factory.LoginAsync("mod", UserLevel.Moderator);
        var html = await moderator.GetStringAsync(Url("/user/alice"));
        StringAssert.Contains(html, "User Information");
        StringAssert.Contains(html, "Danger Zone");
        StringAssert.Contains(html, "<option value=\"trusted\">Trusted</option>");
        Assert.DoesNotContain("value=\"moderator\"", html);
        Assert.DoesNotContain("Nuke Torrents", html);
    }

    [TestMethod]
    public async Task CommentsRequireModerator()
    {
        var alice = await AddUserAsync("alice");
        var torrent = await AddTorrentAsync("commented", alice);
        await factory.WithDbAsync(async db =>
        {
            db.Comments.Add(new Comment { TorrentId = torrent.Id, UserId = alice.Id, Text = "hello\r\nworld", CreatedAt = DateTime.UtcNow });
            return await db.SaveChangesAsync();
        });

        Assert.AreEqual(HttpStatusCode.Forbidden, (await factory.CreateBrowser().GetAsync(Url("/user/alice/comments"))).StatusCode);

        var moderator = await factory.LoginAsync("mod", UserLevel.Moderator);
        var html = await moderator.GetStringAsync(Url("/user/alice/comments"));
        StringAssert.Contains(html, "Total of 1 comments");
        StringAssert.Contains(html, "hello&#xA;world");
    }

    [TestMethod]
    public async Task BanAndUnban()
    {
        await AddUserAsync("alice");
        await factory.WithDbAsync(db => db.Users.Where(u => u.Username == "alice").ExecuteUpdateAsync(s => s.SetProperty(u => u.LastLoginIp, "10.9.8.7")));
        var moderator = await factory.LoginAsync("mod", UserLevel.Moderator);

        var missingReason = await PostAsync(moderator, "/user/alice", "/user/alice/Ban", ("ban_user", "Ban User"), ("reason", ""));
        Assert.AreEqual(HttpStatusCode.OK, missingReason.StatusCode);
        StringAssert.Contains(await missingReason.Content.ReadAsStringAsync(), "This field is required.");

        var ban = await PostAsync(moderator, "/user/alice", "/user/alice/Ban", ("ban_userip", "Ban User+IP"), ("reason", "spam"));
        Assert.AreEqual(HttpStatusCode.Redirect, ban.StatusCode);

        var (status, bans, log) = await factory.WithDbAsync(async db => (
            await db.Users.Where(u => u.Username == "alice").Select(u => u.Status).SingleAsync(),
            await db.Bans.Select(b => b.UserIp + " " + b.Reason).ToListAsync(),
            await db.AdminLog.Select(l => l.Message).ToListAsync()));
        Assert.AreEqual(UserStatus.Banned, status);
        Assert.AreEqual("10.9.8.7 spam", bans.Single());
        Assert.AreEqual("User [alice](/user/alice) IP(10.9.8.7) has been banned.", log.Single());

        var again = await PostAsync(moderator, "/user/alice", "/user/alice/Ban", ("ban_user", "Ban User"), ("reason", "x"));
        Assert.AreEqual(HttpStatusCode.Redirect, again.StatusCode);
        StringAssert.Contains(await moderator.GetStringAsync(Url("/user/alice")), Mogu.Web.Pages.Users.UserPageModel.NonsenseMessage);

        var unban = await PostAsync(moderator, "/user/alice", "/user/alice/Ban", ("unban", "Unban"));
        Assert.AreEqual(HttpStatusCode.Redirect, unban.StatusCode);
        var (statusAfter, banCount) = await factory.WithDbAsync(async db => (
            await db.Users.Where(u => u.Username == "alice").Select(u => u.Status).SingleAsync(),
            await db.Bans.CountAsync()));
        Assert.AreEqual(UserStatus.Active, statusAfter);
        Assert.AreEqual(0, banCount);
    }

    [TestMethod]
    public async Task UserClassModeratorCanGrantTrustedButNotModerator()
    {
        await AddUserAsync("alice");
        var moderator = await factory.LoginAsync("mod", UserLevel.Moderator);

        var invalid = await PostAsync(moderator, "/user/alice", "/user/alice/UserClass", ("user_class", "moderator"));
        Assert.AreEqual(HttpStatusCode.OK, invalid.StatusCode);
        StringAssert.Contains(await invalid.Content.ReadAsStringAsync(), "Not a valid choice");

        var ok = await PostAsync(moderator, "/user/alice", "/user/alice/UserClass", ("user_class", "trusted"));
        Assert.AreEqual(HttpStatusCode.Redirect, ok.StatusCode);
        var level = await factory.WithDbAsync(db => db.Users.Where(u => u.Username == "alice").Select(u => u.Level).SingleAsync());
        Assert.AreEqual(UserLevel.Trusted, level);
    }

    [TestMethod]
    public async Task UserClassForbiddenForSameLevel()
    {
        await AddUserAsync("alice");
        var moderator = await factory.LoginAsync("mod", UserLevel.Moderator);
        await factory.LoginAsync("mod2", UserLevel.Moderator);

        // A moderator can't manage a user of the same level.
        var response = await PostAsync(moderator, "/user/alice", "/user/mod2/UserClass", ("user_class", "regular"));
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        StringAssert.Contains(await moderator.GetStringAsync(Url("/user/mod2")), "User Information");
        Assert.DoesNotContain("Change User Class", await moderator.GetStringAsync(Url("/user/mod2")));
    }

    [TestMethod]
    public async Task NukeTorrentsAdministratorsOnly()
    {
        var alice = await AddUserAsync("alice");
        var torrent = await AddTorrentAsync("doomed", alice);
        var moderator = await factory.LoginAsync("mod", UserLevel.Moderator);
        var admin = await factory.LoginAsync("admin", UserLevel.Administrator);

        var forbidden = await PostAsync(moderator, "/user/alice", "/user/alice/nuke/torrents", ("nuke_torrents", "x"));
        Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var html = await admin.GetStringAsync(Url("/user/alice"));
        StringAssert.Contains(html, "formaction=\"/user/alice/nuke/torrents\"");
        var nuked = await PostAsync(admin, "/user/alice", "/user/alice/nuke/torrents", ("nuke_torrents", "x"));
        Assert.AreEqual(HttpStatusCode.Redirect, nuked.StatusCode);

        var (deleted, events, log) = await factory.WithDbAsync(async db => (
            await db.Torrents.Where(t => t.Id == torrent.Id).Select(t => t.IsDeleted && t.IsBanned).SingleAsync(),
            await db.TrackerWhitelistEvents.Where(e => e.Action == TrackerWhitelistAction.Remove).CountAsync(),
            await db.AdminLog.Select(l => l.Message).SingleAsync()));
        Assert.IsTrue(deleted);
        Assert.AreEqual(1, events);
        Assert.AreEqual("Nuked 1 torrents of [alice](/user/alice)", log);
    }

    [TestMethod]
    public async Task NukeCommentsRecountsTorrents()
    {
        var alice = await AddUserAsync("alice");
        var bob = await AddUserAsync("bob");
        var torrent = await AddTorrentAsync("talked about", bob);
        await factory.WithDbAsync(async db =>
        {
            db.Comments.AddRange(
                new Comment { TorrentId = torrent.Id, UserId = alice.Id, Text = "a", CreatedAt = DateTime.UtcNow },
                new Comment { TorrentId = torrent.Id, UserId = alice.Id, Text = "b", CreatedAt = DateTime.UtcNow },
                new Comment { TorrentId = torrent.Id, UserId = bob.Id, Text = "c", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            return await db.Torrents.Where(t => t.Id == torrent.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.CommentCount, 3));
        });
        var admin = await factory.LoginAsync("admin", UserLevel.Administrator);

        var response = await PostAsync(admin, "/user/alice", "/user/alice/nuke/comments", ("nuke_comments", "x"));

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        var (count, remaining) = await factory.WithDbAsync(async db => (
            await db.Torrents.Where(t => t.Id == torrent.Id).Select(t => t.CommentCount).SingleAsync(),
            await db.Comments.CountAsync()));
        Assert.AreEqual(1, count);
        Assert.AreEqual(1, remaining);
    }

    private static async Task<string> TokenAsync(HttpClient client, string pagePath)
    {
        var html = await client.GetStringAsync(Url(pagePath));
        return TokenPattern().Match(html).Groups[1].Value;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string pagePath, string action, params (string Key, string Value)[] fields)
    {
        var token = await TokenAsync(client, pagePath);
        var form = fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))
            .Append(new KeyValuePair<string, string>("__RequestVerificationToken", token));
        return await client.PostAsync(Url(action), new FormUrlEncodedContent(form));
    }

    private static string Base32(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                output.Append(alphabet[(buffer >> bits) & 31]);
            }
        }

        return output.ToString();
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();
}
