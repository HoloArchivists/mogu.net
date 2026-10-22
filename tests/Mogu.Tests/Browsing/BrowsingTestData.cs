using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mogu.Core.Data;
using Mogu.Core.Entities;
using Mogu.Core.Options;

namespace Mogu.Tests.Browsing;

/// <summary>A migrated SQLite database in a temporary file, deleted on dispose.</summary>
internal sealed class TempDatabase : IDisposable
{
    public TempDatabase()
    {
        Directory = Path.Combine(Path.GetTempPath(), "mogu-tests-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        ConnectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Directory, "mogu.db"), Pooling = false }.ToString();
    }

    public string Directory { get; }

    public string ConnectionString { get; }

    public MoguDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<MoguDbContext>().UseSqlite(ConnectionString).Options);

    public async Task<MoguDbContext> CreateMigratedContextAsync()
    {
        var db = CreateContext();
        await db.Database.MigrateAsync();
        return db;
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the OS cleans the temp directory eventually.
        }
    }
}

internal sealed class FixedOptions<T>(T value) : IOptionsSnapshot<T>
    where T : class
{
    public T Value { get; } = value;

    public T Get(string? name) => Value;
}

internal static class Seed
{
    private static readonly DateTime BaseTime = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static int counter;

    public static User User(string name, UserLevel level = UserLevel.Regular) => new()
    {
        Username = name,
        Level = level,
        DiscordId = Interlocked.Increment(ref counter),
        CreatedAt = BaseTime,
    };

    public static Torrent Torrent(
        string name,
        User? uploader = null,
        int main = 1,
        int sub = 1,
        bool hidden = false,
        bool deleted = false,
        bool anonymous = false,
        bool remake = false,
        bool trusted = false,
        bool complete = false,
        long size = 1000,
        int seeders = 0,
        int comments = 0)
    {
        var n = Interlocked.Increment(ref counter);
        return new Torrent
        {
            InfoHash = SHA256.HashData(Encoding.UTF8.GetBytes(name + "/" + n))[..20],
            DisplayName = name,
            TorrentName = name + ".torrent",
            Uploader = uploader,
            MainCategoryId = main,
            SubCategoryId = sub,
            IsHidden = hidden,
            IsDeleted = deleted,
            IsAnonymous = anonymous,
            IsRemake = remake,
            IsTrusted = trusted,
            IsComplete = complete,
            FileSize = size,
            SeedCount = seeders,
            CommentCount = comments,
            HasTorrent = true,
            CreatedAt = BaseTime.AddMinutes(n),
            UpdatedAt = BaseTime.AddMinutes(n),
        };
    }
}
