// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/custom-editor.ts.
using PiSharp.Tui.Pi;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>Source CustomEditorOptions: EditorOptions plus <see cref="EmbedWorkingStatus"/> (render working, compaction,
/// summarization, and retry status in the editor's top border).</summary>
internal sealed record CustomEditorOptions(int PaddingX = 0, int AutocompleteMaxVisible = 5, bool EmbedWorkingStatus = false);

/// <summary>Custom editor that handles app-level keybindings for coding-agent.</summary>
internal sealed class CustomEditor : Editor
{
    private readonly KeybindingsManager keybindings;
    private StatusIndicator? workingStatusIndicator;
    public bool EmbedWorkingStatus { get; }
    /// <summary>App action handlers by keybinding id, in registration order.</summary>
    public OrderedDictionary<string, Action> ActionHandlers { get; } = new(StringComparer.Ordinal);

    // Special handlers that can be dynamically replaced
    public Action? OnEscape { get; set; }
    public Action? OnCtrlD { get; set; }
    public Action? OnPasteImage { get; set; }
    /// <summary>Handler for extension-registered shortcuts. Returns true if handled.</summary>
    public Func<string, bool>? OnExtensionShortcut { get; set; }

    /// <param name="keybindings">The app keybindings manager (with the app.* definitions).</param>
    public CustomEditor(ITui tui, EditorTheme theme, KeybindingsManager keybindings, CustomEditorOptions? options = null)
        : base(tui, theme, new EditorOptions(options?.PaddingX ?? 0, options?.AutocompleteMaxVisible ?? 5))
    {
        this.keybindings = keybindings;
        EmbedWorkingStatus = options?.EmbedWorkingStatus ?? false;
    }

    public void SetWorkingStatusIndicator(StatusIndicator? indicator) => workingStatusIndicator = indicator;

    private string Border(string text) => (BorderColor ?? (s => s))(text);

    protected override string RenderTopBorder(int width, int hiddenLineCount)
    {
        if (!EmbedWorkingStatus || workingStatusIndicator is null || width <= 0) return base.RenderTopBorder(width, hiddenLineCount);

        var status = workingStatusIndicator.RenderInBorder(Math.Max(1, width - 5));
        var statusWidth = TextUtils.VisibleWidth(status);
        if (statusWidth == 0) return base.RenderTopBorder(width, hiddenLineCount);

        var overflowLabel = hiddenLineCount > 0 ? $" ↑ {hiddenLineCount} more " : null;
        var overflowLabelWidth = overflowLabel is not null ? TextUtils.VisibleWidth(overflowLabel) : 0;
        var overflowStart = (int)Math.Floor((width - overflowLabelWidth) / 2.0);
        bool CanFitOverflow() =>
            overflowLabel is not null && overflowLabelWidth + 2 <= width && overflowStart - (3 + statusWidth + 1) >= 1;

        if (overflowLabel is not null && !CanFitOverflow())
        {
            status = workingStatusIndicator.RenderSpinnerInBorder(width);
            statusWidth = TextUtils.VisibleWidth(status);
        }

        if (CanFitOverflow())
        {
            var leftBlockWidth = 3 + statusWidth + 1;
            return Border("── ") + status +
                Border($" {TextUtils.Repeat("─", overflowStart - leftBlockWidth)}{overflowLabel}{TextUtils.Repeat("─", width - overflowStart - overflowLabelWidth)}");
        }

        if (width >= statusWidth + 5) return Border("── ") + status + Border($" {TextUtils.Repeat("─", width - statusWidth - 4)}");

        status = workingStatusIndicator.RenderSpinnerInBorder(width);
        statusWidth = TextUtils.VisibleWidth(status);
        var prefixWidth = Math.Min(3, Math.Max(0, width - statusWidth));
        return Border(TextUtils.Repeat("─", prefixWidth)) + status + Border(TextUtils.Repeat("─", Math.Max(0, width - prefixWidth - statusWidth)));
    }

    /// <summary>Register a handler for an app action.</summary>
    public void OnAction(string action, Action handler) => ActionHandlers[action] = handler;

    public override void HandleInput(string data)
    {
        // Check extension-registered shortcuts first
        if (OnExtensionShortcut?.Invoke(data) == true) return;

        // Check for clipboard paste keybinding
        if (keybindings.Matches(data, "app.clipboard.pasteImage"))
        {
            OnPasteImage?.Invoke();
            return;
        }

        // Check app keybindings first

        // Escape/interrupt - only if autocomplete is NOT active
        if (keybindings.Matches(data, "app.interrupt"))
        {
            if (!IsShowingAutocomplete())
            {
                // Use dynamic onEscape if set, otherwise registered handler
                var handler = OnEscape ?? ActionHandlers.GetValueOrDefault("app.interrupt");
                if (handler is not null)
                {
                    handler();
                    return;
                }
            }
            // Let parent handle escape for autocomplete cancellation
            base.HandleInput(data);
            return;
        }

        // Exit (Ctrl+D) - only when editor is empty
        if (keybindings.Matches(data, "app.exit"))
        {
            if (GetText().Length == 0)
            {
                var handler = OnCtrlD ?? ActionHandlers.GetValueOrDefault("app.exit");
                handler?.Invoke();
                return;
            }
            // Fall through to editor handling for delete-char-forward when not empty
        }

        // Explicit history bindings take precedence over app actions while the editor is focused.
        // This lets users bind Ctrl+P even though it cycles models by default.
        if (keybindings.Matches(data, "tui.editor.historyPrevious") || keybindings.Matches(data, "tui.editor.historyNext"))
        {
            base.HandleInput(data);
            return;
        }

        // Check all other app actions
        foreach (var (action, handler) in ActionHandlers.ToList())
        {
            if (action != "app.interrupt" && action != "app.exit" && keybindings.Matches(data, action))
            {
                handler();
                return;
            }
        }

        // Pass to parent for editor handling
        base.HandleInput(data);
    }
}
