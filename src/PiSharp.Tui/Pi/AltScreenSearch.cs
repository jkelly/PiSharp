// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/tui/src/alt-screen-search.ts.
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Tui.Pi;

public sealed class AltScreenSearchSegment { public int Row; public int StartCol; public int EndCol; }
public sealed record AltScreenSearchMatch(List<AltScreenSearchSegment> Segments)
{
    public string Key => Segments.Count == 0 ? "" : $"{Segments[0].Row}:{Segments[0].StartCol}:{Segments[^1].Row}:{Segments[^1].EndCol}";
}

/// <summary>Caches the searchable corpus of rendered transcript lines and their matches.</summary>
public sealed class AltScreenSearchIndex
{
    private sealed record Span(int TextStart, int TextEnd, int Row, int StartCol, int EndCol, bool Linear);
    private sealed record Corpus(string Text, List<Span> Spans);
    private List<string>? sourceLines;
    private Corpus? corpus;
    private string? normalizedQuery;
    private List<AltScreenSearchMatch> matches = [];

    private static Corpus Build(IReadOnlyList<string> lines)
    {
        var text = new StringBuilder(); var spans = new List<Span>(); var pendingSeparator = false;
        void Separator() { if (!pendingSeparator) return; text.Append(' '); pendingSeparator = false; }
        for (var row = 0; row < lines.Count; row++)
        {
            var line = TextUtils.StripTerminalSequences(lines[row]);
            var column = 0;
            if (line.All(c => c is >= ' ' and <= '~'))
            {
                var index = 0;
                while (index < line.Length)
                {
                    if (line[index] == ' ') { if (text.Length > 0) pendingSeparator = true; column++; index++; continue; }
                    var end = index + 1;
                    while (end < line.Length && line[end] != ' ') end++;
                    Separator();
                    var chunk = line[index..end];
                    spans.Add(new(text.Length, text.Length + chunk.Length, row, column, column + chunk.Length, true));
                    text.Append(chunk); column += chunk.Length; index = end;
                }
            }
            else
                foreach (var grapheme in TextUtils.Graphemes(line))
                {
                    var width = TextUtils.VisibleWidth(grapheme);
                    if (grapheme.All(TextUtils.IsJsWhitespace)) { if (text.Length > 0) pendingSeparator = true; column += width; continue; }
                    Separator();
                    spans.Add(new(text.Length, text.Length + grapheme.Length, row, column, column + width, false));
                    text.Append(grapheme); column += width;
                }
            if (text.Length > 0) pendingSeparator = true;
        }
        return new(text.ToString(), spans);
    }

    private static string Normalize(string query) => TextUtils.JsTrim(Regex.Replace(query, @"\s+", " "));

    private static List<AltScreenSearchMatch> Find(Corpus corpus, string query)
    {
        if (query.Length == 0) return [];
        var result = new List<AltScreenSearchMatch>(); var spanIndex = 0;
        foreach (Match match in Regex.Matches(corpus.Text, Regex.Escape(query), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var start = match.Index; var end = start + match.Length;
            while (spanIndex < corpus.Spans.Count && corpus.Spans[spanIndex].TextEnd <= start) spanIndex++;
            var segments = new List<AltScreenSearchSegment>();
            for (var index = spanIndex; index < corpus.Spans.Count; index++)
            {
                var span = corpus.Spans[index];
                if (span.TextStart >= end) break;
                if (span.TextEnd <= start) continue;
                var startCol = span.Linear ? span.StartCol + Math.Max(start, span.TextStart) - span.TextStart : span.StartCol;
                var endCol = span.Linear ? span.StartCol + Math.Min(end, span.TextEnd) - span.TextStart : span.EndCol;
                if (segments.Count > 0 && segments[^1].Row == span.Row && startCol <= segments[^1].EndCol) segments[^1].EndCol = Math.Max(segments[^1].EndCol, endCol);
                else segments.Add(new() { Row = span.Row, StartCol = startCol, EndCol = endCol });
            }
            while (spanIndex < corpus.Spans.Count && corpus.Spans[spanIndex].TextEnd <= end) spanIndex++;
            if (segments.Count > 0) result.Add(new(segments));
        }
        return result;
    }

    public (List<AltScreenSearchMatch> Matches, bool Changed) Search(IReadOnlyList<string> lines, string query)
    {
        var sourceChanged = sourceLines is null || sourceLines.Count != lines.Count || !sourceLines.SequenceEqual(lines, StringComparer.Ordinal);
        if (sourceChanged || corpus is null) { sourceLines = [.. lines]; corpus = Build(lines); }
        var normalized = Normalize(query);
        var changed = sourceChanged || normalized != normalizedQuery;
        if (changed) { normalizedQuery = normalized; matches = Find(corpus, normalized); }
        return (matches, changed);
    }

    public static List<AltScreenSearchMatch> FindMatches(IReadOnlyList<string> lines, string query)
    {
        var normalized = Normalize(query);
        return normalized.Length == 0 ? [] : Find(Build(lines), normalized);
    }
}

/// <summary>The transcript search box shown in fullscreen mode.</summary>
public sealed class AltScreenSearchComponent(Action<string> onQueryChange, Func<string, bool, string>? navigationButtonStyle = null) : IComponent, IFocusable, IInputHandler
{
    private readonly Input input = new(" ", "Find in transcript", text => "\u001b[2m" + text + "\u001b[22m");
    private readonly Func<string, bool, string> buttonStyle = navigationButtonStyle ?? ((text, _) => text);
    private int resultCount, resultIndex = -1, previousStart = -1, previousEnd = -1, nextStart = -1, nextEnd = -1;
    private int? hovered;
    private bool focused;
    public bool Focused { get => focused; set { focused = value; input.Focused = value; } }
    public void SetResult(int index, int count) { resultIndex = index; resultCount = count; }
    public int? GetNavigationDirectionAt(int row, int column)
    {
        if (row != 2) return null;
        if (column >= previousStart && column < previousEnd) return -1;
        if (column >= nextStart && column < nextEnd) return 1;
        return null;
    }
    public bool SetHoveredNavigationDirection(int? direction)
    {
        if (direction == hovered) return false;
        hovered = direction; return true;
    }
    public void HandleInput(string data)
    {
        var previous = input.GetValue();
        input.HandleInput(data);
        var query = input.GetValue();
        if (query != previous) onQueryChange(query);
    }
    public void Invalidate() => input.Invalidate();

    private static string FormatKey(string? key) => key is null ? "Unbound" : string.Join('+', key.Split('+').Select(part =>
        OperatingSystem.IsMacOS() && part.Equals("alt", StringComparison.OrdinalIgnoreCase) ? "Option" : part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));

    public List<string> Render(int width)
    {
        var safeWidth = Math.Max(1, width); var inner = Math.Max(0, safeWidth - 2);
        var kb = KeybindingsManager.Global;
        var previousKey = FormatKey(kb.GetKeys("tui.altScreen.searchPrevious").FirstOrDefault());
        var nextKey = FormatKey(kb.GetKeys("tui.altScreen.searchNext").FirstOrDefault());
        var query = input.GetValue();
        var result = query.Length == 0 ? "" : resultCount == 0 ? "No matches" : $"{resultIndex + 1}/{resultCount}";
        var visibleResult = TextUtils.TruncateToWidth(result, Math.Max(0, inner - 3), "");
        var resultText = visibleResult.Length > 0 ? "\u001b[2m " + visibleResult + " \u001b[22m" : "";
        var inputWidth = Math.Max(0, inner - TextUtils.VisibleWidth(resultText));
        var inputLine = TextUtils.TruncateToWidth(input.Render(Math.Max(1, inputWidth)).FirstOrDefault() ?? "", inputWidth, "");
        var content = inputLine + new string(' ', Math.Max(0, inputWidth - TextUtils.VisibleWidth(inputLine))) + resultText;
        string previousButton = "↑ " + previousKey, nextButton = "↓ " + nextKey, separator = " · ";
        const int outerGap = 1;
        var availableControls = Math.Max(0, inner - outerGap * 2 - 1);
        var controlsWidth = TextUtils.VisibleWidth(previousButton) + TextUtils.VisibleWidth(separator) + TextUtils.VisibleWidth(nextButton);
        if (controlsWidth > availableControls) { previousButton = "↑"; nextButton = "↓"; separator = " "; controlsWidth = 3; }
        var showButtons = controlsWidth <= availableControls;
        var rendered = showButtons ? buttonStyle(previousButton, hovered == -1) + separator + buttonStyle(nextButton, hovered == 1) : "";
        var outerGaps = showButtons ? outerGap * 2 : 0;
        var rightRule = rendered.Length > 0 && inner > controlsWidth + outerGaps ? 1 : 0;
        var leftRule = Math.Max(0, inner - (showButtons ? controlsWidth : 0) - outerGaps - rightRule);
        var start = 1 + leftRule + outerGap;
        previousStart = showButtons ? start : -1;
        previousEnd = showButtons ? start + TextUtils.VisibleWidth(previousButton) : -1;
        nextStart = showButtons ? previousEnd + TextUtils.VisibleWidth(separator) : -1;
        nextEnd = showButtons ? nextStart + TextUtils.VisibleWidth(nextButton) : -1;
        if (safeWidth == 1) return ["┌", "│", "└"];
        return
        [
            "┌" + TextUtils.Repeat("─", inner) + "┐",
            "│" + content + "│",
            "└" + TextUtils.Repeat("─", leftRule) + (rendered.Length > 0 ? " " : "") + rendered + (rendered.Length > 0 ? " " : "") + TextUtils.Repeat("─", rightRule) + "┘",
        ];
    }
}
