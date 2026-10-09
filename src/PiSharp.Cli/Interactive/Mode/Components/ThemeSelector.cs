// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/theme-selector.ts.
using PiSharp.Tui.Pi;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>Component that renders a theme selector.</summary>
internal sealed class ThemeSelectorComponent : Container
{
    private static readonly SelectListLayoutOptions ThemeSelectListLayout = new(MinPrimaryColumnWidth: 12, MaxPrimaryColumnWidth: 32);

    private readonly SelectList selectList;
    private readonly Action<string> onPreview;

    public ThemeSelectorComponent(string currentTheme, Action<string> onSelect, Action onCancel, Action<string> onPreview)
    {
        this.onPreview = onPreview;

        // Get available themes and create select items
        var themes = Themes.GetAvailableThemes();
        var themeItems = themes.Select(name => new SelectItem(name, name, name == currentTheme ? "(current)" : null)).ToList();

        // Add top border
        AddChild(new DynamicBorder());

        // Create selector
        selectList = new SelectList(themeItems, 10, Themes.GetSelectListTheme(), ThemeSelectListLayout);

        // Preselect current theme
        var currentIndex = themes.ToList().IndexOf(currentTheme);
        if (currentIndex != -1) selectList.SetSelectedIndex(currentIndex);

        selectList.OnSelect = item => onSelect(item.Value);
        selectList.OnCancel = () => onCancel();
        selectList.OnSelectionChange = item => this.onPreview(item.Value);

        AddChild(selectList);

        // Add bottom border
        AddChild(new DynamicBorder());
    }

    public SelectList GetSelectList() => selectList;
}
