// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/tui/src/tui-alt-screen.ts.
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Tui.Pi;

public sealed record TuiAltScreenOptions
{
    /// <summary>Lines per wheel event; null is "auto" acceleration.</summary>
    public int? WheelScrollLines { get; init; } = 1;
    public bool Mouse { get; init; } = true;
    public Func<string, string>? SearchMatchStyle { get; init; }
    public Func<string, string>? SearchCurrentMatchStyle { get; init; }
    public Func<string, bool, string>? SearchNavigationButtonStyle { get; init; }
    public Func<string>? ScrollToEndIndicator { get; init; }
    public Action<string>? OpenUrl { get; init; }
    public Action? OnRightClickPaste { get; init; }
    public bool CopyOnSelect { get; init; } = true;
    /// <summary>Copies text: true on success, a message on failure, null for a generic failure.</summary>
    public Func<string, Task<(bool Ok, string? Error)>>? CopySelection { get; init; }
    public Func<string, string?>? Environment { get; init; }
}

/// <summary>The fullscreen TUI: alternate screen with an application-owned, scrollable viewport, mouse selection and search.</summary>
public sealed partial class TuiAltScreen : TuiBase, IViewportTui
{
    private const string EnterAltScreen = "\u001b[?1049h", ExitAltScreen = "\u001b[?1049l", DisableAutowrap = "\u001b[?7l", EnableAutowrap = "\u001b[?7h";
    private const string ButtonMotionMouse = "\u001b[?1000h\u001b[?1002h\u001b[?1004h\u001b[?1006h", AllMotionMouse = "\u001b[?1000h\u001b[?1002h\u001b[?1003h\u001b[?1004h\u001b[?1006h";
    private const string DisableMouse = "\u001b[?1006l\u001b[?1004l\u001b[?1003l\u001b[?1002l\u001b[?1000l";
    private const string FocusIn = "\u001b[I", FocusOut = "\u001b[O", BeginSync = "\u001b[?2026h", EndSync = "\u001b[?2026l";
    private const int PageOverlap = 4, AltWheelMultiplier = 5, DoubleClickMs = 500, CopyErrorFlashMs = 5000;
    private const int MaxOffscreenImages = 16;
    private const long MaxOffscreenTransmission = 32L * 1024 * 1024, MaxOffscreenDecoded = 64L * 1024 * 1024;
    [GeneratedRegex(@"^\x1b\]133;A(?:\x07|\x1b\\)")] private static partial Regex PromptStart();
    [GeneratedRegex(@"^\x1b\[<(\d+);(\d+);(\d+)([Mm])$")] private static partial Regex SgrMouse();

    public override TuiMode Mode => TuiMode.Fullscreen;
    private List<string> previousScreen = [], lastDocument = [];
    private int previousScreenWidth, previousScreenHeight;
    private IComponent? layoutRoot;
    private LayoutFrame? currentLayout;
    private readonly ScrollView implicitScrollView;
    private readonly AltScreenFlashContainer flashes;
    private bool altScreenActive;
    private ImageProtocol imageProtocol;
    private TerminalCapabilities? savedCapabilities;
    private readonly LinkedList<(long Id, long Generation, int Transmission, long Decoded)> uploadedKitty = new();

    private sealed record SelectionPoint(int Row, int Col, ScrollView? ScrollView = null, bool Boundary = false);
    private sealed record SelectionRange(SelectionPoint Start, SelectionPoint End);
    private sealed record ClickTarget(long Timestamp, int Count, int Row, ScrollView? ScrollView, int WordStart, int WordEnd);
    private sealed record SgrMouseEvent(int Button, int X, int Y, bool Release);
    private sealed class ActiveSearch
    {
        public required AltScreenSearchComponent Component;
        public AltScreenSearchIndex Index = new();
        public IOverlayHandle? Overlay;
        public string Query = "";
        public List<AltScreenSearchMatch> Matches = [];
        public int SelectedIndex = -1;
        public string? SelectedKey;
        public int AnchorRow;
        public string SelectionMode = "query";
    }

    private SelectionPoint? selectionAnchor, selectionFocus;
    private string selectionGranularity = "character";
    private SelectionRange? selectionInitialRange;
    private ClickTarget? lastClick;
    private (int X, int Y)? selectionDragPointer;
    private int selectionAutoScrollDirection;
    private IDisposable? selectionAutoScrollTimer;
    private bool selectionPressActive, selectionDragged;
    private (ScrollView View, int GrabOffset)? scrollbarDrag;
    private ScrollView? scrollbarHover;
    private (int Row, int Column, int Width)? scrollToEndRect;
    private ActiveSearch? activeSearch;
    private string? pressedUrl;
    private TuiMouseDispatchTarget? mouseCapture, mousePressTarget;
    private (int X, int Y)? mousePressPoint;
    private bool mousePressMoved;
    private (long Timestamp, int Count, IComponent Component, int X, int Y)? lastComponentClick;
    private readonly WheelScrollAccelerator wheelScroll;
    private readonly bool mouseEnabled;
    private readonly Func<string, string> searchMatchStyle, searchCurrentMatchStyle;
    private readonly Func<string, bool, string> searchNavigationButtonStyle;
    private readonly Func<string>? scrollToEndIndicator;
    private readonly Action<string>? openUrl;
    private readonly Action? onRightClickPaste;
    private readonly Func<string, Task<(bool Ok, string? Error)>>? copySelection;
    private readonly Func<string, string?> env;
    public bool CopyOnSelect { get; set; }

    public TuiAltScreen(ITerminal terminal, UiLoop loop, bool? showHardwareCursor = null, string? logDirectory = null, TuiAltScreenOptions? options = null)
        : base(terminal, loop, showHardwareCursor, logDirectory)
    {
        options ??= new();
        env = options.Environment ?? System.Environment.GetEnvironmentVariable;
        implicitScrollView = new ScrollView(new ImplicitDocument(this), new(ScrollViewFollow.End, Primary: true, Loop: loop));
        flashes = new AltScreenFlashContainer(loop, () => RequestRender());
        wheelScroll = new WheelScrollAccelerator(options.WheelScrollLines);
        mouseEnabled = options.Mouse;
        searchMatchStyle = options.SearchMatchStyle ?? (text => "\u001b[4m" + text + "\u001b[24m");
        searchCurrentMatchStyle = options.SearchCurrentMatchStyle ?? (text => "\u001b[1;7m" + text + "\u001b[22;27m");
        searchNavigationButtonStyle = options.SearchNavigationButtonStyle ?? ((text, _) => text);
        scrollToEndIndicator = options.ScrollToEndIndicator; openUrl = options.OpenUrl; onRightClickPaste = options.OnRightClickPaste;
        CopyOnSelect = options.CopyOnSelect; copySelection = options.CopySelection;
        AddInputListener(HandleViewportInput);
    }

    private sealed class ImplicitDocument(TuiAltScreen owner) : IComponent, IMouseHandler
    {
        public List<string> Render(int width) => owner.RenderChildren(width);
        public TuiMouseEventResult? HandleMouse(TuiMouseEvent mouseEvent) => owner.HandleChildrenMouse(mouseEvent);
        public void Invalidate() { foreach (var child in owner.Children.ToArray()) child.Invalidate(); }
    }
    private List<string> RenderChildren(int width) => base.Render(width);
    private TuiMouseEventResult? HandleChildrenMouse(TuiMouseEvent mouseEvent) => base.HandleMouse(mouseEvent);

    public int ViewportTop => PrimaryScrollView.ScrollTop;
    public bool IsFollowingOutput => PrimaryScrollView.IsFollowingEnd;
    public void SetWheelScrollLines(int? lines) => wheelScroll.SetLines(lines);
    public bool HasActiveSelection() => ActiveSelectionText() is not null;
    public async Task<bool> CopyActiveSelectionToClipboard() => ActiveSelectionText() is { } text && await CopyText(text);
    public void ResetTextSelection() { ClearTextSelection(); lastClick = null; }
    public List<string> GetScreenLines() => [.. previousScreen];

    public void SetLayoutRoot(IComponent? component)
    {
        if (layoutRoot == component) return;
        layoutRoot = component; currentLayout = null; RequestRender();
    }
    public override List<string> Render(int width) => layoutRoot?.Render(width) ?? base.Render(width);
    protected override IReadOnlyList<IComponent> GetMountedRoots() => layoutRoot is not null ? [layoutRoot] : Children;
    private ScrollView PrimaryScrollView => currentLayout?.PrimaryScrollView ?? implicitScrollView;

    protected override void BeforeTerminalStart()
    {
        StopSelectionAutoScroll(); selectionPressActive = false; StopScrollbarHover(); scrollbarDrag = null; flashes.Dispose();
        altScreenActive = true;
        var capabilities = TerminalImage.GetCapabilities();
        imageProtocol = capabilities.Images; uploadedKitty.Clear();
        if (capabilities.Images == ImageProtocol.ITerm2)
        {
            savedCapabilities = capabilities;
            TerminalImage.SetCapabilities(capabilities with { Images = ImageProtocol.None });
            Invalidate();
        }
        lastDocument = []; selectionAnchor = null; selectionFocus = null; selectionGranularity = "character"; selectionInitialRange = null;
        lastClick = null; pressedUrl = null; selectionDragged = false; ClearComponentMouseGesture(); lastComponentClick = null;
        ResetRenderState();
        var term = (env("TERM") ?? "").ToLowerInvariant();
        var buttonMotion = env("TMUX") is not null || env("ZELLIJ") is not null || env("STY") is not null || term.StartsWith("tmux", StringComparison.Ordinal) || term.StartsWith("screen", StringComparison.Ordinal);
        Terminal.Write(EnterAltScreen + DisableAutowrap + (mouseEnabled ? buttonMotion ? ButtonMotionMouse : AllMotionMouse : "") + "\u001b[2J\u001b[H\u001b[?25l");
    }

    protected override void BeforeTerminalStop(bool preserveScreen)
    {
        CloseSearch(); StopSelectionAutoScroll(); selectionPressActive = false; StopScrollbarHover(); scrollbarDrag = null; ClearComponentMouseGesture(); flashes.Dispose();
        if (!altScreenActive) return;
        Terminal.Write(BeginSync + (imageProtocol == ImageProtocol.Kitty ? TerminalImage.DeleteAllKittyImages() : "") + (mouseEnabled ? DisableMouse : "") + EnableAutowrap + EndSync);
        uploadedKitty.Clear();
    }

    protected override void AfterTerminalStop(bool preserveScreen)
    {
        if (!altScreenActive) return;
        altScreenActive = false;
        if (preserveScreen) Terminal.Write(BeginSync + ExitAltScreen + "\u001b[?25h" + EndSync);
        else
        {
            var width = Math.Max(1, Terminal.Columns);
            var document = Render(width).Select(line => LayoutEngine.Osc133ZonePrefix().Replace(line, "").Replace(CursorMarker, "", StringComparison.Ordinal)).ToList();
            lastDocument = ApplyLineResets(document).Select(line => TerminalImage.IsImageLine(line) || TextUtils.VisibleWidth(line) <= width ? line : TextUtils.SliceByColumn(line, 0, width, true)).ToList();
            var buffer = new StringBuilder(BeginSync + ExitAltScreen + DisableAutowrap);
            for (var row = 0; row < lastDocument.Count; row++) { if (row > 0) buffer.Append("\r\n"); buffer.Append("\r\u001b[2K").Append(lastDocument[row]); }
            buffer.Append("\u001b[0m" + EnableAutowrap + "\r\n\u001b[?25h" + EndSync);
            Terminal.Write(buffer.ToString());
        }
        if (savedCapabilities is not null) { TerminalImage.SetCapabilities(savedCapabilities); savedCapabilities = null; }
    }

    private (List<string> Lines, string Evicted) PrepareKittyScreen(List<string> screen)
    {
        var visible = new HashSet<long>();
        var lines = screen.Select(line =>
        {
            if (TerminalImage.GetKittyImagePlacement(line) is not { } placement) return line;
            visible.Add(placement.ImageId);
            var cached = uploadedKitty.FirstOrDefault(entry => entry.Id == placement.ImageId);
            var hadCached = uploadedKitty.Any(entry => entry.Id == placement.ImageId);
            if (hadCached) RemoveKitty(placement.ImageId);
            uploadedKitty.AddLast((placement.ImageId, placement.TransmissionGeneration, placement.TransmissionBytes, placement.EstimatedDecodedBytes));
            return hadCached && cached.Generation == placement.TransmissionGeneration ? placement.ReplacementLine : line;
        }).ToList();
        int count = 0; long transmission = 0, decoded = 0;
        foreach (var entry in uploadedKitty) { if (visible.Contains(entry.Id)) continue; count++; transmission += entry.Transmission; decoded += entry.Decoded; }
        var evicted = new StringBuilder();
        foreach (var entry in uploadedKitty.ToList())
        {
            if (count <= MaxOffscreenImages && transmission <= MaxOffscreenTransmission && decoded <= MaxOffscreenDecoded) break;
            if (visible.Contains(entry.Id)) continue;
            evicted.Append(TerminalImage.DeleteKittyImage(entry.Id)); RemoveKitty(entry.Id);
            count--; transmission -= entry.Transmission; decoded -= entry.Decoded;
        }
        return (lines, evicted.ToString());
    }
    private void RemoveKitty(long id) { for (var node = uploadedKitty.First; node is not null; node = node.Next) if (node.Value.Id == id) { uploadedKitty.Remove(node); return; } }

    protected override void ResetRenderState() { previousScreen = []; previousScreenWidth = 0; previousScreenHeight = 0; currentLayout = null; }

    public void ScrollBy(int lines) { PrimaryScrollView.ScrollBy(lines); RequestRender(); }
    public void ScrollToTop() { PrimaryScrollView.ScrollToStart(); RequestRender(); }
    public void ScrollToBottom() { PrimaryScrollView.ScrollToEnd(); RequestRender(); }

    private void ScrollToPrompt(int direction)
    {
        if (currentLayout is null) return;
        var view = PrimaryScrollView;
        if (LayoutEngine.GetScrollViewBox(currentLayout, view)?.ScrollContentLines is not { } lines) return;
        for (var row = view.ScrollTop + direction; row >= 0 && row < lines.Count; row += direction)
        {
            if (!PromptStart().IsMatch(lines[row])) continue;
            view.ScrollTo(row); RequestRender(); return;
        }
    }

    private void ToggleSearch()
    {
        if (activeSearch is not null) { CloseSearch(); return; }
        var component = new AltScreenSearchComponent(UpdateSearchQuery, searchNavigationButtonStyle);
        var search = new ActiveSearch { Component = component, AnchorRow = PrimaryScrollView.ScrollTop };
        activeSearch = search;
        search.Overlay = ShowOverlay(component, new OverlayOptions { Anchor = OverlayAnchor.TopRight, Width = SizeValue.Pct(40), MinWidth = 32, Margin = OverlayMargin.All(1) });
    }
    private void CloseSearch()
    {
        if (activeSearch is not { } search) return;
        activeSearch = null; search.Overlay?.Hide(); RequestRender();
    }
    private void UpdateSearchQuery(string query)
    {
        if (activeSearch is not { } search || query == search.Query) return;
        var selected = search.SelectedIndex >= 0 && search.SelectedIndex < search.Matches.Count ? search.Matches[search.SelectedIndex] : null;
        search.AnchorRow = selected?.Segments.FirstOrDefault()?.Row ?? PrimaryScrollView.ScrollTop;
        search.Query = query; search.SelectionMode = "query"; search.Component.SetResult(-1, 0);
        RequestRender();
    }
    private void NavigateSearch(int direction)
    {
        if (activeSearch is not { } search || search.Query.Length == 0) return;
        search.SelectionMode = direction < 0 ? "previous" : "next"; RequestRender();
    }
    private int? SearchNavigationDirectionAt(int x, int y)
    {
        if (activeSearch?.Overlay?.GetBounds() is not { } bounds) return null;
        if (x < bounds.Col || x >= bounds.Col + bounds.Width || y < bounds.Row || y >= bounds.Row + bounds.Height) return null;
        return activeSearch.Component.GetNavigationDirectionAt(y - bounds.Row, x - bounds.Col);
    }
    private bool HandleSearchMouse(SgrMouseEvent mouseEvent)
    {
        if (activeSearch is not { } search) return false;
        var direction = SearchNavigationDirectionAt(mouseEvent.X, mouseEvent.Y);
        if (search.Component.SetHoveredNavigationDirection(direction)) RequestRender();
        if (direction is null || mouseEvent.Release || (mouseEvent.Button & 32) != 0 || (mouseEvent.Button & 3) != 0) return false;
        NavigateSearch(direction.Value); return true;
    }

    private bool RefreshSearch(LayoutFrame layout)
    {
        if (activeSearch is not { } search) return false;
        var view = layout.PrimaryScrollView ?? implicitScrollView;
        var box = LayoutEngine.GetScrollViewBox(layout, view);
        if (box?.ScrollContentLines is not { } lines || TextUtils.JsTrim(search.Query).Length == 0)
        {
            search.Matches = []; search.SelectedIndex = -1; search.SelectedKey = null; search.SelectionMode = "retain"; search.Component.SetResult(-1, 0);
            return false;
        }
        var reveal = search.SelectionMode != "retain";
        var (matches, changed) = search.Index.Search(lines, search.Query);
        search.Matches = matches;
        if (!changed && search.SelectionMode == "retain") return false;
        var exact = changed ? search.SelectedKey is { } key ? matches.FindIndex(match => match.Key == key) : -1 : search.SelectedIndex;
        var selected = -1;
        if (matches.Count > 0)
        {
            switch (search.SelectionMode)
            {
                case "query":
                    int low = 0, high = matches.Count;
                    while (low < high) { var middle = low + (high - low) / 2; if ((matches[middle].Segments.FirstOrDefault()?.Row ?? 0) < search.AnchorRow) low = middle + 1; else high = middle; }
                    selected = low < matches.Count ? low : 0; break;
                case "next":
                    { var b = exact >= 0 ? exact : Math.Min(search.SelectedIndex, matches.Count - 1); selected = b < 0 ? 0 : (b + 1) % matches.Count; break; }
                case "previous":
                    { var b = exact >= 0 ? exact : Math.Min(search.SelectedIndex, matches.Count - 1); selected = b < 0 ? matches.Count - 1 : (b - 1 + matches.Count) % matches.Count; break; }
                default: selected = exact >= 0 ? exact : Math.Min(Math.Max(0, search.SelectedIndex), matches.Count - 1); break;
            }
        }
        search.SelectedIndex = selected; search.SelectedKey = selected >= 0 ? matches[selected].Key : null;
        search.SelectionMode = "retain"; search.Component.SetResult(selected, matches.Count);
        if (!reveal || selected < 0) return false;
        var first = matches[selected].Segments.FirstOrDefault(); var last = matches[selected].Segments.LastOrDefault();
        if (first is null || last is null || view.ViewportHeight <= 0) return false;
        var before = view.ScrollTop; var bottom = before + view.ViewportHeight - 1; var target = before;
        if (first.Row < before || last.Row > bottom) target = first.Row - view.ViewportHeight / 3;
        view.ScrollTo(target, disableFollow: true);
        return view.ScrollTop != before;
    }

    /// <summary>Shows a transient message in the flash stack.</summary>
    public void Flash(string message, int durationMs = 1000) => flashes.Flash(message, durationMs);

    private bool DeferToOverlay() => IsOverlayFocused() && activeSearch?.Overlay?.IsFocused != true;
    private void ClearComponentMouseGesture() { mouseCapture = null; mousePressTarget = null; mousePressPoint = null; mousePressMoved = false; }

    private TuiInputListenerResult? HandleViewportInput(string data)
    {
        if (data == FocusOut)
        {
            var hadActive = selectionPressActive; var hadNonEmpty = hadActive && SelectionBounds() is not null;
            selectionPressActive = false; StopSelectionAutoScroll(); StopScrollbarHover();
            if (activeSearch?.Component.SetHoveredNavigationDirection(null) == true) RequestRender();
            scrollbarDrag = null; pressedUrl = null; selectionDragged = false; ClearComponentMouseGesture(); lastComponentClick = null;
            if (hadActive)
            {
                selectionAnchor = null; selectionFocus = null; selectionGranularity = "character"; selectionInitialRange = null;
                if (hadNonEmpty) RequestRender();
            }
            lastClick = null;
            return new(Consume: true);
        }
        if (data == FocusIn) return new(Consume: true);
        if (ParseWheel(data) is { } wheel)
        {
            var lines = wheelScroll.Next(wheel.Direction, Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
            var delta = wheel.Direction * ((wheel.Button & 8) != 0 ? lines * AltWheelMultiplier : lines);
            var mouseEvent = CreateMouseEvent(TuiMouseEventType.Wheel, wheel.Button, wheel.X, wheel.Y, wheelDelta: delta);
            var overlay = DispatchMouseToOverlay(mouseEvent);
            var result = overlay.Result ?? (overlay.Hit ? null : DispatchMouseToLayout(mouseEvent));
            if (result is not null) { if (ApplyMouseDispatchResult(mouseEvent, result)) RequestRender(); return new(Consume: true); }
            if (DeferToOverlay()) return null;
            RouteWheel(wheel.X, wheel.Y, delta);
            return new(Consume: true);
        }
        if (ParseSgrMouse(data) is { } mouse) { HandleMouseEvent(mouse); return new(Consume: true); }
        if (IsMouseSequence(data)) return new(Consume: true);
        var kb = KeybindingsManager.Global; var release = Keys.IsKeyRelease(data);
        if (kb.Matches(data, "tui.altScreen.search")) { if (!release) ToggleSearch(); return new(Consume: true); }
        if (activeSearch?.Overlay?.IsFocused == true)
        {
            if (kb.Matches(data, "tui.altScreen.searchNext")) { if (!release) NavigateSearch(1); return new(Consume: true); }
            if (kb.Matches(data, "tui.altScreen.searchPrevious")) { if (!release) NavigateSearch(-1); return new(Consume: true); }
            if (kb.Matches(data, "tui.altScreen.searchClose")) { if (!release) CloseSearch(); return new(Consume: true); }
        }
        if (DeferToOverlay()) return null;
        var height = PrimaryScrollView.ViewportHeight;
        (string Binding, Action Run)[] bindings =
        [
            ("tui.altScreen.pageUp", () => ScrollBy(-Math.Max(1, height - PageOverlap))),
            ("tui.altScreen.pageDown", () => ScrollBy(Math.Max(1, height - PageOverlap))),
            ("tui.altScreen.halfPageUp", () => ScrollBy(-Math.Max(1, height / 2))),
            ("tui.altScreen.halfPageDown", () => ScrollBy(Math.Max(1, height / 2))),
            ("tui.altScreen.lineUp", () => ScrollBy(-1)),
            ("tui.altScreen.lineDown", () => ScrollBy(1)),
            ("tui.altScreen.previousPrompt", () => ScrollToPrompt(-1)),
            ("tui.altScreen.nextPrompt", () => ScrollToPrompt(1)),
            ("tui.altScreen.top", ScrollToTop),
            ("tui.altScreen.bottom", ScrollToBottom),
        ];
        foreach (var (binding, run) in bindings)
            if (kb.Matches(data, binding)) { if (!release) run(); return new(Consume: true); }
        return null;
    }

    private static TuiMouseButton DecodeButton(int button) => (button & 3) switch { 0 => TuiMouseButton.Left, 1 => TuiMouseButton.Middle, 2 => TuiMouseButton.Right, _ => TuiMouseButton.None };
    private TuiMouseEvent CreateMouseEvent(TuiMouseEventType type, int button, int x, int y, int? wheelDelta = null, int? clickCount = null) =>
        new(type, type == TuiMouseEventType.Wheel ? TuiMouseButton.None : DecodeButton(button), x, y, x, y, Math.Max(1, Terminal.Columns), Math.Max(1, Terminal.Rows),
            (button & 4) != 0, (button & 8) != 0, (button & 16) != 0, wheelDelta, clickCount);

    private TuiMouseDispatchResult? DispatchMouseToLayout(TuiMouseEvent mouseEvent)
    {
        if (currentLayout is null) return null;
        var visited = new HashSet<IComponent>(ReferenceEqualityComparer.Instance);
        foreach (var box in LayoutEngine.GetBoxesAt(currentLayout, mouseEvent.ScreenX, mouseEvent.ScreenY))
        {
            if (visited.Contains(box.Component)) continue;
            // Layout containers are laid out by the engine; only their leaf components receive the event.
            if (box.Component is ILayoutComponent && box.Component is Container) continue;
            visited.Add(box.Component);
            var result = Mouse.Dispatch(box.Component, mouseEvent with
            { X = mouseEvent.ScreenX - box.Rect.X, Y = mouseEvent.ScreenY - box.Rect.Y, Width = box.Rect.Width, Height = box.Rect.Height });
            if (result is not null) return result;
        }
        return null;
    }

    private bool ApplyMouseDispatchResult(TuiMouseEvent mouseEvent, TuiMouseDispatchResult result)
    {
        var target = ResolveMouseFocusTarget(result.FocusTarget ?? result.Target.Component);
        var focusChanged = result.Focus && FocusedComponent != target;
        if (result.Focus) SetFocus(target);
        if (result.Capture) mouseCapture = result.Target;
        return result.Render ?? (focusChanged || mouseEvent.Type is TuiMouseEventType.Press or TuiMouseEventType.Click or TuiMouseEventType.Drag or TuiMouseEventType.Wheel);
    }

    private int ComponentClickCount(TuiMouseDispatchTarget target, int x, int y)
    {
        var now = System.Environment.TickCount64;
        var count = lastComponentClick is { } previous && now - previous.Timestamp <= DoubleClickMs && previous.Component == target.Component && previous.X == x && previous.Y == y
            ? previous.Count % 3 + 1 : 1;
        lastComponentClick = (now, count, target.Component, x, y);
        return count;
    }

    private void ClearTextSelection()
    {
        StopSelectionAutoScroll(); selectionPressActive = false; selectionAnchor = null; selectionFocus = null;
        selectionGranularity = "character"; selectionInitialRange = null; pressedUrl = null; selectionDragged = false;
    }

    private void HandleMouseEvent(SgrMouseEvent raw)
    {
        var motion = (raw.Button & 32) != 0;
        var type = raw.Release ? TuiMouseEventType.Release : motion ? DecodeButton(raw.Button) == TuiMouseButton.None ? TuiMouseEventType.Move : TuiMouseEventType.Drag : TuiMouseEventType.Press;
        var mouseEvent = CreateMouseEvent(type, raw.Button, raw.X, raw.Y);
        if ((mouseCapture ?? mousePressTarget) is { } target)
        {
            if (mousePressPoint is { } point && (raw.X != point.X || raw.Y != point.Y)) { mousePressMoved = true; lastComponentClick = null; }
            var render = false;
            if (Mouse.Dispatch(target.Component, Mouse.Retarget(mouseEvent, target)) is { } targetResult) render = ApplyMouseDispatchResult(mouseEvent, targetResult);
            if (raw.Release)
            {
                if (!mousePressMoved && mousePressPoint is { } pressed && pressed.X == raw.X && pressed.Y == raw.Y)
                {
                    var click = CreateMouseEvent(TuiMouseEventType.Click, raw.Button, raw.X, raw.Y, clickCount: ComponentClickCount(target, raw.X, raw.Y));
                    if (Mouse.Dispatch(target.Component, Mouse.Retarget(click, target)) is { } clickResult) render = ApplyMouseDispatchResult(click, clickResult) || render;
                }
                ClearComponentMouseGesture();
            }
            if (render) RequestRender();
            return;
        }
        if (HandleSearchMouse(raw)) return;
        var overlay = DispatchMouseToOverlay(mouseEvent);
        if (!overlay.Hit)
        {
            if (HandleScrollToEndIndicator(raw)) return;
            var handled = HandleScrollbarMouse(raw);
            if (scrollbarDrag is null) UpdateScrollbarHover(raw.X, raw.Y);
            if (handled) return;
        }
        else StopScrollbarHover();
        var result = overlay.Result ?? (overlay.Hit ? null : DispatchMouseToLayout(mouseEvent));
        if (result is not null)
        {
            var render = ApplyMouseDispatchResult(mouseEvent, result);
            if (type == TuiMouseEventType.Press) { ClearTextSelection(); mousePressTarget = result.Target; mousePressPoint = (raw.X, raw.Y); mousePressMoved = false; }
            if (render) RequestRender();
            return;
        }
        if (HandleRightClickPaste(raw)) return;
        HandleSelectionMouse(raw);
    }

    private static (int Direction, int X, int Y, int Button)? ParseWheel(string data)
    {
        var match = SgrMouse().Match(data);
        if (match.Success)
        {
            var button = int.Parse(match.Groups[1].Value);
            if ((button & 64) == 0) return null;
            var direction = button & 3;
            if (direction is not (0 or 1)) return null;
            return (direction == 0 ? -1 : 1, int.Parse(match.Groups[2].Value) - 1, int.Parse(match.Groups[3].Value) - 1, button);
        }
        if (data.Length == 6 && data.StartsWith("\u001b[M", StringComparison.Ordinal))
        {
            var button = data[3] - 32;
            if ((button & 64) == 0) return null;
            var direction = button & 3;
            if (direction is not (0 or 1)) return null;
            return (direction == 0 ? -1 : 1, data[4] - 33, data[5] - 33, button);
        }
        return null;
    }

    private void RouteWheel(int x, int y, int delta)
    {
        var remaining = delta; var seen = new HashSet<ScrollView>(ReferenceEqualityComparer.Instance);
        foreach (var view in currentLayout is null ? [] : LayoutEngine.GetScrollViewsAt(currentLayout, x, y))
        {
            seen.Add(view);
            remaining = view.ScrollBy(remaining);
            if (remaining == 0 || view.Overscroll == ScrollOverscroll.Contain) break;
        }
        var primary = PrimaryScrollView;
        if (remaining != 0 && !seen.Contains(primary)) primary.ScrollBy(remaining);
        UpdateScrollbarHover(x, y);
        RequestRender();
    }

    private static SgrMouseEvent? ParseSgrMouse(string data)
    {
        var match = SgrMouse().Match(data);
        return match.Success ? new(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value) - 1, int.Parse(match.Groups[3].Value) - 1, match.Groups[4].Value == "m") : null;
    }

    private bool HandleRightClickPaste(SgrMouseEvent mouseEvent)
    {
        if (onRightClickPaste is null || !OperatingSystem.IsWindows() || (env("TERM_PROGRAM") ?? "").Equals("vscode", StringComparison.OrdinalIgnoreCase) ||
            mouseEvent.Release || mouseEvent.Button != 2) return false;
        try { onRightClickPaste(); } catch { }
        return true;
    }

    private bool HandleScrollToEndIndicator(SgrMouseEvent mouseEvent)
    {
        if (scrollToEndRect is not { } rect || mouseEvent.Release || (mouseEvent.Button & 32) != 0 || (mouseEvent.Button & 3) != 0) return false;
        if (mouseEvent.Y != rect.Row || mouseEvent.X < rect.Column || mouseEvent.X >= rect.Column + rect.Width) return false;
        ScrollToBottom(); return true;
    }

    private (ScrollView View, ScrollbarGeometry Geometry)? ScrollbarTargetAt(int x, int y, bool includeHiddenAuto = false)
    {
        if (HasOverlay() || currentLayout is null) return null;
        foreach (var view in LayoutEngine.GetScrollViewsAt(currentLayout, x, y))
        {
            var box = LayoutEngine.GetScrollViewBox(currentLayout, view);
            if (box is not null && LayoutEngine.GetScrollbarGeometry(box, includeHiddenAuto) is { } geometry && x == geometry.Column && y >= geometry.TrackTop && y < geometry.TrackTop + geometry.TrackHeight)
                return (view, geometry);
        }
        return null;
    }
    private void SetScrollbarHover(ScrollView? view)
    {
        if (view == scrollbarHover) return;
        scrollbarHover?.SetScrollbarActive(false); scrollbarHover = view; scrollbarHover?.SetScrollbarActive(true);
    }
    private void UpdateScrollbarHover(int x, int y) => SetScrollbarHover(ScrollbarTargetAt(x, y, true)?.View);
    private void StopScrollbarHover() => SetScrollbarHover(null);
    private static void ScrollToPointer(ScrollView view, ScrollbarGeometry geometry, int pointerY, int grabOffset)
    {
        var maxThumb = geometry.TrackHeight - geometry.ThumbHeight;
        var thumbOffset = Math.Max(0, Math.Min(maxThumb, pointerY - geometry.TrackTop - grabOffset));
        view.ScrollTo(maxThumb == 0 ? 0 : (int)Math.Round((double)thumbOffset / maxThumb * geometry.MaxScrollTop, MidpointRounding.AwayFromZero));
    }
    private bool HandleScrollbarMouse(SgrMouseEvent mouseEvent)
    {
        if (scrollbarDrag is { } drag)
        {
            if (mouseEvent.Release) { scrollbarDrag = null; return true; }
            var box = currentLayout is null ? null : LayoutEngine.GetScrollViewBox(currentLayout, drag.View);
            if (box is not null && LayoutEngine.GetScrollbarGeometry(box) is { } geometry) ScrollToPointer(drag.View, geometry, mouseEvent.Y, drag.GrabOffset);
            return true;
        }
        if (mouseEvent.Release || (mouseEvent.Button & 32) != 0 || (mouseEvent.Button & 3) != 0) return false;
        if (ScrollbarTargetAt(mouseEvent.X, mouseEvent.Y) is not { } target) return false;
        StopSelectionAutoScroll(); selectionPressActive = false; selectionAnchor = null; selectionFocus = null; selectionGranularity = "character";
        selectionInitialRange = null; lastClick = null; pressedUrl = null; selectionDragged = false;
        SetScrollbarHover(target.View);
        var onThumb = mouseEvent.Y >= target.Geometry.ThumbTop && mouseEvent.Y < target.Geometry.ThumbTop + target.Geometry.ThumbHeight;
        var grab = onThumb ? mouseEvent.Y - target.Geometry.ThumbTop : target.Geometry.ThumbHeight / 2;
        if (!onThumb) ScrollToPointer(target.View, target.Geometry, mouseEvent.Y, grab);
        scrollbarDrag = (target.View, grab);
        return true;
    }

    private SelectionPoint? ScrollSelectionPoint(ScrollView view, int x, int y)
    {
        if (currentLayout is null) return null;
        var box = LayoutEngine.GetScrollViewBox(currentLayout, view);
        if (box is null || box.Rect.Height <= 0 || box.Clip.Height <= 0) return null;
        var top = Math.Max(0, Math.Max(box.Rect.Y, box.Clip.Y));
        var bottom = Math.Min(Terminal.Rows - 1, Math.Min(box.Rect.Y + box.Rect.Height - 1, box.Clip.Y + box.Clip.Height - 1));
        if (bottom < top) return null;
        var pointerRow = Math.Max(top, Math.Min(bottom, y));
        var maxRow = Math.Max(0, (box.ScrollContentLines?.Count ?? 1) - 1);
        return new(Math.Max(0, Math.Min(maxRow, view.ScrollTop + pointerRow - box.Rect.Y)), Math.Max(0, Math.Min(box.Rect.Width - 1, x - box.Rect.X)), view);
    }
    private SelectionPoint GetSelectionPoint(SgrMouseEvent mouseEvent, ScrollView? view)
    {
        if (view is not null && ScrollSelectionPoint(view, mouseEvent.X, mouseEvent.Y) is { } point) return point;
        return new(Math.Max(0, Math.Min(Terminal.Rows - 1, mouseEvent.Y)), Math.Max(0, Math.Min(Terminal.Columns - 1, mouseEvent.X)));
    }
    private string SelectionSourceLine(SelectionPoint point)
    {
        if (point.ScrollView is not null && currentLayout is not null && LayoutEngine.GetScrollViewBox(currentLayout, point.ScrollView)?.ScrollContentLines is { } lines)
            return point.Row < lines.Count ? lines[point.Row] : "";
        return point.Row < previousScreen.Count ? previousScreen[point.Row] : "";
    }
    private SelectionRange? WordSelection(SelectionPoint point)
    {
        var line = TextUtils.StripTerminalSequences(SelectionSourceLine(point));
        var segments = new List<(int Start, int End, bool Selectable, bool Joiner)>(); var start = 0;
        foreach (var segment in WordSegmenter.Segment(line))
        {
            var end = start + TextUtils.VisibleWidth(segment.Segment);
            var joiner = segment.Segment is "/" or "-";
            segments.Add((start, end, segment.IsWordLike || joiner, joiner)); start = end;
        }
        var clicked = segments.FindIndex(segment => point.Col >= segment.Start && point.Col < segment.End);
        if (clicked < 0) return null;
        static bool CanJoin((int, int, bool Selectable, bool Joiner) left, (int, int, bool Selectable, bool Joiner) right) =>
            left.Selectable && right.Selectable && (left.Joiner || right.Joiner);
        var selectionStart = segments[clicked].Start; var selectionEnd = segments[clicked].End;
        for (var index = clicked; index > 0 && CanJoin(segments[index - 1], segments[index]); index--) selectionStart = segments[index - 1].Start;
        for (var index = clicked; index < segments.Count - 1 && CanJoin(segments[index], segments[index + 1]); index++) selectionEnd = segments[index + 1].End;
        return new(point with { Col = selectionStart }, point with { Col = selectionEnd, Boundary = true });
    }
    private SelectionRange LineSelection(SelectionPoint point) => new(point with { Col = 0 }, point with { Col = TextUtils.VisibleWidth(SelectionSourceLine(point)), Boundary = true });

    private void UpdateSelectionFocus(SelectionPoint point)
    {
        if (selectionGranularity == "character" || selectionInitialRange is null) { selectionFocus = point; return; }
        var range = selectionGranularity == "word" ? WordSelection(point) : LineSelection(point);
        if (range is null) return;
        var initial = selectionInitialRange;
        var before = range.Start.Row < initial.Start.Row || range.Start.Row == initial.Start.Row && range.Start.Col < initial.Start.Col;
        if (before) { selectionAnchor = initial.End; selectionFocus = range.Start; }
        else { selectionAnchor = initial.Start; selectionFocus = range.End; }
    }
    private int ClickCount(SelectionPoint point, SelectionRange? word)
    {
        var now = System.Environment.TickCount64;
        var count = word is not null && lastClick is { } previous && now - previous.Timestamp <= DoubleClickMs && previous.Row == point.Row &&
            previous.ScrollView == point.ScrollView && previous.WordStart == word.Start.Col && previous.WordEnd == word.End.Col ? previous.Count % 3 + 1 : 1;
        lastClick = word is null ? null : new(now, count, point.Row, point.ScrollView, word.Start.Col, word.End.Col);
        return count;
    }
    private void UpdateSelectionAutoScroll(SgrMouseEvent mouseEvent)
    {
        if (selectionAnchor?.ScrollView is not { } view || currentLayout is null) { StopSelectionAutoScroll(); return; }
        var box = LayoutEngine.GetScrollViewBox(currentLayout, view);
        if (box is null || box.Rect.Height <= 0 || box.Clip.Height <= 0) { StopSelectionAutoScroll(); return; }
        var top = Math.Max(0, Math.Max(box.Rect.Y, box.Clip.Y));
        var bottom = Math.Min(Terminal.Rows - 1, Math.Min(box.Rect.Y + box.Rect.Height - 1, box.Clip.Y + box.Clip.Height - 1));
        selectionDragPointer = (mouseEvent.X, mouseEvent.Y);
        selectionAutoScrollDirection = mouseEvent.Y <= top ? -1 : mouseEvent.Y >= bottom ? 1 : 0;
        if (selectionAutoScrollDirection == 0) { StopSelectionAutoScroll(); return; }
        selectionAutoScrollTimer ??= Loop.SetInterval(AutoScrollSelection, 50);
    }
    private void AutoScrollSelection()
    {
        if (selectionAnchor?.ScrollView is not { } view || selectionDragPointer is not { } pointer || selectionAutoScrollDirection == 0) { StopSelectionAutoScroll(); return; }
        var remaining = view.ScrollBy(selectionAutoScrollDirection);
        if (remaining == selectionAutoScrollDirection) { StopSelectionAutoScroll(); return; }
        if (ScrollSelectionPoint(view, pointer.X, pointer.Y) is { } point) UpdateSelectionFocus(point);
        RequestRender();
    }
    private void StopSelectionAutoScroll() { selectionAutoScrollTimer?.Dispose(); selectionAutoScrollTimer = null; selectionAutoScrollDirection = 0; selectionDragPointer = null; }

    private void HandleSelectionMouse(SgrMouseEvent mouseEvent)
    {
        var button = mouseEvent.Button & 3;
        if (button != 0 && !(mouseEvent.Release && button == 3)) return;
        var point = GetSelectionPoint(mouseEvent, selectionAnchor?.ScrollView);
        if (mouseEvent.Release)
        {
            if (!selectionPressActive) return;
            selectionPressActive = false; StopSelectionAutoScroll();
            if (selectionAnchor is null) return;
            UpdateSelectionFocus(point);
            var isClick = !selectionDragged && selectionAnchor.ScrollView == point.ScrollView && selectionAnchor.Row == point.Row && selectionAnchor.Col == point.Col;
            var clickedUrl = isClick ? pressedUrl : null; pressedUrl = null;
            if (clickedUrl is not null && openUrl is not null)
            {
                selectionAnchor = null; selectionFocus = null;
                try { openUrl(clickedUrl); } catch { }
                RequestRender(); return;
            }
            if (isClick)
            {
                var click = CreateMouseEvent(TuiMouseEventType.Click, mouseEvent.Button, mouseEvent.X, mouseEvent.Y, clickCount: lastClick?.Count ?? 1);
                var overlay = DispatchMouseToOverlay(click);
                var result = overlay.Result ?? (overlay.Hit ? null : DispatchMouseToLayout(click));
                if (result is not null) { var render = ApplyMouseDispatchResult(click, result); ClearTextSelection(); if (render) RequestRender(); return; }
            }
            if (CopyOnSelect) _ = CopySelectionToClipboard();
            RequestRender(); return;
        }
        if ((mouseEvent.Button & 32) != 0)
        {
            if (!selectionPressActive || selectionAnchor is null) return;
            selectionDragged = true; lastClick = null; pressedUrl = null;
            UpdateSelectionFocus(point); UpdateSelectionAutoScroll(mouseEvent); RequestRender();
            return;
        }
        StopSelectionAutoScroll();
        selectionPressActive = true;
        var view = !HasOverlay() && currentLayout is not null ? LayoutEngine.GetScrollViewsAt(currentLayout, mouseEvent.X, mouseEvent.Y).FirstOrDefault() : null;
        var anchor = GetSelectionPoint(mouseEvent, view);
        var word = WordSelection(anchor);
        var clicks = ClickCount(anchor, word);
        var range = clicks == 2 ? word : clicks == 3 ? LineSelection(anchor) : null;
        selectionGranularity = range is not null ? clicks == 2 ? "word" : "line" : "character";
        selectionInitialRange = range;
        selectionAnchor = range?.Start ?? anchor; selectionFocus = range?.End ?? anchor;
        selectionDragged = false;
        var row = Math.Max(0, Math.Min(Terminal.Rows - 1, mouseEvent.Y));
        pressedUrl = range is not null ? null : TextUtils.GetOsc8LinkAtColumn(row < previousScreen.Count ? previousScreen[row] : "", Math.Max(0, Math.Min(Terminal.Columns - 1, mouseEvent.X)));
        RequestRender();
    }

    private SelectionRange? SelectionBounds()
    {
        if (selectionAnchor is null || selectionFocus is null || selectionAnchor.ScrollView != selectionFocus.ScrollView) return null;
        if (selectionAnchor.Row == selectionFocus.Row && selectionAnchor.Col == selectionFocus.Col) return null;
        var anchorFirst = selectionAnchor.Row < selectionFocus.Row || selectionAnchor.Row == selectionFocus.Row && selectionAnchor.Col < selectionFocus.Col;
        return anchorFirst ? new(selectionAnchor, selectionFocus) : new(selectionFocus, selectionAnchor);
    }

    private static (int Start, int End) SelectionColumns(string line, int row, SelectionRange selection, int minColumn = 0, int? maxColumn = null)
    {
        var lineWidth = TextUtils.VisibleWidth(line);
        var max = maxColumn ?? lineWidth;
        var start = Math.Max(0, minColumn); var end = Math.Min(lineWidth, max);
        if (row == selection.Start.Row) start = TextUtils.GetGraphemeCellRange(line, selection.Start.Col)?.Start ?? Math.Min(selection.Start.Col, lineWidth);
        if (row == selection.End.Row)
            end = selection.End.Boundary ? Math.Min(selection.End.Col, lineWidth) : TextUtils.GetGraphemeCellRange(line, selection.End.Col)?.End ?? Math.Min(selection.End.Col + 1, lineWidth);
        return (Math.Max(minColumn, start), Math.Min(max, end));
    }

    private string? ActiveSelectionText()
    {
        if (SelectionBounds() is not { } selection) return null;
        IReadOnlyList<string> source = previousScreen;
        if (selection.Start.ScrollView is { } view)
        {
            if (currentLayout is null || LayoutEngine.GetScrollViewBox(currentLayout, view)?.ScrollContentLines is not { } lines) return null;
            source = lines;
        }
        var result = new List<string>();
        for (var row = selection.Start.Row; row <= selection.End.Row; row++)
        {
            var line = row < source.Count ? source[row] : "";
            var (start, end) = SelectionColumns(line, row, selection);
            result.Add(TextUtils.JsTrimEnd(TextUtils.StripTerminalSequences(TextUtils.SliceByColumn(line, start, Math.Max(0, end - start), true))));
        }
        var text = string.Join('\n', result);
        return text.Length == 0 ? null : text;
    }

    private async Task<bool> CopySelectionToClipboard() => ActiveSelectionText() is { } text && await CopyText(text);

    private async Task<bool> CopyText(string text)
    {
        if (copySelection is not null)
        {
            var (ok, error) = await copySelection(text);
            Flash(ok ? "Copied!" : error ?? "Copy failed", ok ? 1000 : CopyErrorFlashMs);
            return ok;
        }
        Terminal.Write("\u001b]52;c;" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) + "\u0007");
        Flash("Copied!");
        return true;
    }

    private string ApplySearchTextHighlight(string text, bool current)
    {
        var style = current ? searchCurrentMatchStyle : searchMatchStyle;
        var result = new StringBuilder(); int plainStart = 0, index = 0;
        while (index < text.Length)
        {
            var code = TextUtils.ExtractAnsiCode(text, index);
            if (code is null) { index++; continue; }
            if (index > plainStart) result.Append(style(text[plainStart..index]));
            result.Append(code); index += code.Length; plainStart = index;
        }
        if (plainStart < text.Length) result.Append(style(text[plainStart..]));
        return result.ToString();
    }

    private List<string> ApplySearchHighlights(List<string> screen, LayoutFrame layout)
    {
        if (activeSearch is not { } search || search.SelectedIndex < 0 || search.Matches.Count == 0) return screen;
        var view = layout.PrimaryScrollView ?? implicitScrollView;
        if (LayoutEngine.GetScrollViewBox(layout, view) is not { } box) return screen;
        var ranges = new Dictionary<int, List<(int Start, int End, bool Current)>>();
        var scrollbarColumn = LayoutEngine.GetScrollbarGeometry(box)?.Column;
        var minRow = Math.Max(0, Math.Max(box.Rect.Y, box.Clip.Y));
        var maxRow = Math.Min(screen.Count, Math.Min(box.Rect.Y + box.Rect.Height, box.Clip.Y + box.Clip.Height));
        var minColumn = Math.Max(0, Math.Max(box.Rect.X, box.Clip.X));
        var maxColumn = Math.Min(Math.Min(Terminal.Columns, box.Rect.X + box.Rect.Width), Math.Min(box.Clip.X + box.Clip.Width, scrollbarColumn ?? int.MaxValue));
        var minContentRow = view.ScrollTop + minRow - box.Rect.Y; var maxContentRow = view.ScrollTop + maxRow - box.Rect.Y - 1;
        int low = 0, high = search.Matches.Count;
        while (low < high) { var middle = low + (high - low) / 2; if ((search.Matches[middle].Segments.LastOrDefault()?.Row ?? -1) < minContentRow) low = middle + 1; else high = middle; }
        for (var matchIndex = low; matchIndex < search.Matches.Count; matchIndex++)
        {
            var match = search.Matches[matchIndex];
            if ((match.Segments.FirstOrDefault()?.Row ?? 0) > maxContentRow) break;
            foreach (var segment in match.Segments)
            {
                var row = box.Rect.Y + segment.Row - view.ScrollTop;
                if (row < minRow || row >= maxRow) continue;
                var startCol = Math.Max(minColumn, box.Rect.X + segment.StartCol); var endCol = Math.Min(maxColumn, box.Rect.X + segment.EndCol);
                if (endCol <= startCol) continue;
                if (!ranges.TryGetValue(row, out var list)) ranges[row] = list = [];
                list.Add((startCol, endCol, matchIndex == search.SelectedIndex));
            }
        }
        var result = new List<string>(screen);
        foreach (var (row, list) in ranges)
        {
            var line = row < result.Count ? result[row] : "";
            if (TerminalImage.IsImageLine(line)) continue;
            var lineWidth = TextUtils.VisibleWidth(line);
            foreach (var range in list.OrderByDescending(range => range.Start))
            {
                var startCol = Math.Min(range.Start, lineWidth); var endCol = Math.Min(range.End, lineWidth);
                if (endCol <= startCol) continue;
                line = TextUtils.SliceByColumn(line, 0, startCol, true) + ApplySearchTextHighlight(TextUtils.SliceByColumn(line, startCol, endCol - startCol, true), range.Current) +
                    TextUtils.SliceByColumn(line, endCol, Math.Max(0, lineWidth - endCol), true);
            }
            result[row] = line;
        }
        return result;
    }

    private static string ApplySelectionHighlight(string text)
    {
        var result = new StringBuilder("\u001b[7m"); var index = 0;
        while (index < text.Length)
        {
            var code = TextUtils.ExtractAnsiCode(text, index);
            if (code is null) { result.Append(text[index]); index++; continue; }
            result.Append(code);
            if (code.EndsWith('m')) result.Append("\u001b[7m");
            index += code.Length;
        }
        return result.Append("\u001b[27m").ToString();
    }

    private List<string> ApplySelection(List<string> screen, LayoutFrame? layout)
    {
        if (SelectionBounds() is not { } selection) return screen;
        var screenSelection = selection;
        int minRow = 0, maxRow = screen.Count - 1, minColumn = 0, maxColumn = Terminal.Columns;
        if (selection.Start.ScrollView is { } view)
        {
            if (layout is null || LayoutEngine.GetScrollViewBox(layout, view) is not { } box) return screen;
            minRow = Math.Max(0, Math.Max(box.Rect.Y, box.Clip.Y));
            maxRow = Math.Min(screen.Count - 1, Math.Min(box.Rect.Y + box.Rect.Height - 1, box.Clip.Y + box.Clip.Height - 1));
            minColumn = Math.Max(0, Math.Max(box.Rect.X, box.Clip.X));
            maxColumn = Math.Min(Terminal.Columns, Math.Min(box.Rect.X + box.Rect.Width, box.Clip.X + box.Clip.Width));
            screenSelection = new(selection.Start with { Row = box.Rect.Y + selection.Start.Row - view.ScrollTop, Col = box.Rect.X + selection.Start.Col },
                selection.End with { Row = box.Rect.Y + selection.End.Row - view.ScrollTop, Col = box.Rect.X + selection.End.Col });
        }
        return screen.Select((line, row) =>
        {
            if (row < minRow || row > maxRow || row < screenSelection.Start.Row || row > screenSelection.End.Row || TerminalImage.IsImageLine(line)) return line;
            var lineWidth = TextUtils.VisibleWidth(line);
            var (start, end) = SelectionColumns(line, row, screenSelection, minColumn, maxColumn);
            if (end <= start) return line;
            return TextUtils.SliceByColumn(line, 0, start, true) + ApplySelectionHighlight(TextUtils.SliceByColumn(line, start, end - start, true)) +
                TextUtils.SliceByColumn(line, end, Math.Max(0, lineWidth - end), true);
        }).ToList();
    }

    private static bool IsMouseSequence(string data) => SgrMouse().IsMatch(data) || data.Length == 6 && data.StartsWith("\u001b[M", StringComparison.Ordinal);

    private List<string> CompositeScrollToEndIndicator(List<string> screen, LayoutFrame layout, int width)
    {
        scrollToEndRect = null;
        var view = layout.PrimaryScrollView ?? implicitScrollView;
        if (scrollToEndIndicator is null || !view.FollowEnd || view.IsFollowingEnd) return screen;
        var box = LayoutEngine.GetScrollViewBox(layout, view);
        if (box?.Clip is not { } clip || clip.Width <= 0 || clip.Height <= 0) return screen;
        var row = clip.Y + clip.Height - 1;
        if (row >= screen.Count || TerminalImage.IsImageLine(screen[row])) return screen;
        var scrollbarColumn = LayoutEngine.GetScrollbarGeometry(box)?.Column;
        var label = TextUtils.TruncateToWidth(scrollToEndIndicator(), clip.Width, "");
        var column = clip.X + (clip.Width - TextUtils.VisibleWidth(label)) / 2;
        var rightEdge = scrollbarColumn ?? clip.X + clip.Width;
        var text = TextUtils.TruncateToWidth(label, Math.Max(0, rightEdge - column), "");
        var textWidth = TextUtils.VisibleWidth(text);
        if (textWidth == 0) return screen;
        var result = new List<string>(screen);
        result[row] = CompositeLine(result[row], text, column, textWidth, width);
        scrollToEndRect = (row, column, textWidth);
        return result;
    }

    private List<string> CompositeFlashes(List<string> screen, int width, int height)
    {
        var flashLines = flashes.Render(width);
        if (flashLines.Count > height) flashLines = flashLines.Skip(flashLines.Count - height).ToList();
        if (flashLines.Count == 0) return screen;
        var result = new List<string>(screen);
        while (result.Count < height) result.Add("");
        for (var row = 0; row < flashLines.Count; row++)
        {
            var flashWidth = TextUtils.VisibleWidth(flashLines[row]);
            if (flashWidth == 0) continue;
            result[row] = CompositeLine(result[row], flashLines[row], width - flashWidth, flashWidth, width);
        }
        return result;
    }

    protected override void DoRender()
    {
        if (stopped || !altScreenActive) return;
        var width = Math.Max(1, Terminal.Columns); var height = Math.Max(1, Terminal.Rows);
        IComponent root = layoutRoot ?? implicitScrollView;
        var next = LayoutEngine.RenderFrame(root, width, height, () => RequestRender());
        if (RefreshSearch(next)) next = LayoutEngine.RenderFrame(root, width, height, () => RequestRender());
        var screen = next.Lines.Select(line => LayoutEngine.Osc133ZonePrefix().Replace(line, "")).ToList();
        screen = ApplySearchHighlights(screen, next);
        screen = CompositeScrollToEndIndicator(screen, next, width);
        screen = CompositeOverlays(screen, width, height);
        if (screen.Count > height) screen = screen.Skip(screen.Count - height).ToList();
        screen = ApplySelection(screen, next);
        screen = CompositeFlashes(screen, width, height);
        var cursorPos = ExtractCursorPosition(screen, height);
        screen = ApplyLineResets(screen).Select(line => TerminalImage.IsImageLine(line) || TextUtils.VisibleWidth(line) <= width ? line : TextUtils.SliceByColumn(line, 0, width, true)).ToList();
        string Previous(int row) => row < previousScreen.Count ? previousScreen[row] : "";
        string Line(List<string> lines, int row) => row < lines.Count ? lines[row] : "";
        var fullRedraw = previousScreen.Count == 0 || previousScreenWidth != width || previousScreenHeight != height;
        var changed = screen.Select((line, row) => row >= previousScreen.Count || !string.Equals(line, previousScreen[row], StringComparison.Ordinal)).ToList();
        var anchorsRedraw = screen.Where((line, row) => changed[row] && (TerminalImage.IsImageLine(line) || TerminalImage.IsImageLine(Previous(row)))).Any();
        var isWezTerm = env("WEZTERM_PANE") is { Length: > 0 } || (env("TERM_PROGRAM") ?? "").Equals("wezterm", StringComparison.OrdinalIgnoreCase);
        var cellsRedraw = !anchorsRedraw && isWezTerm && imageProtocol == ImageProtocol.Kitty && changed.Any(c => c) && screen.Select((line, row) =>
        {
            if (TerminalImage.GetKittyImagePlacementRows(line) is not { } rows) return false;
            for (var covered = row; covered < row + rows; covered++) if (covered < changed.Count && changed[covered]) return true;
            return false;
        }).Any(value => value);
        var imagesRedraw = anchorsRedraw || cellsRedraw;
        var redrawImages = fullRedraw || imagesRedraw;
        var hadUploaded = uploadedKitty.Count > 0;
        var prepared = redrawImages && imageProtocol == ImageProtocol.Kitty ? PrepareKittyScreen(screen) : (screen, "");
        var buffer = new StringBuilder(BeginSync);
        if (fullRedraw)
        {
            fullRedrawCount++;
            buffer.Append(imageProtocol == ImageProtocol.Kitty && hadUploaded ? TerminalImage.DeleteAllKittyPlacements() : imageProtocol == ImageProtocol.Kitty ? TerminalImage.DeleteAllKittyImages() : "");
            buffer.Append("\u001b[2J");
        }
        else if (imagesRedraw)
        {
            if (imageProtocol == ImageProtocol.ITerm2) buffer.Append("\u001b[2J");
            else if (imageProtocol == ImageProtocol.Kitty) buffer.Append(TerminalImage.DeleteAllKittyPlacements());
        }
        buffer.Append(prepared.Item2);
        bool Skip(int row) => !fullRedraw && !imagesRedraw && string.Equals(Line(screen, row), Previous(row), StringComparison.Ordinal);
        if (redrawImages && imageProtocol == ImageProtocol.Kitty && screen.Any(TerminalImage.IsImageLine) && isWezTerm)
        {
            for (var row = 0; row < height; row++) if (!Skip(row)) buffer.Append($"\u001b[{row + 1};1H\u001b[2K");
            for (var row = 0; row < height; row++) if (!Skip(row) && !TerminalImage.IsImageLine(Line(prepared.Item1, row))) buffer.Append($"\u001b[{row + 1};1H").Append(Line(prepared.Item1, row));
            for (var row = 0; row < height; row++) if (!Skip(row) && TerminalImage.IsImageLine(Line(prepared.Item1, row))) buffer.Append($"\u001b[{row + 1};1H").Append(Line(prepared.Item1, row));
        }
        else
            for (var row = 0; row < height; row++) if (!Skip(row)) buffer.Append($"\u001b[{row + 1};1H\u001b[2K").Append(Line(prepared.Item1, row));
        if (cursorPos is { } cursor)
            buffer.Append($"\u001b[{cursor.Row + 1};{Math.Min(width, cursor.Col) + 1}H").Append(ShowHardwareCursor ? "\u001b[?25h" : "\u001b[?25l");
        else buffer.Append("\u001b[?25l");
        buffer.Append(EndSync);
        Terminal.Write(buffer.ToString());
        previousScreen = screen; previousScreenWidth = width; previousScreenHeight = height; currentLayout = next;
    }
}
