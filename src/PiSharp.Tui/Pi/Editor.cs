// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/tui/src/components/editor.ts and packages/tui/src/editor-component.ts.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Tui.Pi;

/// <summary>The contract of the interactive editor, implemented by <see cref="Editor"/> and custom editors.</summary>
public interface IEditorComponent : IComponent, IInputHandler
{
    string GetText();
    void SetText(string text);
    Action<string>? OnSubmit { get; set; }
    Action<string>? OnChange { get; set; }
    void AddToHistory(string text) { }
    void InsertTextAtCursor(string text) => SetText(GetText() + text);
    string GetExpandedText() => GetText();
    void SetAutocompleteProvider(IAutocompleteProvider provider) { }
    Func<string, string>? BorderColor { get => null; set { } }
    void SetPaddingX(int padding) { }
    void SetAutocompleteMaxVisible(int maxVisible) { }
}

public sealed record EditorTheme(Func<string, string> BorderColor, SelectListTheme SelectList);
public sealed record EditorOptions(int PaddingX = 0, int AutocompleteMaxVisible = 5);
public readonly record struct TextChunk(string Text, int StartIndex, int EndIndex);
/// <summary>A grapheme (or merged paste marker) and its UTF-16 index.</summary>
public readonly record struct Segment(string Text, int Index);

/// <summary>The multi-line prompt editor: word wrap, history, kill ring, undo, paste markers and autocomplete.</summary>
public partial class Editor : IEditorComponent, IFocusable, IMouseHandler
{
    [GeneratedRegex(@"\[paste #(\d+)( (\+\d+ lines|\d+ chars))?\]")] private static partial Regex PasteMarker();
    [GeneratedRegex(@"^\[paste #(\d+)( (\+\d+ lines|\d+ chars))?\]$")] private static partial Regex PasteMarkerSingle();
    [GeneratedRegex(@"\x1b\[(\d+);5u")] private static partial Regex CsiUControl();
    private static bool IsPasteMarker(string segment) => segment.Length >= 10 && PasteMarkerSingle().IsMatch(segment);

    private sealed class State
    {
        public List<string> Lines = [""];
        public int CursorLine, CursorCol;
        public State Clone() => new() { Lines = [.. Lines], CursorLine = CursorLine, CursorCol = CursorCol };
    }
    private sealed record Snapshot(State State, Dictionary<int, string> Pastes, int PasteCounter);
    private readonly record struct LayoutLine(string Text, bool HasCursor, int CursorPos);
    private readonly record struct VisualLine(int LogicalLine, int StartCol, int Length);

    private State state = new();
    public bool Focused { get; set; }
    protected readonly ITui Tui;
    private readonly EditorTheme theme;
    private int paddingX;
    private int lastWidth = 80, renderedVisibleLineCount = 1, renderedAutocompleteHeight;
    private int scrollOffset;
    public Func<string, string>? BorderColor { get; set; }
    private IAutocompleteProvider? autocompleteProvider;
    private List<string> triggerCharacters = ["@", "#"];
    private SelectList? autocompleteList;
    private string? autocompleteState;
    private string autocompletePrefix = "";
    private int autocompleteMaxVisible = 5;
    private CancellationTokenSource? autocompleteAbort;
    private IDisposable? autocompleteDebounce;
    private Task autocompleteTask = Task.CompletedTask;
    private int autocompleteStartToken, autocompleteRequestId;
    private Dictionary<int, string> pastes = [];
    private int pasteCounter;
    private string pasteBuffer = "";
    private bool inPaste;
    private readonly List<string> history = [];
    private int historyIndex = -1;
    private State? historyDraft;
    private readonly KillRing killRing = new();
    private string? lastAction;
    private string? jumpMode;
    private int? preferredVisualCol, snappedFromCursorCol;
    private readonly UndoStack<Snapshot> undoStack = new();
    public Action<string>? OnSubmit { get; set; }
    public Action<string>? OnChange { get; set; }
    public bool DisableSubmit { get; set; }

    public Editor(ITui tui, EditorTheme theme, EditorOptions? options = null)
    {
        Tui = tui; this.theme = theme; BorderColor = theme.BorderColor;
        options ??= new();
        paddingX = Math.Max(0, options.PaddingX);
        autocompleteMaxVisible = Math.Max(3, Math.Min(20, options.AutocompleteMaxVisible));
    }

    private IEnumerable<Segment> Graphemes(string text)
    {
        var index = 0;
        var baseSegments = TextUtils.Graphemes(text).Select(g => { var s = new Segment(g, index); index += g.Length; return s; }).ToList();
        return MergeMarkers(text, baseSegments.Select(s => (s.Text, s.Index)).ToList()).Select(s => new Segment(s.Text, s.Index));
    }

    private IEnumerable<WordSegment> Words(string text)
    {
        var words = WordSegmenter.Segment(text).ToList();
        var merged = MergeMarkers(text, words.Select(w => (w.Segment, w.Index)).ToList());
        var lookup = words.ToDictionary(w => w.Index, w => w.IsWordLike);
        return merged.Select(s => new WordSegment(s.Text, s.Index, !IsPasteMarker(s.Text) && lookup.GetValueOrDefault(s.Index)));
    }

    private List<(string Text, int Index)> MergeMarkers(string text, List<(string Text, int Index)> baseSegments)
    {
        if (pastes.Count == 0 || !text.Contains("[paste #", StringComparison.Ordinal)) return baseSegments;
        var markers = new List<(int Start, int End)>();
        foreach (Match match in PasteMarker().Matches(text))
            if (int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && pastes.ContainsKey(id)) markers.Add((match.Index, match.Index + match.Length));
        if (markers.Count == 0) return baseSegments;
        var result = new List<(string, int)>(); var markerIndex = 0;
        foreach (var segment in baseSegments)
        {
            while (markerIndex < markers.Count && markers[markerIndex].End <= segment.Index) markerIndex++;
            if (markerIndex < markers.Count && segment.Index >= markers[markerIndex].Start && segment.Index < markers[markerIndex].End)
            {
                if (segment.Index == markers[markerIndex].Start) result.Add((text[markers[markerIndex].Start..markers[markerIndex].End], markers[markerIndex].Start));
            }
            else result.Add(segment);
        }
        return result;
    }

    /// <summary>Splits a line into word-wrapped chunks (character breaks for over-long words, CJK breaks anywhere).</summary>
    public static List<TextChunk> WordWrapLine(string line, int maxWidth, IReadOnlyList<Segment>? preSegmented = null)
    {
        if (line.Length == 0 || maxWidth <= 0) return [new("", 0, 0)];
        if (TextUtils.VisibleWidth(line) <= maxWidth) return [new(line, 0, line.Length)];
        var chunks = new List<TextChunk>();
        IReadOnlyList<Segment> segments = preSegmented ?? SegmentGraphemes(line);
        int currentWidth = 0, chunkStart = 0, wrapIndex = -1, wrapWidth = 0;
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var width = TextUtils.VisibleWidth(segment.Text);
            var isWs = !IsPasteMarker(segment.Text) && TextUtils.IsWhitespaceChar(segment.Text);
            if (currentWidth + width > maxWidth)
            {
                if (wrapIndex >= 0 && currentWidth - wrapWidth + width <= maxWidth)
                {
                    chunks.Add(new(line[chunkStart..wrapIndex], chunkStart, wrapIndex));
                    chunkStart = wrapIndex; currentWidth -= wrapWidth;
                }
                else if (chunkStart < segment.Index)
                {
                    chunks.Add(new(line[chunkStart..segment.Index], chunkStart, segment.Index));
                    chunkStart = segment.Index; currentWidth = 0;
                }
                wrapIndex = -1;
            }
            if (width > maxWidth)
            {
                var sub = WordWrapLine(segment.Text, maxWidth);
                for (var j = 0; j < sub.Count - 1; j++) chunks.Add(new(sub[j].Text, segment.Index + sub[j].StartIndex, segment.Index + sub[j].EndIndex));
                chunkStart = segment.Index + sub[^1].StartIndex;
                currentWidth = TextUtils.VisibleWidth(sub[^1].Text);
                wrapIndex = -1;
                continue;
            }
            currentWidth += width;
            if (i + 1 < segments.Count)
            {
                var next = segments[i + 1];
                if (isWs && (IsPasteMarker(next.Text) || !TextUtils.IsWhitespaceChar(next.Text))) { wrapIndex = next.Index; wrapWidth = currentWidth; }
                else if (!isWs && !TextUtils.IsWhitespaceChar(next.Text))
                {
                    var cjk = !IsPasteMarker(segment.Text) && TextUtils.IsCjkBreak(segment.Text);
                    var nextCjk = !IsPasteMarker(next.Text) && TextUtils.IsCjkBreak(next.Text);
                    if (cjk || nextCjk) { wrapIndex = next.Index; wrapWidth = currentWidth; }
                }
            }
        }
        chunks.Add(new(line[chunkStart..], chunkStart, line.Length));
        return chunks;
    }

    private static List<Segment> SegmentGraphemes(string text)
    {
        var index = 0; var result = new List<Segment>();
        foreach (var grapheme in TextUtils.Graphemes(text)) { result.Add(new(grapheme, index)); index += grapheme.Length; }
        return result;
    }

    private static string CreateScrollBorder(string direction, int hidden, int width)
    {
        var available = Math.Max(0, width);
        var label = $" {direction} {hidden} more ";
        var labelWidth = TextUtils.VisibleWidth(label);
        if (labelWidth + 2 <= available)
        {
            var left = (available - labelWidth) / 2;
            return TextUtils.Repeat("─", left) + label + TextUtils.Repeat("─", available - left - labelWidth);
        }
        var indicator = $"─── {direction} {hidden} more ";
        var remaining = available - TextUtils.VisibleWidth(indicator);
        if (remaining >= 0) return indicator + TextUtils.Repeat("─", remaining);
        var ellipsis = "..."[..Math.Min(3, available)];
        return TextUtils.SliceByColumn(indicator, 0, available - TextUtils.VisibleWidth(ellipsis), true) + ellipsis;
    }

    public int GetPaddingX() => paddingX;
    public void SetPaddingX(int padding) { var next = Math.Max(0, padding); if (paddingX != next) { paddingX = next; Tui.RequestRender(); } }
    public int GetAutocompleteMaxVisible() => autocompleteMaxVisible;
    public void SetAutocompleteMaxVisible(int maxVisible)
    {
        var next = Math.Max(3, Math.Min(20, maxVisible));
        if (autocompleteMaxVisible != next) { autocompleteMaxVisible = next; Tui.RequestRender(); }
    }
    public void SetAutocompleteProvider(IAutocompleteProvider provider)
    {
        CancelAutocomplete();
        autocompleteProvider = provider;
        var next = new List<string> { "@", "#" };
        foreach (var character in provider.TriggerCharacters)
            if (character.Length == 1 && character != "/" && !TextUtils.IsWhitespaceChar(character) && !next.Contains(character)) next.Add(character);
        triggerCharacters = next;
    }
    private bool MatchesTriggerPattern(string text) => AutocompleteTokens.MatchesTrigger(text, triggerCharacters);
    private bool MatchesDebouncePattern(string text)
    {
        var withoutAt = triggerCharacters.Where(c => c != "@").ToList();
        withoutAt.Add("@");
        return AutocompleteTokens.MatchesTrigger(text, withoutAt, debounce: true);
    }

    public void AddToHistory(string text)
    {
        var trimmed = TextUtils.JsTrim(text);
        if (trimmed.Length == 0 || history.Count > 0 && history[0] == trimmed) return;
        history.Insert(0, trimmed);
        if (history.Count > 100) history.RemoveAt(history.Count - 1);
    }

    private bool IsEditorEmpty => state.Lines.Count == 1 && state.Lines[0].Length == 0;
    private bool IsOnFirstVisualLine() => FindCurrentVisualLine(BuildVisualLineMap(lastWidth)) == 0;
    private bool IsOnLastVisualLine() { var map = BuildVisualLineMap(lastWidth); return FindCurrentVisualLine(map) == map.Count - 1; }

    private void NavigateHistory(int direction)
    {
        lastAction = null;
        if (history.Count == 0) return;
        var newIndex = historyIndex - direction;
        if (newIndex < -1 || newIndex >= history.Count) return;
        if (historyIndex == -1 && newIndex >= 0) { PushUndoSnapshot(); historyDraft = state.Clone(); }
        historyIndex = newIndex;
        if (historyIndex == -1)
        {
            var draft = historyDraft; historyDraft = null;
            if (draft is not null)
            {
                state = draft; preferredVisualCol = null; snappedFromCursorCol = null; scrollOffset = 0;
                OnChange?.Invoke(GetText());
            }
            else SetTextInternal("");
        }
        else SetTextInternal(history[historyIndex], direction == -1 ? "start" : "end");
    }
    private void ExitHistoryBrowsing() { historyIndex = -1; historyDraft = null; }

    private void SetTextInternal(string text, string placement = "end")
    {
        var lines = text.Split('\n').ToList();
        state.Lines = lines.Count == 0 ? [""] : lines;
        state.CursorLine = placement == "start" ? 0 : state.Lines.Count - 1;
        SetCursorCol(placement == "start" ? 0 : state.Lines[state.CursorLine].Length);
        scrollOffset = 0;
        OnChange?.Invoke(GetText());
    }

    public virtual void Invalidate() { }
    protected virtual string RenderTopBorder(int width, int hidden) => (BorderColor ?? (s => s))(hidden > 0 ? CreateScrollBorder("↑", hidden, width) : TextUtils.Repeat("─", width));
    protected virtual string RenderBottomBorder(int width, int hidden) => (BorderColor ?? (s => s))(hidden > 0 ? CreateScrollBorder("↓", hidden, width) : TextUtils.Repeat("─", width));

    public virtual List<string> Render(int width)
    {
        var maxPadding = Math.Max(0, (width - 1) / 2);
        var padX = Math.Min(paddingX, maxPadding);
        var contentWidth = Math.Max(1, width - padX * 2);
        var layoutWidth = Math.Max(1, contentWidth - (padX > 0 ? 0 : 1));
        lastWidth = layoutWidth;
        var layoutLines = LayoutText(layoutWidth);
        var maxVisible = Math.Max(5, (int)Math.Floor(Tui.Terminal.Rows * 0.3));
        var cursorIndex = layoutLines.FindIndex(line => line.HasCursor);
        if (cursorIndex == -1) cursorIndex = 0;
        if (cursorIndex < scrollOffset) scrollOffset = cursorIndex;
        else if (cursorIndex >= scrollOffset + maxVisible) scrollOffset = cursorIndex - maxVisible + 1;
        scrollOffset = Math.Max(0, Math.Min(scrollOffset, Math.Max(0, layoutLines.Count - maxVisible)));
        var visible = layoutLines.Skip(scrollOffset).Take(maxVisible).ToList();
        renderedVisibleLineCount = visible.Count;
        var result = new List<string>();
        var leftPadding = new string(' ', padX); var rightPadding = leftPadding;
        result.Add(RenderTopBorder(width, scrollOffset));
        foreach (var layoutLine in visible)
        {
            var display = layoutLine.Text;
            var lineWidth = TextUtils.VisibleWidth(layoutLine.Text);
            var cursorInPadding = false;
            if (layoutLine.HasCursor)
            {
                var pos = Math.Min(layoutLine.CursorPos, display.Length);
                var before = display[..pos]; var after = display[pos..];
                var marker = Focused ? TuiBase.CursorMarker : "";
                if (after.Length > 0)
                {
                    var first = Graphemes(after).FirstOrDefault().Text ?? "";
                    display = before + marker + "\u001b[7m" + first + "\u001b[0m" + after[first.Length..];
                }
                else
                {
                    display = before + marker + "\u001b[7m \u001b[0m";
                    lineWidth++;
                    if (lineWidth > contentWidth && padX > 0) cursorInPadding = true;
                }
            }
            var padding = new string(' ', Math.Max(0, contentWidth - lineWidth));
            result.Add(leftPadding + display + padding + (cursorInPadding ? rightPadding[1..] : rightPadding));
        }
        result.Add(RenderBottomBorder(width, layoutLines.Count - (scrollOffset + visible.Count)));
        renderedAutocompleteHeight = 0;
        if (autocompleteState is not null && autocompleteList is not null)
        {
            var lines = autocompleteList.Render(contentWidth);
            renderedAutocompleteHeight = lines.Count;
            foreach (var line in lines) result.Add(leftPadding + line + new string(' ', Math.Max(0, contentWidth - TextUtils.VisibleWidth(line))) + rightPadding);
        }
        return result;
    }

    public TuiMouseEventResult? HandleMouse(TuiMouseEvent mouseEvent)
    {
        var autocompleteStart = renderedVisibleLineCount + 2;
        if (autocompleteState is not null && autocompleteList is not null && mouseEvent.Y >= autocompleteStart && mouseEvent.Y < autocompleteStart + renderedAutocompleteHeight)
        {
            var padX = Math.Min(paddingX, Math.Max(0, (mouseEvent.Width - 1) / 2));
            var result = autocompleteList.HandleMouse(mouseEvent with { X = mouseEvent.X - padX, Y = mouseEvent.Y - autocompleteStart, Width = Math.Max(1, mouseEvent.Width - padX * 2), Height = renderedAutocompleteHeight });
            return result is null ? null : result with { Focus = true };
        }
        if (mouseEvent.Type != TuiMouseEventType.Click || mouseEvent.Button != TuiMouseButton.Left) return null;
        if (mouseEvent.Y <= 0 || mouseEvent.Y > renderedVisibleLineCount) return new TuiMouseEventResult(Handled: true, Focus: true);
        var map = BuildVisualLineMap(lastWidth);
        var index = scrollOffset + mouseEvent.Y - 1;
        if (index < 0 || index >= map.Count) return new TuiMouseEventResult(Handled: true, Focus: true);
        var visual = map[index];
        var logical = state.Lines[visual.LogicalLine];
        var chunk = logical.Substring(visual.StartCol, Math.Min(visual.Length, logical.Length - visual.StartCol));
        var padding = Math.Min(paddingX, Math.Max(0, (mouseEvent.Width - 1) / 2));
        var target = Math.Max(0, mouseEvent.X - padding);
        int column = 0, targetIndex = chunk.Length, lastIndex = 0;
        foreach (var grapheme in Graphemes(chunk))
        {
            var next = column + TextUtils.VisibleWidth(grapheme.Text);
            lastIndex = grapheme.Index;
            if (target < next) { targetIndex = grapheme.Index; break; }
            column = next;
        }
        var isLastSegment = index == map.Count - 1 || map[index + 1].LogicalLine != visual.LogicalLine;
        if (!isLastSegment && targetIndex == chunk.Length && chunk.Length > 0) targetIndex = lastIndex;
        state.CursorLine = visual.LogicalLine;
        SetCursorCol(visual.StartCol + targetIndex);
        lastAction = null; ExitHistoryBrowsing();
        if (autocompleteState is not null) UpdateAutocomplete();
        return new TuiMouseEventResult(Handled: true, Focus: true);
    }

    public virtual void HandleInput(string data)
    {
        var kb = KeybindingsManager.Global;
        if (jumpMode is not null)
        {
            if (kb.Matches(data, "tui.editor.jumpForward") || kb.Matches(data, "tui.editor.jumpBackward")) { jumpMode = null; return; }
            var printableJump = Keys.DecodePrintableKey(data) ?? (data.Length > 0 && data[0] >= 32 ? data : null);
            if (printableJump is not null) { var direction = jumpMode; jumpMode = null; JumpToChar(printableJump, direction); return; }
            jumpMode = null;
        }
        if (data.Contains("\u001b[200~", StringComparison.Ordinal))
        {
            inPaste = true; pasteBuffer = "";
            data = data.Remove(data.IndexOf("\u001b[200~", StringComparison.Ordinal), 6);
        }
        if (inPaste)
        {
            pasteBuffer += data;
            var end = pasteBuffer.IndexOf("\u001b[201~", StringComparison.Ordinal);
            if (end != -1)
            {
                var content = pasteBuffer[..end];
                if (content.Length > 0) HandlePaste(content);
                inPaste = false;
                var remaining = pasteBuffer[(end + 6)..];
                pasteBuffer = "";
                if (remaining.Length > 0) HandleInput(remaining);
            }
            return;
        }
        if (kb.Matches(data, "tui.input.copy")) return;
        if (kb.Matches(data, "tui.editor.undo")) { Undo(); return; }
        if (autocompleteState is not null && autocompleteList is not null)
        {
            if (kb.Matches(data, "tui.select.cancel")) { CancelAutocomplete(); return; }
            if (kb.Matches(data, "tui.select.up") || kb.Matches(data, "tui.select.down")) { autocompleteList.HandleInput(data); return; }
            if (kb.Matches(data, "tui.input.tab"))
            {
                if (autocompleteList.GetSelectedItem() is { } selected && autocompleteProvider is not null)
                {
                    PushUndoSnapshot(); lastAction = null;
                    ApplyCompletionResult(autocompleteProvider.ApplyCompletion(state.Lines, state.CursorLine, state.CursorCol, new(selected.Value, selected.Label, selected.Description), autocompletePrefix));
                    CancelAutocomplete();
                    OnChange?.Invoke(GetText());
                }
                return;
            }
            if (kb.Matches(data, "tui.select.confirm"))
            {
                if (autocompleteList.GetSelectedItem() is { } selected && autocompleteProvider is not null)
                {
                    PushUndoSnapshot(); lastAction = null;
                    ApplyCompletionResult(autocompleteProvider.ApplyCompletion(state.Lines, state.CursorLine, state.CursorCol, new(selected.Value, selected.Label, selected.Description), autocompletePrefix));
                    if (autocompletePrefix.StartsWith('/')) CancelAutocomplete();
                    else { CancelAutocomplete(); OnChange?.Invoke(GetText()); return; }
                }
            }
        }
        if (kb.Matches(data, "tui.input.tab") && autocompleteState is null) { HandleTabCompletion(); return; }
        if (kb.Matches(data, "tui.editor.deleteToLineEnd")) { DeleteToEndOfLine(); return; }
        if (kb.Matches(data, "tui.editor.deleteToLineStart")) { DeleteToStartOfLine(); return; }
        if (kb.Matches(data, "tui.editor.deleteWordBackward")) { DeleteWordBackwards(); return; }
        if (kb.Matches(data, "tui.editor.deleteWordForward")) { DeleteWordForward(); return; }
        if (kb.Matches(data, "tui.editor.deleteCharBackward") || Keys.Matches(data, "shift+backspace")) { HandleBackspace(); return; }
        if (kb.Matches(data, "tui.editor.deleteCharForward") || Keys.Matches(data, "shift+delete")) { HandleForwardDelete(); return; }
        if (kb.Matches(data, "tui.editor.yank")) { Yank(); return; }
        if (kb.Matches(data, "tui.editor.yankPop")) { YankPop(); return; }
        if (kb.Matches(data, "tui.editor.historyPrevious")) { CancelAutocomplete(); NavigateHistory(-1); return; }
        if (kb.Matches(data, "tui.editor.historyNext")) { CancelAutocomplete(); NavigateHistory(1); return; }
        if (kb.Matches(data, "tui.editor.cursorLineStart")) { lastAction = null; SetCursorCol(0); return; }
        if (kb.Matches(data, "tui.editor.cursorLineEnd")) { lastAction = null; SetCursorCol(state.Lines[state.CursorLine].Length); return; }
        if (kb.Matches(data, "tui.editor.cursorWordLeft")) { MoveWordBackwards(); return; }
        if (kb.Matches(data, "tui.editor.cursorWordRight")) { MoveWordForwards(); return; }
        if (kb.Matches(data, "tui.input.newLine") || data.Length > 1 && data[0] == '\n' || data == "\u001b\r" || data == "\u001b[13;2~" ||
            data.Length > 1 && data.Contains('\u001b') && data.Contains('\r') || data == "\n")
        {
            if (ShouldSubmitOnBackslashEnter(data, kb)) { HandleBackspace(); SubmitValue(); return; }
            AddNewLine(); return;
        }
        if (kb.Matches(data, "tui.input.submit"))
        {
            if (DisableSubmit) return;
            var line = state.Lines[state.CursorLine];
            if (state.CursorCol > 0 && line[state.CursorCol - 1] == '\\') { HandleBackspace(); AddNewLine(); return; }
            SubmitValue(); return;
        }
        if (kb.Matches(data, "tui.editor.cursorUp"))
        {
            if (IsOnFirstVisualLine() && (IsEditorEmpty || historyIndex > -1 || state.CursorCol == 0)) NavigateHistory(-1);
            else if (IsOnFirstVisualLine()) { lastAction = null; SetCursorCol(0); }
            else MoveCursor(-1, 0);
            return;
        }
        if (kb.Matches(data, "tui.editor.cursorDown"))
        {
            if (historyIndex > -1 && IsOnLastVisualLine()) NavigateHistory(1);
            else if (IsOnLastVisualLine()) { lastAction = null; SetCursorCol(state.Lines[state.CursorLine].Length); }
            else MoveCursor(1, 0);
            return;
        }
        if (kb.Matches(data, "tui.editor.cursorRight")) { MoveCursor(0, 1); return; }
        if (kb.Matches(data, "tui.editor.cursorLeft")) { MoveCursor(0, -1); return; }
        if (kb.Matches(data, "tui.editor.pageUp")) { PageScroll(-1); return; }
        if (kb.Matches(data, "tui.editor.pageDown")) { PageScroll(1); return; }
        if (kb.Matches(data, "tui.editor.jumpForward")) { jumpMode = "forward"; return; }
        if (kb.Matches(data, "tui.editor.jumpBackward")) { jumpMode = "backward"; return; }
        if (Keys.Matches(data, "shift+space")) { InsertCharacter(" "); return; }
        if (Keys.DecodePrintableKey(data) is { } printable) { InsertCharacter(printable); return; }
        if (data.Length > 0 && data[0] >= 32) InsertCharacter(data);
    }

    private void ApplyCompletionResult(CompletionResult result)
    {
        state.Lines = result.Lines; state.CursorLine = result.CursorLine; SetCursorCol(result.CursorCol);
    }

    private List<LayoutLine> LayoutText(int contentWidth)
    {
        var result = new List<LayoutLine>();
        if (state.Lines.Count == 0 || state.Lines.Count == 1 && state.Lines[0].Length == 0) { result.Add(new("", true, 0)); return result; }
        for (var i = 0; i < state.Lines.Count; i++)
        {
            var line = state.Lines[i]; var current = i == state.CursorLine;
            if (TextUtils.VisibleWidth(line) <= contentWidth) { result.Add(new(line, current, current ? state.CursorCol : 0)); continue; }
            var chunks = WordWrapLine(line, contentWidth, Graphemes(line).ToList());
            for (var c = 0; c < chunks.Count; c++)
            {
                var chunk = chunks[c]; var last = c == chunks.Count - 1;
                var has = false; var adjusted = 0;
                if (current)
                {
                    if (last) { has = state.CursorCol >= chunk.StartIndex; adjusted = state.CursorCol - chunk.StartIndex; }
                    else
                    {
                        has = state.CursorCol >= chunk.StartIndex && state.CursorCol < chunk.EndIndex;
                        if (has) adjusted = Math.Min(state.CursorCol - chunk.StartIndex, chunk.Text.Length);
                    }
                }
                result.Add(new(chunk.Text, has, has ? adjusted : 0));
            }
        }
        return result;
    }

    public string GetText() => string.Join('\n', state.Lines);
    private string ExpandPasteMarkers(string text)
    {
        foreach (var (id, content) in pastes)
            text = Regex.Replace(text, $@"\[paste #{id}( (\+\d+ lines|\d+ chars))?\]", _ => content);
        return text;
    }
    public string GetExpandedText() => ExpandPasteMarkers(string.Join('\n', state.Lines));
    public List<string> GetLines() => [.. state.Lines];
    public (int Line, int Col) GetCursor() => (state.CursorLine, state.CursorCol);

    public void SetText(string text)
    {
        CancelAutocomplete(); lastAction = null; ExitHistoryBrowsing();
        var normalized = NormalizeText(text);
        if (GetText() != normalized) PushUndoSnapshot();
        pastes = []; pasteCounter = 0;
        SetTextInternal(normalized);
    }

    public void InsertTextAtCursor(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        CancelAutocomplete(); PushUndoSnapshot(); lastAction = null; ExitHistoryBrowsing();
        InsertTextAtCursorInternal(text);
    }

    private static string NormalizeText(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\t", "    ", StringComparison.Ordinal);

    private void InsertTextAtCursorInternal(string text)
    {
        if (text.Length == 0) return;
        var normalized = NormalizeText(text);
        var inserted = normalized.Split('\n');
        var line = state.Lines[state.CursorLine];
        var before = line[..state.CursorCol]; var after = line[state.CursorCol..];
        if (inserted.Length == 1)
        {
            state.Lines[state.CursorLine] = before + normalized + after;
            SetCursorCol(state.CursorCol + normalized.Length);
        }
        else
        {
            var lines = state.Lines.Take(state.CursorLine).ToList();
            lines.Add(before + inserted[0]);
            lines.AddRange(inserted[1..^1]);
            lines.Add(inserted[^1] + after);
            lines.AddRange(state.Lines.Skip(state.CursorLine + 1));
            state.Lines = lines;
            state.CursorLine += inserted.Length - 1;
            SetCursorCol(inserted[^1].Length);
        }
        OnChange?.Invoke(GetText());
    }

    private void InsertCharacter(string ch, bool skipUndoCoalescing = false)
    {
        ExitHistoryBrowsing();
        if (!skipUndoCoalescing)
        {
            if (TextUtils.IsWhitespaceChar(ch) || lastAction != "type-word") PushUndoSnapshot();
            lastAction = "type-word";
        }
        var line = state.Lines[state.CursorLine];
        state.Lines[state.CursorLine] = line[..state.CursorCol] + ch + line[state.CursorCol..];
        SetCursorCol(state.CursorCol + ch.Length);
        OnChange?.Invoke(GetText());
        if (autocompleteState is null)
        {
            var before = state.Lines[state.CursorLine][..state.CursorCol];
            if (ch == "/" && IsAtStartOfMessage()) TryTriggerAutocomplete();
            else if (triggerCharacters.Contains(ch)) { if (MatchesTriggerPattern(before)) TryTriggerAutocomplete(); }
            else if (ch.Any(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') || TextUtils.IsCjkBreak(ch))
            {
                if (IsInSlashCommandContext(before)) TryTriggerAutocomplete();
                else if (MatchesTriggerPattern(before)) TryTriggerAutocomplete();
            }
        }
        else UpdateAutocomplete();
    }

    private void HandlePaste(string pasted)
    {
        CancelAutocomplete(); ExitHistoryBrowsing(); lastAction = null;
        PushUndoSnapshot();
        var decoded = CsiUControl().Replace(pasted, match =>
        {
            var cp = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            if (cp is >= 97 and <= 122) return ((char)(cp - 96)).ToString();
            if (cp is >= 65 and <= 90) return ((char)(cp - 64)).ToString();
            return match.Value;
        });
        var filtered = new string(NormalizeText(decoded).Where(c => c == '\n' || c >= 32).ToArray());
        if (filtered.Length > 0 && filtered[0] is '/' or '~' or '.')
        {
            var line = state.Lines[state.CursorLine];
            if (state.CursorCol > 0 && (char.IsAsciiLetterOrDigit(line[state.CursorCol - 1]) || line[state.CursorCol - 1] == '_')) filtered = " " + filtered;
        }
        var pastedLines = filtered.Split('\n');
        if (pastedLines.Length > 10 || filtered.Length > 1000)
        {
            pasteCounter++;
            var id = pasteCounter;
            pastes[id] = filtered;
            InsertTextAtCursorInternal(pastedLines.Length > 10 ? $"[paste #{id} +{pastedLines.Length} lines]" : $"[paste #{id} {filtered.Length} chars]");
            return;
        }
        InsertTextAtCursorInternal(filtered);
    }

    private void AddNewLine()
    {
        CancelAutocomplete(); ExitHistoryBrowsing(); lastAction = null;
        PushUndoSnapshot();
        var line = state.Lines[state.CursorLine];
        state.Lines[state.CursorLine] = line[..state.CursorCol];
        state.Lines.Insert(state.CursorLine + 1, line[state.CursorCol..]);
        state.CursorLine++; SetCursorCol(0);
        OnChange?.Invoke(GetText());
    }

    private bool ShouldSubmitOnBackslashEnter(string data, KeybindingsManager kb)
    {
        if (DisableSubmit || !Keys.Matches(data, "enter")) return false;
        var submit = kb.GetKeys("tui.input.submit");
        if (!submit.Contains("shift+enter") && !submit.Contains("shift+return")) return false;
        var line = state.Lines[state.CursorLine];
        return state.CursorCol > 0 && line[state.CursorCol - 1] == '\\';
    }

    private void SubmitValue()
    {
        CancelAutocomplete();
        var result = TextUtils.JsTrim(ExpandPasteMarkers(string.Join('\n', state.Lines)));
        state = new(); pastes = []; pasteCounter = 0; ExitHistoryBrowsing(); scrollOffset = 0; undoStack.Clear(); lastAction = null;
        OnChange?.Invoke("");
        OnSubmit?.Invoke(result);
    }

    private void HandleBackspace()
    {
        ExitHistoryBrowsing(); lastAction = null;
        if (state.CursorCol > 0)
        {
            PushUndoSnapshot();
            var line = state.Lines[state.CursorLine];
            var last = Graphemes(line[..state.CursorCol]).LastOrDefault();
            var length = last.Text?.Length ?? 1;
            var marker = last.Text is null ? Match.Empty : PasteMarkerSingle().Match(last.Text);
            if (marker.Success)
            {
                var targetId = int.Parse(marker.Groups[1].Value, CultureInfo.InvariantCulture);
                pastes.Remove(targetId); pasteCounter--;
                foreach (var id in pastes.Keys.Where(id => id > targetId).OrderBy(id => id).ToList()) { pastes[id - 1] = pastes[id]; pastes.Remove(id); }
                state.Lines = state.Lines.Select(l => PasteMarker().Replace(l, match =>
                {
                    var x = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    return x <= targetId ? match.Value : $"[paste #{x - 1}{match.Groups[2].Value}]";
                })).ToList();
            }
            line = state.Lines[state.CursorLine];
            state.Lines[state.CursorLine] = line[..(state.CursorCol - length)] + line[state.CursorCol..];
            SetCursorCol(state.CursorCol - length);
        }
        else if (state.CursorLine > 0)
        {
            PushUndoSnapshot();
            var current = state.Lines[state.CursorLine]; var previous = state.Lines[state.CursorLine - 1];
            state.Lines[state.CursorLine - 1] = previous + current; state.Lines.RemoveAt(state.CursorLine);
            state.CursorLine--; SetCursorCol(previous.Length);
        }
        OnChange?.Invoke(GetText());
        RetriggerAutocomplete();
    }

    private void RetriggerAutocomplete()
    {
        if (autocompleteState is not null) { UpdateAutocomplete(); return; }
        var before = state.Lines[state.CursorLine][..state.CursorCol];
        if (IsInSlashCommandContext(before)) TryTriggerAutocomplete();
        else if (MatchesTriggerPattern(before)) TryTriggerAutocomplete();
    }

    private void SetCursorCol(int col) { state.CursorCol = col; preferredVisualCol = null; snappedFromCursorCol = null; }

    private void MoveToVisualLine(List<VisualLine> map, int currentIndex, int targetIndex)
    {
        if (currentIndex < 0 || currentIndex >= map.Count || targetIndex < 0 || targetIndex >= map.Count) return;
        var current = map[currentIndex]; var target = map[targetIndex];
        int currentVisualCol;
        if (snappedFromCursorCol is { } snapped)
        {
            var vl = FindVisualLineAt(map, current.LogicalLine, snapped);
            currentVisualCol = snapped - map[vl].StartCol;
        }
        else currentVisualCol = state.CursorCol - current.StartCol;
        var lastSource = currentIndex == map.Count - 1 || map[currentIndex + 1].LogicalLine != current.LogicalLine;
        var sourceMax = lastSource ? current.Length : Math.Max(0, current.Length - 1);
        var lastTarget = targetIndex == map.Count - 1 || map[targetIndex + 1].LogicalLine != target.LogicalLine;
        var targetMax = lastTarget ? target.Length : Math.Max(0, target.Length - 1);
        var moveTo = ComputeVerticalMoveColumn(currentVisualCol, sourceMax, targetMax);
        state.CursorLine = target.LogicalLine;
        var logical = state.Lines[target.LogicalLine];
        state.CursorCol = Math.Min(target.StartCol + moveTo, logical.Length);
        foreach (var segment in Graphemes(logical))
        {
            if (segment.Index > state.CursorCol) break;
            if (segment.Text.Length <= 1) continue;
            if (state.CursorCol < segment.Index + segment.Text.Length)
            {
                var continuation = segment.Index < target.StartCol;
                var movingDown = targetIndex > currentIndex;
                if (continuation && movingDown)
                {
                    var segmentEnd = segment.Index + segment.Text.Length; var next = targetIndex + 1;
                    while (next < map.Count && map[next].LogicalLine == target.LogicalLine && map[next].StartCol < segmentEnd) next++;
                    if (next < map.Count) { MoveToVisualLine(map, currentIndex, next); return; }
                }
                snappedFromCursorCol = state.CursorCol;
                state.CursorCol = segment.Index;
                return;
            }
        }
        snappedFromCursorCol = null;
    }

    private int ComputeVerticalMoveColumn(int currentVisualCol, int sourceMax, int targetMax)
    {
        var hasPreferred = preferredVisualCol is not null;
        var inMiddle = currentVisualCol < sourceMax;
        var tooShort = targetMax < currentVisualCol;
        if (!hasPreferred || inMiddle)
        {
            if (tooShort) { preferredVisualCol = currentVisualCol; return targetMax; }
            preferredVisualCol = null; return currentVisualCol;
        }
        if (tooShort || targetMax < preferredVisualCol!.Value) return targetMax;
        var result = preferredVisualCol.Value; preferredVisualCol = null;
        return result;
    }

    private void DeleteToStartOfLine()
    {
        ExitHistoryBrowsing();
        var line = state.Lines[state.CursorLine];
        if (state.CursorCol > 0)
        {
            PushUndoSnapshot();
            killRing.Push(line[..state.CursorCol], prepend: true, accumulate: lastAction == "kill"); lastAction = "kill";
            state.Lines[state.CursorLine] = line[state.CursorCol..]; SetCursorCol(0);
        }
        else if (state.CursorLine > 0)
        {
            PushUndoSnapshot();
            killRing.Push("\n", prepend: true, accumulate: lastAction == "kill"); lastAction = "kill";
            var previous = state.Lines[state.CursorLine - 1];
            state.Lines[state.CursorLine - 1] = previous + line; state.Lines.RemoveAt(state.CursorLine);
            state.CursorLine--; SetCursorCol(previous.Length);
        }
        OnChange?.Invoke(GetText());
    }

    private void DeleteToEndOfLine()
    {
        ExitHistoryBrowsing();
        var line = state.Lines[state.CursorLine];
        if (state.CursorCol < line.Length)
        {
            PushUndoSnapshot();
            killRing.Push(line[state.CursorCol..], prepend: false, accumulate: lastAction == "kill"); lastAction = "kill";
            state.Lines[state.CursorLine] = line[..state.CursorCol];
        }
        else if (state.CursorLine < state.Lines.Count - 1)
        {
            PushUndoSnapshot();
            killRing.Push("\n", prepend: false, accumulate: lastAction == "kill"); lastAction = "kill";
            state.Lines[state.CursorLine] = line + state.Lines[state.CursorLine + 1]; state.Lines.RemoveAt(state.CursorLine + 1);
        }
        OnChange?.Invoke(GetText());
    }

    private void DeleteWordBackwards()
    {
        ExitHistoryBrowsing();
        var line = state.Lines[state.CursorLine];
        if (state.CursorCol == 0)
        {
            if (state.CursorLine > 0)
            {
                PushUndoSnapshot();
                killRing.Push("\n", prepend: true, accumulate: lastAction == "kill"); lastAction = "kill";
                var previous = state.Lines[state.CursorLine - 1];
                state.Lines[state.CursorLine - 1] = previous + line; state.Lines.RemoveAt(state.CursorLine);
                state.CursorLine--; SetCursorCol(previous.Length);
            }
        }
        else
        {
            PushUndoSnapshot();
            var wasKill = lastAction == "kill";
            var old = state.CursorCol; MoveWordBackwards(); var from = state.CursorCol; SetCursorCol(old);
            killRing.Push(line[from..state.CursorCol], prepend: true, accumulate: wasKill); lastAction = "kill";
            state.Lines[state.CursorLine] = line[..from] + line[state.CursorCol..];
            SetCursorCol(from);
        }
        OnChange?.Invoke(GetText());
    }

    private void DeleteWordForward()
    {
        ExitHistoryBrowsing();
        var line = state.Lines[state.CursorLine];
        if (state.CursorCol >= line.Length)
        {
            if (state.CursorLine < state.Lines.Count - 1)
            {
                PushUndoSnapshot();
                killRing.Push("\n", prepend: false, accumulate: lastAction == "kill"); lastAction = "kill";
                state.Lines[state.CursorLine] = line + state.Lines[state.CursorLine + 1]; state.Lines.RemoveAt(state.CursorLine + 1);
            }
        }
        else
        {
            PushUndoSnapshot();
            var wasKill = lastAction == "kill";
            var old = state.CursorCol; MoveWordForwards(); var to = state.CursorCol; SetCursorCol(old);
            killRing.Push(line[state.CursorCol..to], prepend: false, accumulate: wasKill); lastAction = "kill";
            state.Lines[state.CursorLine] = line[..state.CursorCol] + line[to..];
        }
        OnChange?.Invoke(GetText());
    }

    private void HandleForwardDelete()
    {
        ExitHistoryBrowsing(); lastAction = null;
        var line = state.Lines[state.CursorLine];
        if (state.CursorCol < line.Length)
        {
            PushUndoSnapshot();
            var length = Graphemes(line[state.CursorCol..]).FirstOrDefault().Text?.Length ?? 1;
            state.Lines[state.CursorLine] = line[..state.CursorCol] + line[(state.CursorCol + length)..];
        }
        else if (state.CursorLine < state.Lines.Count - 1)
        {
            PushUndoSnapshot();
            state.Lines[state.CursorLine] = line + state.Lines[state.CursorLine + 1]; state.Lines.RemoveAt(state.CursorLine + 1);
        }
        OnChange?.Invoke(GetText());
        RetriggerAutocomplete();
    }

    private List<VisualLine> BuildVisualLineMap(int width)
    {
        var map = new List<VisualLine>();
        for (var i = 0; i < state.Lines.Count; i++)
        {
            var line = state.Lines[i];
            if (line.Length == 0) map.Add(new(i, 0, 0));
            else if (TextUtils.VisibleWidth(line) <= width) map.Add(new(i, 0, line.Length));
            else foreach (var chunk in WordWrapLine(line, width, Graphemes(line).ToList())) map.Add(new(i, chunk.StartIndex, chunk.EndIndex - chunk.StartIndex));
        }
        return map;
    }

    private static int FindVisualLineAt(List<VisualLine> map, int line, int col)
    {
        for (var i = 0; i < map.Count; i++)
        {
            var vl = map[i];
            if (vl.LogicalLine != line) continue;
            var offset = col - vl.StartCol;
            var last = i == map.Count - 1 || map[i + 1].LogicalLine != vl.LogicalLine;
            if (offset >= 0 && (offset < vl.Length || last && offset == vl.Length)) return i;
        }
        return map.Count - 1;
    }
    private int FindCurrentVisualLine(List<VisualLine> map) => FindVisualLineAt(map, state.CursorLine, state.CursorCol);

    private void MoveCursor(int deltaLine, int deltaCol)
    {
        lastAction = null;
        var map = BuildVisualLineMap(lastWidth);
        var currentIndex = FindCurrentVisualLine(map);
        if (deltaLine != 0)
        {
            var target = currentIndex + deltaLine;
            if (target >= 0 && target < map.Count) MoveToVisualLine(map, currentIndex, target);
        }
        if (deltaCol != 0)
        {
            var line = state.Lines[state.CursorLine];
            if (deltaCol > 0)
            {
                if (state.CursorCol < line.Length) SetCursorCol(state.CursorCol + (Graphemes(line[state.CursorCol..]).FirstOrDefault().Text?.Length ?? 1));
                else if (state.CursorLine < state.Lines.Count - 1) { state.CursorLine++; SetCursorCol(0); }
                else if (currentIndex >= 0 && currentIndex < map.Count) preferredVisualCol = state.CursorCol - map[currentIndex].StartCol;
            }
            else
            {
                if (state.CursorCol > 0) SetCursorCol(state.CursorCol - (Graphemes(line[..state.CursorCol]).LastOrDefault().Text?.Length ?? 1));
                else if (state.CursorLine > 0) { state.CursorLine--; SetCursorCol(state.Lines[state.CursorLine].Length); }
            }
        }
        if (autocompleteState is not null) UpdateAutocomplete();
    }

    private void PageScroll(int direction)
    {
        lastAction = null;
        var pageSize = Math.Max(5, (int)Math.Floor(Tui.Terminal.Rows * 0.3));
        var map = BuildVisualLineMap(lastWidth);
        var current = FindCurrentVisualLine(map);
        MoveToVisualLine(map, current, Math.Max(0, Math.Min(map.Count - 1, current + direction * pageSize)));
    }

    private void MoveWordBackwards()
    {
        lastAction = null;
        var line = state.Lines[state.CursorLine];
        if (state.CursorCol == 0)
        {
            if (state.CursorLine > 0) { state.CursorLine--; SetCursorCol(state.Lines[state.CursorLine].Length); }
            return;
        }
        SetCursorCol(WordNavigation.FindWordBackward(line, state.CursorCol, Words, IsPasteMarker));
    }

    private void MoveWordForwards()
    {
        lastAction = null;
        var line = state.Lines[state.CursorLine];
        if (state.CursorCol >= line.Length)
        {
            if (state.CursorLine < state.Lines.Count - 1) { state.CursorLine++; SetCursorCol(0); }
            return;
        }
        SetCursorCol(WordNavigation.FindWordForward(line, state.CursorCol, Words, IsPasteMarker));
    }

    private void Yank()
    {
        if (killRing.Length == 0) return;
        PushUndoSnapshot();
        InsertYankedText(killRing.Peek()!);
        lastAction = "yank";
    }

    private void YankPop()
    {
        if (lastAction != "yank" || killRing.Length <= 1) return;
        PushUndoSnapshot();
        DeleteYankedText();
        killRing.Rotate();
        InsertYankedText(killRing.Peek()!);
        lastAction = "yank";
    }

    private void InsertYankedText(string text)
    {
        ExitHistoryBrowsing();
        var lines = text.Split('\n');
        var line = state.Lines[state.CursorLine];
        var before = line[..state.CursorCol]; var after = line[state.CursorCol..];
        if (lines.Length == 1) { state.Lines[state.CursorLine] = before + text + after; SetCursorCol(state.CursorCol + text.Length); }
        else
        {
            state.Lines[state.CursorLine] = before + lines[0];
            for (var i = 1; i < lines.Length - 1; i++) state.Lines.Insert(state.CursorLine + i, lines[i]);
            var lastIndex = state.CursorLine + lines.Length - 1;
            state.Lines.Insert(lastIndex, lines[^1] + after);
            state.CursorLine = lastIndex; SetCursorCol(lines[^1].Length);
        }
        OnChange?.Invoke(GetText());
    }

    private void DeleteYankedText()
    {
        if (killRing.Peek() is not { Length: > 0 } yanked) return;
        var yankLines = yanked.Split('\n');
        if (yankLines.Length == 1)
        {
            var line = state.Lines[state.CursorLine];
            state.Lines[state.CursorLine] = line[..(state.CursorCol - yanked.Length)] + line[state.CursorCol..];
            SetCursorCol(state.CursorCol - yanked.Length);
        }
        else
        {
            var startLine = state.CursorLine - (yankLines.Length - 1);
            var startCol = state.Lines[startLine].Length - yankLines[0].Length;
            var afterCursor = state.Lines[state.CursorLine][state.CursorCol..];
            var beforeYank = state.Lines[startLine][..startCol];
            state.Lines.RemoveRange(startLine, yankLines.Length);
            state.Lines.Insert(startLine, beforeYank + afterCursor);
            state.CursorLine = startLine; SetCursorCol(startCol);
        }
        OnChange?.Invoke(GetText());
    }

    private void PushUndoSnapshot() => undoStack.Push(new(state.Clone(), new(pastes), pasteCounter));

    private void Undo()
    {
        ExitHistoryBrowsing();
        if (!undoStack.TryPop(out var snapshot)) return;
        state.Lines = snapshot.State.Lines; state.CursorLine = snapshot.State.CursorLine; state.CursorCol = snapshot.State.CursorCol;
        pastes = snapshot.Pastes; pasteCounter = snapshot.PasteCounter;
        lastAction = null; preferredVisualCol = null;
        OnChange?.Invoke(GetText());
    }

    private void JumpToChar(string ch, string? direction)
    {
        lastAction = null;
        var forward = direction == "forward";
        var end = forward ? state.Lines.Count : -1; var step = forward ? 1 : -1;
        for (var lineIndex = state.CursorLine; lineIndex != end; lineIndex += step)
        {
            var line = state.Lines[lineIndex]; var current = lineIndex == state.CursorLine;
            int index;
            if (forward) { var from = current ? state.CursorCol + 1 : 0; index = from > line.Length ? -1 : line.IndexOf(ch, from, StringComparison.Ordinal); }
            else index = JsLastIndexOf(line, ch, current ? state.CursorCol - 1 : line.Length);
            if (index != -1) { state.CursorLine = lineIndex; SetCursorCol(index); return; }
        }
    }

    /// <summary>JavaScript <c>lastIndexOf(search, fromIndex)</c>.</summary>
    private static int JsLastIndexOf(string text, string search, int fromIndex)
    {
        for (var index = Math.Min(Math.Max(0, fromIndex), text.Length - search.Length); index >= 0; index--)
            if (string.CompareOrdinal(text, index, search, 0, search.Length) == 0) return index;
        return -1;
    }

    private bool IsSlashMenuAllowed() => state.CursorLine == 0;
    private bool IsAtStartOfMessage()
    {
        if (!IsSlashMenuAllowed()) return false;
        var before = TextUtils.JsTrim(state.Lines[state.CursorLine][..state.CursorCol]);
        return before.Length == 0 || before == "/";
    }
    private bool IsInSlashCommandContext(string before) => IsSlashMenuAllowed() && TextUtils.JsTrimStart(before).StartsWith('/');

    private static int BestMatchIndex(List<AutocompleteItem> items, string prefix)
    {
        if (prefix.Length == 0) return -1;
        var firstPrefix = -1;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Value == prefix) return i;
            if (firstPrefix == -1 && items[i].Value.StartsWith(prefix, StringComparison.Ordinal)) firstPrefix = i;
        }
        return firstPrefix;
    }

    private SelectList CreateAutocompleteList(string prefix, List<AutocompleteItem> items)
    {
        var layout = prefix.StartsWith('/') ? new SelectListLayoutOptions(12, 32) : null;
        var list = new SelectList(items.Select(item => new SelectItem(item.Value, item.Label, item.Description)), autocompleteMaxVisible, theme.SelectList, layout);
        list.OnSelect = selected =>
        {
            if (autocompleteProvider is null) return;
            PushUndoSnapshot(); lastAction = null;
            ApplyCompletionResult(autocompleteProvider.ApplyCompletion(state.Lines, state.CursorLine, state.CursorCol, new(selected.Value, selected.Label, selected.Description), autocompletePrefix));
            CancelAutocomplete();
            OnChange?.Invoke(GetText());
        };
        return list;
    }

    private void TryTriggerAutocomplete(bool explicitTab = false) => RequestAutocomplete(false, explicitTab);
    private void HandleTabCompletion()
    {
        if (autocompleteProvider is null) return;
        var before = state.Lines[state.CursorLine][..state.CursorCol];
        if (IsInSlashCommandContext(before) && !TextUtils.JsTrimStart(before).Contains(' ')) RequestAutocomplete(false, true);
        else RequestAutocomplete(true, true);
    }

    private void RequestAutocomplete(bool force, bool explicitTab)
    {
        if (autocompleteProvider is null) return;
        if (force && !autocompleteProvider.ShouldTriggerFileCompletion(state.Lines, state.CursorLine, state.CursorCol)) return;
        CancelAutocompleteRequest();
        var token = ++autocompleteStartToken;
        var debounce = explicitTab || force ? 0 : MatchesDebouncePattern(state.Lines[state.CursorLine][..state.CursorCol]) ? 20 : 0;
        if (debounce > 0) { autocompleteDebounce = Tui.Loop.SetTimeout(() => { autocompleteDebounce = null; _ = StartAutocompleteRequest(token, force, explicitTab); }, debounce); return; }
        _ = StartAutocompleteRequest(token, force, explicitTab);
    }

    private async Task StartAutocompleteRequest(int token, bool force, bool explicitTab)
    {
        var previous = autocompleteTask;
        autocompleteTask = Run();
        try { await autocompleteTask; } catch (OperationCanceledException) { }
        async Task Run()
        {
            try { await previous; } catch { }
            if (token != autocompleteStartToken || autocompleteProvider is null) return;
            var controller = new CancellationTokenSource();
            autocompleteAbort = controller;
            var requestId = ++autocompleteRequestId;
            var text = GetText(); var line = state.CursorLine; var col = state.CursorCol;
            await RunAutocompleteRequest(requestId, controller, text, line, col, force, explicitTab);
        }
    }

    private async Task RunAutocompleteRequest(int requestId, CancellationTokenSource controller, string text, int line, int col, bool force, bool explicitTab)
    {
        if (autocompleteProvider is null) return;
        AutocompleteSuggestions? suggestions;
        try { suggestions = await autocompleteProvider.GetSuggestionsAsync(state.Lines.ToList(), state.CursorLine, state.CursorCol, force, controller.Token); }
        catch { suggestions = null; }
        if (controller.IsCancellationRequested || requestId != autocompleteRequestId || GetText() != text || state.CursorLine != line || state.CursorCol != col) return;
        autocompleteAbort = null;
        if (suggestions is null || suggestions.Items.Count == 0) { CancelAutocomplete(); Tui.RequestRender(); return; }
        if (force && explicitTab && suggestions.Items.Count == 1)
        {
            PushUndoSnapshot(); lastAction = null;
            ApplyCompletionResult(autocompleteProvider.ApplyCompletion(state.Lines, state.CursorLine, state.CursorCol, suggestions.Items[0], suggestions.Prefix));
            OnChange?.Invoke(GetText());
            Tui.RequestRender();
            return;
        }
        autocompletePrefix = suggestions.Prefix;
        autocompleteList = CreateAutocompleteList(suggestions.Prefix, suggestions.Items);
        var best = BestMatchIndex(suggestions.Items, suggestions.Prefix);
        if (best >= 0) autocompleteList.SetSelectedIndex(best);
        autocompleteState = force ? "force" : "regular";
        Tui.RequestRender();
    }

    private void CancelAutocompleteRequest()
    {
        autocompleteStartToken++;
        autocompleteDebounce?.Dispose(); autocompleteDebounce = null;
        autocompleteAbort?.Cancel(); autocompleteAbort = null;
    }
    private void CancelAutocomplete() { CancelAutocompleteRequest(); autocompleteState = null; autocompleteList = null; autocompletePrefix = ""; }
    public bool IsShowingAutocomplete() => autocompleteState is not null;
    private void UpdateAutocomplete()
    {
        if (autocompleteState is null || autocompleteProvider is null) return;
        RequestAutocomplete(autocompleteState == "force", false);
    }
}
