using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mogu.Core.Data;
using Mogu.Core.Entities;
using Mogu.Core.Options;
using Mogu.Core.Torrents;
using Mogu.Web.Infrastructure;
using Mogu.Web.Torrents;
using static Mogu.Tests.Torrents.TestTorrents;

namespace Mogu.Tests.Torrents;

[TestClass]
public sealed class UploadServiceTests : IDisposable
{
    private const string Ip = "203.0.113.7";

    private string _directory = null!;
    private MoguDbContext _db = null!;
    private MoguOptions _options = null!;
    private ManualTimeProvider _time = null!;
    private DataPaths _paths = null!;
    private TorrentUploadService _service = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), "mogu-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _db = new MoguDbContext(new DbContextOptionsBuilder<MoguDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_directory, "mogu.db")};Pooling=False")
            .Options);
        await _db.Database.MigrateAsync();

        _options = new MoguOptions { DataDirectory = Path.Combine(_directory, "data"), MainAnnounceUrl = MainTracker };
        _time = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = new StaticOptions<MoguOptions>(_options);
        _paths = new DataPaths(new TestEnvironment(_directory), options);
        _service = new TorrentUploadService(_db, _paths, _time, options, NullLogger<TorrentUploadService>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    public void Dispose() => _db?.Dispose();

    private static UploadRequest Request(BencodeDictionary torrent, string category = "1_1") =>
        new()
        {
            FileName = "upload.torrent",
            TorrentData = Encode(torrent),
            Category = category,
        };

    private static BencodeDictionary Big(string name) => MultiFile(name, ("video.mkv", 10L * 1024 * 1024));

    private async Task<User> AddUserAsync(string name, UserLevel level = UserLevel.Regular, int ageDays = 30)
    {
        var user = new User { Username = name, DiscordId = Random.Shared.NextInt64(), Level = level, CreatedAt = _time.GetUtcNow().UtcDateTime.AddDays(-ageDays) };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }

    private Task<UploadResult> UploadAsync(UploadRequest request, User? user = null) =>
        _service.UploadAsync(request, user, Ip, inputIsValid: true, CancellationToken.None);

    [TestMethod]
    public async Task StoresTorrentTrackersFilesAndWhitelistEvent()
    {
        var torrent = Big("Show");
        torrent["announce-list"] = new List<object> { new List<object> { Bytes(MainTracker) }, new List<object> { Bytes("http://other/announce") } };
        torrent["url-list"] = new List<object> { Bytes("https://seed/") };
        var user = await AddUserAsync("uploader");

        var result = await UploadAsync(Request(torrent) with { DisplayName = "  Nice name\u0001 ", Description = " desc " }, user);

        Assert.IsTrue(result.Succeeded, string.Join(", ", result.Errors.SelectMany(e => e.Value)));
        var stored = await _db.Torrents.AsNoTracking()
            .Include(t => t.FileList)
            .Include(t => t.Trackers).ThenInclude(tt => tt.Tracker)
            .SingleAsync();
        Assert.AreEqual("Nice name\ufffd", stored.DisplayName);
        Assert.AreEqual("desc", stored.Description);
        Assert.AreEqual(user.Id, stored.UploaderId);
        Assert.AreEqual(Ip, stored.UploaderIp);
        Assert.IsTrue(stored.HasTorrent);
        Assert.AreEqual(10L * 1024 * 1024, stored.FileSize);
        Assert.AreEqual("""{"Show":{"video.mkv":10485760}}""", stored.FileList!.Json);
        Assert.AreEqual(Joined((MainTracker, false), ("http://other/announce", false), ("https://seed/", true)), Joined(stored.Trackers.OrderBy(t => t.Order).Select(t => (object)(t.Tracker.Uri, t.Tracker.IsWebSeed))));

        var whitelist = await _db.TrackerWhitelistEvents.SingleAsync();
        CollectionAssert.AreEqual(stored.InfoHash, whitelist.InfoHash);
        Assert.AreEqual(TrackerWhitelistAction.Insert, whitelist.Action);

        CollectionAssert.AreEqual(Bencode.Encode(Info(torrent)), await File.ReadAllBytesAsync(_paths.InfoDictPath(stored.InfoHashHex)));
        Assert.IsTrue(File.Exists(Path.Combine(_paths.TorrentBackupDirectory, $"{stored.Id}.upload.torrent")));
    }

    [TestMethod]
    public async Task ReusesExistingTrackers()
    {
        Assert.IsTrue((await UploadAsync(Request(Big("one")), await AddUserAsync("a"))).Succeeded);
        Assert.IsTrue((await UploadAsync(Request(Big("two")), await AddUserAsync("b"))).Succeeded);

        Assert.AreEqual(1, await _db.Trackers.CountAsync());
        Assert.AreEqual(2, await _db.TorrentTrackers.CountAsync());
    }

    [TestMethod]
    public async Task UsesTorrentNameWithoutDisplayName()
    {
        var result = await UploadAsync(Request(Big("  From torrent  ")), await AddUserAsync("a"));

        Assert.AreEqual("From torrent", result.Torrent!.DisplayName);
    }

    [TestMethod]
    public async Task RejectsSmallAnonymousUploads()
    {
        var result = await UploadAsync(Request(MultiFile("tiny", ("a", 100))));

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(Joined("Torrent too small for an anonymous uploader"), Joined(result.Errors[UploadFields.TorrentFile]));
    }

    [TestMethod]
    public async Task SmallUploadsAreFineWithAnAccount()
    {
        var result = await UploadAsync(Request(MultiFile("tiny", ("a", 100))) with { IsAnonymous = true }, await AddUserAsync("a"));

        Assert.IsTrue(result.Succeeded);
        Assert.IsTrue(result.Torrent.IsAnonymous);
    }

    [TestMethod]
    public async Task AnonymousIsForcedWithoutAccountAndFlagsNeedLevels()
    {
        var anonymous = await UploadAsync(Request(Big("anon")) with { IsTrusted = true, IsCommentLocked = true });
        var regular = await UploadAsync(Request(Big("regular")) with { IsTrusted = true, IsCommentLocked = true }, await AddUserAsync("r"));
        var moderator = await UploadAsync(Request(Big("mod")) with { IsTrusted = true, IsCommentLocked = true, IsHidden = true }, await AddUserAsync("m", UserLevel.Moderator));

        Assert.IsTrue(anonymous.Torrent!.IsAnonymous);
        Assert.IsNull(anonymous.Torrent.UploaderId);
        Assert.IsFalse(anonymous.Torrent.IsTrusted);
        Assert.IsFalse(regular.Torrent!.IsAnonymous);
        Assert.IsFalse(regular.Torrent.IsTrusted);
        Assert.IsFalse(regular.Torrent.IsCommentLocked);
        Assert.IsTrue(moderator.Torrent!.IsTrusted);
        Assert.IsTrue(moderator.Torrent.IsCommentLocked);
        Assert.IsTrue(moderator.Torrent.IsHidden);
    }

    [TestMethod]
    public async Task RejectsDuplicateInfoHash()
    {
        var first = await UploadAsync(Request(Big("dup")), await AddUserAsync("a"));

        var second = await UploadAsync(Request(Big("dup")), await AddUserAsync("b"));

        Assert.AreEqual(Joined($"This torrent already exists (#{first.Torrent!.Id})"), Joined(second.Errors[UploadFields.TorrentFile]));
    }

    [TestMethod]
    public async Task ReplacesDeletedTorrentKeepingItsId()
    {
        var user = await AddUserAsync("a");
        var first = await UploadAsync(Request(Big("again")) with { Description = "old" }, user);
        _db.Comments.Add(new Comment { TorrentId = first.Torrent!.Id, UserId = user.Id, Text = "hi", CreatedAt = DateTime.UtcNow });
        await _db.Torrents.Where(t => t.Id == first.Torrent.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.IsDeleted, true));
        _db.ChangeTracker.Clear();

        var second = await UploadAsync(Request(Big("again")) with { Description = "new" }, user);

        Assert.IsTrue(second.Succeeded, string.Join(", ", second.Errors.SelectMany(e => e.Value)));
        Assert.AreEqual(first.Torrent.Id, second.Torrent.Id);
        var stored = await _db.Torrents.AsNoTracking().SingleAsync();
        Assert.AreEqual("new", stored.Description);
        Assert.IsFalse(stored.IsDeleted);
        Assert.AreEqual(0, await _db.Comments.CountAsync());
    }

    [TestMethod]
    public async Task RejectsBannedInfoHash()
    {
        var user = await AddUserAsync("a");
        var first = await UploadAsync(Request(Big("banned")), user);
        await _db.Torrents.Where(t => t.Id == first.Torrent!.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsDeleted, true).SetProperty(t => t.IsBanned, true));

        var second = await UploadAsync(Request(Big("banned")), user);

        Assert.AreEqual(Joined("This torrent is banned"), Joined(second.Errors[UploadFields.TorrentFile]));
    }

    [TestMethod]
    public async Task RateLimitsAnonymousUploadsByIp()
    {
        _options.RateLimitUploads = true;
        _options.MaxUploadBurst = 2;
        _options.UploadTimeout = TimeSpan.FromMinutes(15);

        Assert.IsTrue((await UploadAsync(Request(Big("r1")))).Succeeded);
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.IsTrue((await UploadAsync(Request(Big("r2")))).Succeeded);
        _time.Advance(TimeSpan.FromMinutes(1));

        var limited = await UploadAsync(Request(Big("r3")));
        Assert.AreEqual(Joined("You've gone over the upload ratelimit."), Joined(limited.Errors[UploadFields.RateLimit]));
        var status = await _service.GetRateLimitAsync(null, Ip, CancellationToken.None);
        Assert.AreEqual(2, status.Count);
        Assert.AreEqual(_time.GetUtcNow().UtcDateTime.AddMinutes(14), status.NextAllowed);

        _time.Advance(TimeSpan.FromMinutes(15));
        Assert.IsTrue((await UploadAsync(Request(Big("r3")))).Succeeded);
    }

    [TestMethod]
    public async Task RateLimitSparesOldAndTrustedAccounts()
    {
        _options.RateLimitUploads = true;

        Assert.IsTrue(_service.IsSubjectToRateLimit(null));
        Assert.IsTrue(_service.IsSubjectToRateLimit(await AddUserAsync("new", ageDays: 1)));
        Assert.IsFalse(_service.IsSubjectToRateLimit(await AddUserAsync("trusted", UserLevel.Trusted, ageDays: 1)));
        Assert.IsFalse(_service.IsSubjectToRateLimit(await AddUserAsync("old", ageDays: 30)));
    }

    [TestMethod]
    public async Task RaidModeBlocksAnonymousUploads()
    {
        _options.RaidModeLimitUploads = true;

        var result = await UploadAsync(Request(Big("raid")));

        Assert.AreEqual(Joined(_options.RaidModeUploadsMessage), Joined(result.Errors[UploadFields.RangeBanned]));
        Assert.IsTrue((await UploadAsync(Request(Big("raid")), await AddUserAsync("a"))).Succeeded);
    }

    [TestMethod]
    public async Task RangeBanBlocksAnonymousUploads()
    {
        _db.RangeBans.Add(RangeBan.FromCidr("203.0.113.0/24"));
        await _db.SaveChangesAsync();

        var result = await UploadAsync(Request(Big("ranged")));

        Assert.AreEqual(Joined("Your IP is banned from uploading anonymously."), Joined(result.Errors[UploadFields.RangeBanned]));
    }

    [TestMethod]
    public async Task RejectsForbiddenFilenames()
    {
        var result = await UploadAsync(Request(MultiFile("x", ("dir/CON.txt", 1))), await AddUserAsync("a"));

        Assert.AreEqual(Joined("Torrent has forbidden characters in filenames"), Joined(result.Errors[UploadFields.TorrentFile]));
    }

    [TestMethod]
    public async Task ValidatesCategoryAndTorrentTogether()
    {
        var result = await UploadAsync(new UploadRequest { FileName = "x.torrent", TorrentData = "garbage"u8.ToArray(), Category = "1_0" });

        Assert.AreEqual(Joined("Please select a proper category"), Joined(result.Errors[UploadFields.Category]));
        Assert.AreEqual(Joined("Malformed torrent file"), Joined(result.Errors[UploadFields.TorrentFile]));
    }

    [TestMethod]
    public async Task StoresNothingWhenInputWasInvalid()
    {
        var result = await _service.UploadAsync(Request(Big("x")), await AddUserAsync("a"), Ip, inputIsValid: false, CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(0, await _db.Torrents.CountAsync());
    }

    [TestMethod]
    public async Task EnforcesMainAnnounceUrl()
    {
        _options.EnforceMainAnnounceUrl = true;
        var torrent = Big("elsewhere");
        torrent["announce"] = Bytes("udp://elsewhere/announce");

        var result = await UploadAsync(Request(torrent), await AddUserAsync("a"));

        Assert.AreEqual(Joined($"Please include {MainTracker} in the trackers of the torrent"), Joined(result.Errors[UploadFields.TorrentFile]));
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class StaticOptions<T>(T value) : IOptionsSnapshot<T>
        where T : class
    {
        public T Value => value;

        public T Get(string? name) => value;
    }

    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = root;

        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();

        public string ApplicationName { get; set; } = "Mogu.Tests";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();

        public string ContentRootPath { get; set; } = root;

        public string EnvironmentName { get; set; } = "Testing";
    }

    private static string Joined(params IEnumerable<object> items) => string.Join(" | ", items);
}
