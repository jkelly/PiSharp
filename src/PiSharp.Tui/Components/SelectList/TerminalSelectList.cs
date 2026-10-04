using System.Collections.Immutable;
using PiSharp.Tui.Input;
using PiSharp.Tui.Rendering;

namespace PiSharp.Tui.Components.SelectList;

/// <summary>
/// Single UI-owner selectable list translated from Pi v0.99.1. It borrows no terminal or async resource.
/// Input uses the caller's original decoded event and current per-owner keybinding configuration.
/// Source text rendering is a comparison surface; use the frame adapter for terminal output.
/// </summary>
public sealed class TerminalSelectList
{
    private readonly ImmutableArray<TerminalSelectListItem> items;
    private ImmutableArray<TerminalSelectListItem> filtered;
    private readonly TerminalKeybindings keybindings;
    private readonly TerminalSelectListLayoutOptions layout;
    private readonly TerminalSelectListTheme theme;
    private readonly TerminalSelectListLimits limits;
    private int selectedIndex, mousePressedIndex = -1;

    public TerminalSelectList(IEnumerable<TerminalSelectListItem> items, int maxVisible,
        TerminalKeybindings keybindings, TerminalSelectListTheme? theme = null,
        TerminalSelectListLayoutOptions? layout = null, TerminalSelectListLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(items); ArgumentNullException.ThrowIfNull(keybindings);
        this.limits = limits ?? new(); this.limits.Validate();
        if (maxVisible < 1 || maxVisible > this.limits.MaximumVisibleItems)
            throw new ArgumentOutOfRangeException(nameof(maxVisible));
        this.keybindings = keybindings; this.theme = theme ?? new(); this.layout = layout ?? new();
        foreach (var transform in new[] { this.theme.SelectedPrefix, this.theme.SelectedText,
            this.theme.Description, this.theme.ScrollInfo, this.theme.NoMatch }) ArgumentNullException.ThrowIfNull(transform);
        foreach (var column in new[] { this.layout.MinPrimaryColumnWidth, this.layout.MaxPrimaryColumnWidth })
            if (column is { } width && (width < -this.limits.MaximumColumns || width > this.limits.MaximumColumns))
                throw new ArgumentOutOfRangeException(nameof(layout));
        var copy = ImmutableArray.CreateBuilder<TerminalSelectListItem>(); long textCharacters = 0;
        foreach (var item in items)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (copy.Count >= this.limits.MaximumItems) throw new TerminalRenderException(TerminalRenderFailure.ResourceLimit);
            foreach (var text in new[] { item.Value, item.Label, item.Description ?? "" })
            {
                TerminalSelectListText.Validate(text, this.limits.MaximumTextCharacters); textCharacters += text.Length;
                if (textCharacters > this.limits.MaximumTextCharacters) throw new TerminalRenderException(TerminalRenderFailure.ResourceLimit);
            }
            copy.Add(item);
        }
        this.items = copy.ToImmutable(); filtered = this.items; MaxVisible = maxVisible;
    }

    public int MaxVisible { get; }
    public int SelectedIndex => selectedIndex;
    public ImmutableArray<TerminalSelectListItem> FilteredItems => filtered;
    public Action<TerminalSelectListItem>? OnSelect { get; set; }
    public Action? OnCancel { get; set; }
    public Action<TerminalSelectListItem>? OnSelectionChange { get; set; }

    public void SetFilter(string filter)
    {
        TerminalSelectListText.Validate(filter, limits.MaximumTextCharacters);
        var prefix = TerminalSelectListText.Lower(filter);
        filtered = items.Where(item => TerminalSelectListText.Lower(item.Value).StartsWith(prefix, StringComparison.Ordinal)).ToImmutableArray();
        selectedIndex = 0; // Source retains the press anchor and emits no selection-change callback here.
    }
    public void SetSelectedIndex(int index) => selectedIndex = Math.Max(0, Math.Min(index, filtered.Length - 1));
    public void Invalidate() { } // There is no component cache; invalidate the host VtRenderer separately on resize.
    public TerminalSelectListItem? GetSelectedItem() => (uint)selectedIndex < (uint)filtered.Length ? filtered[selectedIndex] : null;

    public TerminalSelectListRange GetVisibleRange()
    {
        // Widen arithmetic: empty-list repeated Source up inputs can retain negative indices.
        var start = (int)Math.Max(0L, Math.Min((long)selectedIndex - MaxVisible / 2, (long)filtered.Length - MaxVisible));
        return new(start, Math.Min(start + MaxVisible, filtered.Length));
    }

    public TerminalSelectListInputOutcome HandleInput(TerminalInputEvent input)
    {
        ArgumentNullException.ThrowIfNull(input);
        bool Matches(string action) => keybindings.GetKeys(action).Any(key => TerminalInputDecoder.MatchesKey(input, key));
        return Handle(Matches);
    }
    public TerminalSelectListInputOutcome HandleInput(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw); TerminalSelectListText.Validate(raw, limits.MaximumTextCharacters);
        return Handle(action => keybindings.Matches(raw, action));
    }
    private TerminalSelectListInputOutcome Handle(Func<string, bool> matches)
    {
        if (matches("tui.select.up"))
        {
            selectedIndex = selectedIndex == 0 ? filtered.Length - 1 : checked(selectedIndex - 1);
            NotifySelectionChange(); return TerminalSelectListInputOutcome.Moved;
        }
        if (matches("tui.select.down"))
        {
            selectedIndex = selectedIndex == filtered.Length - 1 ? 0 : checked(selectedIndex + 1);
            NotifySelectionChange(); return TerminalSelectListInputOutcome.Moved;
        }
        if (matches("tui.select.confirm"))
        {
            if (GetSelectedItem() is { } item) OnSelect?.Invoke(item);
            return TerminalSelectListInputOutcome.Confirmed;
        }
        if (matches("tui.select.cancel")) { OnCancel?.Invoke(); return TerminalSelectListInputOutcome.Cancelled; }
        return TerminalSelectListInputOutcome.Unhandled;
    }

    public TerminalSelectListPointerResult? HandleMouse(TerminalSelectListPointer pointer)
    {
        ArgumentNullException.ThrowIfNull(pointer);
        if (filtered.Length == 0) return null;
        if (pointer.Type == TerminalSelectListPointerType.Wheel && pointer.WheelDelta != 0)
        {
            var previous = selectedIndex;
            selectedIndex = (int)Math.Clamp((long)selectedIndex + (pointer.WheelDelta < 0 ? -1 : 1), 0L, filtered.Length - 1L);
            if (selectedIndex != previous) NotifySelectionChange();
            // Source computes render after the callback; a reentrant callback can alter it.
            return new(Render: selectedIndex != previous);
        }
        if (pointer.Button != TerminalSelectListPointerButton.Left ||
            pointer.Type is not (TerminalSelectListPointerType.Press or TerminalSelectListPointerType.Click)) return null;
        var range = GetVisibleRange(); var index = (long)range.StartIndex + pointer.Y;
        if (index < range.StartIndex || index >= range.EndIndex) return null;
        if (pointer.Type == TerminalSelectListPointerType.Press)
        {
            mousePressedIndex = (int)index;
            if (selectedIndex != index) { selectedIndex = (int)index; NotifySelectionChange(); }
            return new(Focus: true);
        }
        var clickedIndex = mousePressedIndex < 0 ? (int)index : mousePressedIndex;
        mousePressedIndex = -1; var changed = selectedIndex != clickedIndex; selectedIndex = clickedIndex;
        if (changed) NotifySelectionChange();
        if (GetSelectedItem() is { } selected) OnSelect?.Invoke(selected);
        return new();
    }
    private void NotifySelectionChange() { if (GetSelectedItem() is { } item) OnSelectionChange?.Invoke(item); }

    /// <summary>Canonical Source-style text, including utility reset bytes; never write these rows to a terminal.</summary>
    public ImmutableArray<string> Render(int columns) => RenderCore(columns, safe: false).Rows;
    internal RenderedRows RenderForFrame(int columns) => RenderCore(columns, safe: true);
    internal sealed record RenderedRows(ImmutableArray<string> Rows, int SelectedRow, TerminalSelectListRange Range);

    private RenderedRows RenderCore(int columns, bool safe)
    {
        if (columns < 1 || columns > limits.MaximumColumns) throw new ArgumentOutOfRangeException(nameof(columns));
        var rows = ImmutableArray.CreateBuilder<string>(); long characters = 0;
        var range = GetVisibleRange(); var selectedRow = -1;
        string Display(string text) => safe ? TerminalSelectListText.Safe(text) : text;
        string Transform(Func<string, string> transform, string text)
        {
            var value = transform(text); TerminalSelectListText.Validate(value, limits.MaximumTextCharacters);
            return Display(value);
        }
        void Add(string text)
        {
            TerminalSelectListText.Validate(text, limits.MaximumTextCharacters); characters += text.Length;
            if (characters > limits.MaximumTextCharacters) throw new TerminalRenderException(TerminalRenderFailure.ResourceLimit);
            rows.Add(text);
        }
        string Truncate(string text, int width) => TerminalSelectListText.Truncate(text, width, sourceReset: !safe);
        string Primary(TerminalSelectListItem item, bool selected, int width, int columnWidth)
        {
            var text = Display(item.Label.Length == 0 ? item.Value : item.Label);
            var value = layout.TruncatePrimary is { } callback ? callback(new(text, width, columnWidth, item, selected)) : Truncate(text, width);
            TerminalSelectListText.Validate(value, limits.MaximumTextCharacters);
            return Truncate(safe && layout.TruncatePrimary is not null ? Display(value) : value, width);
        }
        if (filtered.Length == 0) { Add(Transform(theme.NoMatch, "  No matching commands")); return new(rows.ToImmutable(), -1, range); }
        var rawMin = layout.MinPrimaryColumnWidth ?? layout.MaxPrimaryColumnWidth ?? 32;
        var rawMax = layout.MaxPrimaryColumnWidth ?? layout.MinPrimaryColumnWidth ?? 32;
        var minimum = Math.Max(1, Math.Min(rawMin, rawMax)); var maximum = Math.Max(1, Math.Max(rawMin, rawMax));
        var widest = filtered.Max(item => TerminalSelectListText.Width(Display(item.Label.Length == 0 ? item.Value : item.Label)) + 2);
        var primaryColumn = Math.Clamp(widest, minimum, maximum);
        for (var at = range.StartIndex; at < range.EndIndex; at++)
        {
            if (at >= filtered.Length) continue; // Source skips missing items after a reentrant render callback.
            var item = filtered[at]; var selected = at == selectedIndex;
            var prefix = selected ? "\u2192 " : "  "; var prefixWidth = TerminalSelectListText.Width(prefix);
            var description = string.IsNullOrEmpty(item.Description) ? "" : Display(TerminalSelectListText.SingleLine(item.Description));
            var row = ""; var withDescription = false;
            if (description.Length > 0 && columns > 40)
            {
                var effectiveColumn = Math.Max(1, Math.Min(primaryColumn, columns - prefixWidth - 4));
                var primary = Primary(item, selected, Math.Max(1, effectiveColumn - 2), effectiveColumn);
                var primaryWidth = TerminalSelectListText.Width(primary);
                var spacing = new string(' ', Math.Max(1, effectiveColumn - primaryWidth));
                var remaining = columns - (prefixWidth + primaryWidth + spacing.Length) - 2;
                if (remaining > 10)
                {
                    var desc = Truncate(description, remaining);
                    row = selected ? Transform(theme.SelectedText, prefix + primary + spacing + desc) :
                        prefix + primary + Transform(theme.Description, spacing + desc);
                    withDescription = true;
                }
            }
            if (!withDescription)
            {
                var primary = Primary(item, selected, columns - prefixWidth - 2, columns - prefixWidth - 2);
                row = selected ? Transform(theme.SelectedText, prefix + primary) : prefix + primary;
            }
            if (selected) selectedRow = rows.Count;
            Add(row);
        }
        if (range.StartIndex > 0 || range.EndIndex < filtered.Length)
            Add(Transform(theme.ScrollInfo, Truncate($"  ({(long)selectedIndex + 1}/{filtered.Length})", columns - 2)));
        return new(rows.ToImmutable(), selectedRow, range);
    }
}
