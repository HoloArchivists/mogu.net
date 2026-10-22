using System.Text;

namespace Mogu.Web.Services;

/// <summary>
/// One search term: the torrent name must contain any of <see cref="Alternatives"/>
/// (or none of them when <see cref="Exclude"/> is set).
/// </summary>
public sealed record SearchTermGroup(IReadOnlyList<string> Alternatives, bool Exclude);

/// <summary>
/// Parses a search query the way the help page documents it. Terms are split on whitespace
/// (shlex style: <c>"foo bar"</c> stays one term), <c>a|b</c> matches either alternative,
/// a leading <c>-</c> excludes the term and unquoted parentheses are ignored. Alternatives
/// shorter than two characters are dropped, as in the old MySQL full text search.
/// </summary>
public static class SearchTerms
{
    public const int MinimumLength = 2;

    public static IReadOnlyList<SearchTermGroup> Parse(string? query)
    {
        var groups = new List<SearchTermGroup>();
        if (string.IsNullOrWhiteSpace(query))
        {
            return groups;
        }

        var alternatives = new List<string>();
        var current = new StringBuilder();
        var exclude = false;
        var tokenStarted = false;
        var inQuotes = false;

        void EndAlternative()
        {
            var text = current.ToString();
            if (text.Length >= MinimumLength)
            {
                alternatives.Add(text);
            }

            current.Clear();
        }

        void EndToken()
        {
            EndAlternative();
            if (alternatives.Count > 0)
            {
                groups.Add(new SearchTermGroup(alternatives.ToArray(), exclude));
            }

            alternatives.Clear();
            exclude = false;
            tokenStarted = false;
        }

        foreach (var c in query)
        {
            if (inQuotes)
            {
                if (c == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (tokenStarted)
                {
                    EndToken();
                }

                continue;
            }

            if (c == '-' && !tokenStarted)
            {
                exclude = true;
                tokenStarted = true;
                continue;
            }

            tokenStarted = true;
            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;
                case '|':
                    EndAlternative();
                    break;
                case '(' or ')':
                    break;
                default:
                    current.Append(c);
                    break;
            }
        }

        // An unterminated quote simply runs to the end of the query.
        if (tokenStarted)
        {
            EndToken();
        }

        return groups;
    }

    /// <summary>Escapes <c>%</c>, <c>_</c> and the escape character itself for a LIKE pattern using <c>\</c>.</summary>
    public static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
