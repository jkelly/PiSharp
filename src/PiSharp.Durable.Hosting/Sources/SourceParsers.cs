// PiSharp-only (decision 0010, M3): source parsers. Each turns a bounded response into items with stable identities and normalized text.
// Normalization, documented in decision 0010: an item's ID is its feed ID (RSS guid, Atom id, RSS 1.0 rdf:about, GitHub release ID), else
// its canonical link, else a hash of its title and date. A canonical link has a lowercase scheme and host, no default port, user info or
// fragment, and keeps its query except well-known tracking parameters (utm_*, fbclid, gclid, mc_cid, mc_eid, igshid). XML with a DTD is
// refused and no external entity is ever resolved; HTML is reduced to text and nothing in it runs.
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace PiSharp.Durable.Hosting;

/// <summary>One parsed item.</summary>
/// <param name="PublishedAt">Unix milliseconds, when the source dates it.</param>
internal sealed record SourceItemDraft(string ItemId, string Title, string? Url, double? PublishedAt, string Text);

/// <summary>A parsed response.</summary>
/// <param name="NextPage">The next page of a paged feed or release list.</param>
internal sealed record ParsedSource(string? Title, IReadOnlyList<SourceItemDraft> Items, Uri? NextPage);

internal static partial class SourceParsers
{
    /// <summary>The parser of a source kind and its version; a new version re-announces nothing by itself but changes content hashes.</summary>
    public static (string Parser, string Version) Of(string kind) => kind switch
    {
        "feed" => ("feed", "1"),
        "github-releases" => ("github-releases", "1"),
        _ => ("page", "1"),
    };

    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Rss1 = "http://purl.org/rss/1.0/";
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Content = "http://purl.org/rss/1.0/modules/content/";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    /// <summary>Parses RSS 2.0, RSS 1.0 (RDF) or Atom. Duplicate IDs keep their first entry in document order.</summary>
    /// <exception cref="RetrievalException">The body is not well-formed XML, has a DTD, or is not a feed (<c>parse</c>).</exception>
    public static ParsedSource Feed(byte[] body, Uri baseUrl, int maxText)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(body), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                MaxCharactersFromEntities = 1024,
                MaxCharactersInDocument = 16L * 1024 * 1024,
            });
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException error)
        {
            throw new RetrievalException("parse", "The feed is not well-formed XML: " + error.Message);
        }
        var root = document.Root!;
        var items = new List<SourceItemDraft>();
        string? title;
        Uri? next = null;
        if (root.Name == Atom + "feed")
        {
            title = Text(root.Element(Atom + "title"));
            next = Link(root.Elements(Atom + "link"), "next", baseUrl);
            foreach (var entry in root.Elements(Atom + "entry"))
            {
                var link = Link(entry.Elements(Atom + "link"), "alternate", baseUrl)?.ToString();
                var content = entry.Element(Atom + "content") ?? entry.Element(Atom + "summary");
                items.Add(Item(Text(entry.Element(Atom + "id")), Text(entry.Element(Atom + "title")), link,
                    Date(Text(entry.Element(Atom + "updated")) ?? Text(entry.Element(Atom + "published"))), Body(content), baseUrl, maxText));
            }
        }
        else if (root.Name.LocalName == "rss")
        {
            var channel = root.Element("channel") ?? throw new RetrievalException("parse", "The RSS feed has no channel.");
            title = Text(channel.Element("title"));
            next = Link(channel.Elements(Atom + "link"), "next", baseUrl);
            foreach (var entry in channel.Elements("item"))
            {
                var guid = entry.Element("guid");
                var link = Text(entry.Element("link")) ??
                    (guid is not null && (string?)guid.Attribute("isPermaLink") is not "false" ? Text(guid) : null);
                items.Add(Item(Text(guid), Text(entry.Element("title")), link,
                    Date(Text(entry.Element("pubDate")) ?? Text(entry.Element(Dc + "date"))),
                    Body(entry.Element(Content + "encoded") ?? entry.Element("description")), baseUrl, maxText));
            }
        }
        else if (root.Name == Rdf + "RDF")
        {
            title = Text(root.Element(Rss1 + "channel")?.Element(Rss1 + "title"));
            foreach (var entry in root.Elements(Rss1 + "item"))
                items.Add(Item((string?)entry.Attribute(Rdf + "about"), Text(entry.Element(Rss1 + "title")), Text(entry.Element(Rss1 + "link")),
                    Date(Text(entry.Element(Dc + "date"))), Body(entry.Element(Content + "encoded") ?? entry.Element(Rss1 + "description")), baseUrl, maxText));
        }
        else throw new RetrievalException("parse", $"The document is not an RSS or Atom feed (its root is {root.Name.LocalName}).");
        return new ParsedSource(title, Distinct(items), next);
    }

    /// <summary>Parses a GitHub releases list (<c>GET /repos/{owner}/{repo}/releases</c>): drafts are skipped, and prereleases unless
    /// asked for.</summary>
    /// <exception cref="RetrievalException">The body is not a release list (<c>parse</c>).</exception>
    public static ParsedSource GitHubReleases(byte[] body, string? link, bool includePrereleases, int maxText)
    {
        var items = new List<SourceItemDraft>();
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new RetrievalException("parse", "The GitHub answer is not a release list.");
            foreach (var release in document.RootElement.EnumerateArray())
            {
                if (release.ValueKind != JsonValueKind.Object || Bool(release, "draft") || (!includePrereleases && Bool(release, "prerelease"))) continue;
                if (!release.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number) continue;
                var tag = String(release, "tag_name") ?? "";
                var name = String(release, "name") is { Length: > 0 } given ? given : tag;
                var url = String(release, "html_url") is { } html && Artifacts.IsSafeUrl(html) ? Canonical(html, null) : null;
                var text = Bound(Normalize(tag + "\n\n" + (String(release, "body") ?? "")), maxText);
                items.Add(new SourceItemDraft("release:" + id.GetRawText(), Title(name, tag), url, Date(String(release, "published_at")), text));
            }
        }
        catch (JsonException error)
        {
            throw new RetrievalException("parse", "The GitHub answer is not valid JSON: " + error.Message);
        }
        return new ParsedSource(null, Distinct(items), NextLink(link));
    }

    /// <summary>Extracts a page's title and text as one item identified by the page's canonical URL.</summary>
    public static ParsedSource Page(byte[] body, string? contentType, Uri pageUrl, int maxText)
    {
        var media = contentType?.Split(';')[0].Trim().ToLowerInvariant();
        var text = Decode(body, contentType);
        string? title = null;
        if (media is null or "text/html" or "application/xhtml+xml")
        {
            title = TitleTag().Match(text) is { Success: true } match ? Normalize(WebUtility.HtmlDecode(match.Groups[1].Value)) : null;
            text = HtmlText(text);
        }
        else if (media is not ("text/plain" or "text/markdown")) throw new RetrievalException("parse", $"A page source reads HTML or plain text, not {media}.");
        var url = Canonical(pageUrl.ToString(), null)!;
        return new ParsedSource(title, [new SourceItemDraft("page:" + Short(url), Title(title, url), url, null, Bound(Normalize(text), maxText))], null);
    }

    /// <summary>The canonical form of an HTTP(S) URL resolved against <paramref name="baseUrl"/>; null when it is not one.</summary>
    public static string? Canonical(string? raw, Uri? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!(baseUrl is null ? Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri) : Uri.TryCreate(baseUrl, raw.Trim(), out uri)) ||
            uri.Scheme is not ("http" or "https"))
            return null;
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair => pair.Split('=')[0].ToLowerInvariant() is var name && !name.StartsWith("utm_", StringComparison.Ordinal) &&
                name is not ("fbclid" or "gclid" or "mc_cid" or "mc_eid" or "igshid"))
            .ToList();
        var builder = new UriBuilder(uri) { Scheme = uri.Scheme.ToLowerInvariant(), Host = uri.IdnHost.ToLowerInvariant(), UserName = "", Password = "", Fragment = "",
            Query = query.Count == 0 ? "" : string.Join('&', query) };
        if (uri.IsDefaultPort) builder.Port = -1;
        var canonical = builder.Uri.ToString();
        return canonical.Length <= 2000 ? canonical : null;
    }

    /// <summary>Reduces HTML to text: scripts, styles and comments go, block elements break lines, entities decode.</summary>
    /// <exception cref="RetrievalException">The markup took too long to reduce (<c>parse</c>).</exception>
    public static string HtmlText(string html)
    {
        try
        {
            var text = Hidden().Replace(html, " ");
            text = Blocks().Replace(text, "\n");
            text = Tags().Replace(text, " ");
            return WebUtility.HtmlDecode(text);
        }
        catch (RegexMatchTimeoutException)
        {
            throw new RetrievalException("parse", "The markup is too complex to reduce to text.");
        }
    }

    private static SourceItemDraft Item(string? id, string? title, string? link, double? date, string text, Uri baseUrl, int maxText)
    {
        var url = Canonical(link, baseUrl);
        var body = Bound(Normalize(text), maxText);
        var identity = id is { Length: > 0 } given ? "id:" + Short(given.Trim())
            : url is not null ? "url:" + Short(url)
            : "hash:" + CanonicalJson.HashText((title ?? "") + "\n" + (date?.ToString(CultureInfo.InvariantCulture) ?? ""))[7..39];
        return new SourceItemDraft(identity, Title(title, url ?? identity), url, date, body);
    }

    /// <summary>Keeps the first item of each ID, in document order.</summary>
    private static List<SourceItemDraft> Distinct(List<SourceItemDraft> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return [.. items.Where(item => seen.Add(item.ItemId))];
    }

    private static string Title(string? title, string fallback)
    {
        var text = Normalize(title ?? "");
        if (text.Length == 0) text = fallback;
        return text.Length <= 300 ? text : text[..299] + "…";
    }

    private static string Short(string value) => value.Length <= 200 ? value : CanonicalJson.HashText(value)[7..];

    private static string? Text(XElement? element) => element is null ? null : element.Value.Trim() is { Length: > 0 } value ? value : null;

    /// <summary>An element's text; HTML (Atom <c>type="html"</c>, RSS descriptions, <c>content:encoded</c>) reduced to text, XHTML
    /// flattened.</summary>
    private static string Body(XElement? element)
    {
        if (element is null) return "";
        var type = (string?)element.Attribute("type");
        if (type == "xhtml") return string.Join(' ', element.DescendantNodes().OfType<XText>().Select(node => node.Value));
        return type == "text" ? element.Value : HtmlText(element.Value);
    }

    private static Uri? Link(IEnumerable<XElement> links, string rel, Uri baseUrl)
    {
        foreach (var link in links)
        {
            var kind = (string?)link.Attribute("rel") ?? "alternate";
            if (kind == rel && Canonical((string?)link.Attribute("href"), baseUrl) is { } href) return new Uri(href);
        }
        return null;
    }

    private static Uri? NextLink(string? header)
    {
        if (header is null) return null;
        foreach (var part in header.Split(','))
        {
            var match = LinkPart().Match(part);
            if (match.Success && match.Groups[2].Value.Split(' ').Contains("next") && Canonical(match.Groups[1].Value, null) is { } next) return new Uri(next);
        }
        return null;
    }

    private static bool Bool(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Unix milliseconds of an RFC 822 (RSS) or RFC 3339 (Atom, GitHub) date; null when it cannot be read.</summary>
    internal static double? Date(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Trim();
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var parsed))
            return parsed.ToUnixTimeMilliseconds();
        // RFC 822: an optional weekday, a named or numeric zone, and two-digit years.
        var match = Rfc822().Match(value);
        if (!match.Success) return null;
        var zone = match.Groups["zone"].Value.ToUpperInvariant() switch
        {
            "GMT" or "UT" or "UTC" or "Z" => "+00:00",
            "EST" => "-05:00", "EDT" => "-04:00", "CST" => "-06:00", "CDT" => "-05:00",
            "MST" => "-07:00", "MDT" => "-06:00", "PST" => "-08:00", "PDT" => "-07:00",
            var numeric when numeric.Length == 5 => numeric[..3] + ":" + numeric[3..],
            _ => null,
        };
        var year = match.Groups["year"].Value is { Length: 2 } shortYear ? "20" + shortYear : match.Groups["year"].Value;
        var time = match.Groups["time"].Value.Length == 5 ? match.Groups["time"].Value + ":00" : match.Groups["time"].Value;
        return zone is not null && DateTimeOffset.TryParseExact($"{match.Groups["day"].Value.PadLeft(2, '0')} {match.Groups["month"].Value} {year} {time} {zone}",
            "dd MMM yyyy HH:mm:ss zzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var rfc)
            ? rfc.ToUnixTimeMilliseconds() : null;
    }

    private static string Decode(byte[] body, string? contentType)
    {
        var charset = contentType is null ? null : Charset().Match(contentType) is { Success: true } match ? match.Groups[1].Value.Trim('"', '\'') : null;
        Encoding encoding;
        try { encoding = charset is null ? Encoding.UTF8 : Encoding.GetEncoding(charset); }
        catch (ArgumentException) { encoding = Encoding.UTF8; }
        using var reader = new StreamReader(new MemoryStream(body), encoding, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>Collapses runs of spaces within lines and blank lines between them.</summary>
    private static string Normalize(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n')
            .Select(line => Spaces().Replace(line, " ").Trim());
        var builder = new StringBuilder();
        var blank = false;
        foreach (var line in lines)
        {
            if (line.Length == 0) { blank = builder.Length > 0; continue; }
            if (builder.Length > 0) builder.Append(blank ? "\n\n" : "\n");
            builder.Append(line);
            blank = false;
        }
        return builder.ToString();
    }

    private static string Bound(string text, int max) => text.Length <= max ? text : text[..max];

    [GeneratedRegex(@"<(script|style|noscript|template|svg|iframe|object|title)\b[^>]*>.*?</\1\s*>|<!--.*?-->", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, 2000)]
    private static partial Regex Hidden();

    [GeneratedRegex(@"<\s*(br|/p|/div|/li|/h[1-6]|/tr|/section|/article|/blockquote|/pre|hr)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 2000)]
    private static partial Regex Blocks();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.CultureInvariant, 2000)]
    private static partial Regex Tags();

    [GeneratedRegex(@"<title\b[^>]*>(.*?)</title\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, 2000)]
    private static partial Regex TitleTag();

    [GeneratedRegex(@"[ \t\f\v ]+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    [GeneratedRegex(@"charset\s*=\s*([^;\s]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Charset();

    [GeneratedRegex(@"^\s*<([^>]+)>\s*;\s*rel\s*=\s*""?([^"";]+)""?", RegexOptions.CultureInvariant)]
    private static partial Regex LinkPart();

    [GeneratedRegex(@"^(?:[A-Za-z]{3},\s*)?(?<day>\d{1,2})\s+(?<month>[A-Za-z]{3})\s+(?<year>\d{2}|\d{4})\s+(?<time>\d{2}:\d{2}(?::\d{2})?)\s*(?<zone>[A-Za-z]{1,3}|[+-]\d{4})$", RegexOptions.CultureInvariant)]
    private static partial Regex Rfc822();
}
