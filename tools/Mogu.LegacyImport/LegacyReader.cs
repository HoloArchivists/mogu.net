using Microsoft.Data.Sqlite;

namespace Mogu.LegacyImport;

internal sealed record LegacyUser(
    long Id, string Username, int Status, int Level, string? CreatedTime, string? LastLoginDate,
    byte[]? LastLoginIp, byte[]? RegistrationIp);

internal sealed record LegacyCategory(int MainId, int SubId, string MainName, string SubName);

internal sealed record LegacyTorrent(
    int Id, byte[] InfoHash, string DisplayName, string TorrentName, string Information, string Description,
    long FileSize, string Encoding, int Flags, byte[]? UploaderIp, bool HasTorrent, int CommentCount,
    string CreatedTime, string UpdatedTime, int MainCategoryId, int SubCategoryId, long? UploaderId, long? Redirect);

internal sealed record LegacyStatistics(int TorrentId, int SeedCount, int LeechCount, int DownloadCount, string? LastUpdated);

internal sealed record LegacyFileList(int TorrentId, byte[]? Blob);

internal sealed record LegacyTracker(int Id, string Uri, bool IsWebSeed, bool Disabled);

internal sealed record LegacyTorrentTracker(int TorrentId, int TrackerId, int Order);

internal sealed record LegacyComment(int Id, string? CreatedTime, string? EditedTime, string Text, int TorrentId, long? UserId);

internal sealed record LegacyReport(int Id, string? CreatedTime, string Reason, int Status, int TorrentId, long? UserId);

internal sealed record LegacyAdminLog(int Id, string? CreatedTime, string Log, long AdminId);

internal sealed record LegacyBan(int Id, string? CreatedTime, long AdminId, long? UserId, byte[]? UserIp, string Reason);

internal sealed record LegacyRangeBan(int Id, string Cidr, long MaskedCidr, long Mask, bool Enabled, string? Temp);

internal sealed record LegacyTrackerApi(int Id, byte[] InfoHash, string Method);

/// <summary>Everything read from the legacy database.</summary>
internal sealed class LegacyData
{
    public required List<LegacyCategory> SubCategories { get; init; }

    public required List<LegacyUser> Users { get; init; }

    public required Dictionary<long, bool> HideComments { get; init; }

    public required List<LegacyTorrent> Torrents { get; init; }

    public required List<LegacyStatistics> Statistics { get; init; }

    public required List<LegacyFileList> FileLists { get; init; }

    public required List<LegacyTracker> Trackers { get; init; }

    public required List<LegacyTorrentTracker> TorrentTrackers { get; init; }

    public required List<LegacyComment> Comments { get; init; }

    public required List<LegacyReport> Reports { get; init; }

    public required List<LegacyAdminLog> AdminLog { get; init; }

    public required List<LegacyBan> Bans { get; init; }

    public required List<LegacyRangeBan> RangeBans { get; init; }

    public required List<LegacyTrackerApi> TrackerApi { get; init; }

    /// <summary>Row counts of legacy tables that have no counterpart (sukebei_*, trusted_*, ...).</summary>
    public required Dictionary<string, long> UnmappedTables { get; init; }

    /// <summary>Legacy tables (by name) that exist in the source, for the summary.</summary>
    public required Dictionary<string, long> TableCounts { get; init; }
}

internal static class LegacyReader
{
    private static readonly HashSet<string> MappedTables =
    [
        "users", "user_preferences", "nyaa_torrents", "nyaa_statistics", "nyaa_torrents_filelist", "trackers",
        "nyaa_torrent_trackers", "nyaa_comments", "nyaa_reports", "nyaa_adminlog", "bans", "rangebans",
        "nyaa_trackerapi", "nyaa_main_categories", "nyaa_sub_categories", "alembic_version",

        // Static category seed of the unused sukebei site.
        "sukebei_main_categories", "sukebei_sub_categories",
    ];

    public static async Task<LegacyData> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Legacy database not found: {path}", path);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var tableNames = await QueryAsync(connection,
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'",
            r => r.GetString(0), cancellationToken);
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in tableNames)
        {
            var count = await QueryAsync(connection, $"SELECT COUNT(*) FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\"",
                r => r.GetInt64(0), cancellationToken);
            counts[table] = count[0];
        }

        foreach (var required in MappedTables.Where(t => t != "alembic_version" && !t.StartsWith("sukebei_", StringComparison.Ordinal)))
        {
            if (!counts.ContainsKey(required))
            {
                throw new InvalidDataException($"Legacy database has no '{required}' table; is this a mogu database?");
            }
        }

        return new LegacyData
        {
            TableCounts = counts,
            UnmappedTables = counts.Where(kv => !MappedTables.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            SubCategories = await QueryAsync(connection,
                """
                SELECT s.main_category_id, s.id, m.name, s.name
                FROM nyaa_sub_categories s JOIN nyaa_main_categories m ON m.id = s.main_category_id
                """,
                r => new LegacyCategory(r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetString(3)), cancellationToken),
            Users = await QueryAsync(connection,
                "SELECT id, username, status, level, created_time, last_login_date, last_login_ip, registration_ip FROM users ORDER BY id",
                r => new LegacyUser(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), Text(r, 4), Text(r, 5),
                    Blob(r, 6), Blob(r, 7)),
                cancellationToken),
            HideComments = (await QueryAsync(connection, "SELECT user_id, hide_comments FROM user_preferences",
                r => (Id: r.GetInt64(0), Hide: r.GetInt64(1) != 0), cancellationToken)).ToDictionary(p => p.Id, p => p.Hide),
            Torrents = await QueryAsync(connection,
                """
                SELECT id, info_hash, display_name, torrent_name, information, description, filesize, encoding, flags,
                       uploader_ip, has_torrent, comment_count, created_time, updated_time, main_category_id,
                       sub_category_id, uploader_id, redirect
                FROM nyaa_torrents ORDER BY id
                """,
                r => new LegacyTorrent(r.GetInt32(0), Blob(r, 1) ?? [], r.GetString(2), r.GetString(3), r.GetString(4),
                    r.GetString(5), r.GetInt64(6), r.GetString(7), r.GetInt32(8), Blob(r, 9), r.GetInt64(10) != 0,
                    r.GetInt32(11), r.GetString(12), r.GetString(13), r.GetInt32(14), r.GetInt32(15), NullableInt64(r, 16),
                    NullableInt64(r, 17)),
                cancellationToken),
            Statistics = await QueryAsync(connection,
                "SELECT torrent_id, seed_count, leech_count, download_count, last_updated FROM nyaa_statistics",
                r => new LegacyStatistics(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), Text(r, 4)),
                cancellationToken),
            FileLists = await QueryAsync(connection, "SELECT torrent_id, filelist_blob FROM nyaa_torrents_filelist",
                r => new LegacyFileList(r.GetInt32(0), Blob(r, 1)), cancellationToken),
            Trackers = await QueryAsync(connection, "SELECT id, uri, is_webseed, disabled FROM trackers ORDER BY id",
                r => new LegacyTracker(r.GetInt32(0), r.GetString(1), r.GetInt64(2) != 0, r.GetInt64(3) != 0),
                cancellationToken),
            TorrentTrackers = await QueryAsync(connection,
                "SELECT torrent_id, tracker_id, \"order\" FROM nyaa_torrent_trackers ORDER BY torrent_id, \"order\"",
                r => new LegacyTorrentTracker(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2)), cancellationToken),
            Comments = await QueryAsync(connection,
                "SELECT id, created_time, edited_time, text, torrent_id, user_id FROM nyaa_comments ORDER BY id",
                r => new LegacyComment(r.GetInt32(0), Text(r, 1), Text(r, 2), r.GetString(3), r.GetInt32(4), NullableInt64(r, 5)),
                cancellationToken),
            Reports = await QueryAsync(connection,
                "SELECT id, created_time, reason, status, torrent_id, user_id FROM nyaa_reports ORDER BY id",
                r => new LegacyReport(r.GetInt32(0), Text(r, 1), r.GetString(2), r.GetInt32(3), r.GetInt32(4), NullableInt64(r, 5)),
                cancellationToken),
            AdminLog = await QueryAsync(connection, "SELECT id, created_time, log, admin_id FROM nyaa_adminlog ORDER BY id",
                r => new LegacyAdminLog(r.GetInt32(0), Text(r, 1), r.GetString(2), r.GetInt64(3)), cancellationToken),
            Bans = await QueryAsync(connection,
                "SELECT id, created_time, admin_id, user_id, user_ip, reason FROM bans ORDER BY id",
                r => new LegacyBan(r.GetInt32(0), Text(r, 1), r.GetInt64(2), NullableInt64(r, 3), Blob(r, 4), r.GetString(5)),
                cancellationToken),
            RangeBans = await QueryAsync(connection,
                "SELECT id, cidr_string, masked_cidr, mask, enabled, \"temp\" FROM rangebans ORDER BY id",
                r => new LegacyRangeBan(r.GetInt32(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4) != 0, Text(r, 5)),
                cancellationToken),
            TrackerApi = await QueryAsync(connection, "SELECT id, info_hash, method FROM nyaa_trackerapi ORDER BY id",
                r => new LegacyTrackerApi(r.GetInt32(0), Blob(r, 1) ?? [], r.GetString(2)), cancellationToken),
        };
    }

    private static async Task<List<T>> QueryAsync<T>(
        SqliteConnection connection, string sql, Func<SqliteDataReader, T> map, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // All SQL is constant apart from quoted table names read from sqlite_master.
        command.CommandText = sql;
#pragma warning restore CA2100
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<T>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    private static string? Text(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? NullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    /// <summary>Reads a BLOB column; TEXT values (shouldn't happen) are returned as their UTF-8 bytes.</summary>
    private static byte[]? Blob(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal) switch
        {
            byte[] bytes => bytes,
            string text => System.Text.Encoding.UTF8.GetBytes(text),
            var other => throw new InvalidDataException($"Unexpected {other.GetType().Name} in BLOB column {reader.GetName(ordinal)}."),
        };
}
