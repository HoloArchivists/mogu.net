using System.Globalization;
using System.Net;
using System.Text;
using System.Xml;
using Mogu.Core.Options;
using Mogu.Web.Infrastructure;

namespace Mogu.Web.Services;

/// <summary>Writes the listing RSS feed in the same shape as the old <c>rss.xml</c> template.</summary>
public static class RssFeedWriter
{
    private const string AtomNamespace = "http://www.w3.org/2005/Atom";

    /// <param name="siteUrl">Absolute site root ending in <c>/</c>, e.g. <c>https://mogu.example/</c>.</param>
    /// <param name="label">Feed label, "Home" for database listings.</param>
    public static byte[] Write(IEnumerable<TorrentRow> torrents, MoguOptions options, TorrentLinks links, string siteUrl, string label, bool magnetLinks)
    {
        var nyaaNamespace = siteUrl + "xmlns/nyaa";
        using var stream = new MemoryStream();
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            IndentChars = "\t",
            CheckCharacters = true,
        };

        using (var xml = XmlWriter.Create(stream, settings))
        {
            xml.WriteStartDocument();
            xml.WriteStartElement("rss");
            xml.WriteAttributeString("xmlns", "atom", null, AtomNamespace);
            xml.WriteAttributeString("xmlns", "nyaa", null, nyaaNamespace);
            xml.WriteAttributeString("version", "2.0");

            xml.WriteStartElement("channel");
            Element(xml, "title", $"{options.SiteName} - {label} - {(magnetLinks ? "Magnet URI" : "Torrent File")} RSS");
            Element(xml, "description", $"RSS Feed for {label}");
            Element(xml, "link", siteUrl);
            xml.WriteStartElement("atom", "link", AtomNamespace);
            xml.WriteAttributeString("href", siteUrl + "?page=rss");
            xml.WriteAttributeString("rel", "self");
            xml.WriteAttributeString("type", "application/rss+xml");
            xml.WriteEndElement();

            foreach (var torrent in torrents)
            {
                var id = torrent.Id.ToString(CultureInfo.InvariantCulture);
                var viewUrl = siteUrl + "view/" + id;
                var categoryCode = torrent.CategoryCode;
                var categoryName = UrlHelpers.CategoryName(categoryCode);
                var size = Format.FileSize(torrent.FileSize);

                xml.WriteStartElement("item");
                Element(xml, "title", torrent.DisplayName);
                Element(xml, "link", torrent.HasTorrent && !magnetLinks
                    ? siteUrl + "download/" + id + ".torrent"
                    : links.Magnet(torrent.DisplayName, torrent.InfoHashHex));
                xml.WriteStartElement("guid");
                xml.WriteAttributeString("isPermaLink", "true");
                xml.WriteString(viewUrl);
                xml.WriteEndElement();
                Element(xml, "pubDate", Format.Rfc822(torrent.CreatedAt));

                NyaaElement(xml, "seeders", nyaaNamespace, torrent.SeedCount.ToString(CultureInfo.InvariantCulture));
                NyaaElement(xml, "leechers", nyaaNamespace, torrent.LeechCount.ToString(CultureInfo.InvariantCulture));
                NyaaElement(xml, "downloads", nyaaNamespace, torrent.DownloadCount.ToString(CultureInfo.InvariantCulture));
                NyaaElement(xml, "infoHash", nyaaNamespace, torrent.InfoHashHex);
                NyaaElement(xml, "categoryId", nyaaNamespace, categoryCode);
                NyaaElement(xml, "category", nyaaNamespace, categoryName);
                NyaaElement(xml, "size", nyaaNamespace, size);
                NyaaElement(xml, "comments", nyaaNamespace, torrent.CommentCount.ToString(CultureInfo.InvariantCulture));
                NyaaElement(xml, "trusted", nyaaNamespace, torrent.IsTrusted ? "Yes" : "No");
                NyaaElement(xml, "remake", nyaaNamespace, torrent.IsRemake ? "Yes" : "No");

                // The description is HTML inside CDATA; the old template HTML-escaped the name in it.
                var description = $"<a href=\"{viewUrl}\">#{id} | {WebUtility.HtmlEncode(torrent.DisplayName)}</a> | {size} | {categoryName} | {torrent.InfoHashHex.ToUpperInvariant()}";
                xml.WriteStartElement("description");
                WriteCData(xml, StripInvalidXmlChars(description));
                xml.WriteEndElement();

                xml.WriteEndElement();
            }

            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndDocument();
        }

        return stream.ToArray();
    }

    /// <summary>Drops characters XML 1.0 can't represent (control characters, lone surrogates).</summary>
    public static string StripInvalidXmlChars(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                builder.Append(c).Append(text[i + 1]);
                i++;
            }
            else if (XmlConvert.IsXmlChar(c))
            {
                builder.Append(c);
            }
        }

        return builder.Length == text.Length ? text : builder.ToString();
    }

    private static void Element(XmlWriter xml, string name, string value) =>
        xml.WriteElementString(name, StripInvalidXmlChars(value));

    private static void NyaaElement(XmlWriter xml, string name, string ns, string value) =>
        xml.WriteElementString("nyaa", name, ns, StripInvalidXmlChars(value));

    /// <summary>CDATA can't contain <c>]]&gt;</c>, so split it across sections.</summary>
    private static void WriteCData(XmlWriter xml, string text)
    {
        var parts = text.Split("]]>");
        for (var i = 0; i < parts.Length; i++)
        {
            var part = i < parts.Length - 1 ? parts[i] + "]]" : parts[i];
            var next = i > 0 ? ">" + part : part;
            xml.WriteCData(next);
        }
    }
}
