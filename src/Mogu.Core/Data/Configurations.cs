using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Mogu.Core.Entities;

namespace Mogu.Core.Data;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.Property(u => u.Username).HasMaxLength(32).UseCollation("NOCASE");
        builder.HasIndex(u => u.Username).IsUnique();
        builder.HasIndex(u => u.DiscordId).IsUnique();
        builder.HasIndex(u => u.ApiTokenHash).IsUnique();
        builder.Property(u => u.LastLoginIp).HasMaxLength(45);
        builder.Property(u => u.RegistrationIp).HasMaxLength(45);
        builder.Ignore(u => u.LevelTitle);
        builder.Ignore(u => u.LevelColor);
    }
}

internal sealed class MainCategoryConfiguration : IEntityTypeConfiguration<MainCategory>
{
    public void Configure(EntityTypeBuilder<MainCategory> builder)
    {
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Name).HasMaxLength(64);
        builder.HasData(CategorySeed.MainCategories);
    }
}

internal sealed class SubCategoryConfiguration : IEntityTypeConfiguration<SubCategory>
{
    public void Configure(EntityTypeBuilder<SubCategory> builder)
    {
        builder.HasKey(c => new { c.MainCategoryId, c.Id });
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Name).HasMaxLength(64);
        builder.HasOne(c => c.MainCategory).WithMany(c => c.SubCategories).HasForeignKey(c => c.MainCategoryId);
        builder.HasData(CategorySeed.SubCategories);
    }
}

internal sealed class TorrentConfiguration : IEntityTypeConfiguration<Torrent>
{
    public void Configure(EntityTypeBuilder<Torrent> builder)
    {
        builder.Property(t => t.InfoHash).HasMaxLength(20);
        builder.HasIndex(t => t.InfoHash).IsUnique();
        builder.Property(t => t.DisplayName).HasMaxLength(255).UseCollation("NOCASE");
        builder.HasIndex(t => t.DisplayName);
        builder.Property(t => t.TorrentName).HasMaxLength(255);
        builder.Property(t => t.Information).HasMaxLength(255);
        builder.Property(t => t.Encoding).HasMaxLength(32);
        builder.Property(t => t.UploaderIp).HasMaxLength(45);

        builder.HasIndex(t => t.FileSize);
        builder.HasIndex(t => t.CommentCount);
        builder.HasIndex(t => t.SeedCount);
        builder.HasIndex(t => t.LeechCount);
        builder.HasIndex(t => t.DownloadCount);
        builder.HasIndex(t => new { t.UploaderId, t.IsDeleted });
        builder.HasIndex(t => new { t.MainCategoryId, t.SubCategoryId });

        builder.HasOne(t => t.Uploader).WithMany(u => u.Torrents)
            .HasForeignKey(t => t.UploaderId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne(t => t.MainCategory).WithMany()
            .HasForeignKey(t => t.MainCategoryId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(t => t.SubCategory).WithMany()
            .HasForeignKey(t => new { t.MainCategoryId, t.SubCategoryId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(t => t.FileList).WithOne()
            .HasForeignKey<TorrentFileList>(f => f.TorrentId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(t => t.Trackers).WithOne()
            .HasForeignKey(tt => tt.TorrentId).OnDelete(DeleteBehavior.Cascade);

        builder.Ignore(t => t.InfoHashHex);
        builder.Ignore(t => t.CategoryCode);
    }
}

internal sealed class TorrentFileListConfiguration : IEntityTypeConfiguration<TorrentFileList>
{
    public void Configure(EntityTypeBuilder<TorrentFileList> builder)
    {
        builder.HasKey(f => f.TorrentId);
    }
}

internal sealed class TrackerConfiguration : IEntityTypeConfiguration<Tracker>
{
    public void Configure(EntityTypeBuilder<Tracker> builder)
    {
        builder.Property(t => t.Uri).HasMaxLength(255).UseCollation("NOCASE");
        builder.HasIndex(t => t.Uri).IsUnique();
    }
}

internal sealed class TorrentTrackerConfiguration : IEntityTypeConfiguration<TorrentTracker>
{
    public void Configure(EntityTypeBuilder<TorrentTracker> builder)
    {
        builder.HasKey(tt => new { tt.TorrentId, tt.TrackerId });
        builder.HasOne(tt => tt.Tracker).WithMany().HasForeignKey(tt => tt.TrackerId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class CommentConfiguration : IEntityTypeConfiguration<Comment>
{
    public void Configure(EntityTypeBuilder<Comment> builder)
    {
        builder.HasOne(c => c.Torrent).WithMany(t => t.Comments)
            .HasForeignKey(c => c.TorrentId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(c => c.User).WithMany(u => u.Comments)
            .HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(c => new { c.UserId, c.CreatedAt });
    }
}

internal sealed class ReportConfiguration : IEntityTypeConfiguration<Report>
{
    public void Configure(EntityTypeBuilder<Report> builder)
    {
        builder.Property(r => r.Reason).HasMaxLength(255);
        builder.HasOne(r => r.Torrent).WithMany().HasForeignKey(r => r.TorrentId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(r => r.User).WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(r => r.Status);
    }
}

internal sealed class AdminLogEntryConfiguration : IEntityTypeConfiguration<AdminLogEntry>
{
    public void Configure(EntityTypeBuilder<AdminLogEntry> builder)
    {
        builder.ToTable("AdminLog");
        builder.Property(l => l.Message).HasMaxLength(1024);
        builder.HasOne(l => l.Admin).WithMany().HasForeignKey(l => l.AdminId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(l => l.CreatedAt);
    }
}

internal sealed class BanConfiguration : IEntityTypeConfiguration<Ban>
{
    public void Configure(EntityTypeBuilder<Ban> builder)
    {
        builder.Property(b => b.Reason).HasMaxLength(2048);
        builder.Property(b => b.UserIp).HasMaxLength(45);
        builder.HasIndex(b => b.UserIp).IsUnique();
        builder.HasIndex(b => b.UserId);
        builder.HasOne(b => b.Admin).WithMany().HasForeignKey(b => b.AdminId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(b => b.User).WithMany().HasForeignKey(b => b.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RangeBanConfiguration : IEntityTypeConfiguration<RangeBan>
{
    public void Configure(EntityTypeBuilder<RangeBan> builder)
    {
        builder.Property(r => r.Cidr).HasMaxLength(18);
        builder.HasIndex(r => r.MaskedAddress);
        builder.HasIndex(r => r.Mask);
    }
}

internal sealed class TrackerWhitelistEventConfiguration : IEntityTypeConfiguration<TrackerWhitelistEvent>
{
    public void Configure(EntityTypeBuilder<TrackerWhitelistEvent> builder)
    {
        builder.Property(e => e.InfoHash).HasMaxLength(20);
    }
}
