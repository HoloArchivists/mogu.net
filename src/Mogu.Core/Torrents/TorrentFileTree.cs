using System.Text.Json;

namespace Mogu.Core.Torrents;

/// <summary>
/// The file tree of a torrent. Serialises to the JSON stored in <see cref="Entities.TorrentFileList.Json"/>
/// (the old <c>filelist_blob</c>): directories are nested objects, files map to their size in bytes,
/// and every level lists directories first, each group sorted by name.
/// </summary>
public sealed class TorrentFileTree
{
    private readonly Dictionary<string, object> _root = new(StringComparer.Ordinal);

    /// <summary>Adds a file at <paramref name="path"/>, creating directories as needed. An empty file name only creates the directories.</summary>
    public void AddFile(IReadOnlyList<string> path, long size)
    {
        ArgumentOutOfRangeException.ThrowIfZero(path.Count);
        var directory = GetDirectory(path.Take(path.Count - 1));
        if (path[^1].Length > 0)
        {
            directory[path[^1]] = size;
        }
    }

    public void AddDirectory(params IEnumerable<string> path) => GetDirectory(path);

    /// <summary>Every directory and file name in the tree, depth first.</summary>
    public IEnumerable<string> AllNames() => Names(_root);

    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            Write(writer, _root);
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private Dictionary<string, object> GetDirectory(IEnumerable<string> path)
    {
        var current = _root;
        foreach (var name in path)
        {
            if (current.GetValueOrDefault(name) is not Dictionary<string, object> next)
            {
                // A directory replaces a file of the same name.
                next = new Dictionary<string, object>(StringComparer.Ordinal);
                current[name] = next;
            }

            current = next;
        }

        return current;
    }

    private static IEnumerable<string> Names(Dictionary<string, object> directory)
    {
        foreach (var (name, value) in directory)
        {
            yield return name;
            if (value is Dictionary<string, object> child)
            {
                foreach (var childName in Names(child))
                {
                    yield return childName;
                }
            }
        }
    }

    private static void Write(Utf8JsonWriter writer, Dictionary<string, object> directory)
    {
        writer.WriteStartObject();
        var ordered = directory
            .OrderBy(kv => kv.Value is Dictionary<string, object> ? 0 : 1)
            .ThenBy(kv => kv.Key, Utf8OrdinalComparer.Instance);
        foreach (var (name, value) in ordered)
        {
            writer.WritePropertyName(name);
            if (value is Dictionary<string, object> child)
            {
                Write(writer, child);
            }
            else
            {
                writer.WriteNumberValue((long)value);
            }
        }

        writer.WriteEndObject();
    }
}
