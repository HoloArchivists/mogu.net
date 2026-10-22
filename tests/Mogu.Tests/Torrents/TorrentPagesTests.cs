using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mogu.Core.Entities;

namespace Mogu.Tests.Torrents;

[TestClass]
public sealed class TorrentPagesTests
{
    private static TorrentSiteFactory site = null!;

    [ClassInitialize]
    public static void Initialize(TestContext context) => site = new TorrentSiteFactory();

    [ClassCleanup]
    public static void Cleanup() => site.Dispose();

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    [TestMethod]
    public async Task ViewShowsTorrentAndHidesDeletedFromNonModerators()
    {
        var owner = Unique("owner");
        using var ownerClient = await site.ClientAsync(owner);
        using var anonymous = await site.ClientAsync();
        using var moderator = await site.ClientAsync(Unique("mod"), UserLevel.Moderator);
        var torrent = await site.AddTorrentAsync(owner, t => t.Information = "https://example.com/info");
        var deleted = await site.AddTorrentAsync(owner, t => t.IsDeleted = true);

        var html = await anonymous.GetStringAsync(new Uri($"/view/{torrent.Id}", UriKind.Relative));
        StringAssert.Contains(html, "<a rel=\"noopener noreferrer nofollow\" href=\"https://example.com/info\">https://example.com/info</a>");
        StringAssert.Contains(html, $"href=\"/view/{torrent.Id}/magnet\"");
        StringAssert.Contains(html, "File list is not available for this torrent.");
        Assert.IsFalse(html.Contains("fa-pencil", StringComparison.Ordinal), "anonymous can't edit");
        Assert.IsFalse(html.Contains("comment-box", StringComparison.Ordinal), "anonymous can't comment");
        StringAssert.Contains(await ownerClient.GetStringAsync(new Uri($"/view/{torrent.Id}", UriKind.Relative)), "fa-pencil");

        Assert.AreEqual(HttpStatusCode.NotFound, (await anonymous.GetAsync(new Uri($"/view/{deleted.Id}", UriKind.Relative))).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await ownerClient.GetAsync(new Uri($"/view/{deleted.Id}", UriKind.Relative))).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await moderator.GetAsync(new Uri($"/view/{deleted.Id}", UriKind.Relative))).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await anonymous.GetAsync(new Uri("/view/999999", UriKind.Relative))).StatusCode);
    }

    [TestMethod]
    public async Task PostingCommentsUpdatesCountAndRespectsLock()
    {
        var user = Unique("commenter");
        using var client = await site.ClientAsync(user);
        var torrent = await site.AddTorrentAsync(null);
        var url = $"/view/{torrent.Id}";

        var token = await TorrentSiteFactory.TokenAsync(client, url);
        using var invalid = await TorrentSiteFactory.PostAsync(client, url, token, ("comment", "ab"));
        Assert.AreEqual(HttpStatusCode.OK, invalid.StatusCode);
        StringAssert.Contains(await invalid.Content.ReadAsStringAsync(), "Comment must be at least 3 characters long and 2048 at most.");

        using var empty = await TorrentSiteFactory.PostAsync(client, url, token, ("comment", "   "));
        StringAssert.Contains(await empty.Content.ReadAsStringAsync(), "Comment must not be empty.");

        using var first = await TorrentSiteFactory.PostAsync(client, url, token, ("comment", "first <b>comment</b>"));
        Assert.AreEqual(HttpStatusCode.Redirect, first.StatusCode);
        Assert.AreEqual($"{url}#com-1", first.Headers.Location!.OriginalString);
        using var second = await TorrentSiteFactory.PostAsync(client, url, token, ("comment", "second comment"));
        Assert.AreEqual($"{url}#com-2", second.Headers.Location!.OriginalString);
        Assert.AreEqual(2, await site.WithDbAsync(db => db.Torrents.Where(t => t.Id == torrent.Id).Select(t => t.CommentCount).SingleAsync()));

        var html = await client.GetStringAsync(new Uri(url, UriKind.Relative));
        StringAssert.Contains(html, "Comment successfully posted.");
        StringAssert.Contains(html, "id=\"com-2\"");
        StringAssert.Contains(html, "first &lt;b&gt;comment&lt;/b&gt;");
        StringAssert.Contains(html, "class=\"edit-comment-box\"");
        StringAssert.Contains(html, "data-until=\"");

        await site.WithDbAsync(db => db.Torrents.Where(t => t.Id == torrent.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.IsCommentLocked, true)));
        using var locked = await TorrentSiteFactory.PostAsync(client, url, token, ("comment", "locked out"));
        Assert.AreEqual(HttpStatusCode.Forbidden, locked.StatusCode);
        StringAssert.Contains(await client.GetStringAsync(new Uri(url, UriKind.Relative)), "Comments have been locked.");

        using var moderator = await site.ClientAsync(Unique("mod"), UserLevel.Moderator);
        var modToken = await TorrentSiteFactory.TokenAsync(moderator, url);
        using var modPost = await TorrentSiteFactory.PostAsync(moderator, url, modToken, ("comment", "moderators may still comment"));
        Assert.AreEqual($"{url}#com-3", modPost.Headers.Location!.OriginalString);
    }

    [TestMethod]
    public async Task CommentRequiresAntiforgeryToken()
    {
        using var client = await site.ClientAsync(Unique("noforgery"));
        var torrent = await site.AddTorrentAsync(null);

        using var response = await client.PostAsync(
            new Uri($"/view/{torrent.Id}", UriKind.Relative),
            new FormUrlEncodedContent([KeyValuePair.Create("comment", "no token here")]));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task EditingCommentsReturnsJson()
    {
        var author = Unique("author");
        using var client = await site.ClientAsync(author);
        using var other = await site.ClientAsync(Unique("other"));
        var torrent = await site.AddTorrentAsync(null);
        var authorId = await site.UserIdAsync(author);
        var (fresh, old) = await site.WithDbAsync(async db =>
        {
            var a = new Comment { TorrentId = torrent.Id, UserId = authorId, Text = "fresh", CreatedAt = DateTime.UtcNow };
            var b = new Comment { TorrentId = torrent.Id, UserId = authorId, Text = "old", CreatedAt = DateTime.UtcNow.AddHours(-1) };
            db.Comments.AddRange(a, b);
            await db.SaveChangesAsync();
            return (a.Id, b.Id);
        });
        var token = await TorrentSiteFactory.TokenAsync(client, $"/view/{torrent.Id}");

        using var ok = await TorrentSiteFactory.PostAsync(client, $"/view/{torrent.Id}/comment/{fresh}/edit", token, ("comment", "edited **text**"));
        Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode);
        Assert.AreEqual("edited **text**", JsonDocument.Parse(await ok.Content.ReadAsStringAsync()).RootElement.GetProperty("comment").GetString());
        var edited = await site.WithDbAsync(db => db.Comments.SingleAsync(c => c.Id == fresh));
        Assert.AreEqual("edited **text**", edited.Text);
        Assert.IsNotNull(edited.EditedAt);

        using var invalid = await TorrentSiteFactory.PostAsync(client, $"/view/{torrent.Id}/comment/{fresh}/edit", token, ("comment", "x"));
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.AreEqual(
            "Comment must be at least 3 characters long and 2048 at most.",
            JsonDocument.Parse(await invalid.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString());

        using var expired = await TorrentSiteFactory.PostAsync(client, $"/view/{torrent.Id}/comment/{old}/edit", token, ("comment", "too late"));
        Assert.AreEqual(HttpStatusCode.BadRequest, expired.StatusCode);
        Assert.AreEqual("Editing time limit exceeded.", JsonDocument.Parse(await expired.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString());

        var otherToken = await TorrentSiteFactory.TokenAsync(other, $"/view/{torrent.Id}");
        using var forbidden = await TorrentSiteFactory.PostAsync(other, $"/view/{torrent.Id}/comment/{fresh}/edit", otherToken, ("comment", "not mine"));
        Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);

        using var missing = await TorrentSiteFactory.PostAsync(client, $"/view/{torrent.Id}/comment/999999/edit", token, ("comment", "where"));
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [TestMethod]
    public async Task DeletingComments()
    {
        var author = Unique("author");
        var adminName = Unique("admin");
        using var client = await site.ClientAsync(author);
        using var moderator = await site.ClientAsync(Unique("mod"), UserLevel.Moderator);
        using var admin = await site.ClientAsync(adminName, UserLevel.Administrator);
        var torrent = await site.AddTorrentAsync(null);
        var authorId = await site.UserIdAsync(author);
        var ids = await site.WithDbAsync(async db =>
        {
            var comments = Enumerable.Range(0, 3).Select(i => new Comment { TorrentId = torrent.Id, UserId = authorId, Text = $"c{i}", CreatedAt = DateTime.UtcNow }).ToList();
            db.Comments.AddRange(comments);
            await db.SaveChangesAsync();
            return comments.Select(c => c.Id).ToList();
        });

        var modToken = await TorrentSiteFactory.TokenAsync(moderator, $"/view/{torrent.Id}");
        using var modDelete = await TorrentSiteFactory.PostAsync(moderator, $"/view/{torrent.Id}/comment/{ids[0]}/delete", modToken);
        Assert.AreEqual(HttpStatusCode.Forbidden, modDelete.StatusCode, "only authors and administrators delete comments");

        var token = await TorrentSiteFactory.TokenAsync(client, $"/view/{torrent.Id}");
        using var own = await TorrentSiteFactory.PostAsync(client, $"/view/{torrent.Id}/comment/{ids[0]}/delete", token);
        Assert.AreEqual(HttpStatusCode.Redirect, own.StatusCode);
        Assert.AreEqual($"/view/{torrent.Id}", own.Headers.Location!.OriginalString);

        var other = await site.AddTorrentAsync(null);
        using var wrongTorrent = await TorrentSiteFactory.PostAsync(client, $"/view/{other.Id}/comment/{ids[1]}/delete", token);
        Assert.AreEqual(HttpStatusCode.BadRequest, wrongTorrent.StatusCode);

        var adminToken = await TorrentSiteFactory.TokenAsync(admin, $"/view/{torrent.Id}");
        using var byAdmin = await TorrentSiteFactory.PostAsync(admin, $"/view/{torrent.Id}/comment/{ids[1]}/delete", adminToken);
        Assert.AreEqual(HttpStatusCode.Redirect, byAdmin.StatusCode);

        var (count, log) = await site.WithDbAsync(async db => (
            await db.Torrents.Where(t => t.Id == torrent.Id).Select(t => t.CommentCount).SingleAsync(),
            await db.AdminLog.Where(l => l.Admin.Username == adminName).Select(l => l.Message).ToListAsync()));
        Assert.AreEqual(1, count);
        CollectionAssert.AreEqual(new[] { $"Comment deleted on torrent [#{torrent.Id}](/view/{torrent.Id})" }, log);
    }

    [TestMethod]
    public async Task ReportingRequiresAccountAge()
    {
        var reporter = Unique("reporter");
        using var client = await site.ClientAsync(reporter);
        var torrent = await site.AddTorrentAsync(null);
        var url = $"/view/{torrent.Id}";
        var token = await TorrentSiteFactory.TokenAsync(client, url);
        StringAssert.Contains(await client.GetStringAsync(new Uri(url, UriKind.Relative)), "id=\"reportModal\"");

        using var ok = await TorrentSiteFactory.PostAsync(client, $"{url}/submit_report", token, ("reason", "this is spam"));
        Assert.AreEqual(HttpStatusCode.Redirect, ok.StatusCode);
        using var empty = await TorrentSiteFactory.PostAsync(client, $"{url}/submit_report", token, ("reason", ""));
        Assert.AreEqual(HttpStatusCode.Redirect, empty.StatusCode);
        var html = await client.GetStringAsync(new Uri(url, UriKind.Relative));
        StringAssert.Contains(html, "Successfully reported torrent!");
        StringAssert.Contains(html, "Please give a report reason!");
        Assert.AreEqual(1, await site.WithDbAsync(db => db.Reports.CountAsync(r => r.TorrentId == torrent.Id)));

        var newbie = Unique("newbie");
        using var young = await site.ClientAsync(newbie);
        await site.WithDbAsync(db => db.Users.Where(u => u.Username == newbie).ExecuteUpdateAsync(s => s.SetProperty(u => u.CreatedAt, DateTime.UtcNow)));
        var youngToken = await TorrentSiteFactory.TokenAsync(young, url);
        Assert.IsFalse((await young.GetStringAsync(new Uri(url, UriKind.Relative))).Contains("reportModal", StringComparison.Ordinal));
        using var denied = await TorrentSiteFactory.PostAsync(young, $"{url}/submit_report", youngToken, ("reason", "this is spam"));
        Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [TestMethod]
    public async Task EditPageAuthorization()
    {
        var owner = Unique("owner");
        using var ownerClient = await site.ClientAsync(owner);
        using var stranger = await site.ClientAsync(Unique("stranger"));
        using var anonymous = await site.ClientAsync();
        using var moderator = await site.ClientAsync(Unique("mod"), UserLevel.Moderator);
        var torrent = await site.AddTorrentAsync(owner);
        var deleted = await site.AddTorrentAsync(owner, t => t.IsDeleted = true);

        async Task<HttpStatusCode> Status(HttpClient client, int id) =>
            (await client.GetAsync(new Uri($"/view/{id}/edit", UriKind.Relative))).StatusCode;

        Assert.AreEqual(HttpStatusCode.Forbidden, await Status(anonymous, torrent.Id));
        Assert.AreEqual(HttpStatusCode.Forbidden, await Status(stranger, torrent.Id));
        Assert.AreEqual(HttpStatusCode.OK, await Status(ownerClient, torrent.Id));
        Assert.AreEqual(HttpStatusCode.OK, await Status(moderator, torrent.Id));
        Assert.AreEqual(HttpStatusCode.NotFound, await Status(ownerClient, deleted.Id));
        Assert.AreEqual(HttpStatusCode.NotFound, await Status(stranger, deleted.Id));
        Assert.AreEqual(HttpStatusCode.OK, await Status(moderator, deleted.Id));
        Assert.AreEqual(HttpStatusCode.NotFound, await Status(moderator, 999999));

        // Owners get no moderation tools.
        var ownerHtml = await ownerClient.GetStringAsync(new Uri($"/view/{torrent.Id}/edit", UriKind.Relative));
        Assert.IsFalse(ownerHtml.Contains("handler=Ban", StringComparison.Ordinal));
        Assert.IsFalse(ownerHtml.Contains("is_comment_locked", StringComparison.Ordinal));
        StringAssert.Contains(await moderator.GetStringAsync(new Uri($"/view/{torrent.Id}/edit", UriKind.Relative)), "handler=BanUserIp");
    }

    [TestMethod]
    public async Task EditingTorrent()
    {
        var owner = Unique("owner");
        var modName = Unique("mod");
        using var ownerClient = await site.ClientAsync(owner);
        using var moderator = await site.ClientAsync(modName, UserLevel.Moderator);
        var torrent = await site.AddTorrentAsync(owner);
        var url = $"/view/{torrent.Id}/edit";

        var token = await TorrentSiteFactory.TokenAsync(ownerClient, url);
        using var invalid = await TorrentSiteFactory.PostAsync(ownerClient, url, token, ("display_name", "ab"), ("category", "1_0"), ("submit", "Save Changes"));
        Assert.AreEqual(HttpStatusCode.OK, invalid.StatusCode);
        var invalidHtml = await invalid.Content.ReadAsStringAsync();
        StringAssert.Contains(invalidHtml, "Torrent display name must be at least 3 characters long and 255 at most.");
        StringAssert.Contains(invalidHtml, "Please select a proper category");

        using var saved = await TorrentSiteFactory.PostAsync(
            ownerClient, url, token,
            ("display_name", "  New name\u0001  "), ("category", "2_3"), ("information", "#chan@irc.example"),
            ("description", "desc"), ("is_remake", "true"), ("is_trusted", "true"), ("is_comment_locked", "true"), ("submit", "Save Changes"));
        Assert.AreEqual(HttpStatusCode.Redirect, saved.StatusCode);
        var edited = await site.WithDbAsync(db => db.Torrents.AsNoTracking().SingleAsync(t => t.Id == torrent.Id));
        Assert.AreEqual("New name�", edited.DisplayName);
        Assert.AreEqual("2_3", edited.CategoryCode);
        Assert.IsTrue(edited.IsRemake);
        Assert.IsFalse(edited.IsTrusted, "regular users can't mark torrents trusted");
        Assert.IsFalse(edited.IsCommentLocked, "only moderators lock comments");

        var modToken = await TorrentSiteFactory.TokenAsync(moderator, url);
        using var locked = await TorrentSiteFactory.PostAsync(
            moderator, url, modToken, ("display_name", "New name"), ("category", "2_3"), ("is_comment_locked", "true"), ("submit", "Save Changes"));
        Assert.AreEqual(HttpStatusCode.Redirect, locked.StatusCode);
        var log = await site.WithDbAsync(db => db.AdminLog.Where(l => l.Admin.Username == modName).Select(l => l.Message).ToListAsync());
        CollectionAssert.AreEqual(new[] { $"Torrent [#{torrent.Id}](/view/{torrent.Id}) marked as comments locked" }, log);
    }

    [TestMethod]
    public async Task DangerZoneHandlers()
    {
        var owner = Unique("owner");
        using var ownerClient = await site.ClientAsync(owner);
        using var moderator = await site.ClientAsync(Unique("mod"), UserLevel.Moderator);
        var torrent = await site.AddTorrentAsync(owner);
        var url = $"/view/{torrent.Id}/edit";

        // Owners can't ban, so pressing "ban" does nothing and goes home.
        var ownerToken = await TorrentSiteFactory.TokenAsync(ownerClient, url);
        using var ownerBan = await TorrentSiteFactory.PostAsync(ownerClient, url + "?handler=Ban", ownerToken, ("ban", "Delete & Ban"));
        Assert.AreEqual("/", ownerBan.Headers.Location!.OriginalString);

        var token = await TorrentSiteFactory.TokenAsync(moderator, url);
        using var noReason = await TorrentSiteFactory.PostAsync(moderator, url + "?handler=BanUser", token, ("ban_user", "x"), ("reason", ""));
        Assert.AreEqual(HttpStatusCode.OK, noReason.StatusCode);
        StringAssert.Contains(await noReason.Content.ReadAsStringAsync(), "This field is required.");

        using var ban = await TorrentSiteFactory.PostAsync(moderator, url + "?handler=BanUser", token, ("ban_user", "x"), ("reason", "rule 1"));
        Assert.AreEqual(HttpStatusCode.Redirect, ban.StatusCode);
        Assert.AreEqual($"/view/{torrent.Id}", ban.Headers.Location!.OriginalString);
        var (banned, status, events) = await site.WithDbAsync(async db => (
            await db.Torrents.AsNoTracking().SingleAsync(t => t.Id == torrent.Id),
            await db.Users.Where(u => u.Username == owner).Select(u => u.Status).SingleAsync(),
            await db.TrackerWhitelistEvents.CountAsync(e => e.InfoHash == torrent.InfoHash)));
        Assert.IsTrue(banned.IsBanned && banned.IsDeleted);
        Assert.AreEqual(UserStatus.Banned, status);
        Assert.AreEqual(1, events);

        using var nonsense = await TorrentSiteFactory.PostAsync(moderator, url + "?handler=Delete", token, ("delete", "Delete"));
        Assert.AreEqual(url, nonsense.Headers.Location!.OriginalString);
        StringAssert.Contains(await moderator.GetStringAsync(new Uri(url, UriKind.Relative)), "What the fuck are you doing?");
    }

    [TestMethod]
    public async Task AdminPagesRequireModerator()
    {
        using var anonymous = await site.ClientAsync();
        using var regular = await site.ClientAsync(Unique("regular"));
        using var trusted = await site.ClientAsync(Unique("trusted"), UserLevel.Trusted);
        using var moderator = await site.ClientAsync(Unique("mod"), UserLevel.Moderator);

        foreach (var page in new[] { "/admin/log", "/admin/bans", "/admin/reports" })
        {
            using var anon = await anonymous.GetAsync(new Uri(page, UriKind.Relative));
            Assert.AreEqual(HttpStatusCode.Redirect, anon.StatusCode);
            StringAssert.StartsWith(anon.Headers.Location!.AbsolutePath, "/login");

            foreach (var client in new[] { regular, trusted })
            {
                using var denied = await client.GetAsync(new Uri(page, UriKind.Relative));
                Assert.AreEqual(HttpStatusCode.Redirect, denied.StatusCode);
                StringAssert.StartsWith(denied.Headers.Location!.AbsolutePath, "/Error/403");
            }

            using var ok = await moderator.GetAsync(new Uri(page, UriKind.Relative));
            Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode, page);
        }

        Assert.AreEqual(HttpStatusCode.NotFound, (await moderator.GetAsync(new Uri("/admin/log?p=0", UriKind.Relative))).StatusCode);
    }

    [TestMethod]
    public async Task AdminBansAndReports()
    {
        var reporterName = Unique("reporter");
        var bannedName = Unique("banned");
        using var reporter = await site.ClientAsync(reporterName);
        using var bannedClient = await site.ClientAsync(bannedName);
        using var moderator = await site.ClientAsync(Unique("mod"), UserLevel.Moderator);
        using var admin = await site.ClientAsync(Unique("admin"), UserLevel.Administrator);
        var torrent = await site.AddTorrentAsync(null, t => t.DisplayName = "Reported torrent");
        var (reportId, banId) = await site.WithDbAsync(async db =>
        {
            var reporterId = await db.Users.Where(u => u.Username == reporterName).Select(u => u.Id).SingleAsync();
            var banned = await db.Users.SingleAsync(u => u.Username == bannedName);
            banned.Status = UserStatus.Banned;
            var report = new Report { TorrentId = torrent.Id, UserId = reporterId, Reason = "bad stuff", CreatedAt = DateTime.UtcNow };
            var ban = new Ban { AdminId = reporterId, UserId = banned.Id, UserIp = "192.0.2." + Random.Shared.Next(1, 250), Reason = "**markdown** reason", CreatedAt = DateTime.UtcNow.AddYears(1) };
            db.AddRange(report, ban);
            await db.SaveChangesAsync();
            return (report.Id, ban.Id);
        });

        var bansHtml = await moderator.GetStringAsync(new Uri("/admin/bans", UriKind.Relative));
        StringAssert.Contains(bansHtml, "<td>hidden</td>");
        StringAssert.Contains(bansHtml, $"value=\"{banId}\">Unban</button>");
        StringAssert.Contains(await admin.GetStringAsync(new Uri("/admin/bans", UriKind.Relative)), "<td>192.0.2.");

        var token = await TorrentSiteFactory.TokenAsync(moderator, "/admin/bans");
        using var unban = await TorrentSiteFactory.PostAsync(moderator, "/admin/bans", token, ("submit", banId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.AreEqual(HttpStatusCode.Redirect, unban.StatusCode);
        Assert.AreEqual(UserStatus.Active, await site.WithDbAsync(db => db.Users.Where(u => u.Username == bannedName).Select(u => u.Status).SingleAsync()));
        using var unbanAgain = await TorrentSiteFactory.PostAsync(moderator, "/admin/bans", token, ("submit", banId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.AreEqual(HttpStatusCode.NotFound, unbanAgain.StatusCode);

        // Moderators don't see IPs in the log, administrators do.
        StringAssert.Contains(await moderator.GetStringAsync(new Uri("/admin/log", UriKind.Relative)), $"Unbanned ban #{banId} {bannedName} IP(hidden)");
        StringAssert.Contains(await admin.GetStringAsync(new Uri("/admin/log", UriKind.Relative)), $"Unbanned ban #{banId} {bannedName} IP(192.0.2.");

        StringAssert.Contains(await moderator.GetStringAsync(new Uri("/admin/reports", UriKind.Relative)), "Reported torrent");
        var reportToken = await TorrentSiteFactory.TokenAsync(moderator, "/admin/reports");
        var id = reportId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var tid = torrent.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var bogus = await TorrentSiteFactory.PostAsync(moderator, "/admin/reports", reportToken, ("action", "nuke"), ("torrent", tid), ("report", id));
        Assert.AreEqual(HttpStatusCode.OK, bogus.StatusCode);
        using var hide = await TorrentSiteFactory.PostAsync(moderator, "/admin/reports", reportToken, ("action", "hide"), ("torrent", tid), ("report", id));
        Assert.AreEqual(HttpStatusCode.Redirect, hide.StatusCode);
        Assert.AreEqual("/admin/reports", hide.Headers.Location!.OriginalString);
        var (hidden, reportStatus) = await site.WithDbAsync(async db => (
            await db.Torrents.Where(t => t.Id == torrent.Id).Select(t => t.IsHidden).SingleAsync(),
            await db.Reports.Where(r => r.Id == reportId).Select(r => r.Status).SingleAsync()));
        Assert.IsTrue(hidden);
        Assert.AreEqual(ReportStatus.Valid, reportStatus);
        using var again = await TorrentSiteFactory.PostAsync(moderator, "/admin/reports", reportToken, ("action", "hide"), ("torrent", tid), ("report", id));
        Assert.AreEqual(HttpStatusCode.NotFound, again.StatusCode);
    }
}
