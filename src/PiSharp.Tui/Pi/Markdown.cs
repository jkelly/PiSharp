// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/tui/src/components/markdown.ts.
using System.Text.RegularExpressions;

namespace PiSharp.Tui.Pi;

/// <summary>Default text styling applied to all Markdown text unless formatting overrides it.</summary>
public sealed record DefaultTextStyle(Func<string, string>? Color = null, Func<string, string>? BgColor = null, bool Bold = false, bool Italic = false, bool Strikethrough = false, bool Underline = false);

/// <summary>Styling functions for Markdown elements.</summary>
public sealed record MarkdownTheme
{
    public required Func<string, string> Heading { get; init; }
    public required Func<string, string> Link { get; init; }
    public required Func<string, string> LinkUrl { get; init; }
    public required Func<string, string> Code { get; init; }
    public required Func<string, string> CodeBlock { get; init; }
    public required Func<string, string> CodeBlockBorder { get; init; }
    public required Func<string, string> Quote { get; init; }
    public required Func<string, string> QuoteBorder { get; init; }
    public required Func<string, string> Hr { get; init; }
    public required Func<string, string> ListBullet { get; init; }
    public required Func<string, string> Bold { get; init; }
    public required Func<string, string> Italic { get; init; }
    public required Func<string, string> Strikethrough { get; init; }
    public required Func<string, string> Underline { get; init; }
    public Func<string, string?, List<string>>? HighlightCode { get; init; }
    public string? CodeBlockIndent { get; init; }
}

public sealed record MarkdownOptions(bool PreserveOrderedListMarkers = false, bool PreserveBackslashEscapes = false, Func<string, int, string>? Transform = null, bool RenderLatex = true);

/// <summary>Renders Markdown to styled, wrapped terminal lines.</summary>
public sealed partial class Markdown : IComponent
{
    private sealed record StyleContext(Func<string, string> ApplyText, string StylePrefix);
    private string text;
    private readonly int paddingX, paddingY;
    private readonly DefaultTextStyle? defaultStyle;
    private readonly MarkdownTheme theme;
    private readonly MarkdownOptions options;
    private string? defaultStylePrefix;
    private string? cachedText;
    private int? cachedWidth;
    private List<string>? cachedLines;
    private sealed record TokenCache(string Source, List<MarkdownToken> Tokens, int LinkCount);
    // Parsed tokens depend only on the source, so they survive theme and width invalidation; held weakly.
    private WeakReference<TokenCache>? cachedTokens;

    /// <summary>A top-level block of the last render, keyed by its source, reused by the next render.</summary>
    private sealed class RenderedBlock
    {
        /// <summary>Width and type of the following block (which decides the spacing) that the lines were rendered for.</summary>
        public int Width = -1;
        public string? NextTokenType;
        public List<string> Lines = [];
        /// <summary>Highlighted lines of the block's code, keyed by language and source; they survive a resize or new spacing.</summary>
        public readonly Dictionary<string, List<string>> HighlightedCode = new(StringComparer.Ordinal);
    }

    // The last render's top-level blocks. While a message streams only its last block changes, so a render styles and wraps only
    // that block. Each render keeps only the blocks it used. Held weakly like the tokens: only a burst of renders needs them.
    private WeakReference<Dictionary<string, RenderedBlock>>? blocks;
    // Highlighted code of the block being rendered, only set while RenderBlock runs.
    private Dictionary<string, List<string>>? blockHighlightedCode;

    public Markdown(string text, int paddingX, int paddingY, MarkdownTheme theme, DefaultTextStyle? defaultStyle = null, MarkdownOptions? options = null)
    { this.text = text; this.paddingX = paddingX; this.paddingY = paddingY; this.theme = theme; this.defaultStyle = defaultStyle; this.options = options ?? new(); }

    public void SetText(string value) { text = value; ClearRenderCache(); }

    public void Invalidate()
    {
        ClearRenderCache();
        // The theme may have changed, which changes every styled line and the default style's prefix.
        blocks = null;
        defaultStylePrefix = null;
    }

    private void ClearRenderCache() { cachedText = null; cachedWidth = null; cachedLines = null; }

    [GeneratedRegex(@"\x1b\[0m")] private static partial Regex ResetCode();

    public List<string> Render(int width)
    {
        if (cachedLines is not null && cachedText == text && cachedWidth == width) return cachedLines;
        var contentWidth = Math.Max(1, width - paddingX * 2);
        var source = options.Transform?.Invoke(text, contentWidth) ?? text;
        if (string.IsNullOrEmpty(source) || TextUtils.JsTrim(source).Length == 0) { blocks = null; cachedText = text; cachedWidth = width; cachedLines = []; return []; }
        var normalized = source.Replace("\t", "   ", StringComparison.Ordinal);
        TokenCache? tokenCache = null;
        if (cachedTokens is not null && cachedTokens.TryGetTarget(out var cached) && cached.Source == normalized) tokenCache = cached;
        if (tokenCache is null)
        {
            var lexed = MarkdownLexer.Lex(normalized, out var linkCount);
            MarkdownLexer.TrimPartialClosingFences(lexed);
            tokenCache = new TokenCache(normalized, lexed, linkCount);
            cachedTokens = new(tokenCache);
        }
        var tokens = tokenCache.Tokens;
        // A reference link resolves through a definition anywhere in the text, so blocks with the same source can render
        // differently; such texts are rare in practice and render without reusing blocks.
        var reuseBlocks = tokenCache.LinkCount == 0;
        Dictionary<string, RenderedBlock>? previousBlocks = null;
        if (blocks is not null && blocks.TryGetTarget(out var previous)) previousBlocks = previous;
        var currentBlocks = new Dictionary<string, RenderedBlock>(StringComparer.Ordinal);
        blocks = new(currentBlocks);
        var content = new List<string>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var nextTokenType = i + 1 < tokens.Count ? tokens[i + 1].Type : null;
            RenderedBlock? reused = null;
            if (reuseBlocks && !currentBlocks.TryGetValue(token.Raw, out reused)) previousBlocks?.TryGetValue(token.Raw, out reused);
            var block = reused ?? new RenderedBlock { NextTokenType = nextTokenType };
            if (block.Width != width || block.NextTokenType != nextTokenType) RenderBlock(block, token, nextTokenType, width, contentWidth);
            if (reuseBlocks) currentBlocks[token.Raw] = block;
            content.AddRange(block.Lines);
        }
        var bg = defaultStyle?.BgColor;
        var empty = new string(' ', width);
        var padding = Enumerable.Range(0, paddingY).Select(_ => bg is not null ? TextUtils.ApplyBackgroundToLine(empty, width, bg) : empty).ToList();
        var result = new List<string>(padding); result.AddRange(content); result.AddRange(padding);
        cachedText = text; cachedWidth = width; cachedLines = result;
        return result.Count > 0 ? result : [""];
    }

    /// <summary>Renders one top-level block to output lines: wrapped, with margins, and padded or backgrounded to the width.</summary>
    private void RenderBlock(RenderedBlock block, MarkdownToken token, string? nextTokenType, int width, int contentWidth)
    {
        blockHighlightedCode = block.HighlightedCode;
        var margin = new string(' ', paddingX);
        var bg = defaultStyle?.BgColor;
        var lines = new List<string>();
        foreach (var line in RenderToken(token, contentWidth, nextTokenType))
        {
            if (TerminalImage.IsImageLine(line)) { lines.Add(line); continue; }
            foreach (var wrapped in TextUtils.WrapTextWithAnsi(line, contentWidth))
            {
                var withMargins = margin + wrapped + margin;
                lines.Add(bg is not null ? TextUtils.ApplyBackgroundToLine(withMargins, width, bg) : withMargins + new string(' ', Math.Max(0, width - TextUtils.VisibleWidth(withMargins))));
            }
        }
        blockHighlightedCode = null;
        block.Lines = lines; block.Width = width; block.NextTokenType = nextTokenType;
    }

    private string ApplyDefaultStyle(string value)
    {
        if (defaultStyle is null) return value;
        var styled = value;
        if (defaultStyle.Color is { } color) styled = color(styled);
        if (defaultStyle.Bold) styled = theme.Bold(styled);
        if (defaultStyle.Italic) styled = theme.Italic(styled);
        if (defaultStyle.Strikethrough) styled = theme.Strikethrough(styled);
        if (defaultStyle.Underline) styled = theme.Underline(styled);
        return styled;
    }

    private string DefaultStylePrefix()
    {
        if (defaultStyle is null) return "";
        if (defaultStylePrefix is not null) return defaultStylePrefix;
        const string sentinel = "\0";
        var styled = ApplyDefaultStyle(sentinel);
        var index = styled.IndexOf(sentinel, StringComparison.Ordinal);
        return defaultStylePrefix = index >= 0 ? styled[..index] : "";
    }

    private static string StylePrefix(Func<string, string> style)
    {
        var styled = style("\0");
        var index = styled.IndexOf('\0');
        return index >= 0 ? styled[..index] : "";
    }

    private StyleContext DefaultContext() => new(ApplyDefaultStyle, DefaultStylePrefix());

    private List<string> RenderToken(MarkdownToken token, int width, string? nextType, StyleContext? context = null)
    {
        var lines = new List<string>();
        switch (token.Type)
        {
            case "heading":
            {
                var level = token.Depth;
                Func<string, string> headingStyle = level == 1 ? value => theme.Heading(theme.Bold(theme.Underline(value))) : value => theme.Heading(theme.Bold(value));
                var headingText = RenderInline(token.Tokens ?? [], new(headingStyle, StylePrefix(headingStyle)));
                lines.Add(level >= 3 ? headingStyle(new string('#', level) + " ") + headingText : headingText);
                if (nextType is not null && nextType != "space") lines.Add("");
                break;
            }
            case "paragraph":
                lines.Add(RenderInline(token.Tokens ?? [], context));
                if (nextType is not null and not "list" and not "space") lines.Add("");
                break;
            case "text": lines.Add(RenderInline([token], context)); break;
            case "latexBlock":
            {
                var rendered = !token.Pending && options.RenderLatex ? Latex.Render(token.Text, display: true) : null;
                // A 2D layout cannot be wrapped row by row; fall back to the raw source when it does not fit.
                if (rendered is null || rendered.Split('\n').Any(line => TextUtils.VisibleWidth(line) > width)) rendered = token.Raw.Trim();
                foreach (var line in rendered.Split('\n')) lines.Add(ApplyDefaultStyle(line));
                if (nextType is not null && nextType != "space") lines.Add("");
                break;
            }
            case "code":
            {
                var indent = theme.CodeBlockIndent ?? "  ";
                lines.Add(theme.CodeBlockBorder("```" + (token.Lang ?? "")));
                if (theme.HighlightCode is { } highlight)
                {
                    var key = (token.Lang ?? "") + "\0" + token.Text;
                    List<string>? highlighted = null;
                    if (blockHighlightedCode is null || !blockHighlightedCode.TryGetValue(key, out highlighted))
                    {
                        highlighted = highlight(token.Text, token.Lang);
                        blockHighlightedCode?.TryAdd(key, highlighted);
                    }
                    foreach (var line in highlighted) lines.Add(indent + line);
                }
                else foreach (var line in token.Text.Split('\n')) lines.Add(indent + theme.CodeBlock(line));
                lines.Add(theme.CodeBlockBorder("```"));
                if (nextType is not null && nextType != "space") lines.Add("");
                break;
            }
            case "list": lines.AddRange(RenderList(token, 0, width, context)); break;
            case "table": lines.AddRange(RenderTable(token, width, nextType, context)); break;
            case "blockquote":
            {
                Func<string, string> quoteStyle = value => theme.Quote(theme.Italic(value));
                var prefix = StylePrefix(quoteStyle);
                string ApplyQuote(string line) => prefix.Length == 0 ? quoteStyle(line) : quoteStyle(ResetCode().Replace(line, "\u001b[0m" + prefix));
                var quoteTokens = token.Tokens ?? [];
                // With no room for another border ("│ " plus at least one content column), render the nested content without one.
                // Otherwise each extra level re-wraps every child line at width 1 and the output grows exponentially with depth.
                if (width < 3)
                {
                    for (var i = 0; i < quoteTokens.Count; i++)
                        lines.AddRange(RenderToken(quoteTokens[i], width, i + 1 < quoteTokens.Count ? quoteTokens[i + 1].Type : null, context));
                    break;
                }
                var quoteWidth = width - 2;
                var quoteContext = new StyleContext(value => value, prefix);
                var quoteLines = new List<string>();
                for (var i = 0; i < quoteTokens.Count; i++)
                    quoteLines.AddRange(RenderToken(quoteTokens[i], quoteWidth, i + 1 < quoteTokens.Count ? quoteTokens[i + 1].Type : null, quoteContext));
                while (quoteLines.Count > 0 && quoteLines[^1].Length == 0) quoteLines.RemoveAt(quoteLines.Count - 1);
                foreach (var line in quoteLines)
                    foreach (var wrapped in TextUtils.WrapTextWithAnsi(ApplyQuote(line), quoteWidth)) lines.Add(theme.QuoteBorder("│ ") + wrapped);
                if (nextType is not null && nextType != "space") lines.Add("");
                break;
            }
            case "hr":
                lines.Add(theme.Hr(TextUtils.Repeat("─", Math.Min(width, 80))));
                if (nextType is not null && nextType != "space") lines.Add("");
                break;
            case "html": lines.Add(ApplyDefaultStyle(TextUtils.JsTrim(token.Raw))); break;
            case "space": lines.Add(""); break;
            default: if (token.Text.Length > 0 || token.Type is "image") lines.Add(token.Text); break;
        }
        return lines;
    }

    private string RenderInline(List<MarkdownToken> tokens, StyleContext? context = null)
    {
        var resolved = context ?? DefaultContext();
        var (apply, prefix) = (resolved.ApplyText, resolved.StylePrefix);
        string WithNewlines(string value) => string.Join('\n', value.Split('\n').Select(apply));
        var result = new System.Text.StringBuilder();
        foreach (var token in tokens)
        {
            switch (token.Type)
            {
                case "latex":
                    result.Append(WithNewlines(!token.Pending && options.RenderLatex ? Latex.Render(token.Text) ?? token.Raw : token.Raw)); break;
                case "escape": result.Append(WithNewlines(options.PreserveBackslashEscapes ? token.Raw : token.Text)); break;
                case "text":
                    if (token.Tokens is { Count: > 0 }) result.Append(RenderInline(token.Tokens, resolved));
                    else result.Append(WithNewlines(token.Text));
                    break;
                case "paragraph": result.Append(RenderInline(token.Tokens ?? [], resolved)); break;
                case "strong": result.Append(theme.Bold(RenderInline(token.Tokens ?? [], resolved))).Append(prefix); break;
                case "em": result.Append(theme.Italic(RenderInline(token.Tokens ?? [], resolved))).Append(prefix); break;
                case "codespan": result.Append(theme.Code(token.Text)).Append(prefix); break;
                case "link":
                {
                    var styled = theme.Link(theme.Underline(RenderInline(token.Tokens ?? [], resolved)));
                    if (TerminalImage.GetCapabilities().Hyperlinks) result.Append(TerminalImage.Hyperlink(styled, token.Href)).Append(prefix);
                    else
                    {
                        var comparison = token.Href.StartsWith("mailto:", StringComparison.Ordinal) ? token.Href[7..] : token.Href;
                        if (token.Text == token.Href || token.Text == comparison) result.Append(styled).Append(prefix);
                        else result.Append(styled).Append(theme.LinkUrl(" (" + token.Href + ")")).Append(prefix);
                    }
                    break;
                }
                case "br": result.Append('\n'); break;
                case "del": result.Append(theme.Strikethrough(RenderInline(token.Tokens ?? [], resolved))).Append(prefix); break;
                case "html": result.Append(WithNewlines(token.Raw)); break;
                default: result.Append(WithNewlines(token.Text)); break;
            }
        }
        var output = result.ToString();
        while (prefix.Length > 0 && output.EndsWith(prefix, StringComparison.Ordinal)) output = output[..^prefix.Length];
        return output;
    }

    [GeneratedRegex(@"^(?: {0,3})(\d{1,9}[.)])[ \t]+")] private static partial Regex OrderedMarker();
    [GeneratedRegex(@"^(?: {0,3})([-+*])(?:[ \t]+|(?=\r?\n|$))")] private static partial Regex UnorderedMarker();

    private List<string> RenderList(MarkdownToken token, int depth, int width, StyleContext? context)
    {
        var lines = new List<string>();
        var indent = TextUtils.Repeat("    ", depth);
        var start = token.Start ?? 1;
        for (var i = 0; i < token.Items.Count; i++)
        {
            var item = token.Items[i];
            var bullet = token.Ordered
                ? options.PreserveOrderedListMarkers && OrderedMarker().Match(item.Raw) is { Success: true } ordered ? ordered.Groups[1].Value + " " : $"{start + i}. "
                : options.PreserveOrderedListMarkers && UnorderedMarker().Match(item.Raw) is { Success: true } unordered ? unordered.Groups[1].Value + " " : "- ";
            var marker = bullet + (item.Task ? $"[{(item.Checked ? "x" : " ")}] " : "");
            var firstPrefix = indent + theme.ListBullet(marker);
            var continuation = indent + new string(' ', TextUtils.VisibleWidth(marker));
            var itemWidth = Math.Max(1, width - TextUtils.VisibleWidth(firstPrefix));
            var any = false;
            foreach (var child in item.Tokens ?? [])
            {
                if (child.Type == "list") { lines.AddRange(RenderList(child, depth + 1, width, context)); any = true; continue; }
                foreach (var line in RenderToken(child, itemWidth, null, context))
                    foreach (var wrapped in TextUtils.WrapTextWithAnsi(line, itemWidth)) { lines.Add((any ? continuation : firstPrefix) + wrapped); any = true; }
            }
            if (!any) lines.Add(firstPrefix);
            if (token.Loose && i < token.Items.Count - 1) lines.Add("");
        }
        return lines;
    }

    private static int LongestWord(string value, int? maxWidth = null)
    {
        var longest = Regex.Split(value, @"\s+").Where(word => word.Length > 0).Select(TextUtils.VisibleWidth).DefaultIfEmpty(0).Max();
        return maxWidth is { } max ? Math.Min(longest, max) : longest;
    }

    private static List<string> WrapCell(string value, int maxWidth, string stylePrefix = "")
    {
        var lines = TextUtils.WrapTextWithAnsi(value, Math.Max(1, maxWidth));
        return lines.Select((line, index) => line + (index < lines.Count - 1 ? "\u001b[22;23;24;25;27;28;29;39m" : "") + stylePrefix).ToList();
    }

    private List<string> RenderTable(MarkdownToken token, int availableWidth, string? nextType, StyleContext? context)
    {
        var lines = new List<string>();
        var columns = token.Header.Count;
        if (columns == 0) return lines;
        var overhead = 3 * columns + 1;
        var availableForCells = availableWidth - overhead;
        if (availableForCells < columns)
        {
            var fallback = token.Raw.Length > 0 ? TextUtils.WrapTextWithAnsi(token.Raw, availableWidth) : [];
            if (nextType is not null && nextType != "space") fallback.Add("");
            return fallback;
        }
        const int maxUnbroken = 30;
        var natural = new int[columns]; var minWords = new int[columns];
        for (var i = 0; i < columns; i++)
        {
            var header = RenderInline(token.Header[i].Tokens ?? [], context);
            natural[i] = TextUtils.VisibleWidth(header);
            minWords[i] = Math.Max(1, LongestWord(header, maxUnbroken));
        }
        foreach (var row in token.Rows)
            for (var i = 0; i < row.Count && i < columns; i++)
            {
                var cell = RenderInline(row[i].Tokens ?? [], context);
                natural[i] = Math.Max(natural[i], TextUtils.VisibleWidth(cell));
                minWords[i] = Math.Max(minWords[i], LongestWord(cell, maxUnbroken));
            }
        var minColumns = minWords.ToArray();
        var minCells = minColumns.Sum();
        if (minCells > availableForCells)
        {
            minColumns = Enumerable.Repeat(1, columns).ToArray();
            var remaining = availableForCells - columns;
            if (remaining > 0)
            {
                var totalWeight = minWords.Sum(w => Math.Max(0, w - 1));
                var growth = minWords.Select(w => totalWeight > 0 ? (int)Math.Floor((double)Math.Max(0, w - 1) / totalWeight * remaining) : 0).ToArray();
                for (var i = 0; i < columns; i++) minColumns[i] += growth[i];
                var leftover = remaining - growth.Sum();
                for (var i = 0; leftover > 0 && i < columns; i++) { minColumns[i]++; leftover--; }
            }
            minCells = minColumns.Sum();
        }
        int[] widths;
        if (natural.Sum() + overhead <= availableWidth) widths = natural.Select((w, i) => Math.Max(w, minColumns[i])).ToArray();
        else
        {
            var growPotential = natural.Select((w, i) => Math.Max(0, w - minColumns[i])).Sum();
            var extra = Math.Max(0, availableForCells - minCells);
            widths = minColumns.Select((min, i) => min + (growPotential > 0 ? (int)Math.Floor((double)Math.Max(0, natural[i] - min) / growPotential * extra) : 0)).ToArray();
            var remaining = availableForCells - widths.Sum();
            while (remaining > 0)
            {
                var grew = false;
                for (var i = 0; i < columns && remaining > 0; i++) if (widths[i] < natural[i]) { widths[i]++; remaining--; grew = true; }
                if (!grew) break;
            }
        }
        lines.Add("┌─" + string.Join("─┬─", widths.Select(w => TextUtils.Repeat("─", w))) + "─┐");
        var headerCells = token.Header.Select((cell, i) => WrapCell(RenderInline(cell.Tokens ?? [], context), widths[i], context?.StylePrefix ?? "")).ToList();
        var headerCount = headerCells.Max(c => c.Count);
        for (var line = 0; line < headerCount; line++)
            lines.Add("│ " + string.Join(" │ ", headerCells.Select((cellLines, col) =>
            {
                var value = line < cellLines.Count ? cellLines[line] : "";
                return theme.Bold(value + new string(' ', Math.Max(0, widths[col] - TextUtils.VisibleWidth(value))));
            })) + " │");
        var separator = "├─" + string.Join("─┼─", widths.Select(w => TextUtils.Repeat("─", w))) + "─┤";
        lines.Add(separator);
        for (var rowIndex = 0; rowIndex < token.Rows.Count; rowIndex++)
        {
            var row = token.Rows[rowIndex];
            var cells = row.Take(columns).Select((cell, i) => WrapCell(RenderInline(cell.Tokens ?? [], context), widths[i], context?.StylePrefix ?? "")).ToList();
            var count = cells.Count == 0 ? 0 : cells.Max(c => c.Count);
            for (var line = 0; line < count; line++)
                lines.Add("│ " + string.Join(" │ ", cells.Select((cellLines, col) =>
                {
                    var value = line < cellLines.Count ? cellLines[line] : "";
                    return value + new string(' ', Math.Max(0, widths[col] - TextUtils.VisibleWidth(value)));
                })) + " │");
            if (rowIndex < token.Rows.Count - 1) lines.Add(separator);
        }
        lines.Add("└─" + string.Join("─┴─", widths.Select(w => TextUtils.Repeat("─", w))) + "─┘");
        if (nextType is not null && nextType != "space") lines.Add("");
        return lines;
    }
}
