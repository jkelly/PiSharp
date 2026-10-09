// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/tui/src/components/settings-list.ts and components/image.ts.
namespace PiSharp.Tui.Pi;

/// <summary>A settings row: cycles through <see cref="Values"/> or opens a <see cref="Submenu"/>.</summary>
public sealed class SettingItem
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public string? Description { get; init; }
    public required string CurrentValue { get; set; }
    public IReadOnlyList<string>? Values { get; init; }
    /// <summary>Builds the submenu: current value and done(selectedValue, navigateTo).</summary>
    public Func<string, Action<string?, string?>, IComponent>? Submenu { get; init; }
}

public sealed record SettingsListTheme(Func<string, bool, string> Label, Func<string, bool, string> Value, Func<string, string> Description, string Cursor, Func<string, string> Hint);

/// <summary>A list of settings with aligned values, optional fuzzy search and submenus.</summary>
public sealed class SettingsList : IComponent, IInputHandler, IMouseHandler
{
    private readonly List<SettingItem> items;
    private List<SettingItem> filtered;
    private readonly SettingsListTheme theme;
    private int selectedIndex;
    private int? mousePressedIndex;
    private readonly int maxVisible;
    private readonly Action<string, string> onChange;
    private readonly Action onCancel;
    private readonly Input? searchInput;
    private readonly bool searchEnabled;
    private IComponent? submenu;
    private int? submenuItemIndex;
    private string? navigateAfterClose;

    public SettingsList(IEnumerable<SettingItem> items, int maxVisible, SettingsListTheme theme, Action<string, string> onChange, Action onCancel, bool enableSearch = false)
    {
        this.items = [.. items]; filtered = this.items; this.maxVisible = maxVisible; this.theme = theme; this.onChange = onChange; this.onCancel = onCancel;
        searchEnabled = enableSearch;
        if (enableSearch) searchInput = new Input();
    }

    public void UpdateValue(string id, string value) { if (items.FirstOrDefault(item => item.Id == id) is { } item) item.CurrentValue = value; }
    public void SelectItem(string id)
    {
        var index = (searchEnabled ? filtered : items).FindIndex(item => item.Id == id);
        if (index != -1) selectedIndex = index;
    }
    public void Invalidate() => submenu?.Invalidate();
    public List<string> Render(int width) => submenu?.Render(width) ?? RenderMain(width);
    private List<SettingItem> DisplayItems => searchEnabled ? filtered : items;

    private List<string> RenderMain(int width)
    {
        var lines = new List<string>();
        if (searchEnabled && searchInput is not null) { lines.AddRange(searchInput.Render(width)); lines.Add(""); }
        if (items.Count == 0)
        {
            lines.Add(theme.Hint("  No settings available"));
            if (searchEnabled) AddHint(lines, width);
            return lines;
        }
        var display = DisplayItems;
        if (display.Count == 0) { lines.Add(TextUtils.TruncateToWidth(theme.Hint("  No matching settings"), width)); AddHint(lines, width); return lines; }
        var (start, end) = VisibleRange(display);
        var maxLabel = Math.Min(36, items.Max(item => TextUtils.VisibleWidth(item.Label)));
        for (var i = start; i < end; i++)
        {
            var item = display[i]; var selected = i == selectedIndex;
            var prefix = selected ? theme.Cursor : "  ";
            var label = theme.Label(item.Label + new string(' ', Math.Max(0, maxLabel - TextUtils.VisibleWidth(item.Label))), selected);
            const string separator = "  ";
            var valueMax = width - (TextUtils.VisibleWidth(prefix) + maxLabel + separator.Length) - 2;
            var value = theme.Value(TextUtils.TruncateToWidth(item.CurrentValue, valueMax, ""), selected);
            lines.Add(TextUtils.TruncateToWidth(prefix + label + separator + value, width));
        }
        if (start > 0 || end < display.Count) lines.Add(theme.Hint(TextUtils.TruncateToWidth($"  ({selectedIndex + 1}/{display.Count})", width - 2, "")));
        if (selectedIndex < display.Count && display[selectedIndex].Description is { Length: > 0 } description)
        {
            lines.Add("");
            foreach (var line in TextUtils.WrapTextWithAnsi(description, width - 4)) lines.Add(theme.Description("  " + line));
        }
        AddHint(lines, width);
        return lines;
    }

    public TuiMouseEventResult? HandleMouse(TuiMouseEvent mouseEvent)
    {
        if (submenu is not null)
        {
            var result = submenu is IMouseHandler handler ? handler.HandleMouse(mouseEvent) : null;
            return result is null ? null : result is TuiMouseDispatchResult dispatch ? dispatch with { Focus = true } : result with { Focus = true };
        }
        if (searchEnabled && searchInput is not null)
        {
            if (mouseEvent.Y == 0) { var result = searchInput.HandleMouse(mouseEvent); return result is null ? null : result with { Focus = true }; }
            if (mouseEvent.Y == 1) return null;
        }
        var display = DisplayItems;
        if (display.Count == 0) return null;
        if (mouseEvent.Type == TuiMouseEventType.Wheel && mouseEvent.WheelDelta is { } delta and not 0)
        {
            var previous = selectedIndex;
            selectedIndex = Math.Max(0, Math.Min(display.Count - 1, selectedIndex + (delta < 0 ? -1 : 1)));
            return new TuiMouseEventResult(Handled: true, Render: selectedIndex != previous);
        }
        if (mouseEvent.Button != TuiMouseButton.Left || mouseEvent.Type is not (TuiMouseEventType.Press or TuiMouseEventType.Click)) return null;
        var (start, end) = VisibleRange(display);
        var itemIndex = start + mouseEvent.Y - (searchEnabled ? 2 : 0);
        if (itemIndex < start || itemIndex >= end) return null;
        if (mouseEvent.Type == TuiMouseEventType.Press) { mousePressedIndex = itemIndex; selectedIndex = itemIndex; return new TuiMouseEventResult(Handled: true, Focus: true); }
        selectedIndex = mousePressedIndex ?? itemIndex; mousePressedIndex = null;
        ActivateItem();
        return new TuiMouseEventResult(Handled: true);
    }

    public void HandleInput(string data)
    {
        if (submenu is not null) { (submenu as IInputHandler)?.HandleInput(data); return; }
        var kb = KeybindingsManager.Global; var display = DisplayItems;
        if (kb.Matches(data, "tui.select.up")) { if (display.Count == 0) return; selectedIndex = selectedIndex == 0 ? display.Count - 1 : selectedIndex - 1; }
        else if (kb.Matches(data, "tui.select.down")) { if (display.Count == 0) return; selectedIndex = selectedIndex == display.Count - 1 ? 0 : selectedIndex + 1; }
        else if (kb.Matches(data, "tui.select.confirm") || data == " " && (!searchEnabled || searchInput?.GetValue().Length == 0)) ActivateItem();
        else if (kb.Matches(data, "tui.select.cancel")) onCancel();
        else if (searchEnabled && searchInput is not null) { searchInput.HandleInput(data); filtered = Fuzzy.Filter(items, searchInput.GetValue(), item => item.Label); selectedIndex = 0; }
    }

    private (int Start, int End) VisibleRange(List<SettingItem> display)
    {
        var start = Math.Max(0, Math.Min(selectedIndex - maxVisible / 2, display.Count - maxVisible));
        return (start, Math.Min(start + maxVisible, display.Count));
    }

    private void ActivateItem()
    {
        var display = DisplayItems;
        if (selectedIndex < 0 || selectedIndex >= display.Count) return;
        var item = display[selectedIndex];
        if (item.Submenu is { } build)
        {
            submenuItemIndex = selectedIndex;
            submenu = build(item.CurrentValue, (selected, navigateTo) =>
            {
                if (selected is not null) { item.CurrentValue = selected; onChange(item.Id, selected); }
                if (!string.IsNullOrEmpty(navigateTo)) navigateAfterClose = navigateTo;
                CloseSubmenu();
            });
        }
        else if (item.Values is { Count: > 0 } values)
        {
            var index = values.ToList().IndexOf(item.CurrentValue);
            var next = values[(index + 1) % values.Count];
            item.CurrentValue = next; onChange(item.Id, next);
        }
    }

    private void CloseSubmenu()
    {
        submenu = null;
        if (navigateAfterClose is { } id) { navigateAfterClose = null; submenuItemIndex = null; SelectItem(id); ActivateItem(); }
        else if (submenuItemIndex is { } index) { selectedIndex = index; submenuItemIndex = null; }
    }

    private void AddHint(List<string> lines, int width)
    {
        lines.Add("");
        lines.Add(TextUtils.TruncateToWidth(theme.Hint(searchEnabled ? "  Type to search · Enter/Space to change · Esc to cancel" : "  Enter/Space to change · Esc to cancel"), width));
    }
}

public sealed record ImageTheme(Func<string, string> FallbackColor);
public sealed record ImageOptions(int? MaxWidthCells = null, int? MaxHeightCells = null, string? Filename = null, int? ImageId = null);

/// <summary>An inline image (Kitty or iTerm2), or a text fallback.</summary>
public sealed class Image : IComponent
{
    /// <summary>Converts base64 image data to base64 PNG (Kitty accepts only PNG), or null.</summary>
    public static Func<string, string, string?>? Transcoder { get; set; }
    private static readonly Dictionary<string, string?> PngCache = new(StringComparer.Ordinal);
    private static readonly LinkedList<string> PngOrder = new();
    private static readonly object CacheGate = new();
    private readonly string base64, mimeType;
    private readonly ImageDimensions dimensions;
    private readonly ImageTheme theme;
    private readonly ImageOptions options;
    private int? imageId;
    private string? pngData;
    private List<string>? cachedLines;
    private int? cachedWidth;

    public Image(string base64, string mimeType, ImageTheme theme, ImageOptions? options = null, ImageDimensions? dimensions = null)
    {
        this.base64 = base64; this.mimeType = mimeType; this.theme = theme; this.options = options ?? new();
        this.dimensions = dimensions ?? TerminalImage.GetImageDimensions(base64, mimeType) ?? new(800, 600);
        imageId = this.options.ImageId;
    }

    private static string? ToPng(string base64, string mimeType)
    {
        if (Transcoder is not { } transcoder) return null;
        lock (CacheGate)
        {
            if (!PngCache.TryGetValue(base64, out var png)) png = transcoder(base64, mimeType);
            PngCache.Remove(base64); PngOrder.Remove(base64);
            PngCache[base64] = png; PngOrder.AddLast(base64);
            if (PngCache.Count > 32 && PngOrder.First is { } oldest) { PngCache.Remove(oldest.Value); PngOrder.RemoveFirst(); }
            return png;
        }
    }

    public int? ImageId => imageId;
    public void Invalidate() { cachedLines = null; cachedWidth = null; }

    public List<string> Render(int width)
    {
        if (cachedLines is not null && cachedWidth == width) return cachedLines;
        var maxWidth = Math.Max(1, Math.Min(width - 2, options.MaxWidthCells ?? 60));
        var cells = TerminalImage.GetCellDimensions();
        var maxHeight = options.MaxHeightCells ?? Math.Max(1, (int)Math.Ceiling((double)maxWidth * cells.WidthPx / cells.HeightPx));
        var caps = TerminalImage.GetCapabilities();
        string? data = base64; var dims = dimensions;
        if (caps.Images == ImageProtocol.Kitty && mimeType != "image/png")
        {
            pngData ??= ToPng(base64, mimeType);
            data = pngData;
            if (data is not null) dims = TerminalImage.GetPngDimensions(data) ?? dims;
        }
        List<string> lines;
        if (caps.Images != ImageProtocol.None && data is not null)
        {
            if (caps.Images == ImageProtocol.Kitty && imageId is null) imageId = TerminalImage.AllocateImageId();
            var result = TerminalImage.RenderImage(data, dims, new(maxWidth, maxHeight, ImageId: imageId, MoveCursor: false));
            if (result is not null)
            {
                if (result.ImageId is { } id) imageId = id;
                if (caps.Images == ImageProtocol.Kitty) { lines = [result.Sequence]; for (var i = 0; i < result.Rows - 1; i++) lines.Add(""); }
                else
                {
                    lines = [];
                    for (var i = 0; i < result.Rows - 1; i++) lines.Add("");
                    lines.Add((result.Rows - 1 > 0 ? $"\u001b[{result.Rows - 1}A" : "") + result.Sequence);
                }
            }
            else lines = [TextUtils.TruncateToWidth(theme.FallbackColor(TerminalImage.ImageFallback(mimeType, dimensions, options.Filename)), width)];
        }
        else lines = [TextUtils.TruncateToWidth(theme.FallbackColor(TerminalImage.ImageFallback(mimeType, dimensions, options.Filename)), width)];
        cachedLines = lines; cachedWidth = width;
        return lines;
    }
}
