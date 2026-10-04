using System.Collections.Immutable;
using System.Text;
using PiSharp.Tui.Input;

namespace PiSharp.Tui.Rendering;

public sealed record TerminalEditorRenderedFrame(TerminalFrame Frame, int FirstVisibleSourceRow,
    int EditorRowOrigin, int TotalSourceRows)
{
    public required TerminalEditorSourceFrame SourceComponent { get; init; }
    public int ComponentWindowOffset { get; init; }
}

/// <summary>
/// Projects a sealed source map into bounded, inert display text and Tui-owned caret styling.
/// Source wrapping and scroll selection are shared with the component projector. Unsafe source
/// controls must already be inert data from BuildForTerminal; raw unsafe rows reject. Any row
/// overflow is explicitly marked clipped, never rewrapped.
/// </summary>
public static class TerminalEditorFrameFactory
{
    public static TerminalEditorRenderedFrame Create(TerminalEditorVisualMap map, int firstVisibleSourceRow,
        ImmutableArray<string> transcriptRows, bool focused = true, TerminalRenderLimits? limits = null)
    {
        var window = TerminalEditorSourceFrameProjector.SelectWindow(map, firstVisibleSourceRow);
        if (map.RenderedRows.Any(text => text.Any(c => c < 0x20 && c != '\t' || c is >= '\u007f' and <= '\u009f')))
            throw new TerminalEditorLayoutException(TerminalEditorLayoutFailure.InvalidInput);
        var bounds = limits ?? new(); bounds.Validate(); bounds.ValidateViewport(map.ViewportRows, map.ContentColumns);
        if (transcriptRows.IsDefault) throw new ArgumentException("Transcript rows must be initialized.", nameof(transcriptRows));
        if (map.SourceText.Length > bounds.MaximumInputCharacters) throw Error(TerminalRenderFailure.ResourceLimit);
        long inputCharacters = 0;
        if (transcriptRows.Length > bounds.MaximumInputCharacters) throw Error(TerminalRenderFailure.ResourceLimit);
        foreach (var text in transcriptRows)
        {
            ArgumentNullException.ThrowIfNull(text); inputCharacters += text.Length;
            if (inputCharacters > bounds.MaximumInputCharacters) throw Error(TerminalRenderFailure.ResourceLimit);
        }
        if (inputCharacters > bounds.MaximumInputCharacters) throw Error(TerminalRenderFailure.ResourceLimit);

        var componentRows = window.Count + 2; var allocated = Math.Min(map.ViewportRows, componentRows);
        var origin = map.ViewportRows - allocated; var cursorComponentRow = map.CursorRenderedRow - window.First + 1;
        // Source layout.ts focus-preserving crop. This does not inspect a user-supplied marker.
        var componentOffset = focused && cursorComponentRow >= allocated ? cursorComponentRow - allocated + 1 : 0;
        var rows = ImmutableArray.CreateBuilder<TerminalFrameRow>(map.ViewportRows);
        var clipped = componentRows > allocated; var characters = 0; var largestGrapheme = 0;
        TerminalInverseSpan? style = null; var cursor = new TerminalCursor(0, 0, false); var positionCursor = false;

        var transcriptStart = Math.Max(0, transcriptRows.Length - origin);
        var blanks = origin - (transcriptRows.Length - transcriptStart);
        for (var at = 0; at < origin; at++)
        {
            var text = at < blanks ? "" : TerminalTextLayout.EscapeTranscriptRow(transcriptRows[transcriptStart + at - blanks]);
            AddRow(text, null, null);
        }
        for (var at = 0; at < allocated; at++)
        {
            var componentRow = componentOffset + at;
            if (componentRow == 0)
                AddRow(TerminalEditorSourceFrameProjector.Border('\u2191', window.First, map.ContentColumns), null, null);
            else if (componentRow == componentRows - 1)
                AddRow(TerminalEditorSourceFrameProjector.Border('\u2193', map.Rows.Length - window.First - window.Count, map.ContentColumns), null, null);
            else
            {
                var sourceRow = window.First + componentRow - 1; var span = map.Rows[sourceRow];
                // The sealed source spans, rather than public SourceFrame.Rows, authorize presentation.
                var raw = map.RenderedRows[sourceRow];
                int? caretColumn = null;
                string safe; (int Start, int Length)? inverse = null;
                if (sourceRow == map.CursorRenderedRow)
                {
                    var offset = map.SourceCursorUtf16Offset - span.SourceStartUtf16;
                    var before = TerminalTextLayout.EscapeSourceSpan(raw[..offset]); var after = raw[offset..];
                    var length = after.Length == 0 ? 0 : TerminalEditorSourceLayout.Segments(after, map.SourceCursorUtf16Offset, map.AtomicRanges)[0].Length;
                    var selected = after.Length == 0 ? " " : TerminalTextLayout.EscapeSourceSpan(after[..length]);
                    var remaining = TerminalTextLayout.EscapeSourceSpan(after[length..]);
                    safe = before + selected + remaining; inverse = (before.Length, selected.Length);
                    if (focused) caretColumn = TerminalEditorSourceWidth.Visible(before);
                }
                else safe = TerminalTextLayout.EscapeSourceSpan(raw);
                // Benign source padding is unchanged. Inert controls consume their displayed cells;
                // remove only surplus padding before considering a bounded overflow of real content.
                safe += new string(' ', Math.Max(0, map.ContentColumns - TerminalEditorSourceWidth.Visible(safe)));
                AddRow(safe, inverse, caretColumn);
            }
        }
        var frame = new TerminalFrame(map.ContentColumns, rows.MoveToImmutable(), cursor, map.PolicyId,
            clipped, characters, largestGrapheme, style, positionCursor, isSourcePresentation: true);
        if (VtRenderer.FullFrameCharacterCount(frame) > bounds.MaximumFrameCharacters) throw Error(TerminalRenderFailure.ResourceLimit);
        return new(frame, window.First, origin, map.Rows.Length)
        {
            SourceComponent = TerminalEditorSourceFrameProjector.Project(map, firstVisibleSourceRow, focused),
            ComponentWindowOffset = componentOffset
        };

        void AddRow(string text, (int Start, int Length)? inverse, int? caretColumn)
        {
            // Clamp only at whole graphemes. The original map/draft and scroll are never changed.
            var accepted = new StringBuilder(); var width = 0;
            foreach (var value in TerminalSourceGraphemeSegmenter.Elements(text))
            {
                if (value.Length > bounds.MaximumGraphemeCharacters) throw Error(TerminalRenderFailure.ResourceLimit);
                var cells = TerminalEditorSourceWidth.Visible(value);
                if (cells > map.ContentColumns - width) { clipped = true; break; }
                if (characters > bounds.MaximumFrameCharacters - value.Length) throw Error(TerminalRenderFailure.ResourceLimit);
                accepted.Append(value); width += cells; characters += value.Length;
                largestGrapheme = Math.Max(largestGrapheme, value.Length);
            }
            var row = rows.Count; var safe = accepted.ToString(); rows.Add(new(safe, width));
            if (inverse is { } token && token.Start < safe.Length)
            {
                var length = Math.Min(token.Length, safe.Length - token.Start);
                if (length > 0) style = new(row, token.Start, length);
            }
            if (caretColumn is { } column && column >= 0 && column < map.ContentColumns && inverse is { } caret &&
                caret.Start < safe.Length && caret.Start + caret.Length <= safe.Length)
            { cursor = new(row, column, false); positionCursor = true; }
        }
    }

    private static TerminalRenderException Error(TerminalRenderFailure failure) => new(failure);
}
