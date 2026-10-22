using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mogu.Core.Data;
using Mogu.Core.Entities;
using Mogu.Web.Infrastructure;
using Mogu.Web.Services;

namespace Mogu.Tests.Torrents;

[TestClass]
public sealed class ModerationServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string[] BanFlashes = ["Torrent has been successfully deleted and banned.", "Uploader has been successfully banned."];
    private static readonly string[] BannedIps = ["10.0.0.1", "10.0.0.2"];

    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly MoguDbContext db;
    private readonly ModerationService service;
    private readonly User moderator;
    private readonly User uploader;

    public ModerationServiceTests()
    {
        connection.Open();
        db = new MoguDbContext(new DbContextOptionsBuilder<MoguDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        service = new ModerationService(db, new FixedTimeProvider(Now));

        moderator = new User { Username = "mod", DiscordId = 1, Level = UserLevel.Moderator, CreatedAt = Now };
        uploader = new User { Username = "uploader", DiscordId = 2, CreatedAt = Now, LastLoginIp = "10.0.0.1" };
        db.Users.AddRange(moderator, uploader);
        db.SaveChanges();
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    private Torrent AddTorrent(User? owner = null, string? uploaderIp = "10.0.0.2", bool deleted = false, bool banned = false)
    {
        var torrent = new Torrent
        {
            InfoHash = Guid.NewGuid().ToByteArray().Concat(new byte[4]).ToArray(),
            DisplayName = "Some torrent",
            TorrentName = "some.torrent",
            UploaderId = owner?.Id,
            UploaderIp = uploaderIp,
            MainCategoryId = 1,
            SubCategoryId = 1,
            IsDeleted = deleted,
            IsBanned = banned,
            SeedCount = 5,
            LeechCount = 6,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        db.Torrents.Add(torrent);
        db.SaveChanges();
        return db.Torrents.Include(t => t.Uploader).Single(t => t.Id == torrent.Id);
    }

    private Task<TorrentModerationResult> ModerateAsync(Torrent torrent, TorrentModerationAction action, User? editor = null, string? reason = "spam") =>
        service.ModerateTorrentAsync(
            torrent,
            editor ?? moderator,
            new TorrentModerationRequest(action, CanBanUploader: (editor ?? moderator).IsModerator, reason, "Mogu", $"https://mogu.test/view/{torrent.Id}"));

    private List<string> Log() => db.AdminLog.OrderBy(l => l.Id).Select(l => l.Message).ToList();

    private List<TrackerWhitelistAction> Whitelist(Torrent torrent) =>
        db.TrackerWhitelistEvents.Where(e => e.InfoHash == torrent.InfoHash).OrderBy(e => e.Id).Select(e => e.Action).ToList();

    [TestMethod]
    public async Task DeleteThenUndelete()
    {
        var torrent = AddTorrent(uploader);

        var result = await ModerateAsync(torrent, TorrentModerationAction.Delete);
        Assert.IsTrue(torrent.IsDeleted);
        Assert.AreEqual($"/view/{torrent.Id}", result.RedirectUrl);
        Assert.AreEqual("Torrent has been successfully deleted.", result.Flashes.Single().Text);

        await ModerateAsync(torrent, TorrentModerationAction.Undelete);
        Assert.IsFalse(torrent.IsDeleted);
        CollectionAssert.AreEqual(
            new[] { $"Torrent [#{torrent.Id}](/view/{torrent.Id}) has been deleted", $"Torrent [#{torrent.Id}](/view/{torrent.Id}) has been undeleted" },
            Log());
        Assert.AreEqual(0, Whitelist(torrent).Count);
    }

    [TestMethod]
    public async Task OwnerDeletingGoesHomeWithoutAdminLog()
    {
        var torrent = AddTorrent(uploader);

        var result = await ModerateAsync(torrent, TorrentModerationAction.Delete, editor: uploader);

        Assert.IsTrue(torrent.IsDeleted);
        Assert.AreEqual("/", result.RedirectUrl);
        Assert.AreEqual(0, Log().Count);
    }

    [TestMethod]
    public async Task BanDeletesZeroesStatsAndRemovesFromTracker()
    {
        var torrent = AddTorrent(uploader);

        var result = await ModerateAsync(torrent, TorrentModerationAction.Ban);

        Assert.IsTrue(torrent.IsDeleted && torrent.IsBanned);
        Assert.AreEqual(0, torrent.SeedCount);
        Assert.AreEqual(0, torrent.LeechCount);
        Assert.AreEqual("Torrent has been successfully deleted and banned.", result.Flashes.Single().Text);
        CollectionAssert.AreEqual(new[] { TrackerWhitelistAction.Remove }, Whitelist(torrent));

        await ModerateAsync(torrent, TorrentModerationAction.Undelete);
        Assert.IsFalse(torrent.IsDeleted || torrent.IsBanned);
        CollectionAssert.AreEqual(new[] { TrackerWhitelistAction.Remove, TrackerWhitelistAction.Insert }, Whitelist(torrent));
        Assert.AreEqual($"Torrent [#{torrent.Id}](/view/{torrent.Id}) has been undeleted and unbanned", Log()[^1]);
    }

    [TestMethod]
    public async Task BanDeletedTorrentThenUnban()
    {
        var torrent = AddTorrent(uploader, deleted: true);

        await ModerateAsync(torrent, TorrentModerationAction.Ban);
        Assert.AreEqual($"Torrent [#{torrent.Id}](/view/{torrent.Id}) has been banned", Log()[^1]);

        await ModerateAsync(torrent, TorrentModerationAction.Unban);
        Assert.IsTrue(torrent.IsDeleted);
        Assert.IsFalse(torrent.IsBanned);
        Assert.AreEqual($"Torrent [#{torrent.Id}](/view/{torrent.Id}) has been unbanned", Log()[^1]);
        CollectionAssert.AreEqual(new[] { TrackerWhitelistAction.Remove, TrackerWhitelistAction.Insert }, Whitelist(torrent));
    }

    [TestMethod]
    public async Task NonsensicalActionsAreRejected()
    {
        var torrent = AddTorrent(uploader);

        foreach (var action in new[] { TorrentModerationAction.Undelete, TorrentModerationAction.Unban })
        {
            var result = await ModerateAsync(torrent, action);
            Assert.AreEqual(FlashLevel.Danger, result.Flashes.Single().Level);
            Assert.AreEqual("What the fuck are you doing?", result.Flashes.Single().Text);
            Assert.AreEqual($"/view/{torrent.Id}/edit", result.RedirectUrl);
        }

        Assert.AreEqual(0, Log().Count);
    }

    [TestMethod]
    public async Task BanUserBansUploaderAccount()
    {
        var torrent = AddTorrent(uploader);

        var result = await ModerateAsync(torrent, TorrentModerationAction.BanUser, reason: "being bad");

        Assert.AreEqual(UserStatus.Banned, uploader.Status);
        Assert.IsTrue(torrent.IsBanned && torrent.IsDeleted);
        var ban = db.Bans.Single();
        Assert.AreEqual(uploader.Id, ban.UserId);
        Assert.IsNull(ban.UserIp);
        Assert.AreEqual($"[Mogu#{torrent.Id}](https://mogu.test/view/{torrent.Id}) being bad", ban.Reason);
        Assert.AreEqual($"Uploader [uploader](/user/uploader) of torrent [#{torrent.Id}](/view/{torrent.Id}) has been banned.", Log()[^1]);
        CollectionAssert.AreEqual(
            BanFlashes,
            result.Flashes.Select(f => f.Text).ToList());

        // Banning again is nonsense.
        var again = await ModerateAsync(torrent, TorrentModerationAction.BanUser);
        Assert.AreEqual("What the fuck are you doing?", again.Flashes.Single().Text);
        Assert.AreEqual(1, db.Bans.Count());
    }

    [TestMethod]
    public async Task BanUserIpBansLastLoginAndUploadIps()
    {
        var torrent = AddTorrent(uploader);

        await ModerateAsync(torrent, TorrentModerationAction.BanUserIp);

        var bans = db.Bans.OrderBy(b => b.Id).ToList();
        CollectionAssert.AreEqual(BannedIps, bans.Select(b => b.UserIp).ToList());
        Assert.IsTrue(bans.All(b => b.UserId == uploader.Id));
        Assert.AreEqual($"Uploader [uploader](/user/uploader) IP(10.0.0.1) IP(10.0.0.2) of torrent [#{torrent.Id}](/view/{torrent.Id}) has been banned.", Log()[^1]);
        Assert.IsTrue(await service.IsUploaderIpBannedAsync(torrent));
    }

    [TestMethod]
    public async Task BanIpOfAnonymousUpload()
    {
        var torrent = AddTorrent(owner: null, uploaderIp: "10.9.9.9");

        await ModerateAsync(torrent, TorrentModerationAction.BanUserIp);

        var ban = db.Bans.Single();
        Assert.IsNull(ban.UserId);
        Assert.AreEqual("10.9.9.9", ban.UserIp);
        Assert.AreEqual($"Uploader Anonymous IP(10.9.9.9) of torrent [#{torrent.Id}](/view/{torrent.Id}) has been banned.", Log()[^1]);

        var again = await ModerateAsync(torrent, TorrentModerationAction.BanUserIp);
        Assert.AreEqual("What the fuck are you doing?", again.Flashes.Single().Text);
    }

    [TestMethod]
    public async Task BanUserIsIgnoredWithoutBanForm()
    {
        var torrent = AddTorrent(uploader);

        var result = await service.ModerateTorrentAsync(
            torrent, moderator, new TorrentModerationRequest(TorrentModerationAction.BanUser, CanBanUploader: false, "x", "Mogu", "https://mogu.test/"));

        Assert.AreEqual("What the fuck are you doing?", result.Flashes.Single().Text);
        Assert.IsFalse(torrent.IsBanned);
        Assert.AreEqual(UserStatus.Active, uploader.Status);
    }

    [TestMethod]
    public async Task LiftBanReactivatesUserAndLogs()
    {
        var torrent = AddTorrent(uploader);
        await ModerateAsync(torrent, TorrentModerationAction.BanUserIp);
        var first = db.Bans.OrderBy(b => b.Id).First();

        Assert.IsTrue(await service.LiftBanAsync(first.Id, moderator));

        Assert.AreEqual(UserStatus.Active, uploader.Status);
        Assert.AreEqual(1, db.Bans.Count());
        Assert.AreEqual($"Unbanned ban #{first.Id} uploader IP(10.0.0.1)", Log()[^1]);
        Assert.IsFalse(await service.LiftBanAsync(first.Id, moderator));
    }

    [TestMethod]
    [DataRow(ReportAction.Close, "Closed", ReportStatus.Invalid, false, false)]
    [DataRow(ReportAction.Hide, "Hid", ReportStatus.Valid, true, false)]
    [DataRow(ReportAction.Delete, "Deleted", ReportStatus.Valid, false, true)]
    public async Task ReviewReport(ReportAction action, string verb, ReportStatus status, bool hidden, bool deleted)
    {
        var torrent = AddTorrent(uploader);
        var other = AddTorrent(uploader);
        var reports = new[]
        {
            new Report { TorrentId = torrent.Id, UserId = moderator.Id, Reason = "bad", CreatedAt = Now },
            new Report { TorrentId = torrent.Id, UserId = uploader.Id, Reason = "also bad", CreatedAt = Now },
            new Report { TorrentId = torrent.Id, UserId = uploader.Id, Reason = "old", Status = ReportStatus.Invalid, CreatedAt = Now },
            new Report { TorrentId = other.Id, UserId = uploader.Id, Reason = "other", CreatedAt = Now },
        };
        db.Reports.AddRange(reports);
        await db.SaveChangesAsync();

        Assert.IsFalse(await service.ReviewReportAsync(reports[0].Id, other.Id, action, moderator), "torrent must match");
        Assert.IsTrue(await service.ReviewReportAsync(reports[0].Id, torrent.Id, action, moderator));

        var reloaded = await db.Torrents.AsNoTracking().SingleAsync(t => t.Id == torrent.Id);
        Assert.AreEqual(hidden, reloaded.IsHidden);
        Assert.AreEqual(deleted, reloaded.IsDeleted);
        Assert.AreEqual(status, (await db.Reports.AsNoTracking().SingleAsync(r => r.Id == reports[0].Id)).Status);
        Assert.AreEqual($"Report #{reports[0].Id}: {verb} [#{torrent.Id}](/view/{torrent.Id}), reported by [mod](/user/mod)", Log()[^1]);

        // Other open reports on the same torrent are dropped; reviewed ones and other torrents' stay.
        var remaining = db.Reports.Select(r => r.Id).OrderBy(id => id).ToList();
        CollectionAssert.AreEqual(new[] { reports[0].Id, reports[2].Id, reports[3].Id }, remaining);

        Assert.IsFalse(await service.ReviewReportAsync(reports[0].Id, torrent.Id, action, moderator), "already reviewed");
    }
}

internal sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
{
    public DateTime UtcNow { get; set; } = utcNow;

    public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);
}
