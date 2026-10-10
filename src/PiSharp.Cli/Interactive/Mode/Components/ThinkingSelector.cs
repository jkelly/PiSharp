// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/thinking-selector.ts.
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>Component that renders a thinking level selector with borders. Levels are Pi's ThinkingLevel strings
/// (off, minimal, low, medium, high, xhigh, max).</summary>
internal sealed class ThinkingSelectorComponent : Container, IFocusable, IInputHandler
{
    private static readonly SelectListLayoutOptions ThinkingSelectListLayout = new(MinPrimaryColumnWidth: 12, MaxPrimaryColumnWidth: 32);

    private static readonly Dictionary<string, string> LevelDescriptions = new(StringComparer.Ordinal)
    {
        ["off"] = "No reasoning",
        ["minimal"] = "Very brief reasoning (~1k tokens)",
        ["low"] = "Light reasoning (~2k tokens)",
        ["medium"] = "Moderate reasoning (~8k tokens)",
        ["high"] = "Deep reasoning (~16k tokens)",
        ["xhigh"] = "Extra-high reasoning (~32k tokens)",
        ["max"] = "Maximum reasoning",
    };

    private readonly Input searchInput;
    private SelectList selectList;
    private readonly int selectListChildIndex;
    private readonly List<SelectItem> allItems;
    private readonly Action<string> onSelect;
    private readonly Action onCancel;
    private readonly Action<string>? onSelectAsDefault;
    private bool focused;

    public bool Focused
    {
        get => focused;
        set { focused = value; searchInput.Focused = value; }
    }

    public ThinkingSelectorComponent(
        string currentLevel,
        IReadOnlyList<string> availableLevels,
        Action<string> onSelect,
        Action onCancel,
        Action<string>? onSelectAsDefault = null,
        string? defaultThinkingLevel = null)
    {
        this.onSelect = onSelect;
        this.onCancel = onCancel;
        this.onSelectAsDefault = onSelectAsDefault;

        allItems = availableLevels.Select(level => new SelectItem(
            level,
            $"{(level == currentLevel ? "✓ " : "  ")}{level}",
            level == defaultThinkingLevel ? $"{Describe(level)} · default" : Describe(level))).ToList();

        // Add top border
        AddChild(new DynamicBorder());
        AddChild(new Spacer(1));
        AddChild(new Text("Thinking Level", 0, 0));
        AddChild(new Spacer(1));
        AddChild(new Text($"{KeybindingHints.KeyDisplayText("app.thinking.cycle")} cycles thinking levels in-session", 0, 0));
        AddChild(new Spacer(1));

        searchInput = new Input();
        searchInput.OnSubmit = _ => selectList!.HandleInput("\r");
        AddChild(searchInput);
        AddChild(new Spacer(1));

        // Create selector
        selectList = BuildSelectList(allItems, currentLevel);
        selectListChildIndex = Children.Count;
        AddChild(selectList);
        AddChild(new Spacer(1));
        AddChild(new Text(theme.Fg("dim",
            $"  {KeybindingHints.KeyDisplayText("tui.select.confirm")} to select · {KeybindingHints.KeyDisplayText("app.thinking.save")} to set as default · {KeybindingHints.KeyDisplayText("tui.select.cancel")} to cancel"), 0, 0));

        // Add bottom border
        AddChild(new DynamicBorder());
    }

    /// <summary>LEVEL_DESCRIPTIONS[level]; an unknown level renders as JS's <c>undefined</c> would.</summary>
    private static string Describe(string level) => LevelDescriptions.TryGetValue(level, out var description) ? description : "undefined";

    private SelectList BuildSelectList(List<SelectItem> items, string? preselect)
    {
        var list = new SelectList(items, Math.Max(1, items.Count), Themes.GetSelectListTheme(), ThinkingSelectListLayout);
        var currentIndex = items.FindIndex(item => item.Value == preselect);
        if (currentIndex != -1) list.SetSelectedIndex(currentIndex);
        list.OnSelect = item => onSelect(item.Value);
        list.OnCancel = () => onCancel();
        return list;
    }

    private void ApplyFilter(string query)
    {
        var filtered = query.Length > 0 ? Fuzzy.Filter(allItems, query, item => $"{item.Value} {item.Description ?? ""}") : allItems;
        var selectedValue = selectList.GetSelectedItem()?.Value;
        var newList = BuildSelectList(filtered, selectedValue);
        Children[selectListChildIndex] = newList;
        selectList = newList;
    }

    public void HandleInput(string keyData)
    {
        var kb = KeybindingsManager.Global;
        if (kb.Matches(keyData, "app.thinking.save") && onSelectAsDefault is not null)
        {
            if (selectList.GetSelectedItem() is { } item) onSelectAsDefault(item.Value);
            return;
        }

        var isNav = kb.Matches(keyData, "tui.select.up") || kb.Matches(keyData, "tui.select.down") ||
            kb.Matches(keyData, "tui.select.confirm") || kb.Matches(keyData, "tui.select.cancel");
        if (isNav)
        {
            selectList.HandleInput(keyData);
            return;
        }

        searchInput.HandleInput(keyData);
        ApplyFilter(searchInput.GetValue());
    }

    public SelectList GetSelectList() => selectList;
}
