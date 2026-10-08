using System.Collections.Immutable;

namespace PiSharp.Tui.Rendering;

/// <summary>
/// Projects admitted original-component rows using cell widths supplied by the actual original
/// visibleWidth callback. The caller owns source/connection authentication and callback admission.
/// This factory never measures text with a replacement width policy or silently clips rows.
/// </summary>
public static class TerminalCustomComponentFrameFactory
{
    public const string OriginalWidthPolicyId = "pi-original-visible-width-v0.99.1";

    /// <summary>Replace transcript slots with the visible tail of admitted original tool rows.
    /// Existing editor rows, caret coordinates and inverse spans retain their sealed identity.</summary>
    public static TerminalFrame CombineToolRows(TerminalFrame existing, int editorOrigin,
        IReadOnlyList<string> sourceRows, IReadOnlyList<int> sourceCellWidths, TerminalRenderLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(existing);
        limits ??= new(); limits.Validate(); limits.ValidateViewport(existing.Rows.Length, existing.Columns);
        if (editorOrigin < 0 || editorOrigin > existing.Rows.Length) throw new TerminalRenderException(TerminalRenderFailure.InvalidOptions);
        // Validate the full source projection before any viewport cropping or physical effect.
        var projection = Create(sourceRows, sourceCellWidths, Math.Max(1, sourceRows.Count), existing.Columns, limits);
        var rows = existing.Rows.ToBuilder(); var visible = Math.Min(editorOrigin, sourceRows.Count);
        for (var index = 0; index < visible; index++) rows[editorOrigin - visible + index] = projection.Rows[sourceRows.Count - visible + index];
        var characters = 0;
        foreach (var row in rows)
        {
            if (characters > limits.MaximumFrameCharacters - row.Text.Length) throw new TerminalRenderException(TerminalRenderFailure.ResourceLimit);
            characters += row.Text.Length;
        }
        var frame = new TerminalFrame(existing.Columns, rows.ToImmutable(), existing.Cursor,
            existing.WidthPolicyId + "+" + OriginalWidthPolicyId, existing.IsClipped || visible < sourceRows.Count,
            characters, existing.LargestGraphemeCharacters, existing.InverseSpan, existing.PositionCursor, isSourcePresentation: true);
        if (VtRenderer.FullFrameCharacterCount(frame) > limits.MaximumFrameCharacters) throw new TerminalRenderException(TerminalRenderFailure.ResourceLimit);
        return frame;
    }

    public static TerminalFrame Create(IReadOnlyList<string> sourceRows, IReadOnlyList<int> sourceCellWidths,
        int viewportRows, int viewportColumns, TerminalRenderLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(sourceRows);
        ArgumentNullException.ThrowIfNull(sourceCellWidths);
        limits ??= new(); limits.Validate(); limits.ValidateViewport(viewportRows, viewportColumns);
        if (sourceRows.Count != sourceCellWidths.Count || sourceRows.Count > viewportRows)
            throw new TerminalRenderException(TerminalRenderFailure.ResourceLimit);
        var rows = ImmutableArray.CreateBuilder<TerminalFrameRow>(viewportRows);
        var characters = 0;
        for (var index = 0; index < sourceRows.Count; index++)
        {
            var text = sourceRows[index];
            ArgumentNullException.ThrowIfNull(text);
            if (text.Length > limits.MaximumInputCharacters || sourceCellWidths[index] < 0 ||
                sourceCellWidths[index] > viewportColumns)
                throw new TerminalRenderException(TerminalRenderFailure.ResourceLimit);
            ValidateText(text);
            // Contain original SGR state within its row, including when a later diff omits this row.
            var contained = text + "\u001b[0m";
            if (characters > limits.MaximumFrameCharacters - contained.Length)
                throw new TerminalRenderException(TerminalRenderFailure.ResourceLimit);
            characters += contained.Length;
            rows.Add(new(contained, sourceCellWidths[index]));
        }
        while (rows.Count < viewportRows) rows.Add(new("", 0));
        var frame = new TerminalFrame(viewportColumns, rows.MoveToImmutable(), new(0, 0, false),
            OriginalWidthPolicyId, false, characters, 0, positionCursor: true, isSourcePresentation: true);
        if (VtRenderer.FullFrameCharacterCount(frame) > limits.MaximumFrameCharacters)
            throw new TerminalRenderException(TerminalRenderFailure.ResourceLimit);
        return frame;
    }

    private static void ValidateText(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            var value = text[index];
            if (value == '\u001b')
            {
                // Only CSI SGR is admitted. OSC, device reports, cursor moves, newlines and images
                // require a separately owned host effect and are refused before any physical write.
                var start = index;
                if (++index >= text.Length || text[index] != '[')
                    throw new TerminalRenderException(TerminalRenderFailure.InvalidOptions);
                while (++index < text.Length && index - start <= 128 &&
                    (text[index] is >= '0' and <= '9' || text[index] == ';')) { }
                if (index >= text.Length || index - start > 128 || text[index] != 'm')
                    throw new TerminalRenderException(TerminalRenderFailure.InvalidOptions);
                continue;
            }
            if (char.IsControl(value)) throw new TerminalRenderException(TerminalRenderFailure.InvalidOptions);
            if (char.IsHighSurrogate(value))
            {
                if (++index >= text.Length || !char.IsLowSurrogate(text[index]))
                    throw new TerminalRenderException(TerminalRenderFailure.InvalidUnicode);
            }
            else if (char.IsLowSurrogate(value)) throw new TerminalRenderException(TerminalRenderFailure.InvalidUnicode);
        }
    }
}
