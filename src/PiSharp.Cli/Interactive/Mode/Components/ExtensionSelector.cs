// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/extension-selector.ts.
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>Source ExtensionSelectorOptions. <see cref="SetInterval"/> overrides the countdown's interval scheduler (tests).</summary>
internal sealed record ExtensionSelectorOptions(
    ITui? Tui = null,
    double? Timeout = null,
    Action? OnToggleToolsExpanded = null,
    string? Description = null,
    Func<Action, double, IDisposable>? SetInterval = null);

/// <summary>Generic selector component for extensions. Displays a list of string options with keyboard navigation.</summary>
internal class ExtensionSelectorComponent : Container, IInputHandler
{
    private readonly List<string> options;
    private int selectedIndex;
    private readonly Container listContainer;
    private readonly Action<string> onSelectCallback;
    private readonly Action onCancelCallback;
    private readonly Text titleText;
    private readonly string baseTitle;
    private readonly CountdownTimer? countdown;
    private readonly Action? onToggleToolsExpanded;

    public ExtensionSelectorComponent(string title, IReadOnlyList<string> options, Action<string> onSelect, Action onCancel, ExtensionSelectorOptions? opts = null)
    {
        this.options = [.. options];
        onSelectCallback = onSelect;
        onCancelCallback = onCancel;
        onToggleToolsExpanded = opts?.OnToggleToolsExpanded;
        baseTitle = title;

        AddChild(new DynamicBorder());
        AddChild(new Spacer(1));

        titleText = new Text(theme.Fg("accent", theme.Bold(title)), 1, 0);
        AddChild(titleText);
        if (!string.IsNullOrEmpty(opts?.Description))
        {
            AddChild(new Spacer(1));
            AddChild(new Text(theme.Fg("text", opts.Description), 1, 0));
        }
        AddChild(new Spacer(1));

        if (opts?.Timeout is { } timeout && timeout > 0 && opts.Tui is not null)
        {
            countdown = new CountdownTimer(
                timeout,
                opts.Tui,
                s => titleText.SetText(theme.Fg("accent", theme.Bold($"{baseTitle} ({s}s)"))),
                () => onCancelCallback(),
                opts.SetInterval);
        }

        listContainer = new Container();
        AddChild(listContainer);
        AddChild(new Spacer(1));
        AddChild(new Text(
            KeybindingHints.RawKeyHint("↑↓", "navigate") + "  " +
            KeybindingHints.KeyHint("tui.select.confirm", "select") + "  " +
            KeybindingHints.KeyHint("tui.select.cancel", "cancel"), 1, 0));
        AddChild(new Spacer(1));
        AddChild(new DynamicBorder());

        UpdateList();
    }

    private void UpdateList()
    {
        listContainer.Clear();
        for (var i = 0; i < options.Count; i++)
        {
            var isSelected = i == selectedIndex;
            var text = isSelected ? theme.Fg("accent", "→ ") + theme.Fg("accent", options[i]) : $"  {theme.Fg("text", options[i])}";
            listContainer.AddChild(new Text(text, 1, 0));
        }
    }

    public void HandleInput(string keyData)
    {
        var kb = KeybindingsManager.Global;
        if (kb.Matches(keyData, "app.tools.expand")) onToggleToolsExpanded?.Invoke();
        else if (kb.Matches(keyData, "tui.select.up") || keyData == "k")
        {
            selectedIndex = Math.Max(0, selectedIndex - 1);
            UpdateList();
        }
        else if (kb.Matches(keyData, "tui.select.down") || keyData == "j")
        {
            selectedIndex = Math.Min(options.Count - 1, selectedIndex + 1);
            UpdateList();
        }
        else if (kb.Matches(keyData, "tui.select.confirm") || keyData == "\n")
        {
            // Upstream: `if (selected) onSelect(selected)` (an empty option string is falsy).
            if (selectedIndex >= 0 && selectedIndex < options.Count && options[selectedIndex].Length > 0) onSelectCallback(options[selectedIndex]);
        }
        else if (kb.Matches(keyData, "tui.select.cancel")) onCancelCallback();
    }

    public void Dispose() => countdown?.Dispose();
}
