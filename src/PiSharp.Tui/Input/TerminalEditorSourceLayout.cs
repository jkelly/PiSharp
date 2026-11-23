using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace PiSharp.Tui.Input;

// This implementation is appended to the owned shared-map source. No caller/view state lives here.
internal static class TerminalEditorSourceLayout
{
    internal readonly record struct Segment(int Start, int Length, bool Atomic);
    internal readonly record struct Chunk(int Start, int Length);
    internal static TerminalEditorVisualMap Build(TerminalEditorLayoutInput input, TerminalEditorGeometrySnapshot geometry, string? displayText = null)
    {
        var text = displayText ?? input.Snapshot.Text; var cursor = input.Snapshot.CursorUtf16Offset;
        var columns = Math.Max(1, geometry.ContentColumns - 1);
        var rows = ImmutableArray.CreateBuilder<TerminalEditorVisualRow>(); var rendered = ImmutableArray.CreateBuilder<string>();
        var cursorRow = -1; var cursorColumn = 0;
        for (var logicalStart = 0; logicalStart <= text.Length;)
        {
            var end = text.IndexOf('\n', logicalStart); if (end < 0) end = text.Length;
            var line = text[logicalStart..end]; var segments = Segments(line, logicalStart, input.AtomicRanges);
            var chunks = Wrap(line, columns, segments);
            for (var at = 0; at < chunks.Count; at++)
            {
                var chunk = chunks[at]; var start = logicalStart + chunk.Start; var rowText = line.Substring(chunk.Start, chunk.Length);
                var rowIndex = rows.Count; rows.Add(new(logicalStart, start, chunk.Length, rowIndex)); rendered.Add(rowText);
                if (cursor >= start && (cursor < start + chunk.Length || at == chunks.Count - 1 && cursor == end))
                { cursorRow = rowIndex; cursorColumn = TerminalEditorSourceWidth.Visible(rowText[..(cursor - start)]); }
            }
            if (end == text.Length) break; logicalStart = end + 1;
        }
        if (cursorRow < 0 || rows.Count > TerminalEditorVisualMapBuilder.MaximumSourceRows ||
            rendered.Count > TerminalEditorVisualMapBuilder.MaximumRenderedRows || rendered.Sum(s => (long)s.Length) > TerminalEditorVisualMapBuilder.MaximumRenderedCharacters)
            throw new TerminalEditorLayoutException(TerminalEditorLayoutFailure.ResourceLimit);
        var map = new TerminalEditorVisualMap(input.Snapshot.Text, cursor, geometry.ContentColumns, geometry.ViewportRows, input.Identity, geometry.Identity,
            input.AtomicRanges, rows.ToImmutable(), rendered.ToImmutable(), cursorRow, cursorColumn)
            { ScrollResetRevision = input.ScrollResetRevision, DisplayText = text };
        map.SealConstruction(); return map;
    }

    internal static List<Segment> Segments(string line, int logicalStart, ImmutableArray<TerminalEditorAtomicRange> ranges)
    {
        var offsets = TerminalSourceGraphemeSegmenter.GetOffsets(line); var result = new List<Segment>(); var at = 0;
        var lineRanges = ranges.Where(r => r.SourceStartUtf16 >= logicalStart &&
            (long)r.SourceStartUtf16 + r.LengthUtf16 <= logicalStart + line.Length).ToArray();
        var spanIndex = 0;
        while (at < offsets.Length)
        {
            var start = offsets[at];
            if (spanIndex < lineRanges.Length && start == lineRanges[spanIndex].SourceStartUtf16 - logicalStart)
            {
                var length = lineRanges[spanIndex++].LengthUtf16; result.Add(new(start, length, true));
                while (at < offsets.Length && offsets[at] < start + length) at++;
            }
            else { var end = at + 1 < offsets.Length ? offsets[at + 1] : line.Length; result.Add(new(start, end - start, false)); at++; }
        }
        return result;
    }

    private static List<Chunk> Wrap(string line, int columns, List<Segment> segments)
    {
        if (line.Length == 0 || TerminalEditorSourceWidth.Visible(line) <= columns) return [new(0, line.Length)];
        var chunks = new List<Chunk>(); var currentWidth = 0; var chunkStart = 0; var wrapAt = -1; var wrapWidth = 0;
        for (var at = 0; at < segments.Count; at++)
        {
            var segment = segments[at]; var value = line.Substring(segment.Start, segment.Length); var width = TerminalEditorSourceWidth.Visible(value);
            var whitespace = !segment.Atomic && TerminalEditorSourceWidth.Whitespace(value);
            if (currentWidth + width > columns)
            {
                if (wrapAt >= 0 && currentWidth - wrapWidth + width <= columns)
                { chunks.Add(new(chunkStart, wrapAt - chunkStart)); chunkStart = wrapAt; currentWidth -= wrapWidth; }
                else if (chunkStart < segment.Start)
                { chunks.Add(new(chunkStart, segment.Start - chunkStart)); chunkStart = segment.Start; currentWidth = 0; }
                wrapAt = -1;
            }
            if (width > columns)
            {
                // Upstream recursively re-wraps merged markers. An indivisible wide grapheme must
                // reject in this bounded experiment rather than recurse forever or truncate it.
                if (!segment.Atomic) throw new TerminalEditorLayoutException(TerminalEditorLayoutFailure.ResourceLimit);
                var parts = Wrap(value, columns, Segments(value, 0, []));
                foreach (var part in parts.Take(parts.Count - 1)) chunks.Add(new(segment.Start + part.Start, part.Length));
                var last = parts[^1]; chunkStart = segment.Start + last.Start;
                currentWidth = TerminalEditorSourceWidth.Visible(value.Substring(last.Start, last.Length)); wrapAt = -1; continue;
            }
            currentWidth += width;
            if (at + 1 < segments.Count)
            {
                var next = segments[at + 1]; var nextText = line.Substring(next.Start, next.Length);
                if (whitespace && (next.Atomic || !TerminalEditorSourceWidth.Whitespace(nextText)) ||
                    !whitespace && !TerminalEditorSourceWidth.Whitespace(nextText) &&
                    (!segment.Atomic && TerminalEditorSourceWidth.Cjk(value) || !next.Atomic && TerminalEditorSourceWidth.Cjk(nextText)))
                { wrapAt = next.Start; wrapWidth = currentWidth; }
            }
        }
        chunks.Add(new(chunkStart, line.Length - chunkStart)); return chunks;
    }
}

/// <summary>Pure source component frame. The host owns incoming/outgoing scroll state and screen placement.</summary>
public sealed record TerminalEditorSourceFrame(ImmutableArray<string> Rows, int FirstVisibleSourceRow,
    int CursorComponentRow, int CursorComponentColumn, int TotalSourceRows);

public static class TerminalEditorSourceFrameProjector
{
    public static TerminalEditorSourceFrame Project(TerminalEditorVisualMap map, int firstVisibleSourceRow, bool focused = true)
    {
        var window = SelectWindow(map, firstVisibleSourceRow);
        var first = window.First; var count = window.Count;
        var output = ImmutableArray.CreateBuilder<string>();
        output.Add(Border('\u2191', first, map.ContentColumns));
        for (var at = first; at < first + count; at++)
        {
            var raw = map.RenderedRows[at]; var width = TerminalEditorSourceWidth.Visible(raw); var displayed = raw;
            if (at == map.CursorRenderedRow)
            {
                var offset = map.SourceCursorUtf16Offset - map.Rows[at].SourceStartUtf16; var before = raw[..offset]; var after = raw[offset..];
                var marker = focused ? "\u001b_pi:c\u0007" : "";
                if (after.Length > 0)
                {
                    // Match source segmentation from the caret suffix, including registered marker atoms.
                    var suffixStart = map.SourceCursorUtf16Offset;
                    var firstSegment = TerminalEditorSourceLayout.Segments(after, suffixStart, map.AtomicRanges)[0];
                    displayed = before + marker + "\u001b[7m" + after[..firstSegment.Length] + "\u001b[0m" + after[firstSegment.Length..];
                }
                else { displayed = before + marker + "\u001b[7m \u001b[0m"; width++; }
            }
            output.Add(displayed + new string(' ', Math.Max(0, map.ContentColumns - width)));
        }
        output.Add(Border('\u2193', map.Rows.Length - first - count, map.ContentColumns));
        if (output.Sum(s => (long)s.Length) > TerminalEditorVisualMapBuilder.MaximumRenderedCharacters)
            throw new TerminalEditorLayoutException(TerminalEditorLayoutFailure.ResourceLimit);
        return new(output.ToImmutable(), first, map.CursorRenderedRow - first + 1, map.CursorRenderedColumn, map.Rows.Length);
    }

    internal readonly record struct Window(int First, int Count, int MaximumVisible);
    internal static Window SelectWindow(TerminalEditorVisualMap map, int firstVisibleSourceRow)
    {
        if (map is null || !map.HasValidConstruction || map.PolicyId != TerminalEditorVisualMapBuilder.SourcePolicyId)
            throw new TerminalEditorLayoutException(TerminalEditorLayoutFailure.InvalidMap);
        if (firstVisibleSourceRow < 0 || firstVisibleSourceRow > TerminalEditorVisualMapBuilder.MaximumSourceRows)
            throw new TerminalEditorLayoutException(TerminalEditorLayoutFailure.InvalidInput);
        var maximumVisible = Math.Max(5, (int)Math.Floor(map.ViewportRows * 0.3)); var first = firstVisibleSourceRow;
        if (map.CursorRenderedRow < first) first = map.CursorRenderedRow;
        else if (map.CursorRenderedRow >= first + maximumVisible) first = map.CursorRenderedRow - maximumVisible + 1;
        first = Math.Clamp(first, 0, Math.Max(0, map.Rows.Length - maximumVisible));
        return new(first, Math.Min(maximumVisible, map.Rows.Length - first), maximumVisible);
    }
    internal static string Border(char direction, int hidden, int columns)
    {
        if (hidden == 0) return new string('\u2500', columns);
        var label = " " + direction + " " + hidden.ToString(CultureInfo.InvariantCulture) + " more ";
        if (label.Length + 2 <= columns)
        { var left = (columns - label.Length) / 2; return new string('\u2500', left) + label + new string('\u2500', columns - label.Length - left); }
        var indicator = "\u2500\u2500\u2500" + label;
        if (indicator.Length <= columns) return indicator + new string('\u2500', columns - indicator.Length);
        var ellipsis = "..."[..Math.Min(3, columns)]; return indicator[..(columns - ellipsis.Length)] + ellipsis;
    }
}
