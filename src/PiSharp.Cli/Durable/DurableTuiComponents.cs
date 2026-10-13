// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/experimental/durable/tui.ts (SELECT_THEME, ListSelector,
// CompactionComponent). Built from PiSharp.Tui's ports of pi-tui's Input, SelectList, Box, Text, Markdown and Spacer, pi's DynamicBorder
// and theme.
using PiSharp.Cli.Interactive.Mode;
using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Durable;

/// <summary>A filterable list in place of the editor.</summary>
internal sealed class DurableListSelector : Container, IFocusable, IInputHandler
{
    private static readonly string[] Forwarded = ["tui.select.up", "tui.select.down", "tui.select.confirm", "tui.select.cancel"];

    private readonly Input input = new();
    private readonly Container listContainer = new();
    private readonly IReadOnlyList<SelectItem> items;
    private readonly Action<string> onSelect;
    private readonly Action onCancel;
    private SelectList list;
    private bool focused;

    public DurableListSelector(string title, IReadOnlyList<SelectItem> items, Action<string> onSelect, Action onCancel)
    {
        this.items = items;
        this.onSelect = onSelect;
        this.onCancel = onCancel;
        list = Build(items);
        AddChild(new DynamicBorder());
        AddChild(new Spacer(1));
        AddChild(new Text(theme.Fg("accent", theme.Bold(title)), 1, 0));
        AddChild(input);
        AddChild(new Spacer(1));
        AddChild(listContainer);
        AddChild(new DynamicBorder());
    }

    /// <summary>The list theme (upstream <c>SELECT_THEME</c>).</summary>
    internal static SelectListTheme SelectTheme => new(
        text => theme.Fg("accent", text),
        text => theme.Fg("accent", text),
        text => theme.Fg("muted", text),
        text => theme.Fg("dim", text),
        text => theme.Fg("warning", text));

    public bool Focused
    {
        get => focused;
        set
        {
            focused = value;
            input.Focused = value;
        }
    }

    public void HandleInput(string data)
    {
        var keybindings = PiSharp.Tui.Pi.KeybindingsManager.Global;
        if (Forwarded.Any(action => keybindings.Matches(data, action)))
        {
            list.HandleInput(data);
            return;
        }
        input.HandleInput(data);
        var query = input.GetValue();
        var filtered = query.Length == 0 ? items : Fuzzy.Filter(items, query, item => $"{item.Label} {item.Value}");
        list = Build(filtered);
    }

    private SelectList Build(IReadOnlyList<SelectItem> shown)
    {
        var built = new SelectList(shown, 10, SelectTheme)
        {
            OnSelect = item => onSelect(item.Value),
            OnCancel = onCancel,
        };
        listContainer.Clear();
        listContainer.AddChild(built);
        return built;
    }
}

/// <summary>The summary that replaced earlier context: collapsed to one line until expanded.</summary>
internal sealed class DurableCompactionComponent : Box
{
    private readonly string summary;

    public DurableCompactionComponent(string summary, bool expanded)
        : base(1, 1, text => theme.Bg("customMessageBg", text))
    {
        this.summary = summary;
        SetExpanded(expanded);
    }

    public void SetExpanded(bool expanded)
    {
        Clear();
        AddChild(new Text(theme.Fg("customMessageLabel", theme.Bold("[compaction]")), 0, 0));
        AddChild(new Spacer(1));
        AddChild(expanded
            ? new Markdown(summary, 0, 0, Themes.GetMarkdownTheme(), new DefaultTextStyle(Color: text => theme.Fg("customMessageText", text)))
            : new Text(
                theme.Fg("customMessageText", "Earlier context summarized (") +
                    theme.Fg("dim", KeybindingHints.KeyText("app.tools.expand")) +
                    theme.Fg("customMessageText", " to expand)"),
                0,
                0));
    }
}
