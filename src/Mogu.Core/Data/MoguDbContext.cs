using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Mogu.Core.Entities;

namespace Mogu.Core.Data;

public class MoguDbContext(DbContextOptions<MoguDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<MainCategory> MainCategories => Set<MainCategory>();

    public DbSet<SubCategory> SubCategories => Set<SubCategory>();

    public DbSet<Torrent> Torrents => Set<Torrent>();

    public DbSet<TorrentFileList> TorrentFileLists => Set<TorrentFileList>();

    public DbSet<Tracker> Trackers => Set<Tracker>();

    public DbSet<TorrentTracker> TorrentTrackers => Set<TorrentTracker>();

    public DbSet<Comment> Comments => Set<Comment>();

    public DbSet<Report> Reports => Set<Report>();

    public DbSet<AdminLogEntry> AdminLog => Set<AdminLogEntry>();

    public DbSet<Ban> Bans => Set<Ban>();

    public DbSet<RangeBan> RangeBans => Set<RangeBan>();

    public DbSet<TrackerWhitelistEvent> TrackerWhitelistEvents => Set<TrackerWhitelistEvent>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Everything is stored as UTC; SQLite loses DateTimeKind, so restore it on read.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MoguDbContext).Assembly);
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
}
