// A Markdown lexer producing marked's token shapes (marked 18.0.11, MIT, the parser Pi's Markdown component uses), with Pi's
// strict strikethrough tokenizer and LaTeX extensions (packages/tui/src/components/markdown.ts, Pi abe508e1b89912adde45528136c3221eb69acdd7).
// PiSharp has no marked dependency: this is an independent C# lexer over the same GFM rules, covering the block and inline
// constructs Pi renders (headings, paragraphs, fenced and indented code, lists with tasks, blockquotes, tables, rules, HTML,
// definitions, links, emphasis, code spans, strikethrough, breaks, autolinks, escapes and LaTeX).
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Tui.Pi;

/// <summary>A marked token: block and inline tokens share this shape.</summary>
public sealed class MarkdownToken
{
    public required string Type { get; set; }
    public string Raw { get; set; } = "";
    public string Text { get; set; } = "";
    public List<MarkdownToken>? Tokens { get; set; }
    public int Depth { get; set; }
    public string? Lang { get; set; }
    public bool Ordered { get; set; }
    public int? Start { get; set; }
    public bool Loose { get; set; }
    public List<MarkdownToken> Items { get; set; } = [];
    public bool Task { get; set; }
    public bool Checked { get; set; }
    public List<MarkdownToken> Header { get; set; } = [];
    public List<List<MarkdownToken>> Rows { get; set; } = [];
    public string Href { get; set; } = "";
    public string? Title { get; set; }
    public bool Pending { get; set; }
}

public static partial class MarkdownLexer
{
    [GeneratedRegex(@"^(?:[ \t]*(?:\n|$))+")] private static partial Regex Newline();
    [GeneratedRegex(@"^((?: {4}| {0,3}\t)[^\n]+(?:\n(?:[ \t]*(?:\n|$))*)?)+")] private static partial Regex IndentedCode();
    [GeneratedRegex(@"^ {0,3}(`{3,}(?=[^`\n]*(?:\n|$))|~{3,})([^\n]*)(?:\n|$)(?:|([\s\S]*?)(?:\n|$))(?: {0,3}\1[~`]* *(?=\n|$)|$)")] private static partial Regex Fences();
    [GeneratedRegex(@"^ {0,3}((?:-[\t ]*){3,}|(?:_[ \t]*){3,}|(?:\*[ \t]*){3,})(?:\n+|$)")] private static partial Regex Hr();
    [GeneratedRegex(@"^ {0,3}(#{1,6})(?=\s|$)(.*)(?:\n+|$)")] private static partial Regex Heading();
    [GeneratedRegex(@"^ {0,3}>")] private static partial Regex BlockquoteStart();
    [GeneratedRegex(@"^( {0,3})([*+-]|\d{1,9}[.)])([ \t][^\n]*|[ \t]*)(?:\n|$)")] private static partial Regex ListItemStart();
    [GeneratedRegex(@"^ {0,3}\[((?!\s*\])(?:\\.|[^\[\]\\])+)\]:[ \t]*(?:\n[ \t]*)?([^<\s][^\s]*|<.*?>)(?:(?:[ \t]+(?:\n[ \t]*)?|\n[ \t]*)(""(?:\\""?|[^""\\])*""|'[^'\n]*(?:\n[^'\n]+)*\n?'|\([^()]*\)))?[ \t]*(?:\n+|$)")] private static partial Regex Def();
    [GeneratedRegex(@"^ {0,3}(?:<(script|pre|style|textarea)[\s>][\s\S]*?(?:</\1>[^\n]*\n+|$)|<!--(?:-?>|[\s\S]*?(?:-->|$))[^\n]*(\n[ \t]*)*(?:\n|$)|</?(address|article|aside|blockquote|body|details|dialog|div|dl|figure|footer|form|h[1-6]|header|hr|html|li|main|nav|ol|p|section|summary|table|tbody|td|tfoot|th|thead|tr|ul)(?: +|\n|/?>)[\s\S]*?(?:(?:\n[ \t]*)+\n|$)|<(?!script|pre|style|textarea)([a-z][\w-]*)(?:\s+[a-zA-Z:_][\w.:-]*(?:\s*=\s*""[^""]*""|\s*=\s*'[^']*'|\s*=\s*[^\s""'=<>`]+)?)*?\s*/?>(?=[ \t]*(?:\n|$))[\s\S]*?(?:(?:\n[ \t]*)+\n|$))", RegexOptions.IgnoreCase)] private static partial Regex HtmlBlock();
    [GeneratedRegex(@"^ {0,3}(=+|-+) *(?:\n+|$)")] private static partial Regex SetextUnderline();
    [GeneratedRegex(@"^ {0,3}\|?(?: *:?-+:? *\|)*(?: *:?-+:? *)\|? *$")] private static partial Regex TableDelimiter();
    [GeneratedRegex(@"^ {0,3}(?:\$\$|\\\[)")] private static partial Regex LatexBlockStart();
    [GeneratedRegex(@"^ {0,3}\$\$[ \t]*(?:\n)?([\s\S]*?)\$\$[ \t]*(?:\n|$)")] private static partial Regex LatexDollarBlock();
    [GeneratedRegex(@"^ {0,3}\\\[[ \t]*(?:\n)?([\s\S]*?)\\\][ \t]*(?:\n|$)")] private static partial Regex LatexBracketBlock();
    [GeneratedRegex(@"^ {0,3}\\\[[ \t]*(?:\n)?([\s\S]*)$")] private static partial Regex LatexPendingBracket();
    [GeneratedRegex(@"^ {0,3}\$\$[ \t]*(?:\n)?([\s\S]*)$")] private static partial Regex LatexPendingDollar();
    [GeneratedRegex(@"\\[A-Za-z]+|[_^=+*/<>()[\]|±≤≥≠≈∈→⇒∞∫∑√-]")] private static partial Regex PendingMath();

    private static string FirstLine(string src) { var n = src.IndexOf('\n'); return n == -1 ? src : src[..n]; }
    private static bool IsBlank(string line) => line.All(c => c is ' ' or '\t');

    /// <summary>Lexes Markdown into marked-shaped block tokens with inline children.</summary>
    public static List<MarkdownToken> Lex(string markdown)
    {
        var src = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var links = new Dictionary<string, (string Href, string? Title)>(StringComparer.OrdinalIgnoreCase);
        var tokens = BlockTokens(src, true, links);
        foreach (var token in tokens) Inline(token, links);
        return tokens;
    }

    private static void Inline(MarkdownToken token, Dictionary<string, (string, string?)> links)
    {
        switch (token.Type)
        {
            case "heading" or "paragraph" or "text" when token.Tokens is not null && token.Tokens.Count == 0:
                token.Tokens = InlineTokens(token.Text, links); break;
            case "blockquote": foreach (var child in token.Tokens ?? []) Inline(child, links); break;
            case "list": foreach (var item in token.Items) foreach (var child in item.Tokens ?? []) Inline(child, links); break;
            case "table":
                foreach (var cell in token.Header) cell.Tokens = InlineTokens(cell.Text, links);
                foreach (var row in token.Rows) foreach (var cell in row) cell.Tokens = InlineTokens(cell.Text, links);
                break;
        }
    }

    private static bool InterruptsParagraph(string line, string rest)
    {
        if (Hr().IsMatch(line) || Heading().IsMatch(line) || BlockquoteStart().IsMatch(line)) return true;
        if (Regex.IsMatch(line, @"^ {0,3}(`{3,}|~{3,})")) return true;
        var item = ListItemStart().Match(line);
        if (item.Success && item.Groups[3].Value.Trim().Length > 0 && (!char.IsDigit(item.Groups[2].Value[0]) || item.Groups[2].Value.StartsWith("1", StringComparison.Ordinal))) return true;
        if (HtmlBlock().IsMatch(rest) && Regex.IsMatch(line, @"^ {0,3}<(?:[a-zA-Z/!?])")) return Regex.IsMatch(line, @"^ {0,3}</?(address|article|aside|blockquote|body|details|dialog|div|dl|figure|footer|form|h[1-6]|header|hr|html|li|main|nav|ol|p|section|summary|table|tbody|td|tfoot|th|thead|tr|ul|script|pre|style|textarea)(?:\s|/?>|$)|^ {0,3}<!--", RegexOptions.IgnoreCase);
        return false;
    }

    private static List<MarkdownToken> BlockTokens(string src, bool top, Dictionary<string, (string, string?)> links)
    {
        var tokens = new List<MarkdownToken>();
        MarkdownToken? Last() => tokens.Count > 0 ? tokens[^1] : null;
        while (src.Length > 0)
        {
            Match match;
            if ((match = Newline().Match(src)).Success && match.Length > 0)
            {
                src = src[match.Length..];
                if (match.Value.Length == 1 && tokens.Count > 0) tokens[^1].Raw += "\n";
                else tokens.Add(new() { Type = "space", Raw = match.Value });
                continue;
            }
            if (LatexBlockStart().IsMatch(src) && LatexBlock(src) is { } latex) { src = src[latex.Raw.Length..]; tokens.Add(latex); continue; }
            if ((match = IndentedCode().Match(src)).Success)
            {
                src = src[match.Length..];
                if (Last() is { Type: "paragraph" or "text" } previous)
                {
                    previous.Raw += (previous.Raw.EndsWith('\n') ? "" : "\n") + match.Value;
                    previous.Text += "\n" + match.Value.TrimEnd('\n');
                }
                else
                {
                    var code = Regex.Replace(match.Value, @"^(?: {1,4}| {0,3}\t)", "", RegexOptions.Multiline);
                    tokens.Add(new() { Type = "code", Raw = match.Value, Text = code.TrimEnd('\n') });
                }
                continue;
            }
            if ((match = Fences().Match(src)).Success)
            {
                src = src[match.Length..];
                var raw = match.Value;
                var indent = Regex.Match(raw, @"^(\s+)(?:```)").Groups[1].Value.Length;
                var text = match.Groups[3].Success ? match.Groups[3].Value : "";
                if (indent > 0) text = string.Join('\n', text.Split('\n').Select(line =>
                { var leading = line.Length - line.TrimStart(' ').Length; return leading >= indent ? line[indent..] : line.TrimStart(' '); }));
                var info = match.Groups[2].Value.Trim();
                tokens.Add(new() { Type = "code", Raw = raw, Lang = info.Length > 0 ? Regex.Replace(info, @"\\([!-/:-@\[-`{-~])", "$1") : null, Text = text });
                continue;
            }
            if ((match = Heading().Match(src)).Success)
            {
                src = src[match.Length..];
                var text = match.Groups[2].Value.Trim();
                if (text.EndsWith('#'))
                {
                    var trimmed = text.TrimEnd('#');
                    if (trimmed.Length == 0 || trimmed.EndsWith(' ') || trimmed.EndsWith('\t')) text = trimmed.Trim();
                }
                tokens.Add(new() { Type = "heading", Raw = match.Value, Depth = match.Groups[1].Value.Length, Text = text, Tokens = [] });
                continue;
            }
            if ((match = Hr().Match(src)).Success) { src = src[match.Length..]; tokens.Add(new() { Type = "hr", Raw = match.Value }); continue; }
            if (BlockquoteStart().IsMatch(src)) { var quote = Blockquote(src, links); src = src[quote.Raw.Length..]; tokens.Add(quote); continue; }
            if (ListItemStart().IsMatch(src) && List(src, links) is { } list) { src = src[list.Raw.Length..]; tokens.Add(list); continue; }
            if ((match = HtmlBlock().Match(src)).Success) { src = src[match.Length..]; tokens.Add(new() { Type = "html", Raw = match.Value, Text = match.Value }); continue; }
            if (top && (match = Def().Match(src)).Success)
            {
                src = src[match.Length..];
                var label = Regex.Replace(match.Groups[1].Value.ToLowerInvariant(), @"\s+", " ");
                var href = match.Groups[2].Value; if (href.StartsWith('<')) href = href[1..^1];
                var title = match.Groups[3].Success && match.Groups[3].Value.Length > 0 ? match.Groups[3].Value[1..^1] : null;
                links.TryAdd(label, (href, title));
                continue;
            }
            if (Table(src) is { } table) { src = src[table.Raw.Length..]; tokens.Add(table); continue; }
            {
                // Setext heading or paragraph (top level), text (inside list items).
                var lines = new List<string>(); var consumed = 0; MarkdownToken? setext = null;
                var remaining = src;
                while (remaining.Length > 0)
                {
                    var line = FirstLine(remaining);
                    if (IsBlank(line)) break;
                    if (lines.Count > 0)
                    {
                        var underline = SetextUnderline().Match(remaining);
                        if (underline.Success && top)
                        {
                            var raw = src[..consumed] + underline.Value;
                            setext = new() { Type = "heading", Raw = raw, Depth = underline.Groups[1].Value[0] == '=' ? 1 : 2, Text = string.Join('\n', lines).Trim(), Tokens = [] };
                            consumed += underline.Length; break;
                        }
                        if (InterruptsParagraph(line, remaining)) break;
                    }
                    lines.Add(line);
                    var advance = Math.Min(remaining.Length, line.Length + 1);
                    consumed += advance; remaining = remaining[advance..];
                    if (!top) break;
                }
                if (setext is not null) { src = src[consumed..]; tokens.Add(setext); continue; }
                if (lines.Count == 0) { var line = FirstLine(src); lines.Add(line); consumed = Math.Min(src.Length, line.Length + 1); }
                var rawText = src[..consumed];
                src = src[consumed..];
                var text = string.Join('\n', lines);
                if (top)
                {
                    var paragraph = text.EndsWith('\n') ? text[..^1] : text;
                    tokens.Add(new() { Type = "paragraph", Raw = rawText, Text = paragraph, Tokens = [] });
                }
                else if (Last() is { Type: "text" } previousText)
                {
                    previousText.Raw += (previousText.Raw.EndsWith('\n') ? "" : "\n") + rawText;
                    previousText.Text += "\n" + text;
                }
                else tokens.Add(new() { Type = "text", Raw = rawText, Text = text, Tokens = [] });
            }
        }
        return tokens;
    }

    private static MarkdownToken? LatexBlock(string src)
    {
        var dollar = LatexDollarBlock().Match(src);
        if (dollar.Success && dollar.Groups[1].Value.Length > 0) return new() { Type = "latexBlock", Raw = dollar.Value, Text = dollar.Groups[1].Value.Trim() };
        var bracket = LatexBracketBlock().Match(src);
        if (bracket.Success && bracket.Groups[1].Value.Length > 0) return new() { Type = "latexBlock", Raw = bracket.Value, Text = bracket.Groups[1].Value.Trim() };
        var pendingBracket = LatexPendingBracket().Match(src);
        if (pendingBracket.Success) return new() { Type = "latexBlock", Raw = pendingBracket.Value, Text = pendingBracket.Groups[1].Value, Pending = true };
        var pendingDollar = LatexPendingDollar().Match(src);
        if (pendingDollar.Success && pendingDollar.Groups[1].Value.Length > 0 && PendingMath().IsMatch(pendingDollar.Groups[1].Value))
            return new() { Type = "latexBlock", Raw = pendingDollar.Value, Text = pendingDollar.Groups[1].Value, Pending = true };
        return null;
    }

    private static MarkdownToken Blockquote(string src, Dictionary<string, (string, string?)> links)
    {
        var raw = new StringBuilder(); var content = new StringBuilder(); var remaining = src; var lazyAllowed = false;
        while (remaining.Length > 0)
        {
            var line = FirstLine(remaining);
            var advance = Math.Min(remaining.Length, line.Length + 1);
            var quote = Regex.Match(line, @"^ {0,3}> ?");
            if (quote.Success)
            {
                var inner = line[quote.Length..];
                content.Append(inner).Append('\n');
                lazyAllowed = !IsBlank(inner) && !Regex.IsMatch(inner, @"^ {0,3}(`{3,}|~{3,})");
            }
            else if (lazyAllowed && !IsBlank(line) && !InterruptsParagraph(line, remaining)) content.Append(line).Append('\n');
            else break;
            raw.Append(remaining, 0, advance);
            remaining = remaining[advance..];
        }
        var text = content.ToString().TrimEnd('\n');
        return new() { Type = "blockquote", Raw = raw.ToString(), Text = text, Tokens = BlockTokens(text, true, links) };
    }

    private static MarkdownToken? List(string src, Dictionary<string, (string, string?)> links)
    {
        var first = ListItemStart().Match(src);
        if (!first.Success) return null;
        var bullet = first.Groups[2].Value;
        var ordered = char.IsDigit(bullet[0]);
        var marker = ordered ? bullet[^1] : bullet[0];
        var list = new MarkdownToken { Type = "list", Ordered = ordered, Start = ordered ? int.Parse(bullet[..^1], System.Globalization.CultureInfo.InvariantCulture) : null };
        var raw = new StringBuilder(); var remaining = src; var endsWithBlank = false;
        while (remaining.Length > 0)
        {
            var match = ListItemStart().Match(remaining);
            if (!match.Success) break;
            var itemBullet = match.Groups[2].Value;
            if (char.IsDigit(itemBullet[0]) != ordered || (ordered ? itemBullet[^1] : itemBullet[0]) != marker) break;
            if (Hr().IsMatch(FirstLine(remaining)) && raw.Length > 0) break;
            var firstLineContent = match.Groups[3].Value;
            var contentStartsAfter = match.Groups[1].Length + itemBullet.Length;
            var spacing = firstLineContent.Length - firstLineContent.TrimStart(' ', '\t').Length;
            var blankFirst = IsBlank(firstLineContent);
            var indent = blankFirst ? contentStartsAfter + 1 : spacing > 4 ? contentStartsAfter + 1 : contentStartsAfter + spacing;
            var itemRaw = new StringBuilder(); var itemLines = new List<string>();
            var lineText = FirstLine(remaining);
            var advance = Math.Min(remaining.Length, lineText.Length + 1);
            itemRaw.Append(remaining, 0, advance);
            itemLines.Add(blankFirst ? "" : spacing > 4 ? firstLineContent[1..] : firstLineContent.TrimStart(' ', '\t'));
            remaining = remaining[advance..];
            var blankLines = 0; var lastWasBlank = blankFirst;
            while (remaining.Length > 0)
            {
                var line = FirstLine(remaining);
                var lineAdvance = Math.Min(remaining.Length, line.Length + 1);
                if (IsBlank(line))
                {
                    if (blankFirst && itemLines.Count == 1 && itemLines[0].Length == 0) break;
                    blankLines++; lastWasBlank = true;
                    itemLines.Add(""); itemRaw.Append(remaining, 0, lineAdvance); remaining = remaining[lineAdvance..];
                    continue;
                }
                var leading = line.Length - line.TrimStart(' ').Length;
                if (leading >= indent) { itemLines.Add(line[indent..]); lastWasBlank = false; itemRaw.Append(remaining, 0, lineAdvance); remaining = remaining[lineAdvance..]; continue; }
                if (lastWasBlank) break;
                // Lazy continuation of a paragraph.
                if (ListItemStart().IsMatch(line) || InterruptsParagraph(line, remaining)) break;
                itemLines.Add(line.TrimStart()); itemRaw.Append(remaining, 0, lineAdvance); remaining = remaining[lineAdvance..];
            }
            // Trailing blank lines belong between items, not to the item text.
            while (itemLines.Count > 1 && itemLines[^1].Length == 0) itemLines.RemoveAt(itemLines.Count - 1);
            var itemText = string.Join('\n', itemLines);
            var item = new MarkdownToken { Type = "list_item", Raw = itemRaw.ToString(), Text = itemText };
            var task = Regex.Match(itemText, @"^\[[ xX]\] +");
            if (task.Success) { item.Task = true; item.Checked = task.Value[1] != ' '; item.Text = itemText[task.Length..]; }
            if (endsWithBlank) list.Loose = true;
            endsWithBlank = itemRaw.ToString().EndsWith("\n\n", StringComparison.Ordinal) || Regex.IsMatch(itemRaw.ToString(), @"\n[ \t]*\n$");
            list.Items.Add(item); raw.Append(itemRaw);
        }
        if (list.Items.Count == 0) return null;
        // marked trims trailing whitespace from the last item's raw.
        var listRaw = raw.ToString();
        var lastItem = list.Items[^1];
        var trimmedLast = lastItem.Raw.TrimEnd();
        listRaw = listRaw[..^(lastItem.Raw.Length - trimmedLast.Length)];
        lastItem.Raw = trimmedLast;
        list.Raw = listRaw;
        foreach (var item in list.Items)
        {
            item.Tokens = BlockTokens(item.Text, false, links);
            if (!list.Loose && item.Tokens.Any(token => token.Type == "space" && Regex.IsMatch(token.Raw, @"\n.*\n", RegexOptions.Singleline))) list.Loose = true;
        }
        if (list.Loose)
            foreach (var item in list.Items)
            {
                item.Loose = true;
                item.Tokens = BlockTokens(item.Text, true, links);
            }
        return list;
    }

    private static List<string> SplitCells(string row, int? count = null)
    {
        var text = row.Trim();
        if (text.StartsWith('|')) text = text[1..];
        if (text.EndsWith('|') && !text.EndsWith("\\|", StringComparison.Ordinal)) text = text[..^1];
        var cells = new List<string>(); var current = new StringBuilder(); var inCode = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && text[i + 1] == '|') { current.Append('|'); i++; continue; }
            if (c == '`') inCode = !inCode;
            if (c == '|' && !inCode) { cells.Add(current.ToString().Trim()); current.Clear(); continue; }
            current.Append(c);
        }
        cells.Add(current.ToString().Trim());
        if (count is { } n)
        {
            while (cells.Count < n) cells.Add("");
            if (cells.Count > n) cells = cells.Take(n).ToList();
        }
        return cells;
    }

    private static MarkdownToken? Table(string src)
    {
        var lines = src.Split('\n');
        if (lines.Length < 2 || !lines[0].Contains('|') && !lines[1].Contains('|')) return null;
        if (!TableDelimiter().IsMatch(lines[1]) || !lines[1].Contains('-')) return null;
        var header = SplitCells(lines[0]);
        var aligns = SplitCells(lines[1]);
        if (header.Count != aligns.Count) return null;
        var raw = new StringBuilder(lines[0]).Append('\n').Append(lines[1]);
        var rows = new List<List<MarkdownToken>>();
        var index = 2;
        for (; index < lines.Length; index++)
        {
            var line = lines[index];
            if (IsBlank(line) || Hr().IsMatch(line) || Heading().IsMatch(line) || BlockquoteStart().IsMatch(line) || Regex.IsMatch(line, @"^ {0,3}(`{3,}|~{3,})") || ListItemStart().IsMatch(line)) break;
            raw.Append('\n').Append(line);
            rows.Add(SplitCells(line, header.Count).Select(cell => new MarkdownToken { Type = "cell", Text = cell }).ToList());
        }
        if (index < lines.Length) raw.Append('\n');
        var rawText = raw.ToString();
        // Consume following blank lines as marked does (\n*).
        while (rawText.Length < src.Length && src[rawText.Length] == '\n' && index < lines.Length && lines[index].Length == 0) { rawText += "\n"; index++; }
        return new() { Type = "table", Raw = rawText, Header = header.Select(cell => new MarkdownToken { Type = "cell", Text = cell }).ToList(), Rows = rows };
    }

    // ---------------------------------------------------------------- inline

    [GeneratedRegex(@"^\\([!""#$%&'()*+,\-./:;<=>?@\[\]\\^_`{|}~])")] private static partial Regex Escape();
    [GeneratedRegex(@"^(`+)([^`]|[^`][\s\S]*?[^`])\1(?!`)")] private static partial Regex CodeSpan();
    [GeneratedRegex(@"^( {2,}|\\)\n(?!\s*$)")] private static partial Regex Break();
    [GeneratedRegex(@"^(~~)(?=[^\s~])((?:\\.|[^\\])*?(?:\\.|[^\s~\\]))\1(?=[^~]|$)")] private static partial Regex StrictDel();
    [GeneratedRegex(@"^<([a-zA-Z][a-zA-Z0-9+.-]{1,31}:[^\s\x00-\x1f<>]*|[a-zA-Z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)+)>")] private static partial Regex Autolink();
    [GeneratedRegex(@"^(?:(?:ftp|https?)://|www\.)(?:[a-zA-Z0-9\-]+\.?)+[^\s<]*")] private static partial Regex Url();
    [GeneratedRegex(@"^[a-zA-Z0-9.!#$%&'*+/=?_`{|}~-]+@[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*\.[a-zA-Z]{2,}(?![-_])")] private static partial Regex Email();
    [GeneratedRegex(@"^(?:<!--[\s\S]*?-->|</?[a-zA-Z][\w-]*(?:\s+[a-zA-Z:_][\w.:-]*(?:\s*=\s*""[^""]*""|\s*=\s*'[^']*'|\s*=\s*[^\s""'=<>`]+)?)*?\s*/?>)")] private static partial Regex InlineTag();
    [GeneratedRegex(@"^!?\[((?:\[(?:\\.|[^\[\]\\])*\]|\\.|`[^`]*`|[^\[\]\\`])*?)\]\(\s*(<(?:\\.|[^\n<>\\])+>|[^\s\x00-\x1f]*)(?:\s+(""(?:\\""?|[^""\\])*""|'(?:\\'?|[^'\\])*'|\((?:\\\)?|[^)\\])*\)))?\s*\)")] private static partial Regex Link();
    [GeneratedRegex(@"^!?\[((?:\[(?:\\.|[^\[\]\\])*\]|\\.|`[^`]*`|[^\[\]\\`])*?)\]\[((?!\s*\])(?:\\.|[^\[\]\\])+)\]")] private static partial Regex RefLink();
    [GeneratedRegex(@"^!?\[((?!\s*\])(?:\\.|[^\[\]\\])+)\](?:\[\])?")] private static partial Regex NoLink();

    private const string Punctuation = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";
    private static bool IsPunct(char c) => Punctuation.Contains(c) || char.IsPunctuation(c) || char.IsSymbol(c);

    private static MarkdownToken? InlineLatex(string source)
    {
        string opening, closing;
        if (source.StartsWith("$$", StringComparison.Ordinal)) { opening = "$$"; closing = "$$"; }
        else if (source.StartsWith("\\(", StringComparison.Ordinal)) { opening = "\\("; closing = "\\)"; }
        else if (source.StartsWith("\\[", StringComparison.Ordinal)) { opening = "\\["; closing = "\\]"; }
        else if (source.StartsWith('$') && !Regex.IsMatch(source, @"^\$\s")) { opening = "$"; closing = "$"; }
        else return null;
        var closingIndex = FindClosing(source, closing, opening.Length);
        if (closingIndex >= 0 && opening == "$")
        {
            var inner = source[opening.Length..closingIndex]; var after = source[(closingIndex + 1)..];
            if (Regex.IsMatch(inner, @"\s$") || Regex.IsMatch(after, @"^\d") ||
                Regex.IsMatch(inner, @"^[A-Z_][A-Z0-9_]*(?:[^A-Za-z0-9_\s])?$") && Regex.IsMatch(after, "^[A-Za-z_][A-Za-z0-9_]*") || inner.Contains('`')) return null;
        }
        if (closingIndex < 0)
        {
            var pending = source[opening.Length..];
            if (opening.StartsWith('\\') || PendingMath().IsMatch(pending)) return new() { Type = "latex", Raw = source, Text = pending, Pending = true };
            return null;
        }
        var text = source[opening.Length..closingIndex];
        if (text.Length == 0 || text.Contains('\n')) return null;
        return new() { Type = "latex", Raw = source[..(closingIndex + closing.Length)], Text = text };
    }

    private static int FindClosing(string source, string closing, int start)
    {
        var index = source.IndexOf(closing, start, StringComparison.Ordinal);
        while (index >= 0 && IsEscaped(source, index)) index = source.IndexOf(closing, index + closing.Length, StringComparison.Ordinal);
        return index;
    }
    private static bool IsEscaped(string source, int index)
    {
        var backslashes = 0;
        for (var position = index - 1; position >= 0 && source[position] == '\\'; position--) backslashes++;
        return backslashes % 2 == 1;
    }

    /// <summary>Lexes inline Markdown.</summary>
    public static List<MarkdownToken> InlineTokens(string src, Dictionary<string, (string Href, string? Title)>? links = null)
    {
        links ??= [];
        var tokens = new List<MarkdownToken>();
        var text = new StringBuilder();
        var position = 0;
        char previous = '\n';
        void FlushText()
        {
            if (text.Length == 0) return;
            var value = text.ToString(); text.Clear();
            if (tokens.Count > 0 && tokens[^1].Type == "text") { tokens[^1].Raw += value; tokens[^1].Text += value; }
            else tokens.Add(new() { Type = "text", Raw = value, Text = value });
        }
        void Add(MarkdownToken token) { FlushText(); tokens.Add(token); }
        while (position < src.Length)
        {
            var rest = src[position..];
            Match match;
            var c = rest[0];
            if (c == '\\' && (match = Escape().Match(rest)).Success) { Add(new() { Type = "escape", Raw = match.Value, Text = match.Groups[1].Value }); position += match.Length; previous = match.Value[^1]; continue; }
            if (c is '$' or '\\' && InlineLatex(rest) is { } latex) { Add(latex); position += latex.Raw.Length; previous = latex.Raw[^1]; continue; }
            if (c == '<')
            {
                if ((match = Autolink().Match(rest)).Success)
                {
                    var target = match.Groups[1].Value;
                    var href = target.Contains('@') && !target.Contains(':') ? "mailto:" + target : target;
                    Add(new() { Type = "link", Raw = match.Value, Text = target, Href = href, Tokens = [new() { Type = "text", Raw = target, Text = target }] });
                    position += match.Length; previous = '>'; continue;
                }
                if ((match = InlineTag().Match(rest)).Success) { Add(new() { Type = "html", Raw = match.Value, Text = match.Value }); position += match.Length; previous = '>'; continue; }
            }
            if (c is '[' or '!')
            {
                if ((match = Link().Match(rest)).Success)
                {
                    var image = match.Value[0] == '!';
                    var label = match.Groups[1].Value;
                    var href = match.Groups[2].Value.Trim();
                    if (href.StartsWith('<') && href.EndsWith('>')) href = href[1..^1];
                    href = Regex.Replace(href, @"\\([!-/:-@\[-`{-~])", "$1");
                    var title = match.Groups[3].Success && match.Groups[3].Value.Length > 0 ? match.Groups[3].Value[1..^1] : null;
                    Add(image ? new() { Type = "image", Raw = match.Value, Text = label, Href = href, Title = title }
                        : new() { Type = "link", Raw = match.Value, Text = label, Href = href, Title = title, Tokens = InlineTokens(label, links) });
                    position += match.Length; previous = ')'; continue;
                }
                var reference = RefLink().Match(rest);
                if (!reference.Success) reference = NoLink().Match(rest);
                if (reference.Success)
                {
                    var key = Regex.Replace((reference.Groups.Count > 2 && reference.Groups[2].Success ? reference.Groups[2].Value : reference.Groups[1].Value).ToLowerInvariant(), @"\s+", " ");
                    if (links.TryGetValue(key, out var definition))
                    {
                        var image = reference.Value[0] == '!';
                        var label = reference.Groups[1].Value;
                        Add(image ? new() { Type = "image", Raw = reference.Value, Text = label, Href = definition.Href, Title = definition.Title }
                            : new() { Type = "link", Raw = reference.Value, Text = label, Href = definition.Href, Title = definition.Title, Tokens = InlineTokens(label, links) });
                        position += reference.Length; previous = ']'; continue;
                    }
                }
            }
            if (c is '*' or '_' && EmStrong(src, position, previous, links) is { } emphasis) { Add(emphasis); position += emphasis.Raw.Length; previous = emphasis.Raw[^1]; continue; }
            if (c == '`' && (match = CodeSpan().Match(rest)).Success)
            {
                var code = match.Groups[2].Value.Replace('\n', ' ');
                if (code.Length > 2 && code[0] == ' ' && code[^1] == ' ' && code.Any(ch => ch != ' ')) code = code[1..^1];
                Add(new() { Type = "codespan", Raw = match.Value, Text = code });
                position += match.Length; previous = '`'; continue;
            }
            if (c is ' ' or '\\' && (match = Break().Match(rest)).Success) { Add(new() { Type = "br", Raw = match.Value }); position += match.Length; previous = '\n'; continue; }
            if (c == '~' && (match = StrictDel().Match(rest)).Success)
            {
                Add(new() { Type = "del", Raw = match.Value, Text = match.Groups[2].Value, Tokens = InlineTokens(match.Groups[2].Value, links) });
                position += match.Length; previous = '~'; continue;
            }
            if ((c is 'h' or 'f' or 'w') && (position == 0 || !char.IsLetterOrDigit(previous)) && (match = Url().Match(rest)).Success)
            {
                var url = TrimUrl(match.Value);
                var href = url.StartsWith("www.", StringComparison.Ordinal) ? "http://" + url : url;
                Add(new() { Type = "link", Raw = url, Text = url, Href = href, Tokens = [new() { Type = "text", Raw = url, Text = url }] });
                position += url.Length; previous = url[^1]; continue;
            }
            if (char.IsLetterOrDigit(c) && (position == 0 || !char.IsLetterOrDigit(previous) && previous is not ('.' or '+' or '-' or '_')) && (match = Email().Match(rest)).Success)
            {
                Add(new() { Type = "link", Raw = match.Value, Text = match.Value, Href = "mailto:" + match.Value, Tokens = [new() { Type = "text", Raw = match.Value, Text = match.Value }] });
                position += match.Length; previous = match.Value[^1]; continue;
            }
            text.Append(c); previous = c; position++;
        }
        FlushText();
        return tokens;
    }

    /// <summary>GFM extended autolink trailing punctuation and unbalanced parenthesis trimming.</summary>
    private static string TrimUrl(string url)
    {
        while (url.Length > 0)
        {
            var last = url[^1];
            if (last is '?' or '!' or '.' or ',' or ':' or '*' or '_' or '~' or '\'' or '"') { url = url[..^1]; continue; }
            if (last == ')' && url.Count(ch => ch == ')') > url.Count(ch => ch == '(')) { url = url[..^1]; continue; }
            if (last == ';' && Regex.IsMatch(url, @"&[a-zA-Z0-9]+;$")) { url = url[..url.LastIndexOf('&')]; continue; }
            break;
        }
        return url;
    }

    private static MarkdownToken? EmStrong(string src, int start, char previous, Dictionary<string, (string, string?)> links)
    {
        var marker = src[start];
        var runEnd = start;
        while (runEnd < src.Length && src[runEnd] == marker) runEnd++;
        var openLength = runEnd - start;
        var next = runEnd < src.Length ? src[runEnd] : '\n';
        var prevWhite = char.IsWhiteSpace(previous); var nextWhite = char.IsWhiteSpace(next);
        var leftFlanking = !nextWhite && (!IsPunct(next) || prevWhite || IsPunct(previous));
        if (!leftFlanking) return null;
        if (marker == '_')
        {
            var rightFlankingOpen = !prevWhite && (!IsPunct(previous) || nextWhite || IsPunct(next));
            if (rightFlankingOpen && !IsPunct(previous)) return null;
        }
        // Find a closing run.
        for (var i = runEnd; i < src.Length; i++)
        {
            if (src[i] == '`') { var code = CodeSpan().Match(src[i..]); if (code.Success) { i += code.Length - 1; continue; } }
            if (src[i] == '\\') { i++; continue; }
            if (src[i] != marker) continue;
            var closeStart = i; var closeEnd = i;
            while (closeEnd < src.Length && src[closeEnd] == marker) closeEnd++;
            var before = src[closeStart - 1]; var after = closeEnd < src.Length ? src[closeEnd] : '\n';
            var rightFlanking = !char.IsWhiteSpace(before) && (!IsPunct(before) || char.IsWhiteSpace(after) || IsPunct(after));
            if (marker == '_' && rightFlanking)
            {
                var leftFlankingClose = !char.IsWhiteSpace(after) && (!IsPunct(after) || char.IsWhiteSpace(before) || IsPunct(before));
                if (leftFlankingClose && !IsPunct(after)) rightFlanking = false;
            }
            if (!rightFlanking || closeStart == runEnd) { i = closeEnd - 1; continue; }
            var closeLength = closeEnd - closeStart;
            var use = Math.Min(Math.Min(openLength, closeLength), 3);
            if (use >= 2 && openLength >= 2 && closeLength >= 2)
            {
                if (use == 3)
                {
                    var innerText = src[runEnd..closeStart];
                    var raw = src[(runEnd - 3)..(closeStart + 3)];
                    var strong = new MarkdownToken { Type = "strong", Raw = raw[1..^1], Text = innerText, Tokens = InlineTokens(innerText, links) };
                    return Offset(new() { Type = "em", Raw = raw, Text = raw[1..^1], Tokens = [strong] }, start, runEnd - 3);
                }
                var inner = src[runEnd..closeStart];
                return Offset(new() { Type = "strong", Raw = src[(runEnd - 2)..(closeStart + 2)], Text = inner, Tokens = InlineTokens(inner, links) }, start, runEnd - 2);
            }
            var emInner = src[runEnd..closeStart];
            return Offset(new() { Type = "em", Raw = src[(runEnd - 1)..(closeStart + 1)], Text = emInner, Tokens = InlineTokens(emInner, links) }, start, runEnd - 1);
        }
        return null;

        // Unused opening delimiters before the consumed ones stay literal text.
        static MarkdownToken? Offset(MarkdownToken token, int runStart, int consumedStart) => consumedStart == runStart ? token : null;
    }

    /// <summary>Pi's trimPartialClosingFences: hides a streamed, incomplete closing fence.</summary>
    public static void TrimPartialClosingFences(IReadOnlyList<MarkdownToken> tokens)
    {
        if (tokens.Count == 0) return;
        var token = tokens[^1];
        if (token.Type == "list") { if (token.Items.Count > 0) TrimPartialClosingFences(token.Items[^1].Tokens ?? []); return; }
        if (token.Type == "blockquote") { TrimPartialClosingFences(token.Tokens ?? []); return; }
        if (token.Type != "code") return;
        var marker = Regex.Match(token.Raw, "^(`{3,}|~{3,})");
        var lastLine = token.Raw.Split('\n')[^1];
        if (!marker.Success || lastLine.Length == 0 || lastLine.Length >= marker.Value.Length || lastLine != new string(marker.Value[0], lastLine.Length)) return;
        token.Text = Regex.Replace(token.Text[..Math.Max(0, token.Text.Length - lastLine.Length)], "\n$", "");
    }
}
