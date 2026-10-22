using System.Buffers;
using System.Globalization;
using System.Text;

namespace Mogu.Core.Torrents;

/// <summary>String rules applied to uploads (from the old <c>backend.py</c>).</summary>
public static class UploadRules
{
    /// <summary>Windows reserved file names, rejected (ignoring the last extension) anywhere in a torrent's file tree.</summary>
    public static readonly IReadOnlySet<string> FilenameBlacklist = new HashSet<string>(StringComparer.Ordinal)
    {
        "con", "nul", "prn", "aux",
        "com0", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt0", "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    /// <summary>Characters rejected in file names: RIGHT-TO-LEFT OVERRIDE.</summary>
    public const string CharacterBlacklist = "\u202E";

    private static readonly SearchValues<char> BlacklistedCharacters = SearchValues.Create(CharacterBlacklist);

    /// <summary>False if any directory or file name is a reserved name or contains a blacklisted character.</summary>
    public static bool AreFilenamesAllowed(IEnumerable<string> names) =>
        !names.Any(name =>
        {
            var lastDot = name.LastIndexOf('.');
            var stem = lastDot >= 0 ? name[..lastDot] : name;
            return FilenameBlacklist.Contains(stem.ToLowerInvariant()) || name.AsSpan().ContainsAny(BlacklistedCharacters);
        });

    /// <summary>
    /// ASCII-only file name safe for the filesystem, like werkzeug's <c>secure_filename</c>:
    /// "My cool movie.mov" becomes "My_cool_movie.mov", "../../../etc/passwd" becomes "etc_passwd".
    /// </summary>
    public static string SecureFilename(string filename)
    {
        var ascii = new StringBuilder();
        foreach (var c in filename.Normalize(NormalizationForm.FormKD))
        {
            if (c < 128)
            {
                ascii.Append(c is '/' or '\\' ? ' ' : c);
            }
        }

        var joined = string.Join('_', ascii.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var result = new StringBuilder(joined.Length);
        foreach (var c in joined)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-')
            {
                result.Append(c);
            }
        }

        return result.ToString().Trim('.', '_');
    }

    /// <summary>The file name part of an uploaded file name (browsers on Windows used to send full paths).</summary>
    public static string BaseName(string filename)
    {
        var index = filename.LastIndexOfAny(['/', '\\']);
        return index >= 0 ? filename[(index + 1)..] : filename;
    }

    /// <summary>Python's <c>urllib.parse.quote</c>: UTF-8 percent-encoding of everything except <c>A-Za-z0-9_.-~</c> and <paramref name="safe"/>.</summary>
    public static string Quote(string value, string safe = "/")
    {
        var builder = new StringBuilder(value.Length);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (b < 128 && (char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or '~' || safe.Contains(c, StringComparison.Ordinal)))
            {
                builder.Append(c);
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }
}
