// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/tui/src/tui.ts.
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PiSharp.Tui.Pi;

public enum TuiMouseEventType { Press, Release, Move, Drag, Click, Wheel }
public enum TuiMouseButton { Left, Middle, Right, None }

/// <summary>A normalized cell-based mouse event; coordinates are zero-based.</summary>
public sealed record TuiMouseEvent(TuiMouseEventType Type, TuiMouseButton Button, int X, int Y, int ScreenX, int ScreenY, int Width, int Height,
    bool Shift = false, bool Alt = false, bool Ctrl = false, int? WheelDelta = null, int? ClickCount = null);

public record TuiMouseEventResult(bool Handled = false, bool Capture = false, bool Focus = false, bool? Render = null);

public sealed record TuiMouseDispatchTarget(IComponent Component, int OriginX, int OriginY, int Width, int Height);
public sealed record TuiMouseDispatchResult(TuiMouseDispatchTarget Target, bool Capture = false, bool Focus = false, bool? Render = null, IComponent? FocusTarget = null)
    : TuiMouseEventResult(true, Capture, Focus, Render);

/// <summary>Everything rendered by the TUI: lines for a width, and invalidation of cached state.</summary>
public interface IComponent
{
    List<string> Render(int width);
    void Invalidate();
}
/// <summary>Keyboard input for the focused component.</summary>
public interface IInputHandler
{
    void HandleInput(string data);
    /// <summary>Receive Kitty key release events (filtered out otherwise).</summary>
    bool WantsKeyRelease => false;
}
public interface IMouseHandler { TuiMouseEventResult? HandleMouse(TuiMouseEvent mouseEvent); }
/// <summary>A component that shows the hardware cursor: when focused it emits <see cref="Tui.CursorMarker"/> at the cursor.</summary>
public interface IFocusable { bool Focused { get; set; } }
public interface IDisposableComponent { void Dispose(); }

public static class Mouse
{
    /// <summary>Dispatches to a component, retaining the exact target and coordinate transform.</summary>
    public static TuiMouseDispatchResult? Dispatch(IComponent component, TuiMouseEvent mouseEvent)
    {
        if (component is not IMouseHandler handler) return null;
        var result = handler.HandleMouse(mouseEvent);
        if (result is null) return null;
        if (result is TuiMouseDispatchResult forwarded)
            return forwarded.Focus && component is IInputHandler ? forwarded with { FocusTarget = component } : forwarded;
        if (!result.Handled && !result.Capture && !result.Focus) return null;
        return new(new(component, mouseEvent.ScreenX - mouseEvent.X, mouseEvent.ScreenY - mouseEvent.Y, mouseEvent.Width, mouseEvent.Height),
            result.Capture, result.Focus, result.Render, result.Focus ? component : null);
    }
    public static TuiMouseEvent Retarget(TuiMouseEvent mouseEvent, TuiMouseDispatchTarget target) => mouseEvent with
    { X = mouseEvent.ScreenX - target.OriginX, Y = mouseEvent.ScreenY - target.OriginY, Width = target.Width, Height = target.Height };
}

/// <summary>A component that stacks its children vertically.</summary>
public class Container : IComponent, IMouseHandler
{
    public List<IComponent> Children { get; protected set; } = [];
    private (int Width, List<(IComponent Component, int Height)> Children)? mouseLayout;
    public virtual void AddChild(IComponent component) => Children.Add(component);
    public virtual void RemoveChild(IComponent component) => Children.Remove(component);
    public virtual void Clear() => Children = [];
    public virtual void Invalidate() { foreach (var child in Children.ToArray()) child.Invalidate(); }
    public virtual TuiMouseEventResult? HandleMouse(TuiMouseEvent mouseEvent)
    {
        if (mouseEvent.Y < 0 || mouseEvent.Y >= mouseEvent.Height) return null;
        var children = mouseLayout is { } layout && layout.Width == mouseEvent.Width ? layout.Children :
            Children.Select(component => (component, component.Render(mouseEvent.Width).Count)).ToList();
        var childY = 0;
        foreach (var (child, height) in children)
        {
            if (mouseEvent.Y >= childY && mouseEvent.Y < childY + height)
            {
                var result = Mouse.Dispatch(child, mouseEvent with { Y = mouseEvent.Y - childY, Height = height });
                if (result is { Focus: true } && this is IInputHandler) return result with { FocusTarget = this };
                return result;
            }
            childY += height;
        }
        return null;
    }
    public virtual List<string> Render(int width)
    {
        var lines = new List<string>(); var children = new List<(IComponent, int)>();
        foreach (var child in Children.ToArray())
        {
            var childLines = child.Render(width);
            children.Add((child, childLines.Count));
            lines.AddRange(childLines);
        }
        mouseLayout = (width, children);
        return lines;
    }
}

public enum OverlayAnchor { Center, TopLeft, TopRight, BottomLeft, BottomRight, TopCenter, BottomCenter, LeftCenter, RightCenter }
/// <summary>An absolute number of cells or a percentage of the reference size (<c>"50%"</c>).</summary>
public readonly record struct SizeValue(double Value, bool Percent)
{
    public static implicit operator SizeValue(int value) => new(value, false);
    public static SizeValue Pct(double percent) => new(percent, true);
    public int? Resolve(int reference) => Percent ? (int)Math.Floor(reference * Value / 100) : (int)Value;
}
public sealed record OverlayMargin(int Top = 0, int Right = 0, int Bottom = 0, int Left = 0)
{
    public static OverlayMargin All(int value) => new(value, value, value, value);
}
public sealed record OverlayOptions
{
    public SizeValue? Width { get; init; }
    public int? MinWidth { get; init; }
    public SizeValue? MaxHeight { get; init; }
    public OverlayAnchor? Anchor { get; init; }
    public int? OffsetX { get; init; }
    public int? OffsetY { get; init; }
    public SizeValue? Row { get; init; }
    public SizeValue? Col { get; init; }
    public OverlayMargin? Margin { get; init; }
    public Func<int, int, bool>? Visible { get; init; }
    public bool NonCapturing { get; init; }
}
public sealed record OverlayBounds(int Row, int Col, int Width, int Height);
public interface IOverlayHandle
{
    void Hide();
    void SetHidden(bool hidden);
    bool IsHidden { get; }
    void Focus();
    /// <summary>Releases focus to the next capturing overlay or the previous target; with <paramref name="hasTarget"/>, to <paramref name="target"/>.</summary>
    void Unfocus(IComponent? target = null, bool hasTarget = false);
    bool IsFocused { get; }
    OverlayBounds? GetBounds();
}

public enum TuiMode { Regular, Fullscreen }
public sealed record TuiInputListenerResult(bool Consume = false, string? Data = null);

/// <summary>The terminal UI a component may talk to.</summary>
public interface ITui
{
    TuiMode Mode { get; }
    ITerminal Terminal { get; }
    UiLoop Loop { get; }
    int FullRedraws { get; }
    List<IComponent> Children { get; }
    void AddChild(IComponent component);
    void RemoveChild(IComponent component);
    void Clear();
    bool ShowHardwareCursor { get; set; }
    bool ClearOnShrink { get; set; }
    IComponent? FocusedComponent { get; }
    void SetFocus(IComponent? component);
    IOverlayHandle ShowOverlay(IComponent component, OverlayOptions? options = null);
    void HideOverlay();
    bool HasOverlay();
    void Start();
    void Stop(bool preserveScreen = false);
    void RenderNow(bool force = false);
    void RequestRender(bool force = false);
    Action AddInputListener(Func<string, TuiInputListenerResult?> listener);
    Action OnTerminalColorSchemeChange(Action<TerminalColorScheme> listener);
    void SetTerminalColorSchemeNotifications(bool enabled);
    Task<TerminalColors> QueryTerminalColors(int timeoutMs, Action<TerminalColors>? onLateReply = null);
    Action? OnDebug { get; set; }
    void Invalidate();
}

/// <summary>A TUI that lays out a single root in a fixed viewport (the fullscreen renderer).</summary>
public interface IViewportTui : ITui { void SetLayoutRoot(IComponent? component); }

/// <summary>Shared state of Pi's TUI renderers: focus, overlays, input routing, render scheduling and terminal queries.</summary>
public abstract partial class TuiBase : Container, ITui
{
    public const string CursorMarker = "\u001b_pi:c\u0007";
    public const string SegmentReset = "\u001b[0m\u001b]8;;\u0007";
    public abstract TuiMode Mode { get; }
    public ITerminal Terminal { get; }
    public UiLoop Loop { get; }
    private IComponent? focusedComponent;
    private readonly List<Func<string, TuiInputListenerResult?>> inputListeners = [];
    public Action? OnDebug { get; set; }
    private bool renderRequested, immediateRenderScheduled;
    private IDisposable? renderTimer;
    private long lastRenderAt;
    private const int MinRenderIntervalMs = 16;
    private bool showHardwareCursor;
    public bool ClearOnShrink { get; set; }
    protected int fullRedrawCount;
    protected bool stopped;
    protected readonly string? LogDirectory;
    private readonly List<PendingColorQuery> pendingColorQueries = [];
    private readonly List<Action<TerminalColorScheme>> schemeListeners = [];
    private bool schemeNotifications;
    private int focusOrderCounter;
    private readonly List<OverlayEntry> overlayStack = [];
    private List<(OverlayEntry Entry, int Row, int Col, int Width, int Height)> renderedOverlayLayouts = [];
    private FocusRestore overlayFocusRestore = FocusRestore.Inactive;
    protected bool HasOverlayEntries => overlayStack.Count > 0;

    private sealed class OverlayEntry
    {
        public required IComponent Component;
        public OverlayOptions? Options;
        public IComponent? PreFocus;
        public bool Hidden;
        public int FocusOrder;
        public OverlayBounds? Bounds;
    }
    private sealed record FocusRestore(string Status, OverlayEntry? Overlay = null, IComponent? BlockedBy = null, bool ResumeRestoreOverlay = true, IComponent? ResumeTarget = null)
    {
        public static FocusRestore Inactive { get; } = new("inactive");
    }
    private sealed class PendingColorQuery
    {
        public RgbColor? Foreground, Background;
        public RgbColor?[] Palette = new RgbColor?[16];
        public HashSet<string> Replied = [];
        public Action<TerminalColors>? Deliver;
        public IDisposable? Timer;
    }

    protected TuiBase(ITerminal terminal, UiLoop loop, bool? showHardwareCursor = null, string? logDirectory = null)
    {
        Terminal = terminal; Loop = loop; LogDirectory = logDirectory;
        if (showHardwareCursor is { } value) this.showHardwareCursor = value;
    }

    protected abstract void DoRender();
    protected virtual void ResetRenderState() { }
    protected virtual void BeforeTerminalStart() { }
    protected virtual void AfterTerminalStart() { }
    protected virtual void BeforeTerminalStop(bool preserveScreen) { }
    protected virtual void AfterTerminalStop(bool preserveScreen) { }
    public int FullRedraws => fullRedrawCount;

    public bool ShowHardwareCursor
    {
        get => showHardwareCursor;
        set
        {
            if (showHardwareCursor == value) return;
            showHardwareCursor = value;
            if (!value) HideTerminalCursor();
            RequestRender();
        }
    }

    public IComponent? FocusedComponent => focusedComponent;
    public void SetFocus(IComponent? component) => SetFocusInternal(component, clearRestore: true);

    private void SetFocusInternal(IComponent? component, bool clearRestore)
    {
        var previous = focusedComponent; var next = component;
        var previousOverlay = previous is null ? null : overlayStack.FirstOrDefault(entry => entry.Component == previous && IsOverlayVisible(entry));
        var nextIsOverlay = next is not null && overlayStack.Any(entry => entry.Component == next);
        var restore = VisibleFocusRestore();
        if (next is not null && !nextIsOverlay)
        {
            if (restore.Status == "blocked" && restore.BlockedBy == previous)
            {
                if (!restore.ResumeRestoreOverlay || !IsMounted(restore.BlockedBy!)) next = ResolveBlocked(restore);
                else overlayFocusRestore = restore with { BlockedBy = next };
            }
            else if (previousOverlay is not null && restore.Status != "inactive" && restore.Overlay == previousOverlay && !IsFocusAncestor(previousOverlay, next))
                overlayFocusRestore = new("blocked", previousOverlay, next, true, null);
        }
        else if (next is null)
        {
            if (restore.Status == "blocked" && restore.BlockedBy == previous) next = ResolveBlocked(restore);
            else if (clearRestore) overlayFocusRestore = FocusRestore.Inactive;
        }
        if (focusedComponent is IFocusable oldFocusable) oldFocusable.Focused = false;
        focusedComponent = next;
        if (next is IFocusable newFocusable) newFocusable.Focused = true;
        var focusedOverlay = next is null ? null : overlayStack.FirstOrDefault(entry => entry.Component == next && IsOverlayVisible(entry));
        if (focusedOverlay is not null) overlayFocusRestore = new("eligible", focusedOverlay);
    }

    private void ClearRestoreFor(OverlayEntry overlay) { if (overlayFocusRestore.Status != "inactive" && overlayFocusRestore.Overlay == overlay) overlayFocusRestore = FocusRestore.Inactive; }
    private IComponent? ResolveBlocked(FocusRestore restore)
    {
        if (restore.ResumeRestoreOverlay) return restore.Overlay!.Component;
        overlayFocusRestore = FocusRestore.Inactive;
        return restore.ResumeTarget;
    }
    private FocusRestore VisibleFocusRestore()
    {
        var restore = overlayFocusRestore;
        if (restore.Status == "inactive") return restore;
        return !overlayStack.Contains(restore.Overlay!) || !IsOverlayVisible(restore.Overlay!) ? FocusRestore.Inactive : restore;
    }
    private bool IsFocusAncestor(OverlayEntry entry, IComponent component)
    {
        var visited = new HashSet<IComponent>(ReferenceEqualityComparer.Instance); var current = entry.PreFocus;
        while (current is not null && visited.Add(current))
        {
            if (current == component) return true;
            current = overlayStack.FirstOrDefault(overlay => overlay.Component == current)?.PreFocus;
        }
        return false;
    }
    private void RetargetPreFocus(OverlayEntry removed)
    {
        foreach (var overlay in overlayStack) if (overlay != removed && overlay.PreFocus == removed.Component) overlay.PreFocus = removed.PreFocus;
    }
    protected virtual IReadOnlyList<IComponent> GetMountedRoots() => Children;
    private bool IsMounted(IComponent component) => GetMountedRoots().Any(child => Contains(child, component));
    protected static bool Contains(IComponent root, IComponent target) => root == target || root is Container container && container.Children.Any(child => Contains(child, target));

    public IOverlayHandle ShowOverlay(IComponent component, OverlayOptions? options = null)
    {
        var entry = new OverlayEntry { Component = component, Options = options, PreFocus = focusedComponent, FocusOrder = ++focusOrderCounter };
        overlayStack.Add(entry);
        if (options?.NonCapturing != true && IsOverlayVisible(entry)) SetFocus(component);
        HideTerminalCursor();
        RequestRender();
        return new OverlayHandle(this, entry);
    }

    private sealed class OverlayHandle(TuiBase tui, OverlayEntry entry) : IOverlayHandle
    {
        public void Hide()
        {
            if (!tui.overlayStack.Contains(entry)) return;
            tui.ClearRestoreFor(entry); tui.RetargetPreFocus(entry); tui.overlayStack.Remove(entry);
            if (tui.focusedComponent == entry.Component) tui.SetFocus(tui.TopmostVisibleOverlay()?.Component ?? entry.PreFocus);
            if (tui.overlayStack.Count == 0) tui.HideTerminalCursor();
            tui.RequestRender();
        }
        public void SetHidden(bool hidden)
        {
            if (entry.Hidden == hidden) return;
            entry.Hidden = hidden;
            if (hidden)
            {
                tui.ClearRestoreFor(entry);
                if (tui.focusedComponent == entry.Component) tui.SetFocus(tui.TopmostVisibleOverlay()?.Component ?? entry.PreFocus);
            }
            else if (entry.Options?.NonCapturing != true && tui.IsOverlayVisible(entry))
            { entry.FocusOrder = ++tui.focusOrderCounter; tui.SetFocus(entry.Component); }
            tui.RequestRender();
        }
        public bool IsHidden => entry.Hidden;
        public void Focus()
        {
            if (!tui.overlayStack.Contains(entry) || !tui.IsOverlayVisible(entry)) return;
            entry.FocusOrder = ++tui.focusOrderCounter; tui.SetFocus(entry.Component); tui.RequestRender();
        }
        public void Unfocus(IComponent? target = null, bool hasTarget = false)
        {
            var isFocused = tui.focusedComponent == entry.Component;
            var restore = tui.overlayFocusRestore;
            var pending = restore.Status != "inactive" && restore.Overlay == entry;
            if (!isFocused && !pending) return;
            if (restore.Status == "blocked" && restore.Overlay == entry && tui.focusedComponent == restore.BlockedBy)
            {
                tui.overlayFocusRestore = hasTarget ? restore with { ResumeRestoreOverlay = false, ResumeTarget = target } : FocusRestore.Inactive;
                tui.RequestRender(); return;
            }
            tui.ClearRestoreFor(entry);
            if (isFocused || hasTarget)
            {
                var top = tui.TopmostVisibleOverlay();
                var fallback = top is not null && top != entry ? top.Component : entry.PreFocus;
                tui.SetFocus(hasTarget ? target : fallback);
            }
            tui.RequestRender();
        }
        public bool IsFocused => tui.focusedComponent == entry.Component;
        public OverlayBounds? GetBounds() => !tui.overlayStack.Contains(entry) || !tui.IsOverlayVisible(entry) ? null : entry.Bounds;
    }

    public void HideOverlay()
    {
        if (overlayStack.Count == 0) return;
        var overlay = overlayStack[^1];
        ClearRestoreFor(overlay); RetargetPreFocus(overlay); overlayStack.RemoveAt(overlayStack.Count - 1);
        if (focusedComponent == overlay.Component) SetFocus(TopmostVisibleOverlay()?.Component ?? overlay.PreFocus);
        if (overlayStack.Count == 0) HideTerminalCursor();
        RequestRender();
    }

    private void HideTerminalCursor() { if (!stopped) Terminal.HideCursor(); }
    public bool HasOverlay() => overlayStack.Any(IsOverlayVisible);
    protected bool IsOverlayFocused() => overlayStack.Any(entry => entry.Component == focusedComponent && IsOverlayVisible(entry));
    protected IComponent ResolveMouseFocusTarget(IComponent component)
    {
        for (var index = overlayStack.Count - 1; index >= 0; index--)
            if (IsOverlayVisible(overlayStack[index]) && Contains(overlayStack[index].Component, component)) return overlayStack[index].Component;
        return component;
    }
    protected (bool Hit, TuiMouseDispatchResult? Result) DispatchMouseToOverlay(TuiMouseEvent mouseEvent)
    {
        for (var index = renderedOverlayLayouts.Count - 1; index >= 0; index--)
        {
            var layout = renderedOverlayLayouts[index];
            if (mouseEvent.ScreenX < layout.Col || mouseEvent.ScreenX >= layout.Col + layout.Width || mouseEvent.ScreenY < layout.Row || mouseEvent.ScreenY >= layout.Row + layout.Height) continue;
            var result = Mouse.Dispatch(layout.Entry.Component, mouseEvent with
            { X = mouseEvent.ScreenX - layout.Col, Y = mouseEvent.ScreenY - layout.Row, Width = layout.Width, Height = layout.Height });
            return (true, result is null ? null : result.Focus ? result with { FocusTarget = layout.Entry.Component } : result);
        }
        return (false, null);
    }
    private bool IsOverlayVisible(OverlayEntry entry) => !entry.Hidden && (entry.Options?.Visible is not { } visible || visible(Terminal.Columns, Terminal.Rows));
    private OverlayEntry? TopmostVisibleOverlay()
    {
        OverlayEntry? top = null;
        foreach (var overlay in overlayStack)
        {
            if (overlay.Options?.NonCapturing == true || !IsOverlayVisible(overlay)) continue;
            if (top is null || overlay.FocusOrder > top.FocusOrder) top = overlay;
        }
        return top;
    }

    public override void Invalidate()
    {
        foreach (var root in GetMountedRoots().ToArray()) root.Invalidate();
        foreach (var overlay in overlayStack.ToArray()) overlay.Component.Invalidate();
    }

    public void Start()
    {
        stopped = false;
        BeforeTerminalStart();
        Terminal.Start(data => HandleTerminalInput(data), () => RequestRender());
        AfterTerminalStart();
        Terminal.HideCursor();
        if (schemeNotifications) Terminal.Write("\u001b[?2031h");
        QueryCellSize();
        RequestRender();
    }

    public Action AddInputListener(Func<string, TuiInputListenerResult?> listener)
    {
        inputListeners.Add(listener);
        return () => inputListeners.Remove(listener);
    }
    public void RemoveInputListener(Func<string, TuiInputListenerResult?> listener) => inputListeners.Remove(listener);
    public Action OnTerminalColorSchemeChange(Action<TerminalColorScheme> listener)
    {
        schemeListeners.Add(listener);
        return () => schemeListeners.Remove(listener);
    }
    public void SetTerminalColorSchemeNotifications(bool enabled)
    {
        if (schemeNotifications == enabled) return;
        schemeNotifications = enabled;
        if (!stopped) Terminal.Write(enabled ? "\u001b[?2031h" : "\u001b[?2031l");
    }
    private void QueryCellSize() { if (TerminalImage.GetCapabilities().Images != ImageProtocol.None) Terminal.Write("\u001b[16t"); }

    public void Stop(bool preserveScreen = false)
    {
        stopped = true;
        CancelRenderTimer();
        if (schemeNotifications) Terminal.Write("\u001b[?2031l");
        BeforeTerminalStop(preserveScreen);
        Terminal.ShowCursor();
        Terminal.Stop();
        AfterTerminalStop(preserveScreen);
    }

    public void RenderNow(bool force = false)
    {
        if (force) ResetRenderState();
        renderRequested = false; CancelRenderTimer();
        lastRenderAt = Stopwatch.GetTimestamp();
        DoRender();
    }

    public void RequestRender(bool force = false)
    {
        if (force) { ResetRenderState(); RequestImmediateRender(); return; }
        if (renderRequested) return;
        renderRequested = true;
        Loop.NextTick(ScheduleRender);
    }

    private void RequestImmediateRender()
    {
        CancelRenderTimer();
        renderRequested = true;
        if (immediateRenderScheduled) return;
        immediateRenderScheduled = true;
        Loop.NextTick(() =>
        {
            immediateRenderScheduled = false;
            if (stopped || !renderRequested) return;
            CancelRenderTimer();
            renderRequested = false;
            lastRenderAt = Stopwatch.GetTimestamp();
            DoRender();
        });
    }

    private void CancelRenderTimer() { renderTimer?.Dispose(); renderTimer = null; }

    private void ScheduleRender()
    {
        if (stopped || renderTimer is not null || !renderRequested) return;
        var elapsed = Stopwatch.GetElapsedTime(lastRenderAt).TotalMilliseconds;
        var delay = Math.Max(0, MinRenderIntervalMs - elapsed);
        renderTimer = Loop.SetTimeout(() =>
        {
            renderTimer = null;
            if (stopped || !renderRequested) return;
            renderRequested = false;
            lastRenderAt = Stopwatch.GetTimestamp();
            DoRender();
            if (renderRequested) ScheduleRender();
        }, lastRenderAt == 0 ? 0 : delay);
    }

    /// <summary>Routes one input sequence: terminal replies first, then input listeners, then the focused component.</summary>
    protected virtual void HandleTerminalInput(string data)
    {
        if (ConsumeColorResponse(data)) return;
        if (ConsumeSchemeReport(data)) return;
        if (inputListeners.Count > 0)
        {
            var current = data;
            foreach (var listener in inputListeners.ToArray())
            {
                var result = listener(current);
                if (result?.Consume == true) return;
                if (result?.Data is { } replaced) current = replaced;
            }
            if (current.Length == 0) return;
            data = current;
        }
        if (ConsumeCellSizeResponse(data)) return;
        if (Keys.Matches(data, "shift+ctrl+d") && OnDebug is { } debug) { debug(); return; }
        DispatchKeyboardInput(data);
    }

    protected void DispatchKeyboardInput(string data)
    {
        var focusedOverlay = overlayStack.FirstOrDefault(overlay => overlay.Component == focusedComponent);
        if (focusedOverlay is not null && !IsOverlayVisible(focusedOverlay))
        {
            var top = TopmostVisibleOverlay();
            if (top is not null) SetFocus(top.Component);
            else SetFocusInternal(focusedOverlay.PreFocus, clearRestore: false);
        }
        if (!overlayStack.Any(overlay => overlay.Component == focusedComponent))
        {
            var restore = VisibleFocusRestore();
            if (restore.Status == "eligible") SetFocus(restore.Overlay!.Component);
            else if (restore.Status == "blocked" && restore.BlockedBy != focusedComponent)
            {
                if (restore.ResumeRestoreOverlay) SetFocus(restore.Overlay!.Component);
                else { overlayFocusRestore = FocusRestore.Inactive; SetFocus(restore.ResumeTarget); }
            }
        }
        if (focusedComponent is IInputHandler handler)
        {
            if (Keys.IsKeyRelease(data) && !handler.WantsKeyRelease) return;
            handler.HandleInput(data);
            RequestImmediateRender();
        }
    }

    [GeneratedRegex(@"^\x1b\[\?[\d;]*c$")] private static partial Regex DeviceAttributes();
    [GeneratedRegex(@"^\x1b\[6;(\d+);(\d+)t$")] private static partial Regex CellSizeReply();

    private bool ConsumeColorResponse(string data)
    {
        if (pendingColorQueries.Count == 0) return false;
        var query = pendingColorQueries[0];
        if (DeviceAttributes().IsMatch(data)) { pendingColorQueries.RemoveAt(0); CompleteColorQuery(query); return true; }
        if (TerminalColorParsing.ParseOscColorResponse(data) is not { } response) return false;
        var key = response.Target.ToString()!;
        if (query.Deliver is null || !query.Replied.Add(key)) return true;
        if (response.Target is "foreground") query.Foreground = response.Rgb;
        else if (response.Target is "background") query.Background = response.Rgb;
        else if (response.Target is int index && index < 16) query.Palette[index] = response.Rgb;
        if (query.Replied.Count == 18) CompleteColorQuery(query);
        return true;
    }
    private static TerminalColors ColorResult(PendingColorQuery query) =>
        new(query.Foreground, query.Background, query.Palette.All(color => color is not null) ? query.Palette.Select(color => color!.Value).ToArray() : null);
    private static void CompleteColorQuery(PendingColorQuery query)
    {
        var deliver = query.Deliver; query.Deliver = null; query.Timer?.Dispose();
        deliver?.Invoke(ColorResult(query));
    }
    private bool ConsumeSchemeReport(string data)
    {
        if (TerminalColorParsing.ParseTerminalColorSchemeReport(data) is not { } scheme) return false;
        foreach (var listener in schemeListeners.ToArray()) listener(scheme);
        return true;
    }
    private bool ConsumeCellSizeResponse(string data)
    {
        var match = CellSizeReply().Match(data);
        if (!match.Success) return false;
        var heightPx = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture); var widthPx = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        if (heightPx <= 0 || widthPx <= 0) return true;
        TerminalImage.SetCellDimensions(new(widthPx, heightPx));
        Invalidate(); RequestRender();
        return true;
    }

    public Task<TerminalColors> QueryTerminalColors(int timeoutMs, Action<TerminalColors>? onLateReply = null)
    {
        var done = new TaskCompletionSource<TerminalColors>(TaskCreationOptions.RunContinuationsAsynchronously);
        var query = new PendingColorQuery();
        query.Deliver = colors => done.TrySetResult(colors);
        query.Timer = Loop.SetTimeout(() => { query.Deliver = onLateReply; done.TrySetResult(ColorResult(query)); }, timeoutMs);
        pendingColorQueries.Add(query);
        Terminal.Write("\u001b]10;?\u0007\u001b]11;?\u0007" + string.Concat(Enumerable.Range(0, 16).Select(index => "\u001b]4;" + index.ToString(CultureInfo.InvariantCulture) + ";?\u0007")) + "\u001b[c");
        return done.Task;
    }

    private (int Width, int Row, int Col, int? MaxHeight) ResolveOverlayLayout(OverlayOptions? options, int overlayHeight, int termWidth, int termHeight)
    {
        var opt = options ?? new OverlayOptions();
        var margin = opt.Margin ?? new OverlayMargin();
        int marginTop = Math.Max(0, margin.Top), marginRight = Math.Max(0, margin.Right), marginBottom = Math.Max(0, margin.Bottom), marginLeft = Math.Max(0, margin.Left);
        var availWidth = Math.Max(1, termWidth - marginLeft - marginRight);
        var availHeight = Math.Max(1, termHeight - marginTop - marginBottom);
        var width = opt.Width?.Resolve(termWidth) ?? Math.Min(80, availWidth);
        if (opt.MinWidth is { } minWidth) width = Math.Max(width, minWidth);
        width = Math.Max(1, Math.Min(width, availWidth));
        var maxHeight = opt.MaxHeight?.Resolve(termHeight);
        if (maxHeight is { } mh) maxHeight = Math.Max(1, Math.Min(mh, availHeight));
        var effectiveHeight = maxHeight is { } limit ? Math.Min(overlayHeight, limit) : overlayHeight;
        int row, col;
        if (opt.Row is { } rowValue)
            row = rowValue.Percent ? marginTop + (int)Math.Floor(Math.Max(0, availHeight - effectiveHeight) * rowValue.Value / 100) : (int)rowValue.Value;
        else row = AnchorRow(opt.Anchor ?? OverlayAnchor.Center, effectiveHeight, availHeight, marginTop);
        if (opt.Col is { } colValue)
            col = colValue.Percent ? marginLeft + (int)Math.Floor(Math.Max(0, availWidth - width) * colValue.Value / 100) : (int)colValue.Value;
        else col = AnchorCol(opt.Anchor ?? OverlayAnchor.Center, width, availWidth, marginLeft);
        if (opt.OffsetY is { } offsetY) row += offsetY;
        if (opt.OffsetX is { } offsetX) col += offsetX;
        row = Math.Max(marginTop, Math.Min(row, termHeight - marginBottom - effectiveHeight));
        col = Math.Max(marginLeft, Math.Min(col, termWidth - marginRight - width));
        return (width, row, col, maxHeight);
    }
    private static int AnchorRow(OverlayAnchor anchor, int height, int availHeight, int marginTop) => anchor switch
    {
        OverlayAnchor.TopLeft or OverlayAnchor.TopCenter or OverlayAnchor.TopRight => marginTop,
        OverlayAnchor.BottomLeft or OverlayAnchor.BottomCenter or OverlayAnchor.BottomRight => marginTop + availHeight - height,
        _ => marginTop + (int)Math.Floor((availHeight - height) / 2.0)
    };
    private static int AnchorCol(OverlayAnchor anchor, int width, int availWidth, int marginLeft) => anchor switch
    {
        OverlayAnchor.TopLeft or OverlayAnchor.LeftCenter or OverlayAnchor.BottomLeft => marginLeft,
        OverlayAnchor.TopRight or OverlayAnchor.RightCenter or OverlayAnchor.BottomRight => marginLeft + availWidth - width,
        _ => marginLeft + (int)Math.Floor((availWidth - width) / 2.0)
    };

    /// <summary>Composites visible overlays (by focus order) onto the rendered lines.</summary>
    protected List<string> CompositeOverlays(List<string> lines, int termWidth, int termHeight)
    {
        if (overlayStack.Count == 0) { renderedOverlayLayouts = []; return lines; }
        var result = new List<string>(lines);
        foreach (var entry in overlayStack) entry.Bounds = null;
        var rendered = new List<(OverlayEntry Entry, List<string> Lines, int Row, int Col, int Width)>();
        var minLines = result.Count;
        foreach (var entry in overlayStack.Where(IsOverlayVisible).OrderBy(entry => entry.FocusOrder).ToList())
        {
            var (width, _, _, maxHeight) = ResolveOverlayLayout(entry.Options, 0, termWidth, termHeight);
            var overlayLines = entry.Component.Render(width);
            if (maxHeight is { } mh && overlayLines.Count > mh) overlayLines = overlayLines.Take(mh).ToList();
            var (_, row, col, _) = ResolveOverlayLayout(entry.Options, overlayLines.Count, termWidth, termHeight);
            entry.Bounds = new(row, col, width, overlayLines.Count);
            rendered.Add((entry, overlayLines, row, col, width));
            minLines = Math.Max(minLines, row + overlayLines.Count);
        }
        renderedOverlayLayouts = rendered.Select(r => (r.Entry, r.Row, r.Col, r.Width, r.Lines.Count)).ToList();
        var workingHeight = Math.Max(Math.Max(result.Count, termHeight), minLines);
        while (result.Count < workingHeight) result.Add("");
        var viewportStart = Math.Max(0, workingHeight - termHeight);
        foreach (var (_, overlayLines, row, col, w) in rendered)
            for (var i = 0; i < overlayLines.Count; i++)
            {
                var index = viewportStart + row + i;
                if (index < 0 || index >= result.Count) continue;
                var line = TextUtils.VisibleWidth(overlayLines[i]) > w ? TextUtils.SliceByColumn(overlayLines[i], 0, w, true) : overlayLines[i];
                result[index] = CompositeLine(result[index], line, col, w, termWidth);
            }
        return result;
    }

    protected static List<string> ApplyLineResets(List<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
            if (!TerminalImage.IsImageLine(lines[i])) lines[i] = TextUtils.NormalizeTerminalOutput(lines[i]) + SegmentReset;
        return lines;
    }

    /// <summary>Composites overlay content into a line at a fixed column.</summary>
    public static string CompositeLine(string baseLine, string overlayLine, int startCol, int overlayWidth, int totalWidth)
    {
        if (TerminalImage.IsImageLine(baseLine)) return baseLine;
        var afterStart = startCol + overlayWidth;
        var segments = TextUtils.ExtractSegments(baseLine, startCol, afterStart, totalWidth - afterStart, true);
        var overlay = TextUtils.SliceWithWidth(overlayLine, 0, overlayWidth, true);
        var beforePad = Math.Max(0, startCol - segments.BeforeWidth);
        var overlayPad = Math.Max(0, overlayWidth - overlay.Width);
        var actualBefore = Math.Max(startCol, segments.BeforeWidth);
        var actualOverlay = Math.Max(overlayWidth, overlay.Width);
        var afterTarget = Math.Max(0, totalWidth - actualBefore - actualOverlay);
        var afterPad = Math.Max(0, afterTarget - segments.AfterWidth);
        var result = segments.Before + new string(' ', beforePad) + SegmentReset + overlay.Text + new string(' ', overlayPad) + SegmentReset + segments.After + new string(' ', afterPad);
        return TextUtils.VisibleWidth(result) <= totalWidth ? result : TextUtils.SliceByColumn(result, 0, totalWidth, true);
    }

    /// <summary>Finds and strips the cursor marker in the visible viewport (bottom <paramref name="height"/> lines).</summary>
    protected static (int Row, int Col)? ExtractCursorPosition(List<string> lines, int height)
    {
        var top = Math.Max(0, lines.Count - height);
        for (var row = lines.Count - 1; row >= top; row--)
        {
            var line = lines[row]; var index = line.IndexOf(CursorMarker, StringComparison.Ordinal);
            if (index == -1) continue;
            var col = TextUtils.VisibleWidth(line[..index]);
            lines[row] = line[..index] + line[(index + CursorMarker.Length)..];
            return (row, col);
        }
        return null;
    }
}
