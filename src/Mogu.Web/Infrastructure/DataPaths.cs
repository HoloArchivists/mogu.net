using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Mogu.Core.Options;

namespace Mogu.Web.Infrastructure;

/// <summary>Filesystem locations under <see cref="MoguOptions.DataDirectory"/>.</summary>
public sealed class DataPaths(IWebHostEnvironment environment, IOptions<MoguOptions> options)
{
    public string Root { get; } = Path.GetFullPath(options.Value.DataDirectory, environment.ContentRootPath);

    /// <summary>Bencoded info dicts, sharded like the old app: <c>info_dicts/aa/bb/aabbcc...</c>.</summary>
    public string InfoDictPath(string infoHashHex) =>
        Path.Combine(Root, "info_dicts", infoHashHex[..2], infoHashHex[2..4], infoHashHex);

    /// <summary>Backups of the original uploaded .torrent files.</summary>
    public string TorrentBackupDirectory => Path.Combine(Root, "torrents");

    public static string Resolve(IWebHostEnvironment environment, IConfiguration configuration) =>
        Path.GetFullPath(
            configuration[$"{MoguOptions.SectionName}:{nameof(MoguOptions.DataDirectory)}"] ?? "data",
            environment.ContentRootPath);

    /// <summary>Makes a relative SQLite <c>Data Source</c> relative to the content root rather than the working directory.</summary>
    public static string ResolveConnectionString(string? connectionString, IWebHostEnvironment environment)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString ?? "Data Source=data/mogu.db");
        if (builder.DataSource != ":memory:" && builder.Mode != SqliteOpenMode.Memory)
        {
            builder.DataSource = Path.GetFullPath(builder.DataSource, environment.ContentRootPath);
        }

        return builder.ToString();
    }
}
