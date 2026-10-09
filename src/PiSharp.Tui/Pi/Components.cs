// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/tui/src/components/text.ts, truncated-text.ts, box.ts, spacer.ts,
// loader.ts, cancellable-loader.ts, input.ts, select-list.ts.
namespace PiSharp.Tui.Pi;

/// <summary>Multi-line text with word wrapping, padding and an optional background.</summary>
public class Text : IComponent
{
    private string text;
    private int paddingX;
    private readonly int paddingY;
    private Func<string, string>? customBg;
    private string? cachedText;
    private int? cachedWidth;
    private List<string>? cachedLines;

    public Text(string text = "", int paddingX = 1, int paddingY = 1, Func<string, string>? customBg = null)
    { this.text = text; this.paddingX = paddingX; this.paddingY = paddingY; this.customBg = customBg; }

    public string Value => text;
    public void SetText(string value) { text = value; cachedText = null; cachedWidth = null; cachedLines = null; }
    public void SetCustomBgFn(Func<string, string>? value) { customBg = value; cachedText = null; cachedWidth = null; cachedLines = null; }
    public void SetPaddingX(int value) { paddingX = value; Invalidate(); }
    public virtual void Invalidate() { cachedText = null; cachedWidth = null; cachedLines = null; }

    public virtual List<string> Render(int width)
    {
        if (cachedLines is not null && cachedText == text && cachedWidth == width) return cachedLines;
        if (string.IsNullOrEmpty(text) || TextUtils.JsTrim(text).Length == 0)
        { cachedText = text; cachedWidth = width; cachedLines = []; return []; }
        var normalized = text.Replace("\t", "   ", StringComparison.Ordinal);
        var padX = Math.Min(paddingX, Math.Max(0, (width - 1) / 2));
        var contentWidth = Math.Max(1, width - padX * 2);
        var margin = new string(' ', padX);
        var content = new List<string>();
        foreach (var line in TextUtils.WrapTextWithAnsi(normalized, contentWidth))
        {
            var withMargins = margin + line + margin;
            content.Add(customBg is not null ? TextUtils.ApplyBackgroundToLine(withMargins, width, customBg) : withMargins + new string(' ', Math.Max(0, width - TextUtils.VisibleWidth(withMargins))));
        }
        var empty = new string(' ', width);
        var padding = Enumerable.Range(0, paddingY).Select(_ => customBg is not null ? TextUtils.ApplyBackgroundToLine(empty, width, customBg) : empty).ToList();
        var result = new List<string>(padding.Count * 2 + content.Count);
        result.AddRange(padding); result.AddRange(content); result.AddRange(padding);
        cachedText = text; cachedWidth = width; cachedLines = result;
        return result.Count > 0 ? result : [""];
    }
}

/// <summary>Single-line text truncated to the width.</summary>
public sealed class TruncatedText(string text, int paddingX = 0, int paddingY = 0) : IComponent
{
    public void Invalidate() { }
    public List<string> Render(int width)
    {
        var result = new List<string>(); var empty = new string(' ', Math.Max(0, width));
        for (var i = 0; i < paddingY; i++) result.Add(empty);
        var available = Math.Max(1, width - paddingX * 2);
        var newline = text.IndexOf('\n');
        var single = newline == -1 ? text : text[..newline];
        var line = new string(' ', paddingX) + TextUtils.TruncateToWidth(single, available) + new string(' ', paddingX);
        result.Add(line + new string(' ', Math.Max(0, width - TextUtils.VisibleWidth(line))));
        for (var i = 0; i < paddingY; i++) result.Add(empty);
        return result;
    }
}

/// <summary>A container that applies padding and a background to all children.</summary>
public class Box : IComponent, IMouseHandler
{
    public List<IComponent> Children { get; private set; } = [];
    private int paddingX;
    private readonly int paddingY;
    private Func<string, string>? bg;
    private (List<string> ChildLines, int Width, string? BgSample, List<string> Lines)? cache;
    private (int Width, List<(IComponent Component, int Height)> Children)? mouseLayout;

    public Box(int paddingX = 1, int paddingY = 1, Func<string, string>? bg = null) { this.paddingX = paddingX; this.paddingY = paddingY; this.bg = bg; }
    public void AddChild(IComponent component) { Children.Add(component); cache = null; }
    public void RemoveChild(IComponent component) { if (Children.Remove(component)) cache = null; }
    public void Clear() { Children = []; cache = null; }
    public void SetBgFn(Func<string, string>? value) => bg = value;
    public void SetPaddingX(int value) { paddingX = value; cache = null; }
    public virtual void Invalidate() { cache = null; foreach (var child in Children.ToArray()) child.Invalidate(); }

    public TuiMouseEventResult? HandleMouse(TuiMouseEvent mouseEvent)
    {
        var contentWidth = Math.Max(1, mouseEvent.Width - paddingX * 2);
        var contentY = mouseEvent.Y - paddingY; var contentX = mouseEvent.X - paddingX;
        if (contentY < 0 || contentX < 0 || contentX >= contentWidth) return null;
        var children = mouseLayout is { } layout && layout.Width == contentWidth ? layout.Children :
            Children.Select(component => (component, component.Render(contentWidth).Count)).ToList();
        var childY = 0;
        foreach (var (child, height) in children)
        {
            if (contentY >= childY && contentY < childY + height)
                return Mouse.Dispatch(child, mouseEvent with { X = contentX, Y = contentY - childY, Width = contentWidth, Height = height });
            childY += height;
        }
        return null;
    }

    public virtual List<string> Render(int width)
    {
        if (Children.Count == 0) return [];
        var contentWidth = Math.Max(1, width - paddingX * 2);
        var leftPad = new string(' ', paddingX);
        var childLines = new List<string>(); var mouseChildren = new List<(IComponent, int)>();
        foreach (var child in Children.ToArray())
        {
            var lines = child.Render(contentWidth);
            mouseChildren.Add((child, lines.Count)); childLines.AddRange(lines);
        }
        mouseLayout = (contentWidth, mouseChildren);
        if (childLines.Count == 0) return [];
        var bgSample = bg?.Invoke("test");
        if (cache is { } c && c.Width == width && c.BgSample == bgSample && c.ChildLines.SequenceEqual(childLines, StringComparer.Ordinal)) return c.Lines;
        var result = new List<string>();
        for (var i = 0; i < paddingY; i++) result.Add(ApplyBg("", width));
        foreach (var line in childLines) result.Add(ApplyBg(leftPad + line, width));
        for (var i = 0; i < paddingY; i++) result.Add(ApplyBg("", width));
        cache = (childLines, width, bgSample, result);
        return result;
    }

    private string ApplyBg(string line, int width)
    {
        var padded = line + new string(' ', Math.Max(0, width - TextUtils.VisibleWidth(line)));
        return bg is not null ? bg(padded) : padded;
    }
}

/// <summary>Empty lines.</summary>
public sealed class Spacer(int lines = 1) : IComponent
{
    private int lines = lines;
    public void SetLines(int value) => lines = value;
    public void Invalidate() { }
    public List<string> Render(int width) => Enumerable.Repeat("", Math.Max(0, lines)).ToList();
}

public sealed record LoaderIndicatorOptions(IReadOnlyList<string>? Frames = null, int? IntervalMs = null);

/// <summary>A message with an optional spinning indicator.</summary>
public class Loader : Text, IDisposableComponent
{
    private static readonly string[] DefaultFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    private List<string> frames = [.. DefaultFrames];
    private int intervalMs = 80, currentFrame;
    private IDisposable? interval;
    private readonly ITui? ui;
    private bool verbatim;
    private readonly Func<string, string> spinnerColor, messageColor;
    private string message;

    public Loader(ITui ui, Func<string, string> spinnerColor, Func<string, string> messageColor, string message = "Loading...", LoaderIndicatorOptions? indicator = null)
        : base("", 1, 0)
    {
        this.ui = ui; this.spinnerColor = spinnerColor; this.messageColor = messageColor; this.message = message;
        SetIndicator(indicator);
    }

    public override List<string> Render(int width) { var lines = new List<string> { "" }; lines.AddRange(base.Render(width)); return lines; }
    public void Start() { UpdateDisplay(); RestartAnimation(); }
    public void Stop() { interval?.Dispose(); interval = null; }
    public virtual void Dispose() => Stop();
    public void SetMessage(string value) { message = value; UpdateDisplay(); }
    public override void Invalidate() { base.Invalidate(); UpdateDisplay(); }
    public void SetIndicator(LoaderIndicatorOptions? indicator)
    {
        verbatim = indicator is not null;
        frames = indicator?.Frames is { } custom ? [.. custom] : [.. DefaultFrames];
        intervalMs = indicator?.IntervalMs is > 0 ? indicator.IntervalMs.Value : 80;
        currentFrame = 0;
        Start();
    }
    private void RestartAnimation()
    {
        Stop();
        if (frames.Count <= 1 || ui is null) return;
        interval = ui.Loop.SetInterval(() => { currentFrame = (currentFrame + 1) % frames.Count; UpdateDisplay(); }, intervalMs);
    }
    protected virtual string GetRenderedIndicator()
    {
        var frame = currentFrame < frames.Count ? frames[currentFrame] : "";
        return verbatim ? frame : spinnerColor(frame);
    }
    private void UpdateDisplay()
    {
        var rendered = GetRenderedIndicator();
        SetText((rendered.Length > 0 ? rendered + " " : "") + messageColor(message));
        ui?.RequestRender();
    }
}

/// <summary>A loader cancelled with Escape.</summary>
public sealed class CancellableLoader(ITui ui, Func<string, string> spinnerColor, Func<string, string> messageColor, string message = "Loading...", LoaderIndicatorOptions? indicator = null)
    : Loader(ui, spinnerColor, messageColor, message, indicator), IInputHandler
{
    private readonly CancellationTokenSource abort = new();
    public Action? OnAbort { get; set; }
    public CancellationToken Signal => abort.Token;
    public bool Aborted => abort.IsCancellationRequested;
    public void HandleInput(string data)
    {
        if (!KeybindingsManager.Global.Matches(data, "tui.select.cancel")) return;
        abort.Cancel(); OnAbort?.Invoke();
    }
}

/// <summary>Single-line text input with horizontal scrolling, kill ring and undo.</summary>
public sealed class Input : IComponent, IFocusable, IInputHandler, IMouseHandler
{
    private string value = "";
    private int cursor, renderedStartColumn;
    private readonly string prompt, placeholder;
    private readonly Func<string, string> placeholderStyle;
    public Action<string>? OnSubmit { get; set; }
    public Action? OnEscape { get; set; }
    public bool Focused { get; set; }
    private string pasteBuffer = "";
    private bool inPaste;
    private readonly KillRing killRing = new();
    private string? lastAction;
    private readonly UndoStack<(string Value, int Cursor)> undo = new();

    public Input(string prompt = "> ", string placeholder = "", Func<string, string>? placeholderStyle = null)
    { this.prompt = prompt; this.placeholder = placeholder; this.placeholderStyle = placeholderStyle ?? (text => text); }

    public string GetValue() => value;
    public void SetValue(string newValue) { value = newValue; cursor = Math.Min(cursor, newValue.Length); }

    private static string? LastGrapheme(string text) => text.Length == 0 ? null : TextUtils.Graphemes(text).LastOrDefault();
    private static string? FirstGrapheme(string text) => text.Length == 0 ? null : TextUtils.Graphemes(text).FirstOrDefault();

    public void HandleInput(string data)
    {
        if (data.Contains("\u001b[200~", StringComparison.Ordinal))
        {
            inPaste = true; pasteBuffer = "";
            var start = data.IndexOf("\u001b[200~", StringComparison.Ordinal);
            data = data.Remove(start, 6);
        }
        if (inPaste)
        {
            pasteBuffer += data;
            var end = pasteBuffer.IndexOf("\u001b[201~", StringComparison.Ordinal);
            if (end != -1)
            {
                var content = pasteBuffer[..end];
                HandlePaste(content);
                inPaste = false;
                var remaining = pasteBuffer[(end + 6)..];
                pasteBuffer = "";
                if (remaining.Length > 0) HandleInput(remaining);
            }
            return;
        }
        var kb = KeybindingsManager.Global;
        if (kb.Matches(data, "tui.select.cancel")) { OnEscape?.Invoke(); return; }
        if (kb.Matches(data, "tui.editor.undo")) { Undo(); return; }
        if (kb.Matches(data, "tui.input.submit") || data == "\n") { OnSubmit?.Invoke(value); return; }
        if (kb.Matches(data, "tui.editor.deleteCharBackward")) { Backspace(); return; }
        if (kb.Matches(data, "tui.editor.deleteCharForward")) { ForwardDelete(); return; }
        if (kb.Matches(data, "tui.editor.deleteWordBackward")) { DeleteWordBackwards(); return; }
        if (kb.Matches(data, "tui.editor.deleteWordForward")) { DeleteWordForward(); return; }
        if (kb.Matches(data, "tui.editor.deleteToLineStart")) { DeleteToLineStart(); return; }
        if (kb.Matches(data, "tui.editor.deleteToLineEnd")) { DeleteToLineEnd(); return; }
        if (kb.Matches(data, "tui.editor.yank")) { Yank(); return; }
        if (kb.Matches(data, "tui.editor.yankPop")) { YankPop(); return; }
        if (kb.Matches(data, "tui.editor.cursorLeft"))
        { lastAction = null; if (cursor > 0) cursor -= LastGrapheme(value[..cursor])?.Length ?? 1; return; }
        if (kb.Matches(data, "tui.editor.cursorRight"))
        { lastAction = null; if (cursor < value.Length) cursor += FirstGrapheme(value[cursor..])?.Length ?? 1; return; }
        if (kb.Matches(data, "tui.editor.cursorLineStart")) { lastAction = null; cursor = 0; return; }
        if (kb.Matches(data, "tui.editor.cursorLineEnd")) { lastAction = null; cursor = value.Length; return; }
        if (kb.Matches(data, "tui.editor.cursorWordLeft")) { MoveWordBackwards(); return; }
        if (kb.Matches(data, "tui.editor.cursorWordRight")) { MoveWordForwards(); return; }
        if (Keys.DecodeKittyPrintable(data) is { } printable) { InsertCharacter(printable); return; }
        if (!data.Any(ch => ch < 32 || ch == 0x7f || ch is >= '\x80' and <= '\x9f')) InsertCharacter(data);
    }

    public TuiMouseEventResult? HandleMouse(TuiMouseEvent mouseEvent)
    {
        if (mouseEvent.Type != TuiMouseEventType.Press || mouseEvent.Button != TuiMouseButton.Left || mouseEvent.Y != 0) return null;
        var target = renderedStartColumn + Math.Max(0, mouseEvent.X - 2);
        var column = 0; var index = 0;
        cursor = value.Length;
        foreach (var grapheme in TextUtils.Graphemes(value))
        {
            var next = column + TextUtils.VisibleWidth(grapheme);
            if (target < next) { cursor = index; break; }
            column = next; index += grapheme.Length;
        }
        lastAction = null;
        return new TuiMouseEventResult(Handled: true, Focus: true);
    }

    private void InsertCharacter(string ch)
    {
        if (TextUtils.IsWhitespaceChar(ch) || lastAction != "type-word") PushUndo();
        lastAction = "type-word";
        value = value[..cursor] + ch + value[cursor..]; cursor += ch.Length;
    }
    private void Backspace()
    {
        lastAction = null;
        if (cursor <= 0) return;
        PushUndo();
        var length = LastGrapheme(value[..cursor])?.Length ?? 1;
        value = value[..(cursor - length)] + value[cursor..]; cursor -= length;
    }
    private void ForwardDelete()
    {
        lastAction = null;
        if (cursor >= value.Length) return;
        PushUndo();
        var length = FirstGrapheme(value[cursor..])?.Length ?? 1;
        value = value[..cursor] + value[(cursor + length)..];
    }
    private void DeleteToLineStart()
    {
        if (cursor == 0) return;
        PushUndo();
        killRing.Push(value[..cursor], prepend: true, accumulate: lastAction == "kill"); lastAction = "kill";
        value = value[cursor..]; cursor = 0;
    }
    private void DeleteToLineEnd()
    {
        if (cursor >= value.Length) return;
        PushUndo();
        killRing.Push(value[cursor..], prepend: false, accumulate: lastAction == "kill"); lastAction = "kill";
        value = value[..cursor];
    }
    private void DeleteWordBackwards()
    {
        if (cursor == 0) return;
        var wasKill = lastAction == "kill";
        PushUndo();
        var old = cursor; MoveWordBackwards(); var from = cursor; cursor = old;
        killRing.Push(value[from..cursor], prepend: true, accumulate: wasKill); lastAction = "kill";
        value = value[..from] + value[cursor..]; cursor = from;
    }
    private void DeleteWordForward()
    {
        if (cursor >= value.Length) return;
        var wasKill = lastAction == "kill";
        PushUndo();
        var old = cursor; MoveWordForwards(); var to = cursor; cursor = old;
        killRing.Push(value[cursor..to], prepend: false, accumulate: wasKill); lastAction = "kill";
        value = value[..cursor] + value[to..];
    }
    private void Yank()
    {
        if (killRing.Peek() is not { Length: > 0 } text) return;
        PushUndo();
        value = value[..cursor] + text + value[cursor..]; cursor += text.Length; lastAction = "yank";
    }
    private void YankPop()
    {
        if (lastAction != "yank" || killRing.Length <= 1) return;
        PushUndo();
        var previous = killRing.Peek() ?? "";
        value = value[..(cursor - previous.Length)] + value[cursor..]; cursor -= previous.Length;
        killRing.Rotate();
        var text = killRing.Peek() ?? "";
        value = value[..cursor] + text + value[cursor..]; cursor += text.Length; lastAction = "yank";
    }
    private void PushUndo() => undo.Push((value, cursor));
    private void Undo() { if (!undo.TryPop(out var snapshot)) return; value = snapshot.Value; cursor = snapshot.Cursor; lastAction = null; }
    private void MoveWordBackwards() { if (cursor == 0) return; lastAction = null; cursor = WordNavigation.FindWordBackward(value, cursor); }
    private void MoveWordForwards() { if (cursor >= value.Length) return; lastAction = null; cursor = WordNavigation.FindWordForward(value, cursor); }
    private void HandlePaste(string pasted)
    {
        lastAction = null; PushUndo();
        var clean = pasted.Replace("\r\n", "", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal).Replace("\t", "    ", StringComparison.Ordinal);
        value = value[..cursor] + clean + value[cursor..]; cursor += clean.Length;
    }

    public void Invalidate() { }

    public List<string> Render(int width)
    {
        var available = width - TextUtils.VisibleWidth(prompt);
        if (available <= 0) return [TextUtils.TruncateToWidth(prompt, width, "")];
        var marker = Focused ? TuiBase.CursorMarker : "";
        if (value.Length == 0 && placeholder.Length > 0)
        {
            var text = TextUtils.TruncateToWidth(placeholder, available, "");
            var at = FirstGrapheme(text) ?? " ";
            var after = text.Length >= at.Length ? text[Math.Min(at.Length, text.Length)..] : "";
            var withCursor = marker + "\u001b[7m" + placeholderStyle(at) + "\u001b[27m" + placeholderStyle(after);
            return [prompt + withCursor + new string(' ', Math.Max(0, available - TextUtils.VisibleWidth(withCursor)))];
        }
        string visible; var cursorDisplay = cursor;
        renderedStartColumn = 0;
        var total = TextUtils.VisibleWidth(value);
        if (total < available) visible = value;
        else
        {
            var scrollWidth = cursor == value.Length ? available - 1 : available;
            var cursorCol = TextUtils.VisibleWidth(value[..cursor]);
            if (scrollWidth > 0)
            {
                var half = scrollWidth / 2;
                var startCol = cursorCol < half ? 0 : cursorCol > total - half ? Math.Max(0, total - scrollWidth) : Math.Max(0, cursorCol - half);
                renderedStartColumn = startCol;
                visible = TextUtils.SliceByColumn(value, startCol, scrollWidth, true);
                cursorDisplay = TextUtils.SliceByColumn(value, startCol, Math.Max(0, cursorCol - startCol), true).Length;
            }
            else { visible = ""; cursorDisplay = 0; }
        }
        cursorDisplay = Math.Min(cursorDisplay, visible.Length);
        var before = visible[..cursorDisplay];
        var atCursor = FirstGrapheme(visible[cursorDisplay..]) ?? " ";
        var afterStart = Math.Min(visible.Length, cursorDisplay + atCursor.Length);
        var afterCursor = cursorDisplay < visible.Length ? visible[afterStart..] : "";
        var line = before + marker + "\u001b[7m" + atCursor + "\u001b[27m" + afterCursor;
        return [prompt + line + new string(' ', Math.Max(0, available - TextUtils.VisibleWidth(line)))];
    }
}

public sealed record SelectItem(string Value, string Label, string? Description = null);
public sealed record SelectListTheme(Func<string, string> SelectedPrefix, Func<string, string> SelectedText, Func<string, string> Description,
    Func<string, string> ScrollInfo, Func<string, string> NoMatch);
public sealed record SelectListTruncatePrimaryContext(string Text, int MaxWidth, int ColumnWidth, SelectItem Item, bool IsSelected);
public sealed record SelectListLayoutOptions(int? MinPrimaryColumnWidth = null, int? MaxPrimaryColumnWidth = null, Func<SelectListTruncatePrimaryContext, string>? TruncatePrimary = null);

/// <summary>A scrolling list with an arrow on the selected item and an optional description column.</summary>
public sealed class SelectList : IComponent, IInputHandler, IMouseHandler
{
    private readonly List<SelectItem> items;
    private List<SelectItem> filtered;
    private int selectedIndex;
    private int? mousePressedIndex;
    private readonly int maxVisible;
    private readonly SelectListTheme theme;
    private readonly SelectListLayoutOptions layout;
    public Action<SelectItem>? OnSelect { get; set; }
    public Action? OnCancel { get; set; }
    public Action<SelectItem>? OnSelectionChange { get; set; }

    public SelectList(IEnumerable<SelectItem> items, int maxVisible, SelectListTheme theme, SelectListLayoutOptions? layout = null)
    { this.items = [.. items]; filtered = this.items; this.maxVisible = maxVisible; this.theme = theme; this.layout = layout ?? new(); }

    public void SetFilter(string filter)
    {
        filtered = items.Where(item => item.Value.StartsWith(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        selectedIndex = 0;
    }
    public void SetSelectedIndex(int index) => selectedIndex = Math.Max(0, Math.Min(index, filtered.Count - 1));
    public void Invalidate() { }

    private static string SingleLine(string text) => TextUtils.JsTrim(System.Text.RegularExpressions.Regex.Replace(text, "[\r\n]+", " "));

    public List<string> Render(int width)
    {
        var lines = new List<string>();
        if (filtered.Count == 0) { lines.Add(theme.NoMatch("  No matching commands")); return lines; }
        var primaryWidth = PrimaryColumnWidth();
        var (start, end) = VisibleRange();
        for (var i = start; i < end; i++)
        {
            var item = filtered[i];
            lines.Add(RenderItem(item, i == selectedIndex, width, item.Description is { } d ? SingleLine(d) : null, primaryWidth));
        }
        if (start > 0 || end < filtered.Count)
            lines.Add(theme.ScrollInfo(TextUtils.TruncateToWidth($"  ({selectedIndex + 1}/{filtered.Count})", width - 2, "")));
        return lines;
    }

    public TuiMouseEventResult? HandleMouse(TuiMouseEvent mouseEvent)
    {
        if (filtered.Count == 0) return null;
        if (mouseEvent.Type == TuiMouseEventType.Wheel && mouseEvent.WheelDelta is { } delta and not 0)
        {
            var previous = selectedIndex;
            selectedIndex = Math.Max(0, Math.Min(filtered.Count - 1, selectedIndex + (delta < 0 ? -1 : 1)));
            if (selectedIndex != previous) NotifySelectionChange();
            return new TuiMouseEventResult(Handled: true, Render: selectedIndex != previous);
        }
        if (mouseEvent.Button != TuiMouseButton.Left || mouseEvent.Type is not (TuiMouseEventType.Press or TuiMouseEventType.Click)) return null;
        var (start, end) = VisibleRange();
        var itemIndex = start + mouseEvent.Y;
        if (itemIndex < start || itemIndex >= end) return null;
        if (mouseEvent.Type == TuiMouseEventType.Press)
        {
            mousePressedIndex = itemIndex;
            if (selectedIndex != itemIndex) { selectedIndex = itemIndex; NotifySelectionChange(); }
            return new TuiMouseEventResult(Handled: true, Focus: true);
        }
        var clicked = mousePressedIndex ?? itemIndex; mousePressedIndex = null;
        var changed = selectedIndex != clicked;
        selectedIndex = clicked;
        if (changed) NotifySelectionChange();
        if (selectedIndex < filtered.Count) OnSelect?.Invoke(filtered[selectedIndex]);
        return new TuiMouseEventResult(Handled: true);
    }

    public void HandleInput(string data)
    {
        var kb = KeybindingsManager.Global;
        if (kb.Matches(data, "tui.select.up"))
        { selectedIndex = selectedIndex == 0 ? filtered.Count - 1 : selectedIndex - 1; NotifySelectionChange(); }
        else if (kb.Matches(data, "tui.select.down"))
        { selectedIndex = selectedIndex == filtered.Count - 1 ? 0 : selectedIndex + 1; NotifySelectionChange(); }
        else if (kb.Matches(data, "tui.select.confirm")) { if (selectedIndex >= 0 && selectedIndex < filtered.Count) OnSelect?.Invoke(filtered[selectedIndex]); }
        else if (kb.Matches(data, "tui.select.cancel")) OnCancel?.Invoke();
    }

    private (int Start, int End) VisibleRange()
    {
        var start = Math.Max(0, Math.Min(selectedIndex - maxVisible / 2, filtered.Count - maxVisible));
        return (start, Math.Min(start + maxVisible, filtered.Count));
    }

    private string RenderItem(SelectItem item, bool selected, int width, string? description, int primaryWidth)
    {
        var prefix = selected ? "→ " : "  ";
        var prefixWidth = TextUtils.VisibleWidth(prefix);
        if (!string.IsNullOrEmpty(description) && width > 40)
        {
            var effective = Math.Max(1, Math.Min(primaryWidth, width - prefixWidth - 4));
            var maxPrimary = Math.Max(1, effective - 2);
            var truncated = TruncatePrimary(item, selected, maxPrimary, effective);
            var truncatedWidth = TextUtils.VisibleWidth(truncated);
            var spacing = new string(' ', Math.Max(1, effective - truncatedWidth));
            var remaining = width - (prefixWidth + truncatedWidth + spacing.Length) - 2;
            if (remaining > 10)
            {
                var desc = TextUtils.TruncateToWidth(description, remaining, "");
                if (selected) return theme.SelectedText(prefix + truncated + spacing + desc);
                return prefix + truncated + theme.Description(spacing + desc);
            }
        }
        var maxWidth = width - prefixWidth - 2;
        var value = TruncatePrimary(item, selected, maxWidth, maxWidth);
        return selected ? theme.SelectedText(prefix + value) : prefix + value;
    }

    private int PrimaryColumnWidth()
    {
        var rawMin = layout.MinPrimaryColumnWidth ?? layout.MaxPrimaryColumnWidth ?? 32;
        var rawMax = layout.MaxPrimaryColumnWidth ?? layout.MinPrimaryColumnWidth ?? 32;
        var min = Math.Max(1, Math.Min(rawMin, rawMax)); var max = Math.Max(1, Math.Max(rawMin, rawMax));
        var widest = filtered.Select(item => TextUtils.VisibleWidth(Display(item)) + 2).DefaultIfEmpty(0).Max();
        return Math.Max(min, Math.Min(widest, max));
    }

    private string TruncatePrimary(SelectItem item, bool selected, int maxWidth, int columnWidth)
    {
        var display = Display(item);
        var truncated = layout.TruncatePrimary is { } custom ? custom(new(display, maxWidth, columnWidth, item, selected)) : TextUtils.TruncateToWidth(display, maxWidth, "");
        return TextUtils.TruncateToWidth(truncated, maxWidth, "");
    }
    private static string Display(SelectItem item) => item.Label.Length > 0 ? item.Label : item.Value;
    private void NotifySelectionChange() { if (selectedIndex >= 0 && selectedIndex < filtered.Count) OnSelectionChange?.Invoke(filtered[selectedIndex]); }
    public SelectItem? GetSelectedItem() => selectedIndex >= 0 && selectedIndex < filtered.Count ? filtered[selectedIndex] : null;
}
