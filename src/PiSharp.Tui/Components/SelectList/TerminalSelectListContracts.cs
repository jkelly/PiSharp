namespace PiSharp.Tui.Components.SelectList;

/// <summary>Canonical item data. An empty label falls back to Value; whitespace labels do not.</summary>
public sealed record TerminalSelectListItem(string Value, string Label, string? Description = null);

public sealed record TerminalSelectListTruncateContext(string Text, int MaxWidth, int ColumnWidth,
    TerminalSelectListItem Item, bool IsSelected);

public sealed record TerminalSelectListLayoutOptions(int? MinPrimaryColumnWidth = null,
    int? MaxPrimaryColumnWidth = null,
    Func<TerminalSelectListTruncateContext, string>? TruncatePrimary = null);

/// <summary>Source text transforms. ANSI returned here is data, never trusted native frame styling.</summary>
public sealed record TerminalSelectListTheme
{
    public Func<string, string> SelectedPrefix { get; init; } = static text => text;
    public Func<string, string> SelectedText { get; init; } = static text => text;
    public Func<string, string> Description { get; init; } = static text => text;
    public Func<string, string> ScrollInfo { get; init; } = static text => text;
    public Func<string, string> NoMatch { get; init; } = static text => text;
}

/// <summary>Component-specific local pointer adapter; the host owns decoding, capture and focus.</summary>
public enum TerminalSelectListPointerType { Press, Release, Move, Click, Wheel }
public enum TerminalSelectListPointerButton { None, Left, Middle, Right }
public sealed record TerminalSelectListPointer(TerminalSelectListPointerType Type, int X, int Y,
    TerminalSelectListPointerButton Button = TerminalSelectListPointerButton.None, int WheelDelta = 0);
public sealed record TerminalSelectListPointerResult(bool Handled = true, bool? Focus = null, bool? Render = null);

public enum TerminalSelectListInputOutcome { Unhandled, Moved, Confirmed, Cancelled }
public sealed record TerminalSelectListRange(int StartIndex, int EndIndex);

/// <summary>Bounded native profile; JavaScript fractional/NaN dimensions and mutable item aliases are excluded.</summary>
public sealed record TerminalSelectListLimits(int MaximumItems = 4096, int MaximumTextCharacters = 65_536,
    int MaximumVisibleItems = 256, int MaximumColumns = 4096)
{
    internal void Validate()
    {
        if (MaximumItems is < 1 or > 65_536 || MaximumTextCharacters is < 1 or > 1_048_576 ||
            MaximumVisibleItems is < 1 or > 1024 || MaximumColumns is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(TerminalSelectListLimits));
    }
}
