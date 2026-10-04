using System.Collections.Immutable;
using System.Text;
using PiSharp.Tui.Input;
using PiSharp.Tui.Rendering;

namespace PiSharp.Tui.Components.SelectList;

public sealed record TerminalSelectListRenderedFrame(TerminalFrame Frame, int ComponentRowOffset,
    TerminalSelectListRange SourceVisibleRange)
{
    /// <summary>Translate frame-local Y through the short-window crop. The host owns origin/capture.</summary>
    public TerminalSelectListPointer? ToComponentPointer(TerminalSelectListPointer pointer)
    {
        ArgumentNullException.ThrowIfNull(pointer);
        if (pointer.Y < 0 || pointer.Y >= Frame.Rows.Length) return null;
        return pointer with { Y = checked(pointer.Y + ComponentRowOffset) };
    }
}

/// <summary>Creates inert, bounded rows and private selection styling without borrowing or owning terminal I/O.</summary>
public static class TerminalSelectListFrameFactory
{
    public static TerminalSelectListRenderedFrame Create(TerminalSelectList component, int viewportRows, int columns,
        bool focused = true, TerminalRenderLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(component);
        var bounds = limits ?? new(); bounds.Validate(); bounds.ValidateViewport(viewportRows, columns);
        var source = component.RenderForFrame(columns);
        long inputCharacters = 0;
        foreach (var text in source.Rows)
        {
            inputCharacters += text.Length;
            if (inputCharacters > bounds.MaximumInputCharacters)
                throw new TerminalRenderException(TerminalRenderFailure.ResourceLimit);
        }
        var count = Math.Min(viewportRows, source.Rows.Length);
        var offset = source.SelectedRow >= count ? source.SelectedRow - count + 1 : 0;
        var rows = ImmutableArray.CreateBuilder<TerminalFrameRow>(count); var clipped = count < source.Rows.Length;
        var characters = 0; var largest = 0; TerminalInverseSpan? selection = null;
        for (var row = offset; row < offset + count; row++)
        {
            var text = source.Rows[row]; var accepted = new StringBuilder(); var width = 0;
            if (text.Length > bounds.MaximumInputCharacters) throw new TerminalRenderException(TerminalRenderFailure.ResourceLimit);
            foreach (var segment in TerminalSourceGraphemeSegmenter.Elements(text))
            {
                if (segment.Length > bounds.MaximumGraphemeCharacters) throw new TerminalRenderException(TerminalRenderFailure.ResourceLimit);
                var cells = TerminalSelectListText.Width(segment);
                if (cells > columns - width) { clipped = true; break; }
                if (characters > bounds.MaximumFrameCharacters - segment.Length) throw new TerminalRenderException(TerminalRenderFailure.ResourceLimit);
                accepted.Append(segment); width += cells; characters += segment.Length; largest = Math.Max(largest, segment.Length);
            }
            var safe = accepted.ToString();
            if (focused && row == source.SelectedRow && safe.Length > 0) selection = new(rows.Count, 0, safe.Length);
            rows.Add(new(safe, width));
        }
        var frame = new TerminalFrame(columns, rows.MoveToImmutable(), new(Visible: false),
            "pisharp-select-list-source-safe-v1", clipped, characters, largest, selection,
            positionCursor: false, isSourcePresentation: true);
        if (VtRenderer.FullFrameCharacterCount(frame) > bounds.MaximumFrameCharacters)
            throw new TerminalRenderException(TerminalRenderFailure.ResourceLimit);
        return new(frame, offset, source.Range);
    }
}
