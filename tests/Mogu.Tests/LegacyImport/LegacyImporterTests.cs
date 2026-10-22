#pragma warning disable CA1861 // Inline expected arrays read better in tests.
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mogu.Core.Data;
using Mogu.Core.Entities;
using Mogu.LegacyImport;

namespace Mogu.Tests.LegacyImport;

[TestClass]
public sealed class LegacyImporterTests
{
    private const long AliceId = 100000000000000001;
    private const long BobId = 200000000000000002;
    private const long CarolId = 300000000000000003;

    private const string SeedData = """
        INSERT INTO users (id, username, email, password_hash, status, level, created_time, last_login_date, last_login_ip, registration_ip) VALUES
            (200000000000000002, 'Bob', '200000000000000002@holopirates.moe', x'00', 1, 0, '2022-03-01 10:00:00.5', '2022-04-01 00:00:00', x'C0000201', x'20010DB8000000000000000000000001'),
            (100000000000000001, 'Alice', '100000000000000001@holopirates.moe', x'00', 1, 3, '2022-02-16 11:32:58.135478', NULL, x'0A000001', NULL),
            (300000000000000003, 'Carol', '300000000000000003@holopirates.moe', x'00', 2, 1, NULL, NULL, NULL, NULL);
        INSERT INTO user_preferences VALUES (200000000000000002, 1), (100000000000000001, 0);
        INSERT INTO nyaa_torrents VALUES
            (1, x'1111111111111111111111111111111111111111', 'First torrent', 'first.torrent', 'https://example.com', 'desc 1',
             1234, 'utf-8', 21, x'C0000202', 1, 1, '2022-02-16 12:00:00.000001', '2022-02-17 12:00:00', 1, 100000000000000001, 2, NULL),
            (5, x'2222222222222222222222222222222222222222', 'Second torrent', 'second.torrent', '', 'desc 5',
             99, 'UTF-8', 234, x'20010DB8000000000000000000000002', 1, 3, '2022-03-02 00:00:00.25', '2022-03-02 00:00:00.25', 3, 200000000000000002, 1, NULL);
        INSERT INTO nyaa_statistics VALUES (10, 2, 99, '2022-05-05 05:05:05.5', 1), (0, 0, 0, NULL, 5);
        INSERT INTO nyaa_torrents_filelist VALUES (CAST('{"ホロ":{"b.mkv":123}}' AS BLOB), 1), (CAST('{"x.txt":1}' AS BLOB), 5);
        INSERT INTO trackers VALUES (1, 'udp://a.example/announce', 0, 0), (2, 'https://b.example/seed', 1, 1);
        INSERT INTO nyaa_torrent_trackers VALUES (0, 1, 1), (1, 1, 2), (0, 5, 1);
        INSERT INTO nyaa_comments VALUES
            (3, '2022-02-17 00:00:00', NULL, 'first!', 1, 200000000000000002),
            (7, '2022-03-03 01:02:03.456789', '2022-03-04 00:00:00', 'nice', 5, 100000000000000001),
            (8, '2022-03-05 00:00:00', NULL, 'orphan', 5, NULL);
        INSERT INTO nyaa_reports VALUES (2, '2024-03-01 06:37:56.622365', 'dead', 1, 5, 300000000000000003);
        INSERT INTO nyaa_adminlog VALUES (4, '2022-02-16 11:48:01.369830', 'Torrent [#5](/view/5) deleted', 100000000000000001);
        INSERT INTO bans VALUES
            (1, '2022-06-01 00:00:00', 100000000000000001, 300000000000000003, x'C6336401', 'spam'),
            (2, '2022-06-02 00:00:00', 100000000000000001, NULL, x'20010DB800000000000000000000DEAD', 'ip only');
        INSERT INTO rangebans VALUES (1, '10.0.0.0/8', 167772160, 4278190080, 1, NULL);
        INSERT INTO nyaa_trackerapi VALUES (1, x'1111111111111111111111111111111111111111', 'insert');
        """;

    private static MoguDbContext OpenTarget(string path) =>
        new(new DbContextOptionsBuilder<MoguDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString())
            .Options);

    private static LegacyDatabase CreateSeeded()
    {
        var legacy = new LegacyDatabase();
        legacy.Execute(SeedData);
        return legacy;
    }

    private static Task<ImportReport> RunAsync(LegacyDatabase legacy, params string[] extra) =>
        LegacyImporter.RunAsync(
            ImportOptions.Parse(["--source", legacy.SourcePath, "--target", legacy.TargetPath, .. extra])!,
            TextWriter.Null);

    [TestMethod]
    public async Task ImportsUsersWithSurrogateIdsAndPreferences()
    {
        using var legacy = CreateSeeded();
        await RunAsync(legacy);

        await using var db = OpenTarget(legacy.TargetPath);
        var users = await db.Users.OrderBy(u => u.Id).ToListAsync();

        // Ordered by registration; Carol has no created_time so her snowflake timestamp (2017) is used.
        CollectionAssert.AreEqual(new[] { CarolId, AliceId, BobId }, users.Select(u => u.DiscordId).ToArray());
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, users.Select(u => u.Id).ToArray());

        var (carol, alice, bob) = (users[0], users[1], users[2]);
        Assert.AreEqual(LegacyConvert.SnowflakeTime(CarolId), carol.CreatedAt);
        Assert.AreEqual(UserStatus.Banned, carol.Status);
        Assert.AreEqual(UserLevel.Trusted, carol.Level);

        Assert.AreEqual("Alice", alice.Username);
        Assert.AreEqual(UserLevel.Administrator, alice.Level);
        Assert.AreEqual(new DateTime(2022, 2, 16, 11, 32, 58, DateTimeKind.Utc).AddTicks(1354780), alice.CreatedAt);
        Assert.AreEqual(DateTimeKind.Utc, alice.CreatedAt.Kind);
        Assert.AreEqual("10.0.0.1", alice.LastLoginIp);
        Assert.IsNull(alice.RegistrationIp);
        Assert.IsNull(alice.LastLoginAt);
        Assert.IsFalse(alice.HideComments);

        Assert.AreEqual("192.0.2.1", bob.LastLoginIp);
        Assert.AreEqual("2001:db8::1", bob.RegistrationIp);
        Assert.AreEqual(new DateTime(2022, 4, 1, 0, 0, 0, DateTimeKind.Utc), bob.LastLoginAt);
        Assert.IsTrue(bob.HideComments);
    }

    [TestMethod]
    public async Task ImportsTorrentsWithFlagsStatsFileListsAndTrackers()
    {
        using var legacy = CreateSeeded();
        var report = await RunAsync(legacy);

        await using var db = OpenTarget(legacy.TargetPath);
        var aliceId = await db.Users.Where(u => u.DiscordId == AliceId).Select(u => u.Id).SingleAsync();
        var bobId = await db.Users.Where(u => u.DiscordId == BobId).Select(u => u.Id).SingleAsync();
        var torrents = await db.Torrents.Include(t => t.FileList).Include(t => t.Trackers).OrderBy(t => t.Id).ToListAsync();
        CollectionAssert.AreEqual(new[] { 1, 5 }, torrents.Select(t => t.Id).ToArray());

        var first = torrents[0];
        Assert.AreEqual(new string('1', 40), first.InfoHashHex);
        Assert.AreEqual("First torrent", first.DisplayName);
        Assert.AreEqual("https://example.com", first.Information);
        Assert.AreEqual(1234, first.FileSize);
        Assert.IsTrue(first.IsAnonymous && first.IsTrusted && first.IsComplete);
        Assert.IsFalse(first.IsHidden || first.IsRemake || first.IsDeleted || first.IsBanned || first.IsCommentLocked);
        Assert.AreEqual(aliceId, first.UploaderId);
        Assert.AreEqual("192.0.2.2", first.UploaderIp);
        Assert.AreEqual("2_1", first.CategoryCode);
        Assert.AreEqual((10, 2, 99), (first.SeedCount, first.LeechCount, first.DownloadCount));
        Assert.AreEqual(new DateTime(2022, 5, 5, 5, 5, 5, 500, DateTimeKind.Utc), first.StatsUpdatedAt);
        Assert.AreEqual(new DateTime(2022, 2, 16, 12, 0, 0, DateTimeKind.Utc).AddTicks(10), first.CreatedAt);
        Assert.AreEqual("""{"ホロ":{"b.mkv":123}}""", first.FileList!.Json);
        CollectionAssert.AreEquivalent(new[] { (1, 0), (2, 1) }, first.Trackers.Select(t => (t.TrackerId, t.Order)).ToArray());
        Assert.AreEqual(1, first.CommentCount);

        var second = torrents[1];
        Assert.IsTrue(second.IsHidden && second.IsRemake && second.IsDeleted && second.IsBanned && second.IsCommentLocked);
        Assert.IsFalse(second.IsAnonymous || second.IsTrusted || second.IsComplete);
        Assert.AreEqual(bobId, second.UploaderId);
        Assert.AreEqual("2001:db8::2", second.UploaderIp);
        Assert.AreEqual("1_3", second.CategoryCode);
        Assert.IsNull(second.StatsUpdatedAt);
        Assert.AreEqual("UTF-8", second.Encoding);

        // Stored comment_count 3, but only one comment can be imported (the other has no user).
        Assert.AreEqual(1, second.CommentCount);
        Assert.AreEqual(new CommentCountDifference(5, 3, 1), report.CommentCountDifferences.Single());

        var trackers = await db.Trackers.OrderBy(t => t.Id).ToListAsync();
        Assert.AreEqual(2, trackers.Count);
        Assert.IsTrue(trackers[1].IsWebSeed && trackers[1].IsDisabled);
        Assert.IsFalse(trackers[0].IsWebSeed || trackers[0].IsDisabled);
    }

    [TestMethod]
    public async Task ImportsCommentsReportsAdminLogAndBans()
    {
        using var legacy = CreateSeeded();
        var report = await RunAsync(legacy);

        await using var db = OpenTarget(legacy.TargetPath);
        var ids = await db.Users.ToDictionaryAsync(u => u.DiscordId, u => u.Id);

        var comments = await db.Comments.OrderBy(c => c.Id).ToListAsync();
        CollectionAssert.AreEqual(new[] { 3, 7 }, comments.Select(c => c.Id).ToArray());
        Assert.AreEqual(ids[BobId], comments[0].UserId);
        Assert.IsNull(comments[0].EditedAt);
        Assert.AreEqual(ids[AliceId], comments[1].UserId);
        Assert.AreEqual(5, comments[1].TorrentId);
        Assert.AreEqual(new DateTime(2022, 3, 4, 0, 0, 0, DateTimeKind.Utc), comments[1].EditedAt);
        Assert.IsTrue(report.Warnings.Any(w => w.StartsWith("Comment 8 ", StringComparison.Ordinal)));

        var r = await db.Reports.SingleAsync();
        Assert.AreEqual((2, 5, ids[CarolId], ReportStatus.Valid, "dead"), (r.Id, r.TorrentId, r.UserId!.Value, r.Status, r.Reason));

        var log = await db.AdminLog.SingleAsync();
        Assert.AreEqual((4, ids[AliceId], "Torrent [#5](/view/5) deleted"), (log.Id, log.AdminId, log.Message));

        var bans = await db.Bans.OrderBy(b => b.Id).ToListAsync();
        Assert.AreEqual((ids[AliceId], ids[CarolId], "198.51.100.1"), (bans[0].AdminId, bans[0].UserId!.Value, bans[0].UserIp));
        Assert.IsNull(bans[1].UserId);
        Assert.AreEqual("2001:db8::dead", bans[1].UserIp);

        var rangeBan = await db.RangeBans.SingleAsync();
        Assert.AreEqual(("10.0.0.0/8", 167772160L, 4278190080L, true), (rangeBan.Cidr, rangeBan.MaskedAddress, rangeBan.Mask, rangeBan.IsEnabled));

        Assert.AreEqual(0, await db.TrackerWhitelistEvents.CountAsync());
        Assert.IsFalse(report.Warnings.Any(w => w.Contains("trusted_applications", StringComparison.Ordinal)),
            "Empty unmapped tables should not warn.");
        Assert.IsFalse(report.Warnings.Any(w => w.Contains("sukebei", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task SummaryCountsMatch()
    {
        using var legacy = CreateSeeded();
        var report = await RunAsync(legacy, "--include-tracker-api");

        var byTable = report.Tables.ToDictionary(t => t.Legacy);
        Assert.AreEqual(3, byTable["users"].ImportedCount);
        Assert.AreEqual(2, byTable["user_preferences"].ImportedCount);
        Assert.AreEqual(3, byTable["nyaa_comments"].LegacyCount);
        Assert.AreEqual(2, byTable["nyaa_comments"].ImportedCount);
        Assert.AreEqual(3, byTable["nyaa_torrent_trackers"].ImportedCount);
        Assert.AreEqual(1, byTable["nyaa_trackerapi"].ImportedCount);

        var output = new StringWriter();
        report.Write(output);
        StringAssert.Contains(output.ToString(), "nyaa_torrents");
    }

    [TestMethod]
    public async Task AppInsertsAfterImportDoNotCollideWithExplicitIds()
    {
        using var legacy = CreateSeeded();
        await RunAsync(legacy);

        await using var db = OpenTarget(legacy.TargetPath);
        var user = new User { DiscordId = 42, Username = "newbie", CreatedAt = DateTime.UtcNow };
        var torrent = new Torrent
        {
            InfoHash = new byte[20],
            DisplayName = "new",
            TorrentName = "new.torrent",
            MainCategoryId = 1,
            SubCategoryId = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Uploader = user,
        };
        db.Torrents.Add(torrent);
        db.Comments.Add(new Comment { Torrent = torrent, User = user, Text = "hi", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        Assert.AreEqual(4, user.Id);
        Assert.AreEqual(6, torrent.Id);
        Assert.AreEqual(8, await db.Comments.MaxAsync(c => c.Id));
    }

    [TestMethod]
    public async Task RefusesNonEmptyTargetUnlessForced()
    {
        using var legacy = CreateSeeded();
        await RunAsync(legacy);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => RunAsync(legacy));

        var report = await RunAsync(legacy, "--force");
        Assert.AreEqual(2, report.Tables.Single(t => t.Legacy == "nyaa_torrents").ImportedCount);
        await using var db = OpenTarget(legacy.TargetPath);
        Assert.AreEqual(3, await db.Users.CountAsync());
        Assert.AreEqual(3, await db.Users.MaxAsync(u => u.Id));
    }

    [TestMethod]
    public async Task FailedImportLeavesTargetEmpty()
    {
        using var legacy = CreateSeeded();
        legacy.Execute("""
            INSERT INTO nyaa_torrents VALUES (9, x'3333333333333333333333333333333333333333', 'Bad', 'bad.torrent', '', '',
                1, 'utf-8', 0, NULL, 1, 0, '2022-01-01 00:00:00', '2022-01-01 00:00:00', 9, NULL, 1, NULL)
            """);

        var e = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => RunAsync(legacy));
        StringAssert.Contains(e.Message, "torrent 9: category 1_9");
        Assert.IsFalse(File.Exists(legacy.TargetPath), "Mapping errors are detected before the target is touched.");
    }

    [TestMethod]
    public async Task RenamesCaseInsensitiveUsernameCollisions()
    {
        // The legacy UNIQUE constraint prevents this in practice; drop it to exercise the fallback.
        using var legacy = new LegacyDatabase(LegacyDatabase.Schema.Replace("UNIQUE (username),", "", StringComparison.Ordinal));
        legacy.Execute("""
            INSERT INTO users (id, username, password_hash, status, level, created_time) VALUES
                (1, 'Pekora', x'00', 1, 0, '2022-01-01 00:00:00'),
                (2, 'pekora', x'00', 1, 0, '2022-01-02 00:00:00'),
                (3, 'PEKORA', x'00', 1, 0, '2022-01-03 00:00:00'),
                (4, 'pekora_2', x'00', 1, 0, '2022-01-04 00:00:00');
            """);

        var report = await RunAsync(legacy);

        await using var db = OpenTarget(legacy.TargetPath);
        var names = await db.Users.OrderBy(u => u.DiscordId).Select(u => u.Username).ToListAsync();
        CollectionAssert.AreEqual(new[] { "Pekora", "pekora_2", "PEKORA_3", "pekora_2_2" }, names);
        Assert.AreEqual(3, report.UsernameRenames.Count);
    }

    [TestMethod]
    public async Task CopiesFilesAndMarksMissingInfoDicts()
    {
        using var legacy = CreateSeeded();
        var oldInfoDicts = legacy.Sub("old/info_dicts");
        var oldTorrents = legacy.Sub("old/torrents");
        var dataDir = legacy.Sub("data");
        var hex1 = new string('1', 40);
        Directory.CreateDirectory(Path.Combine(oldInfoDicts, "11", "11"));
        await File.WriteAllBytesAsync(Path.Combine(oldInfoDicts, "11", "11", hex1), [1, 2, 3]);
        Directory.CreateDirectory(oldTorrents);
        await File.WriteAllTextAsync(Path.Combine(oldTorrents, "1.first.torrent"), "d4:infod...e");

        var report = await RunAsync(legacy, "--data-dir", dataDir, "--info-dicts", oldInfoDicts, "--torrents", oldTorrents,
            "--mark-missing-torrents");

        CollectionAssert.AreEqual(new[] { 5 }, report.MissingInfoDicts);
        CollectionAssert.AreEqual(new[] { 5 }, report.MissingTorrentBackups);
        Assert.AreEqual(1, report.InfoDictFilesCopied);
        Assert.AreEqual(1, report.TorrentFilesCopied);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(dataDir, "info_dicts", "11", "11", hex1)));
        Assert.IsTrue(File.Exists(Path.Combine(dataDir, "torrents", "1.first.torrent")));

        await using var db = OpenTarget(legacy.TargetPath);
        var hasTorrent = await db.Torrents.OrderBy(t => t.Id).Select(t => t.HasTorrent).ToListAsync();
        CollectionAssert.AreEqual(new[] { true, false }, hasTorrent);
    }

    [TestMethod]
    public async Task RecoversMissingInfoDictsFromTorrentBackups()
    {
        using var legacy = CreateSeeded();
        var info = new Mogu.Core.Torrents.BencodeDictionary
        {
            ["length"] = 5L,
            ["name"] = "a.txt"u8.ToArray(),
            ["piece length"] = 16384L,
            ["pieces"] = new byte[20],
        };
        var infoBytes = Mogu.Core.Torrents.Bencode.Encode(info);
#pragma warning disable CA5350
        var hex = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(infoBytes));
#pragma warning restore CA5350
        legacy.Execute($"""
            UPDATE nyaa_torrents SET info_hash = x'{hex}' WHERE id = 5
            """);
        var oldInfoDicts = legacy.Sub("old/info_dicts");
        var oldTorrents = legacy.Sub("old/torrents");
        var dataDir = legacy.Sub("data");
        Directory.CreateDirectory(oldInfoDicts);
        Directory.CreateDirectory(oldTorrents);
        // Only the info dict's bytes matter for the hash; the rest of the .torrent can be anything.
        var backup = "d8:announce3:url4:info"u8.ToArray().Concat(infoBytes).Append((byte)'e').ToArray();
        await File.WriteAllBytesAsync(Path.Combine(oldTorrents, "5.second.torrent"), backup);
        await File.WriteAllTextAsync(Path.Combine(oldTorrents, "1.first.torrent"), "not bencode");

        var report = await RunAsync(legacy, "--data-dir", dataDir, "--info-dicts", oldInfoDicts, "--torrents", oldTorrents);

        Assert.AreEqual(1, report.RecoveredInfoDicts.Count, string.Join(",", report.MissingInfoDicts!));
        CollectionAssert.AreEqual(new[] { 1 }, report.MissingInfoDicts);
        CollectionAssert.AreEqual(infoBytes, await File.ReadAllBytesAsync(Path.Combine(dataDir, "info_dicts", hex[..2], hex[2..4], hex)));
    }

    [TestMethod]
    public async Task ReportsMissingInfoDictsWithoutChangingHasTorrentByDefault()
    {
        using var legacy = CreateSeeded();
        var report = await RunAsync(legacy, "--data-dir", legacy.Sub("empty-data"));

        CollectionAssert.AreEqual(new[] { 1, 5 }, report.MissingInfoDicts);
        Assert.IsNull(report.MissingTorrentBackups);
        await using var db = OpenTarget(legacy.TargetPath);
        Assert.IsTrue(await db.Torrents.AllAsync(t => t.HasTorrent));
    }

    [TestMethod]
    public void ConvertsLegacyValues()
    {
        Assert.AreEqual(new DateTime(2022, 2, 16, 11, 32, 58, DateTimeKind.Utc).AddTicks(1354780),
            LegacyConvert.ParseDateTime("2022-02-16 11:32:58.135478"));
        Assert.AreEqual(DateTimeKind.Utc, LegacyConvert.ParseDateTime("2022-02-16 11:32:58")!.Value.Kind);
        Assert.IsNull(LegacyConvert.ParseDateTime(null));
        Assert.AreEqual("::ffff:192.0.2.1",
            LegacyConvert.IpFromBlob([0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xff, 0xff, 192, 0, 2, 1]));
        Assert.IsNull(LegacyConvert.IpFromBlob([1, 2, 3]));
        Assert.AreEqual(new DateTime(2015, 1, 1, 0, 0, 0, DateTimeKind.Utc), LegacyConvert.SnowflakeTime(0));
        Assert.AreEqual("abcÄ", LegacyConvert.NoCaseKey("AbCÄ"));
    }

    [TestMethod]
    public void ParsesOptions()
    {
        var options = ImportOptions.Parse(["--source", "a.db", "--target", "b.db", "--force", "--data-dir", "d", "--info-dicts", "i"])!;
        Assert.IsTrue(options.Force);
        Assert.AreEqual(Path.GetFullPath("a.db"), options.SourcePath);
        Assert.AreEqual(Path.GetFullPath("i"), options.InfoDictsDirectory);
        Assert.IsFalse(options.IncludeTrackerApi);

        Assert.IsNull(ImportOptions.Parse(["--help"]));
        Assert.ThrowsExactly<FormatException>(() => ImportOptions.Parse(["--source", "a.db"]));
        Assert.ThrowsExactly<FormatException>(() => ImportOptions.Parse(["--source", "a.db", "--target"]));
        Assert.ThrowsExactly<FormatException>(() => ImportOptions.Parse(["--source", "a", "--target", "b", "--wat"]));
        Assert.ThrowsExactly<FormatException>(() => ImportOptions.Parse(["--source", "a", "--target", "b", "--torrents", "t"]));
    }
}
