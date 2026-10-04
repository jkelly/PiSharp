using System.Collections.Immutable;
using System.Text;
using PiSharp.Tui.Components.SelectList;
using PiSharp.Tui.Input;
using PiSharp.Tui.Rendering;

namespace PiSharp.Tui.Components.Text;

// Text child rules translated from MIT-licensed extension-selector.ts at d86654abb8862e201933517d6f1fce9f88dd117f.
// Copyright (c) 2025 Mario Zechner; full notice retained in UPSTREAM-LICENSE.
public sealed record TerminalExtensionSelectorRenderedFrame(TerminalFrame Frame, int HeadingRows,
    TerminalSelectListRange OptionRange, int BodyRowOffset, int? SelectedFrameRow);

/// <summary>
/// Pi ExtensionSelector's title/option Text children, under the existing bounded native selector profile.
/// Canonical option values stay in the list; this factory owns no input, response authority or terminal.
/// </summary>
public static class TerminalExtensionSelectorFrameFactory
{
    public static TerminalExtensionSelectorRenderedFrame Create(string title, TerminalSelectList options,
        int viewportRows, int columns, bool focused = true, TerminalRenderLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var bounds = limits ?? new(); bounds.Validate(); bounds.ValidateViewport(viewportRows, columns);
        var heading = new TerminalText(title, 1, 0).RenderForFrame(columns);
        var range = options.GetVisibleRange();
        var body = new TerminalTextComponentBounds.Rows();
        var selectedStart = -1; var selectedCount = 0; var bodyCount = 0;
        for (var index = range.StartIndex; index < range.EndIndex; index++)
        {
            // Pinned extension-selector.ts updateList uses canonical option text, not SelectList's
            // one-line label/description truncation. Prefixes are display data; no option is rewritten.
            var selected = index == options.SelectedIndex;
            var rows = new TerminalText((selected ? "\u2192 " : "  ") + options.FilteredItems[index].Value, 1, 0)
                .RenderForFrame(columns);
            if (selected) { selectedStart = bodyCount; selectedCount = rows.Length; }
            foreach (var row in rows) body.Add(row);
            bodyCount += rows.Length;
        }
        var bodyRows = body.ToArray();
        // Reserve at least one option row in short windows. Long titles crop explicitly rather than
        // stealing keyboard selection. In a one-row window the selected option owns the viewport.
        var headingCount = Math.Min(heading.Length, Math.Max(0, viewportRows - (bodyRows.Length > 0 ? 1 : 0)));
        var available = viewportRows - headingCount;
        var offset = selectedStart < 0 || available == 0 ? 0 : selectedCount >= available ? selectedStart :
            Math.Max(0, selectedStart + selectedCount - available);
        var rowCount = Math.Min(available, bodyRows.Length - offset);
        var clipped = headingCount < heading.Length || offset > 0 || offset + rowCount < bodyRows.Length ||
            range.StartIndex > 0 || range.EndIndex < options.FilteredItems.Length;
        long inputCharacters = 0;
        foreach (var row in heading.Concat(bodyRows))
        {
            inputCharacters += row.Length;
            if (inputCharacters > bounds.MaximumInputCharacters) throw TerminalTextComponentBounds.Limit();
        }
        var result = ImmutableArray.CreateBuilder<TerminalFrameRow>(viewportRows);
        var characters = 0; var largest = 0; TerminalInverseSpan? style = null; int? selectedFrameRow = null;
        for (var row = 0; row < headingCount; row++) Add(heading[row], false);
        for (var row = offset; row < offset + rowCount; row++) Add(bodyRows[row], row == selectedStart);
        while (result.Count < viewportRows) Add("", false);
        var frame = new TerminalFrame(columns, result.MoveToImmutable(), new(Visible: false),
            "pisharp-extension-selector-text-safe-v1", clipped, characters, largest, style,
            positionCursor: false, isSourcePresentation: true);
        if (VtRenderer.FullFrameCharacterCount(frame) > bounds.MaximumFrameCharacters) throw TerminalTextComponentBounds.Limit();
        return new(frame, headingCount, range, offset, selectedFrameRow);

        void Add(string text, bool selected)
        {
            var accepted = new StringBuilder(); var width = 0;
            foreach (var segment in TerminalSourceGraphemeSegmenter.Elements(text))
            {
                if (segment.Length > bounds.MaximumGraphemeCharacters) throw TerminalTextComponentBounds.Limit();
                var cells = TerminalTextSource.Width(segment);
                if (cells > columns - width) { clipped = true; break; }
                if (characters > bounds.MaximumFrameCharacters - segment.Length) throw TerminalTextComponentBounds.Limit();
                accepted.Append(segment); width += cells; characters += segment.Length;
                largest = Math.Max(largest, segment.Length);
            }
            var safe = accepted.ToString();
            if (selected)
            {
                selectedFrameRow = result.Count;
                if (focused && safe.Length > 0) style = new(result.Count, 0, safe.Length);
            }
            result.Add(new(safe, width));
        }
    }
}
