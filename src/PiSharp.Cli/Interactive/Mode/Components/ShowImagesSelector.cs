// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/show-images-selector.ts.
using PiSharp.Tui.Pi;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>Component that renders a show images selector with borders.</summary>
internal sealed class ShowImagesSelectorComponent : Container
{
    private static readonly SelectListLayoutOptions ShowImagesSelectListLayout = new(MinPrimaryColumnWidth: 12, MaxPrimaryColumnWidth: 32);

    private readonly SelectList selectList;

    public ShowImagesSelectorComponent(bool currentValue, Action<bool> onSelect, Action onCancel)
    {
        SelectItem[] items =
        [
            new("yes", "Yes", "Show images inline in terminal"),
            new("no", "No", "Show text placeholder instead"),
        ];

        // Add top border
        AddChild(new DynamicBorder());

        // Create selector
        selectList = new SelectList(items, 5, Themes.GetSelectListTheme(), ShowImagesSelectListLayout);

        // Preselect current value
        selectList.SetSelectedIndex(currentValue ? 0 : 1);

        selectList.OnSelect = item => onSelect(item.Value == "yes");
        selectList.OnCancel = () => onCancel();

        AddChild(selectList);

        // Add bottom border
        AddChild(new DynamicBorder());
    }

    public SelectList GetSelectList() => selectList;
}
