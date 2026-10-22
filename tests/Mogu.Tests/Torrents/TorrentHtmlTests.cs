using Microsoft.AspNetCore.Html;
using Mogu.Core.Entities;
using Mogu.Core.Text;
using Mogu.Web.Infrastructure;
using Mogu.Web.Services;

namespace Mogu.Tests.Torrents;

[TestClass]
public sealed class TorrentHtmlTests
{
    private static string Html(IHtmlContent content)
    {
        using var writer = new StringWriter();
        content.WriteTo(writer, System.Text.Encodings.Web.HtmlEncoder.Default);
        return writer.ToString();
    }

    [TestMethod]
    public void IrcChannelBecomesIrcLink() =>
        Assert.AreEqual(
            "<a href=\"irc://irc.rizon.net:6697/holo-pirates_\">#holo-pirates_@irc.rizon.net:6697</a>",
            Html(TorrentHtml.InformationAsLink("#holo-pirates_@irc.rizon.net:6697")));

    [TestMethod]
    public void HttpUrlBecomesNofollowLinkWithUnquotedText() =>
        Assert.AreEqual(
            "<a rel=\"noopener noreferrer nofollow\" href=\"https://example.com/a%20b?x=1&amp;y=2\">https://example.com/a b?x=1&amp;y=2</a>",
            Html(TorrentHtml.InformationAsLink("https://example.com/a%20b?x=1&y=2")));

    [TestMethod]
    public void UrlEscapesThatDecodeToMarkupStayEscaped() =>
        Assert.AreEqual(
            "<a rel=\"noopener noreferrer nofollow\" href=\"http://x.y/%3Cscript%3E\">http://x.y/&lt;script&gt;</a>",
            Html(TorrentHtml.InformationAsLink("http://x.y/%3Cscript%3E")));

    [TestMethod]
    [DataRow("https://example.com/\"onmouseover=\"alert(1)", "https://example.com/&quot;onmouseover=&quot;alert(1)")]
    [DataRow("https://example.com/<script>", "https://example.com/&lt;script&gt;")]
    [DataRow("javascript:alert(1)", "javascript:alert(1)")]
    [DataRow("ftp://example.com", "ftp://example.com")]
    [DataRow("HTTP://example.com", "HTTP://example.com")]
    [DataRow("#chan@host/with/slash", "#chan@host/with/slash")]
    [DataRow("<b>Just text</b> & more", "&lt;b&gt;Just text&lt;/b&gt; &amp; more")]
    [DataRow("ホロライブ", "ホロライブ")]
    public void EverythingElseIsEscapedText(string information, string expected) =>
        Assert.AreEqual(expected, Html(TorrentHtml.InformationAsLink(information)));

    [TestMethod]
    public void EscapeMultilineEncodesNewlinesAsEntities()
    {
        Assert.AreEqual("a &lt;b&gt;&#10;&#10;c&#10;d", Html(TorrentHtml.EscapeMultiline("a <b>\r\n\r\nc\nd")));
        Assert.AreEqual("", Html(TorrentHtml.EscapeMultiline(null)));
    }

    [TestMethod]
    public void SanitizeStringReplacesIllegalXmlCharacters()
    {
        Assert.AreEqual("a�b\tc\nd\re��", TextSanitizer.SanitizeString("a\u0001b\tc\nd\re￾￿"));
        Assert.AreEqual("lone � and �", TextSanitizer.SanitizeString("lone \uD800 and \uDC00"));
        Assert.AreEqual("emoji 😀 ok", TextSanitizer.SanitizeString("emoji 😀 ok"));
        const string clean = "nothing to do";
        Assert.AreSame(clean, TextSanitizer.SanitizeString(clean));
    }

    [TestMethod]
    public void FileListRendersNestedTreeAndPreExpandsSmallTopLevelFolders()
    {
        var bigFolder = string.Join(",", Enumerable.Range(0, 21).Select(i => $"\"f{i}\":1"));
        var json = $"{{\"Small\":{{\"Inner\":{{\"x.mkv\":2048}},\"<b>.txt\":1}},\"Big\":{{{bigFolder}}},\"top.mp4\":1073741824}}";

        var html = TorrentFileListRenderer.Render(json, maxFiles: 1000);

        StringAssert.Contains(html, "<a href=\"\" class=\"folder\"><i class=\"fa fa-folder-open\"></i>Small</a>\n<ul data-show=\"yes\">");
        StringAssert.Contains(html, "<i class=\"fa fa-folder\"></i>Inner</a>\n<ul>\n");
        StringAssert.Contains(html, "<i class=\"fa fa-folder\"></i>Big</a>\n<ul>\n");
        StringAssert.Contains(html, "<li><i class=\"fa fa-file\"></i>x.mkv <span class=\"file-size\">(2.0 KiB)</span></li>");
        StringAssert.Contains(html, "&lt;b&gt;.txt");
        StringAssert.Contains(html, "top.mp4 <span class=\"file-size\">(1.0 GiB)</span>");
        Assert.IsTrue(html.IndexOf("Small", StringComparison.Ordinal) < html.IndexOf("Big", StringComparison.Ordinal), "JSON order is kept");
    }

    [TestMethod]
    public void FileListFallbacks()
    {
        StringAssert.Contains(TorrentFileListRenderer.Render(null, 10), "File list is not available for this torrent.");
        StringAssert.Contains(TorrentFileListRenderer.Render("{}", 10), "File list is not available for this torrent.");
        StringAssert.Contains(TorrentFileListRenderer.Render("not json", 10), "File list is not available for this torrent.");
        StringAssert.Contains(TorrentFileListRenderer.Render("{\"a\":{\"b\":1,\"c\":2},\"d\":3}", 2), "Too many files to display.");
        StringAssert.Contains(TorrentFileListRenderer.Render("{\"a\":{\"b\":1,\"c\":2},\"d\":3}", 3), "File list</h3>");
    }

    [TestMethod]
    public void CommentEditingLimit()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var comment = new Comment { Text = "x", CreatedAt = now.AddMinutes(-10) };

        Assert.IsFalse(CommentRules.EditingLimitExceeded(comment, now, TimeSpan.Zero), "zero disables the limit");
        Assert.IsFalse(CommentRules.EditingLimitExceeded(comment, now, TimeSpan.FromMinutes(11)));
        Assert.IsTrue(CommentRules.EditingLimitExceeded(comment, now, TimeSpan.FromMinutes(10)));
    }

    [TestMethod]
    public void CommentButtonsAndEndpointRules()
    {
        var author = new User { Id = 1, Username = "author" };
        var other = new User { Id = 2, Username = "other" };
        var moderator = new User { Id = 3, Username = "mod", Level = UserLevel.Moderator };
        var admin = new User { Id = 4, Username = "admin", Level = UserLevel.Administrator };
        var open = new Torrent { Id = 10, InfoHash = new byte[20], DisplayName = "t", TorrentName = "t" };
        var locked = new Torrent { Id = 10, InfoHash = new byte[20], DisplayName = "t", TorrentName = "t", IsCommentLocked = true };
        var comment = new Comment { Id = 5, TorrentId = 10, UserId = 1, Text = "hi" };

        // Comment form
        Assert.IsTrue(CommentRules.CanComment(author, open));
        Assert.IsFalse(CommentRules.CanComment(null, open));
        Assert.IsFalse(CommentRules.CanComment(author, locked));
        Assert.IsTrue(CommentRules.CanComment(moderator, locked));

        // Buttons
        Assert.IsTrue(CommentRules.ShowEditButton(author, open, comment, limitExceeded: false));
        Assert.IsFalse(CommentRules.ShowEditButton(author, open, comment, limitExceeded: true));
        Assert.IsFalse(CommentRules.ShowEditButton(author, locked, comment, limitExceeded: false));
        Assert.IsFalse(CommentRules.ShowEditButton(admin, open, comment, limitExceeded: false));
        Assert.IsTrue(CommentRules.ShowDeleteButton(author, open, comment, limitExceeded: false));
        Assert.IsFalse(CommentRules.ShowDeleteButton(author, locked, comment, limitExceeded: false));
        Assert.IsFalse(CommentRules.ShowDeleteButton(author, open, comment, limitExceeded: true));
        Assert.IsFalse(CommentRules.ShowDeleteButton(other, open, comment, limitExceeded: false));
        Assert.IsFalse(CommentRules.ShowDeleteButton(moderator, open, comment, limitExceeded: false));
        Assert.IsTrue(CommentRules.ShowDeleteButton(admin, locked, comment, limitExceeded: true));

        // Endpoints
        Assert.IsNull(CommentRules.EditDenied(author, open, comment));
        Assert.AreEqual(403, CommentRules.EditDenied(other, open, comment));
        Assert.AreEqual(403, CommentRules.EditDenied(admin, open, comment));
        Assert.AreEqual(403, CommentRules.EditDenied(author, locked, comment));
        Assert.IsNull(CommentRules.DeleteDenied(author, open, comment, limitExceeded: false));
        Assert.AreEqual(403, CommentRules.DeleteDenied(author, open, comment, limitExceeded: true));
        Assert.AreEqual(403, CommentRules.DeleteDenied(author, locked, comment, limitExceeded: false));
        Assert.AreEqual(403, CommentRules.DeleteDenied(moderator, open, comment, limitExceeded: false));
        Assert.IsNull(CommentRules.DeleteDenied(admin, locked, comment, limitExceeded: true));
        var elsewhere = new Torrent { Id = 11, InfoHash = new byte[20], DisplayName = "t", TorrentName = "t" };
        Assert.AreEqual(400, CommentRules.DeleteDenied(author, elsewhere, comment, limitExceeded: false));
    }
}
