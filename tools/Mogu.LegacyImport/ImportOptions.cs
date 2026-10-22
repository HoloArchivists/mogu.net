namespace Mogu.LegacyImport;

/// <summary>Command line options of the legacy importer.</summary>
public sealed record ImportOptions
{
    public const string Usage = """
        Usage: Mogu.LegacyImport --source <legacy.db> --target <mogu.db> [options]

          --source <file>            Legacy mogu SQLite database (opened read-only).
          --target <file>            New Mogu.NET SQLite database (created/migrated if needed).
          --data-dir <dir>           New app data directory (Mogu:DataDirectory) to copy files into.
          --info-dicts <dir>         Legacy info_dicts directory (<old base>/info_dicts); copied to <data-dir>/info_dicts.
          --torrents <dir>           Legacy torrent backup directory; copied to <data-dir>/torrents.
          --mark-missing-torrents    Set HasTorrent=false for torrents whose info dict file is missing.
          --include-tracker-api      Also import the nyaa_trackerapi queue as TrackerWhitelistEvents (skipped by default).
          --force                    Wipe all imported tables in a non-empty target first.
          --help                     Show this help.
        """;

    public required string SourcePath { get; init; }

    public required string TargetPath { get; init; }

    public string? DataDirectory { get; init; }

    public string? InfoDictsDirectory { get; init; }

    public string? TorrentsDirectory { get; init; }

    public bool MarkMissingTorrents { get; init; }

    public bool IncludeTrackerApi { get; init; }

    public bool Force { get; init; }

    /// <summary>Parses <paramref name="args"/>; returns null when help was requested.</summary>
    /// <exception cref="FormatException">The arguments are invalid.</exception>
    public static ImportOptions? Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? source = null, target = null, dataDir = null, infoDicts = null, torrents = null;
        bool markMissing = false, trackerApi = false, force = false;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string Value()
            {
                if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new FormatException($"Missing value for {arg}.");
                }

                return args[++i];
            }

            switch (arg)
            {
                case "--source": source = Value(); break;
                case "--target": target = Value(); break;
                case "--data-dir": dataDir = Value(); break;
                case "--info-dicts": infoDicts = Value(); break;
                case "--torrents": torrents = Value(); break;
                case "--mark-missing-torrents": markMissing = true; break;
                case "--include-tracker-api": trackerApi = true; break;
                case "--force": force = true; break;
                case "--help" or "-h": return null;
                default: throw new FormatException($"Unknown argument '{arg}'.");
            }
        }

        if (source is null || target is null)
        {
            throw new FormatException("--source and --target are required.");
        }

        if ((infoDicts is not null || torrents is not null) && dataDir is null)
        {
            throw new FormatException("--info-dicts and --torrents require --data-dir.");
        }

        if (markMissing && infoDicts is null && dataDir is null)
        {
            throw new FormatException("--mark-missing-torrents requires --info-dicts or --data-dir to check against.");
        }

        return new ImportOptions
        {
            SourcePath = Path.GetFullPath(source),
            TargetPath = Path.GetFullPath(target),
            DataDirectory = dataDir is null ? null : Path.GetFullPath(dataDir),
            InfoDictsDirectory = infoDicts is null ? null : Path.GetFullPath(infoDicts),
            TorrentsDirectory = torrents is null ? null : Path.GetFullPath(torrents),
            MarkMissingTorrents = markMissing,
            IncludeTrackerApi = trackerApi,
            Force = force,
        };
    }
}
