using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using Microsoft.AspNetCore.Html;

namespace Mogu.Web.Infrastructure;

/// <summary>HTML fragments for torrent pages that the old models/templates built by hand.</summary>
public static partial class TorrentHtml
{
    /// <summary>Escapes only what HTML needs, leaving non-Latin text readable (like markupsafe).</summary>
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Create(UnicodeRanges.All);

    public static string Encode(string value) => Encoder.Encode(value);

    /// <summary>
    /// Port of <c>Torrent.information_as_link</c>: <c>#chan@host</c> becomes an <c>irc://</c> link, an
    /// http(s) URL without <c>&lt;&gt;"</c> becomes a nofollow link, anything else is escaped text.
    /// </summary>
    public static IHtmlContent InformationAsLink(string information)
    {
        ArgumentNullException.ThrowIfNull(information);

        var irc = IrcRegex().Match(information);
        if (irc.Success)
        {
            var channel = irc.Groups[1].Value;
            var host = irc.Groups[2].Value;
            return new HtmlString($"<a href=\"irc://{host}/{channel}\">#{channel}@{host}</a>");
        }

        if (UrlRegex().IsMatch(information) && information.IndexOfAny(['<', '>', '"']) < 0)
        {
            return new HtmlString(
                $"<a rel=\"noopener noreferrer nofollow\" href=\"{Encode(information)}\">{Encode(Uri.UnescapeDataString(information))}</a>");
        }

        return new HtmlString(Encode(information));
    }

    /// <summary>
    /// Escaped text with newlines written as <c>&amp;#10;</c> (Cloudflare strips blank lines otherwise),
    /// for <c>markdown-text</c> divs and textareas. main.js decodes it before rendering markdown.
    /// </summary>
    public static IHtmlContent EscapeMultiline(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return HtmlString.Empty;
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return new HtmlString(string.Join("&#10;", lines.Select(Encode)));
    }

    // Information is stored stripped, so a trailing newline (which Python's $ tolerates) can't occur.
    [GeneratedRegex(@"^#([a-zA-Z0-9\-_]+)@([a-zA-Z0-9\-_.:]+)\z")]
    private static partial Regex IrcRegex();

    [GeneratedRegex(@"^https?://[^\n]+\z")]
    private static partial Regex UrlRegex();
}
