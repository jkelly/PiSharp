using PiSharp.Tui.Components.SelectList;
using PiSharp.Tui.Rendering;

namespace PiSharp.Tui.Components.Text;

// Fixed native resource profile, deliberately separate from Pi's unbounded JavaScript API.
internal static class TerminalTextComponentBounds
{
    internal const int MaximumTextCharacters = 65_536, MaximumColumns = 512,
        MaximumPadding = 256, MaximumRows = 4096, MaximumRenderedCharacters = 1_048_576;

    internal static void ValidateText(string value) => TerminalSelectListText.Validate(value, MaximumTextCharacters);
    internal static void ValidateWidth(int width)
    {
        if (width is < 1 or > MaximumColumns) throw Limit();
    }
    internal static void ValidatePadding(int paddingX, int paddingY)
    {
        if (paddingX is < 0 or > MaximumPadding || paddingY is < 0 or > MaximumPadding)
            throw new ArgumentOutOfRangeException(paddingX is < 0 or > MaximumPadding ? nameof(paddingX) : nameof(paddingY));
    }
    internal static TerminalRenderException Limit() => new(TerminalRenderFailure.ResourceLimit);

    internal sealed class Rows
    {
        private readonly List<string> rows = [];
        private int characters;
        internal void Add(string row)
        {
            TerminalSelectListText.Validate(row, MaximumRenderedCharacters);
            if (rows.Count >= MaximumRows || characters > MaximumRenderedCharacters - row.Length) throw Limit();
            characters += row.Length; rows.Add(row);
        }
        internal string[] ToArray() => rows.ToArray();
    }
}
