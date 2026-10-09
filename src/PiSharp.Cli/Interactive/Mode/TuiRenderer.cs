// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/tui-renderer.ts (createInteractiveTui,
// createInteractiveTuiReference) and coding-agent/src/modes/interactive/chat-viewport.ts (createChatViewport).
using PiSharp.Tui.Pi;

namespace PiSharp.Cli.Interactive.Mode;

internal sealed record InteractiveTuiOptions(string TuiMode, bool ShowHardwareCursor, string LogDirectory, ITerminal Terminal, UiLoop Loop,
    Action? OnRightClickPaste = null, bool FullscreenCopyOnSelect = true, int? FullscreenWheelScrollLines = null,
    Func<string, Task>? CopyToClipboard = null, Action<string>? OpenUrl = null);

internal static class InteractiveTui
{
    /// <summary>createInteractiveTui: the fullscreen renderer with themed search, scroll indicator and clipboard, or the main screen.</summary>
    public static TuiBase Create(InteractiveTuiOptions options)
    {
        if (options.TuiMode == "fullscreen")
        {
            string StyleSearchMatch(string text) => Themes.Current.Bg("searchMatchBg", Themes.Current.Fg("searchMatchText", text));
            return new TuiAltScreen(options.Terminal, options.Loop, options.ShowHardwareCursor, options.LogDirectory, new TuiAltScreenOptions
            {
                SearchMatchStyle = text => Themes.Current.Underline(StyleSearchMatch(text)),
                SearchCurrentMatchStyle = text => Themes.Current.Bold(Themes.Current.Inverse(StyleSearchMatch(text))),
                SearchNavigationButtonStyle = (text, hovered) => hovered ? Themes.Current.Underline(text) : text,
                ScrollToEndIndicator = () =>
                {
                    var shortcut = KeybindingHints.KeyDisplayText("tui.altScreen.bottom");
                    var label = $" ↓ Jump to latest message{(shortcut.Length > 0 ? " · " + shortcut : "")} ";
                    return Themes.Current.Bg("selectedBg", Themes.Current.Fg("text", label));
                },
                OpenUrl = options.OpenUrl,
                OnRightClickPaste = options.OnRightClickPaste,
                CopyOnSelect = options.FullscreenCopyOnSelect,
                WheelScrollLines = options.FullscreenWheelScrollLines,
                CopySelection = async text =>
                {
                    if (options.CopyToClipboard is null) return (false, "Clipboard is unavailable");
                    try { await options.CopyToClipboard(text).ConfigureAwait(false); return (true, null); }
                    catch (Exception error) { return (false, error.Message); }
                }
            });
        }
        return new TuiMainScreen(options.Terminal, options.Loop, options.ShowHardwareCursor, options.LogDirectory);
    }

    /// <summary>createChatViewport: the scrolling transcript over the document and the fixed input dock below it.</summary>
    public static (IComponent Root, ScrollView Transcript) CreateChatViewport(IComponent document, IComponent pendingMessages, IComponent status,
        IComponent? widgetsAbove, IComponent editor, IComponent? widgetsBelow, IComponent footer, ScrollViewScrollbar scrollbar,
        Func<string, string>? trackStyle, Func<string, string>? thumbStyle, UiLoop? loop)
    {
        var transcript = new ScrollView(document, new ScrollViewOptions(ScrollViewFollow.End, Primary: true, Overscroll: ScrollOverscroll.Chain,
            Scrollbar: scrollbar, ScrollbarTrackStyle: trackStyle, ScrollbarThumbStyle: thumbStyle, Loop: loop));
        var dockEntries = new List<StackEntry>
        {
            new(pendingMessages, Shrink: 1, MinSize: 0),
            new(status, Shrink: 1, MinSize: 0)
        };
        if (widgetsAbove is not null) dockEntries.Add(new(widgetsAbove, Shrink: 1, MinSize: 0));
        dockEntries.Add(new(editor, Shrink: 1, MinSize: 3));
        if (widgetsBelow is not null) dockEntries.Add(new(widgetsBelow, Shrink: 1, MinSize: 0));
        dockEntries.Add(new(footer, Shrink: 1, MinSize: 0));
        var dock = new VStack(dockEntries);
        return (new VStack([new(transcript, Basis: 0, Grow: 1, Shrink: 1, MinSize: 1), new(dock, Basis: null, Grow: 0, Shrink: 1, MinSize: 1)]), transcript);
    }
}

/// <summary>createInteractiveTuiReference: a stable TUI for components while the interactive mode replaces the active renderer.</summary>
internal sealed class TuiReference(Func<TuiBase> current) : ITui
{
    public TuiBase Current => current();
    public TuiMode Mode => current().Mode;
    public ITerminal Terminal => current().Terminal;
    public UiLoop Loop => current().Loop;
    public int FullRedraws => current().FullRedraws;
    public List<IComponent> Children => current().Children;
    public void AddChild(IComponent component) => current().AddChild(component);
    public void RemoveChild(IComponent component) => current().RemoveChild(component);
    public void Clear() => current().Clear();
    public bool ShowHardwareCursor { get => current().ShowHardwareCursor; set => current().ShowHardwareCursor = value; }
    public bool ClearOnShrink { get => current().ClearOnShrink; set => current().ClearOnShrink = value; }
    public IComponent? FocusedComponent => current().FocusedComponent;
    public void SetFocus(IComponent? component) => current().SetFocus(component);
    public IOverlayHandle ShowOverlay(IComponent component, OverlayOptions? options = null) => current().ShowOverlay(component, options);
    public void HideOverlay() => current().HideOverlay();
    public bool HasOverlay() => current().HasOverlay();
    public void Start() => current().Start();
    public void Stop(bool preserveScreen = false) => current().Stop(preserveScreen);
    public void RenderNow(bool force = false) => current().RenderNow(force);
    public void RequestRender(bool force = false) => current().RequestRender(force);
    public Action AddInputListener(Func<string, TuiInputListenerResult?> listener) => current().AddInputListener(listener);
    public Action OnTerminalColorSchemeChange(Action<TerminalColorScheme> listener) => current().OnTerminalColorSchemeChange(listener);
    public void SetTerminalColorSchemeNotifications(bool enabled) => current().SetTerminalColorSchemeNotifications(enabled);
    public Task<TerminalColors> QueryTerminalColors(int timeoutMs, Action<TerminalColors>? onLateReply = null) => current().QueryTerminalColors(timeoutMs, onLateReply);
    public Action? OnDebug { get => current().OnDebug; set => current().OnDebug = value; }
    public void Invalidate() => current().Invalidate();
}
