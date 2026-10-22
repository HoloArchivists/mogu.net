using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mogu.Core.Data;
using Mogu.Core.Entities;
using Mogu.Core.Torrents;

namespace Mogu.LegacyImport;

/// <summary>One-time migration of a legacy mogu (Nyaa) SQLite database into the Mogu.NET schema.</summary>
public static class LegacyImporter
{
    private const int MaxUsernameLength = 32;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static async Task<ImportReport> RunAsync(ImportOptions options, TextWriter log, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);
        var report = new ImportReport();

        if (string.Equals(options.SourcePath, options.TargetPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("--source and --target must be different files.");
        }

        log.WriteLine($"Reading legacy database {options.SourcePath}");
        var legacy = await LegacyReader.ReadAsync(options.SourcePath, cancellationToken);
        foreach (var (table, count) in legacy.UnmappedTables.Where(t => t.Value > 0))
        {
            report.Warn(string.Create(CultureInfo.InvariantCulture, $"Table '{table}' has {count} rows and is NOT migrated (no counterpart in the new schema)."));
        }

        var imported = Map(legacy, options, report);

        CheckFiles(imported.Torrents, options, report);

        log.WriteLine($"Writing target database {options.TargetPath}");
        await WriteTargetAsync(imported, options, report, log, cancellationToken);

        CopyFiles(options, report, log);
        return report;
    }

    /// <summary>Converted entities, ready to insert.</summary>
    private sealed class ImportSet
    {
        public List<User> Users { get; } = [];

        public List<Torrent> Torrents { get; } = [];

        public List<TorrentFileList> FileLists { get; } = [];

        public List<Tracker> Trackers { get; } = [];

        public List<TorrentTracker> TorrentTrackers { get; } = [];

        public List<Comment> Comments { get; } = [];

        public List<Report> Reports { get; } = [];

        public List<AdminLogEntry> AdminLog { get; } = [];

        public List<Ban> Bans { get; } = [];

        public List<RangeBan> RangeBans { get; } = [];

        public List<TrackerWhitelistEvent> WhitelistEvents { get; } = [];

        public required Dictionary<string, long> SourceCounts { get; init; }

        public int PreferencesApplied { get; set; }

        public int StatisticsApplied { get; set; }
    }

    private static ImportSet Map(LegacyData legacy, ImportOptions options, ImportReport report)
    {
        var set = new ImportSet { SourceCounts = legacy.TableCounts };
        var c = CultureInfo.InvariantCulture;

        foreach (var category in legacy.SubCategories)
        {
            var main = CategorySeed.MainCategories.FirstOrDefault(m => m.Id == category.MainId);
            var sub = CategorySeed.SubCategories.FirstOrDefault(s => s.MainCategoryId == category.MainId && s.Id == category.SubId);
            if (main?.Name != category.MainName || sub?.Name != category.SubName)
            {
                report.Warn(string.Create(c,
                    $"Legacy category {category.MainId}_{category.SubId} '{category.MainName} - {category.SubName}' differs from CategorySeed ('{main?.Name} - {sub?.Name}')."));
            }
        }

        // Users: fresh surrogate ids in registration order; the snowflake becomes DiscordId.
        var userIds = new Dictionary<long, int>();
        var orderedUsers = legacy.Users
            .Select(u => (Row: u, Created: LegacyConvert.ParseDateTime(u.CreatedTime) ?? LegacyConvert.SnowflakeTime(u.Id)))
            .OrderBy(u => u.Created).ThenBy(u => u.Row.Id)
            .ToList();
        var takenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (row, created) in orderedUsers)
        {
            if (row.CreatedTime is null)
            {
                report.Warn(string.Create(c, $"User {row.Id} has no created_time; using its snowflake timestamp."));
            }

            var status = (UserStatus)row.Status;
            if (!Enum.IsDefined(status))
            {
                report.Warn(string.Create(c, $"User {row.Id} has unknown status {row.Status}; imported as Active."));
                status = UserStatus.Active;
            }

            var level = (UserLevel)row.Level;
            if (!Enum.IsDefined(level))
            {
                report.Warn(string.Create(c, $"User {row.Id} has unknown level {row.Level}; imported as Regular."));
                level = UserLevel.Regular;
            }

            var username = UniqueUsername(row.Username, takenNames);
            if (!string.Equals(username, row.Username, StringComparison.Ordinal))
            {
                report.UsernameRenames.Add(new UsernameRename(row.Id, row.Username, username));
            }

            var user = new User
            {
                Id = set.Users.Count + 1,
                DiscordId = row.Id,
                Username = username,
                Status = status,
                Level = level,
                CreatedAt = created,
                LastLoginAt = LegacyConvert.ParseDateTime(row.LastLoginDate),
                LastLoginIp = Ip(row.LastLoginIp, $"users[{row.Id}].last_login_ip", report),
                RegistrationIp = Ip(row.RegistrationIp, $"users[{row.Id}].registration_ip", report),
            };
            if (legacy.HideComments.TryGetValue(row.Id, out var hide))
            {
                user.HideComments = hide;
                set.PreferencesApplied++;
            }

            userIds[row.Id] = user.Id;
            set.Users.Add(user);
        }

        foreach (var orphan in legacy.HideComments.Keys.Where(id => !userIds.ContainsKey(id)))
        {
            report.Warn(string.Create(c, $"user_preferences for unknown user {orphan} skipped."));
        }

        // Comments first (in memory) so CommentCount can be recomputed.
        var torrentIds = legacy.Torrents.Select(t => t.Id).ToHashSet();
        foreach (var row in legacy.Comments)
        {
            if (!torrentIds.Contains(row.TorrentId))
            {
                report.Warn(string.Create(c, $"Comment {row.Id} references missing torrent {row.TorrentId}; skipped."));
                continue;
            }

            if (row.UserId is not { } legacyUserId || !userIds.TryGetValue(legacyUserId, out var userId))
            {
                report.Warn(string.Create(c, $"Comment {row.Id} has no (existing) user {row.UserId?.ToString(c) ?? "NULL"}; skipped."));
                continue;
            }

            var created = LegacyConvert.ParseDateTime(row.CreatedTime);
            if (created is null)
            {
                report.Warn(string.Create(c, $"Comment {row.Id} has no created_time; using the Unix epoch."));
            }

            set.Comments.Add(new Comment
            {
                Id = row.Id,
                TorrentId = row.TorrentId,
                UserId = userId,
                Text = row.Text,
                CreatedAt = created ?? DateTime.UnixEpoch,
                EditedAt = LegacyConvert.ParseDateTime(row.EditedTime),
            });
        }

        var commentCounts = set.Comments.GroupBy(cm => cm.TorrentId).ToDictionary(g => g.Key, g => g.Count());

        // Torrents keep their ids so /view/{id} URLs stay valid.
        var categories = CategorySeed.SubCategories.Select(s => (s.MainCategoryId, s.Id)).ToHashSet();
        var statistics = legacy.Statistics.ToDictionary(s => s.TorrentId);
        var categoryErrors = new List<string>();
        foreach (var row in legacy.Torrents)
        {
            if (!categories.Contains((row.MainCategoryId, row.SubCategoryId)))
            {
                categoryErrors.Add(string.Create(c, $"torrent {row.Id}: category {row.MainCategoryId}_{row.SubCategoryId}"));
                continue;
            }

            if (row.InfoHash.Length != 20)
            {
                throw new InvalidDataException(string.Create(c, $"Torrent {row.Id} has a {row.InfoHash.Length}-byte info_hash."));
            }

            if (row.Redirect is { } redirect)
            {
                report.Warn(string.Create(c, $"Torrent {row.Id} has redirect={redirect}, which is dropped."));
            }

            var flags = (LegacyTorrentFlags)row.Flags;
            if ((row.Flags & ~0xFF) != 0)
            {
                report.Warn(string.Create(c, $"Torrent {row.Id} has unknown flag bits 0x{row.Flags & ~0xFF:X}; ignored."));
            }

            int? uploaderId = null;
            if (row.UploaderId is { } legacyUploader)
            {
                if (userIds.TryGetValue(legacyUploader, out var mapped))
                {
                    uploaderId = mapped;
                }
                else
                {
                    report.Warn(string.Create(c, $"Torrent {row.Id} uploader {legacyUploader} does not exist; uploader cleared."));
                }
            }

            var actualComments = commentCounts.GetValueOrDefault(row.Id);
            if (actualComments != row.CommentCount)
            {
                report.CommentCountDifferences.Add(new CommentCountDifference(row.Id, row.CommentCount, actualComments));
            }

            var torrent = new Torrent
            {
                Id = row.Id,
                InfoHash = row.InfoHash,
                DisplayName = row.DisplayName,
                TorrentName = row.TorrentName,
                Information = row.Information,
                Description = row.Description,
                FileSize = row.FileSize,
                Encoding = row.Encoding,
                IsAnonymous = flags.HasFlag(LegacyTorrentFlags.Anonymous),
                IsHidden = flags.HasFlag(LegacyTorrentFlags.Hidden),
                IsTrusted = flags.HasFlag(LegacyTorrentFlags.Trusted),
                IsRemake = flags.HasFlag(LegacyTorrentFlags.Remake),
                IsComplete = flags.HasFlag(LegacyTorrentFlags.Complete),
                IsDeleted = flags.HasFlag(LegacyTorrentFlags.Deleted),
                IsBanned = flags.HasFlag(LegacyTorrentFlags.Banned),
                IsCommentLocked = flags.HasFlag(LegacyTorrentFlags.CommentLocked),
                HasTorrent = row.HasTorrent,
                UploaderId = uploaderId,
                UploaderIp = Ip(row.UploaderIp, $"nyaa_torrents[{row.Id}].uploader_ip", report),
                MainCategoryId = row.MainCategoryId,
                SubCategoryId = row.SubCategoryId,
                CommentCount = actualComments,
                CreatedAt = LegacyConvert.ParseDateTime(row.CreatedTime) ?? DateTime.UnixEpoch,
                UpdatedAt = LegacyConvert.ParseDateTime(row.UpdatedTime) ?? DateTime.UnixEpoch,
            };

            if (statistics.TryGetValue(row.Id, out var stats))
            {
                torrent.SeedCount = stats.SeedCount;
                torrent.LeechCount = stats.LeechCount;
                torrent.DownloadCount = stats.DownloadCount;
                torrent.StatsUpdatedAt = LegacyConvert.ParseDateTime(stats.LastUpdated);
                set.StatisticsApplied++;
            }
            else
            {
                report.Warn(string.Create(c, $"Torrent {row.Id} has no nyaa_statistics row; counters set to 0."));
            }

            set.Torrents.Add(torrent);
        }

        if (categoryErrors.Count > 0)
        {
            throw new InvalidDataException(
                "Torrents with categories missing from CategorySeed (add them to the seed first): " + string.Join("; ", categoryErrors));
        }

        foreach (var orphan in legacy.Statistics.Where(s => !torrentIds.Contains(s.TorrentId)))
        {
            report.Warn(string.Create(c, $"nyaa_statistics for missing torrent {orphan.TorrentId} skipped."));
        }

        foreach (var row in legacy.FileLists)
        {
            if (!torrentIds.Contains(row.TorrentId))
            {
                report.Warn(string.Create(c, $"File list of missing torrent {row.TorrentId} skipped."));
                continue;
            }

            if (row.Blob is null)
            {
                report.Warn(string.Create(c, $"Torrent {row.TorrentId} has a NULL filelist_blob; no file list imported."));
                continue;
            }

            string json;
            try
            {
                json = StrictUtf8.GetString(row.Blob);
                using var _ = JsonDocument.Parse(json);
            }
            catch (Exception e) when (e is DecoderFallbackException or JsonException)
            {
                report.Warn(string.Create(c, $"Torrent {row.TorrentId} file list is not valid UTF-8 JSON ({e.GetType().Name}); skipped."));
                continue;
            }

            set.FileLists.Add(new TorrentFileList { TorrentId = row.TorrentId, Json = json });
        }

        var trackerIds = new HashSet<int>();
        foreach (var row in legacy.Trackers)
        {
            set.Trackers.Add(new Tracker { Id = row.Id, Uri = row.Uri, IsWebSeed = row.IsWebSeed, IsDisabled = row.Disabled });
            trackerIds.Add(row.Id);
        }

        foreach (var row in legacy.TorrentTrackers)
        {
            if (!torrentIds.Contains(row.TorrentId) || !trackerIds.Contains(row.TrackerId))
            {
                report.Warn(string.Create(c, $"Torrent tracker link {row.TorrentId}->{row.TrackerId} references a missing row; skipped."));
                continue;
            }

            set.TorrentTrackers.Add(new TorrentTracker { TorrentId = row.TorrentId, TrackerId = row.TrackerId, Order = row.Order });
        }

        foreach (var row in legacy.Reports)
        {
            if (!torrentIds.Contains(row.TorrentId))
            {
                report.Warn(string.Create(c, $"Report {row.Id} references missing torrent {row.TorrentId}; skipped."));
                continue;
            }

            var status = (ReportStatus)row.Status;
            if (!Enum.IsDefined(status))
            {
                report.Warn(string.Create(c, $"Report {row.Id} has unknown status {row.Status}; imported as InReview."));
                status = ReportStatus.InReview;
            }

            set.Reports.Add(new Report
            {
                Id = row.Id,
                TorrentId = row.TorrentId,
                UserId = OptionalUser(row.UserId, $"Report {row.Id}", userIds, report),
                Reason = row.Reason,
                Status = status,
                CreatedAt = LegacyConvert.ParseDateTime(row.CreatedTime) ?? DateTime.UnixEpoch,
            });
        }

        foreach (var row in legacy.AdminLog)
        {
            if (!userIds.TryGetValue(row.AdminId, out var adminId))
            {
                report.Warn(string.Create(c, $"Admin log {row.Id} references missing admin {row.AdminId}; skipped."));
                continue;
            }

            set.AdminLog.Add(new AdminLogEntry
            {
                Id = row.Id,
                AdminId = adminId,
                Message = row.Log,
                CreatedAt = LegacyConvert.ParseDateTime(row.CreatedTime) ?? DateTime.UnixEpoch,
            });
        }

        foreach (var row in legacy.Bans)
        {
            if (!userIds.TryGetValue(row.AdminId, out var adminId))
            {
                report.Warn(string.Create(c, $"Ban {row.Id} references missing admin {row.AdminId}; skipped."));
                continue;
            }

            var ban = new Ban
            {
                Id = row.Id,
                AdminId = adminId,
                UserId = OptionalUser(row.UserId, $"Ban {row.Id}", userIds, report),
                UserIp = Ip(row.UserIp, $"bans[{row.Id}].user_ip", report),
                Reason = row.Reason,
                CreatedAt = LegacyConvert.ParseDateTime(row.CreatedTime) ?? DateTime.UnixEpoch,
            };
            if (ban.UserId is null && ban.UserIp is null)
            {
                report.Warn(string.Create(c, $"Ban {row.Id} has neither a user nor an IP after mapping; skipped."));
                continue;
            }

            set.Bans.Add(ban);
        }

        foreach (var row in legacy.RangeBans)
        {
            RangeBan rangeBan;
            try
            {
                rangeBan = RangeBan.FromCidr(row.Cidr);
                if (rangeBan.Mask != row.Mask || rangeBan.MaskedAddress != row.MaskedCidr)
                {
                    report.Warn(string.Create(c, $"Range ban {row.Id} ({row.Cidr}): stored mask/address differ from the CIDR; recomputed."));
                }
            }
            catch (FormatException)
            {
                report.Warn(string.Create(c, $"Range ban {row.Id} CIDR '{row.Cidr}' does not parse; stored mask/address copied as-is."));
                rangeBan = new RangeBan { Cidr = row.Cidr, Mask = row.Mask, MaskedAddress = row.MaskedCidr };
            }

            rangeBan.Id = row.Id;
            rangeBan.IsEnabled = row.Enabled;
            rangeBan.TemporarySince = LegacyConvert.ParseDateTime(row.Temp);
            set.RangeBans.Add(rangeBan);
        }

        if (options.IncludeTrackerApi)
        {
            foreach (var row in legacy.TrackerApi)
            {
                TrackerWhitelistAction? action = row.Method switch
                {
                    "insert" => TrackerWhitelistAction.Insert,
                    "remove" => TrackerWhitelistAction.Remove,
                    _ => null,
                };
                if (action is null || row.InfoHash.Length != 20)
                {
                    report.Warn(string.Create(c, $"nyaa_trackerapi {row.Id} (method '{row.Method}') is malformed; skipped."));
                    continue;
                }

                set.WhitelistEvents.Add(new TrackerWhitelistEvent { Id = row.Id, InfoHash = row.InfoHash, Action = action.Value });
            }
        }

        return set;
    }

    /// <summary>
    /// Keeps the name if no earlier user holds it under SQLite NOCASE (ASCII case folding, like the unique index);
    /// otherwise appends <c>_2</c>, <c>_3</c>, ... (trimmed to fit 32 characters).
    /// </summary>
    private static string UniqueUsername(string name, HashSet<string> taken)
    {
        if (taken.Add(LegacyConvert.NoCaseKey(name)))
        {
            return name;
        }

        for (var n = 2; ; n++)
        {
            var suffix = "_" + n.ToString(CultureInfo.InvariantCulture);
            var candidate = name[..Math.Min(name.Length, MaxUsernameLength - suffix.Length)] + suffix;
            if (taken.Add(LegacyConvert.NoCaseKey(candidate)))
            {
                return candidate;
            }
        }
    }

    private static string? Ip(byte[]? packed, string what, ImportReport report)
    {
        var ip = LegacyConvert.IpFromBlob(packed);
        if (packed is not null && ip is null)
        {
            report.Warn(string.Create(CultureInfo.InvariantCulture, $"{what} is a {packed.Length}-byte value, not an IP address; dropped."));
        }

        return ip;
    }

    private static int? OptionalUser(long? legacyId, string what, Dictionary<long, int> userIds, ImportReport report)
    {
        if (legacyId is not { } id)
        {
            return null;
        }

        if (userIds.TryGetValue(id, out var mapped))
        {
            return mapped;
        }

        report.Warn(string.Create(CultureInfo.InvariantCulture, $"{what} references missing user {id}; user cleared."));
        return null;
    }

    private static string InfoDictPath(string root, string hex) => Path.Combine(root, hex[..2], hex[2..4], hex);

    /// <summary>Checks info dicts / backups in the legacy directories, or in the data directory when those aren't given.</summary>
    private static void CheckFiles(List<Torrent> torrents, ImportOptions options, ImportReport report)
    {
        var infoDicts = options.InfoDictsDirectory
            ?? (options.DataDirectory is null ? null : Path.Combine(options.DataDirectory, "info_dicts"));
        if (options.InfoDictsDirectory is not null && !Directory.Exists(options.InfoDictsDirectory))
        {
            throw new DirectoryNotFoundException($"--info-dicts directory not found: {options.InfoDictsDirectory}");
        }

        var backups = options.TorrentsDirectory
            ?? (options.DataDirectory is null ? null : Path.Combine(options.DataDirectory, "torrents"));
        if (options.TorrentsDirectory is not null && !Directory.Exists(options.TorrentsDirectory))
        {
            throw new DirectoryNotFoundException($"--torrents directory not found: {options.TorrentsDirectory}");
        }

        if (backups is not null && Directory.Exists(backups))
        {
            var present = Directory.EnumerateFiles(backups)
                .Select(f => Path.GetFileName(f).Split('.', 2)[0])
                .Select(prefix => int.TryParse(prefix, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0)
                .ToHashSet();
            report.MissingTorrentBackups = torrents.Where(t => !present.Contains(t.Id)).Select(t => t.Id).ToList();
        }

        if (infoDicts is not null)
        {
            var missing = torrents.Where(t => t.HasTorrent && !File.Exists(InfoDictPath(infoDicts, t.InfoHashHex))).ToList();
            if (backups is not null && Directory.Exists(backups))
            {
                RecoverInfoDicts(missing, backups, report);
            }

            report.MissingInfoDicts = missing.Where(t => !report.RecoveredInfoDicts.ContainsKey(t.InfoHashHex)).Select(t => t.Id).ToList();
            if (options.MarkMissingTorrents)
            {
                var stillMissing = report.MissingInfoDicts.ToHashSet();
                foreach (var torrent in torrents.Where(t => stillMissing.Contains(t.Id)))
                {
                    torrent.HasTorrent = false;
                }
            }
        }
    }

    /// <summary>
    /// The old site lost the info dicts of some torrents, but kept the uploaded .torrent. Re-extract the
    /// info dict from <c>{id}.*</c> backups, accepting it only if it hashes to the torrent's info hash.
    /// </summary>
    private static void RecoverInfoDicts(List<Torrent> missing, string backups, ImportReport report)
    {
        var files = Directory.EnumerateFiles(backups)
            .Select(f => (Path: f, Prefix: Path.GetFileName(f).Split('.', 2)[0]))
            .Where(f => int.TryParse(f.Prefix, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .GroupBy(f => int.Parse(f.Prefix, NumberStyles.None, CultureInfo.InvariantCulture))
            .ToDictionary(g => g.Key, g => g.Select(f => f.Path).ToList());

        foreach (var torrent in missing)
        {
            foreach (var file in files.GetValueOrDefault(torrent.Id) ?? [])
            {
                try
                {
                    if (Bencode.Decode(File.ReadAllBytes(file), allowTrailingData: true) is BencodeDictionary root
                        && root.TryGetValue("info", out var info))
                    {
                        var encoded = Bencode.Encode(info);
#pragma warning disable CA5350 // BitTorrent v1 info hashes are SHA-1 by definition.
                        if (SHA1.HashData(encoded).AsSpan().SequenceEqual(torrent.InfoHash))
#pragma warning restore CA5350
                        {
                            report.RecoveredInfoDicts[torrent.InfoHashHex] = encoded;
                            break;
                        }
                    }
                }
                catch (BencodeException)
                {
                }
            }
        }
    }

    private static void CopyFiles(ImportOptions options, ImportReport report, TextWriter log)
    {
        if (options.DataDirectory is null)
        {
            return;
        }

        if (options.InfoDictsDirectory is not null)
        {
            log.WriteLine($"Copying info dicts to {options.DataDirectory}");
            report.InfoDictFilesCopied = CopyDirectory(options.InfoDictsDirectory, Path.Combine(options.DataDirectory, "info_dicts"));
        }

        if (options.TorrentsDirectory is not null)
        {
            log.WriteLine($"Copying torrent backups to {options.DataDirectory}");
            report.TorrentFilesCopied = CopyDirectory(options.TorrentsDirectory, Path.Combine(options.DataDirectory, "torrents"));
        }

        foreach (var (hex, infoDict) in report.RecoveredInfoDicts)
        {
            var path = InfoDictPath(Path.Combine(options.DataDirectory, "info_dicts"), hex);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, infoDict);
        }
    }

    private static int CopyDirectory(string source, string destination)
    {
        source = Path.GetFullPath(source);
        destination = Path.GetFullPath(destination);
        if (string.Equals(source, destination, StringComparison.Ordinal))
        {
            return 0;
        }

        var copied = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            copied++;
        }

        return copied;
    }

    private static async Task WriteTargetAsync(
        ImportSet set, ImportOptions options, ImportReport report, TextWriter log, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(options.TargetPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connectionString = new SqliteConnectionStringBuilder { DataSource = options.TargetPath, Pooling = false }.ToString();
        var dbOptions = new DbContextOptionsBuilder<MoguDbContext>().UseSqlite(connectionString).Options;

        await using (var db = new MoguDbContext(dbOptions))
        {
            await db.Database.MigrateAsync(cancellationToken);

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            if (await HasDataAsync(db, cancellationToken))
            {
                if (!options.Force)
                {
                    throw new InvalidOperationException("Target database already contains data; use --force to wipe it first.");
                }

                log.WriteLine("Target is not empty; wiping imported tables (--force)");
                await WipeAsync(db, cancellationToken);
            }

            db.ChangeTracker.AutoDetectChangesEnabled = false;
            db.Users.AddRange(set.Users);
            db.Trackers.AddRange(set.Trackers);
            db.Torrents.AddRange(set.Torrents);
            db.TorrentFileLists.AddRange(set.FileLists);
            db.TorrentTrackers.AddRange(set.TorrentTrackers);
            db.Comments.AddRange(set.Comments);
            db.Reports.AddRange(set.Reports);
            db.AdminLog.AddRange(set.AdminLog);
            db.Bans.AddRange(set.Bans);
            db.RangeBans.AddRange(set.RangeBans);
            db.TrackerWhitelistEvents.AddRange(set.WhitelistEvents);
            await db.SaveChangesAsync(cancellationToken);

            await VerifyAsync(db, set, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        // Read the counts back from a fresh context for the summary.
        await using var check = new MoguDbContext(dbOptions);
        var tables = report.Tables;
        void Row(string legacyTable, string target, long legacyCount, long importedCount, string note = "") =>
            tables.Add(new TableSummary(legacyTable, target, legacyCount, importedCount, note));

        var hideComments = await check.Users.CountAsync(u => u.HideComments, cancellationToken);
        var withTorrent = await check.Torrents.CountAsync(t => t.HasTorrent, cancellationToken);
        Row("users", "Users", SourceCount("users"), await check.Users.CountAsync(cancellationToken));
        Row("user_preferences", "Users.HideComments", SourceCount("user_preferences"), set.PreferencesApplied,
            string.Create(CultureInfo.InvariantCulture, $"{hideComments} hide comments"));
        Row("nyaa_torrents", "Torrents", SourceCount("nyaa_torrents"), await check.Torrents.CountAsync(cancellationToken),
            string.Create(CultureInfo.InvariantCulture, $"{withTorrent} with HasTorrent"));
        Row("nyaa_statistics", "Torrents.*Count", SourceCount("nyaa_statistics"), set.StatisticsApplied);
        Row("nyaa_torrents_filelist", "TorrentFileLists", SourceCount("nyaa_torrents_filelist"), await check.TorrentFileLists.CountAsync(cancellationToken));
        Row("trackers", "Trackers", SourceCount("trackers"), await check.Trackers.CountAsync(cancellationToken));
        Row("nyaa_torrent_trackers", "TorrentTrackers", SourceCount("nyaa_torrent_trackers"), await check.TorrentTrackers.CountAsync(cancellationToken));
        Row("nyaa_comments", "Comments", SourceCount("nyaa_comments"), await check.Comments.CountAsync(cancellationToken));
        Row("nyaa_reports", "Reports", SourceCount("nyaa_reports"), await check.Reports.CountAsync(cancellationToken));
        Row("nyaa_adminlog", "AdminLog", SourceCount("nyaa_adminlog"), await check.AdminLog.CountAsync(cancellationToken));
        Row("bans", "Bans", SourceCount("bans"), await check.Bans.CountAsync(cancellationToken));
        Row("rangebans", "RangeBans", SourceCount("rangebans"), await check.RangeBans.CountAsync(cancellationToken));
        Row("nyaa_trackerapi", "TrackerWhitelistEvents", SourceCount("nyaa_trackerapi"),
            await check.TrackerWhitelistEvents.CountAsync(cancellationToken),
            options.IncludeTrackerApi ? "" : "skipped (built-in tracker whitelists from Torrents)");

        long SourceCount(string table) => set.SourceCounts.GetValueOrDefault(table);
    }

    private static async Task<bool> HasDataAsync(MoguDbContext db, CancellationToken ct) =>
        await db.Users.AnyAsync(ct) || await db.Torrents.AnyAsync(ct) || await db.Trackers.AnyAsync(ct)
        || await db.RangeBans.AnyAsync(ct) || await db.TrackerWhitelistEvents.AnyAsync(ct);

    private static async Task WipeAsync(MoguDbContext db, CancellationToken ct)
    {
        await db.TorrentTrackers.ExecuteDeleteAsync(ct);
        await db.TorrentFileLists.ExecuteDeleteAsync(ct);
        await db.Comments.ExecuteDeleteAsync(ct);
        await db.Reports.ExecuteDeleteAsync(ct);
        await db.Bans.ExecuteDeleteAsync(ct);
        await db.AdminLog.ExecuteDeleteAsync(ct);
        await db.Torrents.ExecuteDeleteAsync(ct);
        await db.Trackers.ExecuteDeleteAsync(ct);
        await db.RangeBans.ExecuteDeleteAsync(ct);
        await db.TrackerWhitelistEvents.ExecuteDeleteAsync(ct);
        await db.Users.ExecuteDeleteAsync(ct);
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM sqlite_sequence WHERE name IN ('Users', 'Torrents', 'Trackers', 'Comments', 'Reports', 'AdminLog', 'Bans', 'RangeBans', 'TrackerWhitelistEvents')",
            ct);
    }

    /// <summary>
    /// Sanity checks inside the transaction: row counts match, and SQLite's AUTOINCREMENT sequences are past the
    /// explicit ids so rows created later by the app can't collide.
    /// </summary>
    private static async Task VerifyAsync(MoguDbContext db, ImportSet set, CancellationToken ct)
    {
        Expect("Users", set.Users.Count, await db.Users.CountAsync(ct));
        Expect("Torrents", set.Torrents.Count, await db.Torrents.CountAsync(ct));
        Expect("Comments", set.Comments.Count, await db.Comments.CountAsync(ct));
        Expect("TorrentTrackers", set.TorrentTrackers.Count, await db.TorrentTrackers.CountAsync(ct));

        var sequences = await db.Database
            .SqlQueryRaw<SequenceRow>("SELECT name AS Name, seq AS Seq FROM sqlite_sequence")
            .ToDictionaryAsync(s => s.Name, s => s.Seq, ct);
        CheckSequence("Users", set.Users.Select(x => x.Id));
        CheckSequence("Torrents", set.Torrents.Select(x => x.Id));
        CheckSequence("Trackers", set.Trackers.Select(x => x.Id));
        CheckSequence("Comments", set.Comments.Select(x => x.Id));
        CheckSequence("Reports", set.Reports.Select(x => x.Id));
        CheckSequence("AdminLog", set.AdminLog.Select(x => x.Id));
        CheckSequence("Bans", set.Bans.Select(x => x.Id));
        CheckSequence("RangeBans", set.RangeBans.Select(x => x.Id));

        void CheckSequence(string table, IEnumerable<int> ids)
        {
            var max = ids.DefaultIfEmpty(0).Max();
            if (max > 0 && sequences.GetValueOrDefault(table) < max)
            {
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                    $"sqlite_sequence for {table} is {sequences.GetValueOrDefault(table)}, below the imported max id {max}."));
            }
        }

        static void Expect(string table, int expected, int actual)
        {
            if (expected != actual)
            {
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                    $"{table}: inserted {expected} rows but found {actual}."));
            }
        }
    }

    private sealed record SequenceRow(string Name, long Seq);
}
