using System.Globalization;

namespace Mogu.LegacyImport;

public sealed record TableSummary(string Legacy, string Target, long LegacyCount, long ImportedCount, string Note = "");

public sealed record UsernameRename(long DiscordId, string Original, string Renamed);

public sealed record CommentCountDifference(int TorrentId, int Stored, int Actual);

/// <summary>Outcome of an import, printed at the end and inspected by tests.</summary>
public sealed class ImportReport
{
    public List<TableSummary> Tables { get; } = [];

    public List<string> Warnings { get; } = [];

    public List<UsernameRename> UsernameRenames { get; } = [];

    public List<CommentCountDifference> CommentCountDifferences { get; } = [];

    /// <summary>Torrents with has_torrent=1 whose info dict file was not found (null if not checked).</summary>
    public List<int>? MissingInfoDicts { get; set; }

    /// <summary>Torrents without a <c>{id}.*</c> backup file (null if not checked).</summary>
    public List<int>? MissingTorrentBackups { get; set; }

    public int InfoDictFilesCopied { get; set; }

    public int TorrentFilesCopied { get; set; }

    public void Warn(string message) => Warnings.Add(message);

    public void Write(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var c = CultureInfo.InvariantCulture;

        var w1 = Math.Max("Legacy table".Length, Tables.Max(t => t.Legacy.Length));
        var w2 = Math.Max("New table".Length, Tables.Max(t => t.Target.Length));
        writer.WriteLine();
        writer.WriteLine(string.Create(c, $"{"Legacy table".PadRight(w1)}  {"New table".PadRight(w2)}  {"Legacy",7}  {"New",7}  Note"));
        writer.WriteLine(new string('-', w1 + w2 + 28));
        foreach (var t in Tables)
        {
            writer.WriteLine(string.Create(c, $"{t.Legacy.PadRight(w1)}  {t.Target.PadRight(w2)}  {t.LegacyCount,7}  {t.ImportedCount,7}  {t.Note}"));
        }

        writer.WriteLine();
        writer.WriteLine(string.Create(c, $"Username collisions renamed: {UsernameRenames.Count}"));
        foreach (var r in UsernameRenames)
        {
            writer.WriteLine(string.Create(c, $"  discord {r.DiscordId}: '{r.Original}' -> '{r.Renamed}'"));
        }

        writer.WriteLine(string.Create(c, $"Comment count differences (stored vs actual, actual was used): {CommentCountDifferences.Count}"));
        foreach (var d in CommentCountDifferences)
        {
            writer.WriteLine(string.Create(c, $"  torrent {d.TorrentId}: {d.Stored} -> {d.Actual}"));
        }

        writer.WriteLine(MissingInfoDicts is null
            ? "Info dicts: not checked (no --info-dicts/--data-dir)"
            : string.Create(c, $"Info dicts missing for has_torrent torrents: {MissingInfoDicts.Count}{IdList(MissingInfoDicts)}"));
        writer.WriteLine(MissingTorrentBackups is null
            ? "Torrent backups: not checked (no --torrents)"
            : string.Create(c, $"Torrents without a backup .torrent file: {MissingTorrentBackups.Count}{IdList(MissingTorrentBackups)}"));
        writer.WriteLine(string.Create(c, $"Files copied: {InfoDictFilesCopied} info dicts, {TorrentFilesCopied} torrent backups"));

        writer.WriteLine(string.Create(c, $"Warnings: {Warnings.Count}"));
        foreach (var warning in Warnings)
        {
            writer.WriteLine(string.Create(c, $"  {warning}"));
        }
    }

    private static string IdList(List<int> ids) =>
        ids.Count == 0 ? "" : " (ids " + string.Join(", ", ids.Take(50)) + (ids.Count > 50 ? ", ..." : "") + ")";
}
