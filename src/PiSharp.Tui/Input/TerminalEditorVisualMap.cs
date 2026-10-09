using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace PiSharp.Tui.Input;

public readonly record struct TerminalEditorAtomicRange(int SourceStartUtf16, int LengthUtf16);
public readonly record struct TerminalEditorVisualRow(int LogicalLineStartUtf16, int SourceStartUtf16, int LengthUtf16, int RenderedRowIndex);
public readonly record struct TerminalEditorLayoutIdentity(Guid EditorLifetimeId, long EditorRevision);
public readonly record struct TerminalEditorGeometryIdentity(Guid ViewLifetimeId, long GeometryRevision, string PolicyId);
public sealed record TerminalEditorLayoutInput(TerminalTextEditorSnapshot Snapshot, ImmutableArray<TerminalEditorAtomicRange> AtomicRanges, TerminalEditorLayoutIdentity Identity)
{ public long ScrollResetRevision { get; init; } }
public readonly record struct TerminalEditorGeometrySnapshot(TerminalEditorGeometryIdentity Identity, int ContentColumns, int ViewportRows);
public enum TerminalEditorLayoutFailure { InvalidInput, InvalidMap, StaleEditor, StaleGeometry, ResourceLimit }
public sealed class TerminalEditorLayoutException(TerminalEditorLayoutFailure failure) : Exception(failure switch
{
    TerminalEditorLayoutFailure.StaleEditor => "Terminal layout belongs to a stale editor context.",
    TerminalEditorLayoutFailure.StaleGeometry => "Terminal layout belongs to a stale view geometry context.",
    TerminalEditorLayoutFailure.ResourceLimit => "Terminal layout exceeds its declared bounds.",
    TerminalEditorLayoutFailure.InvalidMap => "Terminal layout was not produced by the shared immutable builder.",
    _ => "Terminal layout input or geometry is invalid."
}) { public TerminalEditorLayoutFailure Failure { get; } = failure; }

/// <summary>One complete layout under its explicit policy, before clipping or transcript placement. No host is retained.</summary>
public sealed record TerminalEditorVisualMap(string SourceText, int SourceCursorUtf16Offset, int ContentColumns, int ViewportRows,
    TerminalEditorLayoutIdentity EditorIdentity, TerminalEditorGeometryIdentity GeometryIdentity,
    ImmutableArray<TerminalEditorAtomicRange> AtomicRanges, ImmutableArray<TerminalEditorVisualRow> Rows,
    ImmutableArray<string> RenderedRows, int CursorRenderedRow, int CursorRenderedColumn)
{
    // An exact-reference construction seal rejects public malformed records and with-clones without
    // rebuilding/retaining a second full map. There is no static registry or consumer cache.
    private ConstructionSeal? construction;
    // Identity-only class: generated record hashing must never follow Owner back to this map.
    private sealed class ConstructionSeal(TerminalEditorVisualMap owner)
    { internal TerminalEditorVisualMap Owner { get; } = owner; }
    internal void SealConstruction() => construction = new(this);
    internal bool HasValidConstruction => construction is not null && ReferenceEquals(construction.Owner, this);
    public string PolicyId => GeometryIdentity.PolicyId;
    public long ScrollResetRevision { get; init; }
    internal string DisplayText { get; init; } = SourceText;

    public TerminalTextEditorProjection Project(int maximumVisibleRows)
    {
        if (!HasValidConstruction) throw new TerminalEditorLayoutException(TerminalEditorLayoutFailure.InvalidMap);
        if (maximumVisibleRows is < 1 or > TerminalTextEditorProjection.MaximumVisibleRows)
            throw new TerminalTextEditorException(TerminalTextEditorFailure.InvalidOptions);
        var count = Math.Min(maximumVisibleRows, RenderedRows.Length);
        var first = Math.Clamp(CursorRenderedRow - count + 1, 0, RenderedRows.Length - count);
        return new(RenderedRows.Skip(first).Take(count).ToImmutableArray(), CursorRenderedRow - first,
            CursorRenderedColumn, SourceCursorUtf16Offset, first, RenderedRows.Length);
    }
}

public interface ITerminalEditorVisualMapSource
{
    TerminalEditorVisualMap Build(TerminalEditorLayoutInput input, TerminalEditorGeometrySnapshot geometry);
}

/// <summary>Pure immutable inputs only: no console, callback, clock, semaphore, history or cached map.</summary>
public sealed class TerminalEditorVisualMapBuilder : ITerminalEditorVisualMapSource
{
    public const string PolicyId = "pisharp-escaped-editor-layout-v2";
    // Retain the historical identity. The actual terminal default uses BuildForTerminal;
    // Build remains the raw Source oracle for unchanged historical comparisons.
    public const string SourcePolicyId = "pisharp-source-editor-layout-experimental-v1";
    public const int MaximumSourceRows = 65_537, MaximumRenderedRows = 65_539, MaximumRenderedCharacters = 655_362;

    /// <summary>Mandatory safe terminal data mapping before the shared Source wrapping/navigation.</summary>
    public TerminalEditorVisualMap BuildForTerminal(TerminalEditorLayoutInput input, TerminalEditorGeometrySnapshot geometry)
    {
        Validate(input, geometry);
        if (geometry.Identity.PolicyId != SourcePolicyId) throw new TerminalEditorLayoutException(TerminalEditorLayoutFailure.InvalidInput);
        var canonical = input.Snapshot.Text; char[]? display = null;
        for (var at = 0; at < canonical.Length; at++)
        {
            var value = canonical[at];
            var mapped = value < 0x20 && value is not ('\t' or '\n') ? (char)(0x2400 + value) :
                value == 0x7f ? '\u2421' : value is >= '\u0080' and <= '\u009f' ? '\u2426' : value;
            if (mapped == value) continue;
            display ??= canonical.ToCharArray(); display[at] = mapped;
        }
        return TerminalEditorSourceLayout.Build(input, geometry, display is null ? canonical : new string(display));
    }

    public TerminalEditorVisualMap Build(TerminalEditorLayoutInput input, TerminalEditorGeometrySnapshot geometry)
    {
        Validate(input, geometry);
        if (geometry.Identity.PolicyId == SourcePolicyId) return TerminalEditorSourceLayout.Build(input, geometry);
        var draft = input.Snapshot; var columns = geometry.ContentColumns; var cursor = draft.CursorUtf16Offset;
        var rendered = new List<string>(); var source = new List<TerminalEditorVisualRow>(); var row = new StringBuilder();
        var cursorRow = -1; var cursorColumn = 0; var logicalStart = 0;
        int? sourceStart = null; var sourceEnd = 0; var logicalHasSource = false;
        AppendDecoration(">"); AppendDecoration(" ");
        for (var index = 0; index < draft.Text.Length;)
        {
            var scalar = Rune.GetRuneAt(draft.Text, index);
            if (scalar.Value == '\n' && index > 0 && draft.Text[index - 1] == '\r')
            {
                if (index == cursor) Mark();
                logicalStart = index + 1; logicalHasSource = false;
            }
            else if (scalar.Value is '\r' or '\n')
            {
                if (index == cursor) Mark();
                if (scalar.Value == '\r')
                { sourceStart ??= index; sourceEnd = index + 1; logicalHasSource = true; }
                Finish(!logicalHasSource, index);
                if (scalar.Value == '\n') { logicalStart = index + 1; logicalHasSource = false; }
            }
            else
            {
                var token = scalar.Value switch
                {
                    '\\' => "\\\\", '\t' => "\\t", >= 0x20 and <= 0x7e => scalar.ToString(),
                    <= 0xffff => "\\u" + scalar.Value.ToString("x4", CultureInfo.InvariantCulture),
                    _ => "\\U" + scalar.Value.ToString("x8", CultureInfo.InvariantCulture)
                };
                if (token.Length > columns) token = "?";
                if (row.Length + token.Length > columns) Finish(false, index);
                if (index == cursor) Mark();
                sourceStart ??= index; sourceEnd = index + scalar.Utf16SequenceLength; logicalHasSource = true;
                row.Append(token);
            }
            index += scalar.Utf16SequenceLength;
        }
        if (cursor == draft.Text.Length) Mark();
        Finish(!logicalHasSource, draft.Text.Length, final: true);
        if (cursorRow < 0 || cursorRow >= rendered.Count || cursorColumn < 0 || cursorColumn >= columns)
            throw new TerminalEditorLayoutException(TerminalEditorLayoutFailure.InvalidInput);
        if (source.Count > MaximumSourceRows || rendered.Count > MaximumRenderedRows || rendered.Sum(value => (long)value.Length) > MaximumRenderedCharacters)
            throw new TerminalEditorLayoutException(TerminalEditorLayoutFailure.ResourceLimit);
        var map = new TerminalEditorVisualMap(draft.Text, cursor, columns, geometry.ViewportRows, input.Identity, geometry.Identity,
            input.AtomicRanges, source.ToImmutableArray(), rendered.ToImmutableArray(), cursorRow, cursorColumn)
            { ScrollResetRevision = input.ScrollResetRevision };
        map.SealConstruction(); return map;

        void AppendDecoration(string token)
        { if (row.Length + token.Length > columns) Finish(false, 0); row.Append(token); }
        void Mark() { cursorRow = rendered.Count + (row.Length == columns ? 1 : 0); cursorColumn = row.Length == columns ? 0 : row.Length; }
        void Finish(bool emptyLogical, int at, bool final = false)
        {
            if (sourceStart is { } start)
                source.Add(new(logicalStart, start, sourceEnd - start, rendered.Count));
            else if (emptyLogical)
                source.Add(new(logicalStart, at, 0, rendered.Count + (row.Length == columns ? 1 : 0)));
            var full = row.Length == columns;
            rendered.Add(row.ToString()); row.Clear(); sourceStart = null;
            if (final && full) rendered.Add("");
        }
    }

    private static void Validate(TerminalEditorLayoutInput input, TerminalEditorGeometrySnapshot geometry)
    {
        if (input is null || input.Snapshot is null || input.Snapshot.Text is null || input.AtomicRanges.IsDefault ||
            input.Identity.EditorLifetimeId == Guid.Empty || input.Identity.EditorRevision < 0 || input.ScrollResetRevision < 0 ||
            geometry.Identity.ViewLifetimeId == Guid.Empty || geometry.Identity.GeometryRevision < 0 ||
            geometry.Identity.PolicyId is not (PolicyId or SourcePolicyId) || geometry.ContentColumns is < 1 or > TerminalTextEditorProjection.MaximumColumns ||
            geometry.ViewportRows is < 1 or > 1024)
            throw new TerminalEditorLayoutException(TerminalEditorLayoutFailure.InvalidInput);
        var text = input.Snapshot.Text; var cursor = input.Snapshot.CursorUtf16Offset;
        if (text.Length > TerminalTextEditor.MaximumSupportedCharacters)
            throw new TerminalEditorLayoutException(TerminalEditorLayoutFailure.ResourceLimit);
        TerminalTextEditor.ValidateUnicode(text);
        if (!ValidSeam(text, cursor)) throw new TerminalTextEditorException(TerminalTextEditorFailure.InvalidCursor);
        var previousEnd = 0;
        foreach (var span in input.AtomicRanges)
        {
            var end = (long)span.SourceStartUtf16 + span.LengthUtf16;
            if (span.SourceStartUtf16 < previousEnd || span.LengthUtf16 < 1 || end > text.Length ||
                !ValidSeam(text, span.SourceStartUtf16) || !ValidSeam(text, (int)end) || text.AsSpan(span.SourceStartUtf16, span.LengthUtf16).Contains('\n'))
                throw new TerminalEditorLayoutException(TerminalEditorLayoutFailure.InvalidInput);
            previousEnd = (int)end;
        }
    }
    internal static bool ValidSeam(string text, int cursor) => cursor >= 0 && cursor <= text.Length &&
        !(cursor > 0 && cursor < text.Length && char.IsHighSurrogate(text[cursor - 1]) && char.IsLowSurrogate(text[cursor]));
}

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

internal static partial class TerminalEditorSourceWidth
{
    private static bool In(int value, int[] ranges)
    {
        var low = 0; var high = ranges.Length / 2 - 1;
        while (low <= high) { var mid = (low + high) / 2; if (value < ranges[mid * 2]) high = mid - 1; else if (value > ranges[mid * 2 + 1]) low = mid + 1; else return true; }
        return false;
    }
    internal static bool Cjk(string value) => value.EnumerateRunes().Any(r => In(r.Value, CjkRanges));
    internal static bool Whitespace(string value) => value.Length > 0 && value[0] is '\u0009' or '\u000a' or '\u000b' or '\u000c' or '\u000d' or '\u0020' or '\u00a0' or '\u1680' or >= '\u2000' and <= '\u200a' or '\u2028' or '\u2029' or '\u202f' or '\u205f' or '\u3000' or '\ufeff';
    internal static int Visible(string text)
    {
        // Raw source rows stay unchanged. Width normalization is confined to the pure cell calculation.
        var clean = new StringBuilder(text.Length);
        for (var at = 0; at < text.Length; at++)
        {
            if (text[at] == '\t') { clean.Append("   "); continue; }
            if (text[at] == '\u001b' && at + 1 < text.Length)
            {
                var end = at + 2;
                if (text[at + 1] == '[')
                { while (end < text.Length && text[end] is >= '\x20' and <= '\x3f') end++; if (end < text.Length && text[end] is >= '\x40' and <= '\x7e') { at = end; continue; } }
                else if (text[at + 1] is ']' or '_')
                { while (end < text.Length && text[end] != '\a' && !(text[end] == '\u001b' && end + 1 < text.Length && text[end + 1] == '\\')) end++; if (end < text.Length) { at = text[end] == '\a' ? end : end + 1; continue; } }
            }
            clean.Append(text[at]);
        }
        var value = clean.ToString(); var offsets = TerminalSourceGraphemeSegmenter.GetOffsets(value); var width = 0;
        for (var at = 0; at < offsets.Length; at++) width += Grapheme(value[offsets[at]..(at + 1 < offsets.Length ? offsets[at + 1] : value.Length)]);
        return width;
    }
    internal static int Grapheme(string value)
    {
        var chars = value.EnumerateRunes().Select(r => r.Value).ToArray();
        if (chars.All(c => In(c, SpacingRanges))) return chars.Length;
        if (chars.All(c => In(c, ZeroRanges))) return 0;
        if (IsRgiEmoji(value)) return 2;
        var start = Array.FindIndex(chars, c => !In(c, NonPrintingRanges)); if (start < 0) return 0;
        var cp = chars[start]; if (cp is >= 0x1f1e6 and <= 0x1f1ff) return 2;
        var width = In(cp, WideRanges) ? 2 : 1; var followsMark = false;
        foreach (var c in chars.Skip(start + 1))
        {
            if (In(c, SpacingRanges)) { width++; followsMark = false; }
            else if (In(c, MarkRanges)) followsMark = true;
            else if (!In(c, NonPrintingRanges))
            { if (followsMark || c is >= 0xff00 and <= 0xffef) width += In(c, WideRanges) ? 2 : 1; else if (c is 0x0e33 or 0x0eb3) width++; followsMark = false; }
        }
        return width;
    }
}


// Width ranges: get-east-asian-width1.6.0, MIT Copyright Sindre Sorhus <sindresorhus@gmail.com> (https://sindresorhus.com).
// Full MIT notice is retained in docs and frozen reference. Node scalar classifications are generated from the pinned runtime.
internal static partial class TerminalEditorSourceWidth
{
    private static readonly int[] WideRanges = [
        0x1100, 0x115f, 0x231a, 0x231b, 0x2329, 0x232a, 0x23e9, 0x23ec, 0x23f0, 0x23f0, 0x23f3, 0x23f3, 0x25fd, 0x25fe, 0x2614, 0x2615, 0x2630, 0x2637,
        0x2648, 0x2653, 0x267f, 0x267f, 0x268a, 0x268f, 0x2693, 0x2693, 0x26a1, 0x26a1, 0x26aa, 0x26ab, 0x26bd, 0x26be, 0x26c4, 0x26c5, 0x26ce, 0x26ce,
        0x26d4, 0x26d4, 0x26ea, 0x26ea, 0x26f2, 0x26f3, 0x26f5, 0x26f5, 0x26fa, 0x26fa, 0x26fd, 0x26fd, 0x2705, 0x2705, 0x270a, 0x270b, 0x2728, 0x2728,
        0x274c, 0x274c, 0x274e, 0x274e, 0x2753, 0x2755, 0x2757, 0x2757, 0x2795, 0x2797, 0x27b0, 0x27b0, 0x27bf, 0x27bf, 0x2b1b, 0x2b1c, 0x2b50, 0x2b50,
        0x2b55, 0x2b55, 0x2e80, 0x2e99, 0x2e9b, 0x2ef3, 0x2f00, 0x2fd5, 0x2ff0, 0x2fff, 0x3000, 0x3000, 0x3001, 0x303e, 0x3041, 0x3096, 0x3099, 0x30ff,
        0x3105, 0x312f, 0x3131, 0x318e, 0x3190, 0x31e5, 0x31ef, 0x321e, 0x3220, 0x3247, 0x3250, 0xa48c, 0xa490, 0xa4c6, 0xa960, 0xa97c, 0xac00, 0xd7a3,
        0xf900, 0xfaff, 0xfe10, 0xfe19, 0xfe30, 0xfe52, 0xfe54, 0xfe66, 0xfe68, 0xfe6b, 0xff01, 0xff60, 0xffe0, 0xffe6, 0x16fe0, 0x16fe4, 0x16ff0, 0x16ff6,
        0x17000, 0x18cd5, 0x18cff, 0x18d1e, 0x18d80, 0x18df2, 0x1aff0, 0x1aff3, 0x1aff5, 0x1affb, 0x1affd, 0x1affe, 0x1b000, 0x1b122, 0x1b132, 0x1b132, 0x1b150, 0x1b152,
        0x1b155, 0x1b155, 0x1b164, 0x1b167, 0x1b170, 0x1b2fb, 0x1d300, 0x1d356, 0x1d360, 0x1d376, 0x1f004, 0x1f004, 0x1f0cf, 0x1f0cf, 0x1f18e, 0x1f18e, 0x1f191, 0x1f19a,
        0x1f200, 0x1f202, 0x1f210, 0x1f23b, 0x1f240, 0x1f248, 0x1f250, 0x1f251, 0x1f260, 0x1f265, 0x1f300, 0x1f320, 0x1f32d, 0x1f335, 0x1f337, 0x1f37c, 0x1f37e, 0x1f393,
        0x1f3a0, 0x1f3ca, 0x1f3cf, 0x1f3d3, 0x1f3e0, 0x1f3f0, 0x1f3f4, 0x1f3f4, 0x1f3f8, 0x1f43e, 0x1f440, 0x1f440, 0x1f442, 0x1f4fc, 0x1f4ff, 0x1f53d, 0x1f54b, 0x1f54e,
        0x1f550, 0x1f567, 0x1f57a, 0x1f57a, 0x1f595, 0x1f596, 0x1f5a4, 0x1f5a4, 0x1f5fb, 0x1f64f, 0x1f680, 0x1f6c5, 0x1f6cc, 0x1f6cc, 0x1f6d0, 0x1f6d2, 0x1f6d5, 0x1f6d8,
        0x1f6dc, 0x1f6df, 0x1f6eb, 0x1f6ec, 0x1f6f4, 0x1f6fc, 0x1f7e0, 0x1f7eb, 0x1f7f0, 0x1f7f0, 0x1f90c, 0x1f93a, 0x1f93c, 0x1f945, 0x1f947, 0x1f9ff, 0x1fa70, 0x1fa7c,
        0x1fa80, 0x1fa8a, 0x1fa8e, 0x1fac6, 0x1fac8, 0x1fac8, 0x1facd, 0x1fadc, 0x1fadf, 0x1faea, 0x1faef, 0x1faf8, 0x20000, 0x2fffd, 0x30000, 0x3fffd,
    ];
    private static readonly int[] CjkRanges = [
        0xb7, 0xb7, 0x2c7, 0x2c7, 0x2c9, 0x2cb, 0x2d9, 0x2d9, 0x2ea, 0x2eb, 0x305, 0x305, 0x323, 0x323, 0x1100, 0x11ff, 0x2e80, 0x2e99,
        0x2e9b, 0x2ef3, 0x2f00, 0x2fd5, 0x2ff0, 0x2fff, 0x3001, 0x3003, 0x3005, 0x3011, 0x3013, 0x301f, 0x3021, 0x3035, 0x3037, 0x303f, 0x3041, 0x3096,
        0x3099, 0x30ff, 0x3105, 0x312f, 0x3131, 0x318e, 0x3190, 0x31e5, 0x31ef, 0x321e, 0x3220, 0x3247, 0x3260, 0x327e, 0x3280, 0x32b0, 0x32c0, 0x32cb,
        0x32d0, 0x3370, 0x337b, 0x337f, 0x33e0, 0x33fe, 0x3400, 0x4dbf, 0x4e00, 0x9fff, 0xa700, 0xa707, 0xa960, 0xa97c, 0xac00, 0xd7a3, 0xd7b0, 0xd7c6,
        0xd7cb, 0xd7fb, 0xf900, 0xfa6d, 0xfa70, 0xfad9, 0xfe45, 0xfe46, 0xff61, 0xffbe, 0xffc2, 0xffc7, 0xffca, 0xffcf, 0xffd2, 0xffd7, 0xffda, 0xffdc,
        0x16fe2, 0x16fe3, 0x16ff0, 0x16ff6, 0x1aff0, 0x1aff3, 0x1aff5, 0x1affb, 0x1affd, 0x1affe, 0x1b000, 0x1b122, 0x1b132, 0x1b132, 0x1b150, 0x1b152, 0x1b155, 0x1b155,
        0x1b164, 0x1b167, 0x1d360, 0x1d371, 0x1f200, 0x1f200, 0x1f250, 0x1f251, 0x20000, 0x2a6df, 0x2a700, 0x2b81d, 0x2b820, 0x2cead, 0x2ceb0, 0x2ebe0, 0x2ebf0, 0x2ee5d,
        0x2f800, 0x2fa1d, 0x30000, 0x3134a, 0x31350, 0x33479,
    ];
    private static readonly int[] ZeroRanges = [
        0x0, 0x1f, 0x7f, 0x9f, 0xad, 0xad, 0x300, 0x36f, 0x483, 0x489, 0x591, 0x5bd, 0x5bf, 0x5bf, 0x5c1, 0x5c2, 0x5c4, 0x5c5,
        0x5c7, 0x5c7, 0x610, 0x61a, 0x61c, 0x61c, 0x64b, 0x65f, 0x670, 0x670, 0x6d6, 0x6dc, 0x6df, 0x6e4, 0x6e7, 0x6e8, 0x6ea, 0x6ed,
        0x711, 0x711, 0x730, 0x74a, 0x7a6, 0x7b0, 0x7eb, 0x7f3, 0x7fd, 0x7fd, 0x816, 0x819, 0x81b, 0x823, 0x825, 0x827, 0x829, 0x82d,
        0x859, 0x85b, 0x897, 0x89f, 0x8ca, 0x8e1, 0x8e3, 0x903, 0x93a, 0x93c, 0x93e, 0x94f, 0x951, 0x957, 0x962, 0x963, 0x981, 0x983,
        0x9bc, 0x9bc, 0x9be, 0x9c4, 0x9c7, 0x9c8, 0x9cb, 0x9cd, 0x9d7, 0x9d7, 0x9e2, 0x9e3, 0x9fe, 0x9fe, 0xa01, 0xa03, 0xa3c, 0xa3c,
        0xa3e, 0xa42, 0xa47, 0xa48, 0xa4b, 0xa4d, 0xa51, 0xa51, 0xa70, 0xa71, 0xa75, 0xa75, 0xa81, 0xa83, 0xabc, 0xabc, 0xabe, 0xac5,
        0xac7, 0xac9, 0xacb, 0xacd, 0xae2, 0xae3, 0xafa, 0xaff, 0xb01, 0xb03, 0xb3c, 0xb3c, 0xb3e, 0xb44, 0xb47, 0xb48, 0xb4b, 0xb4d,
        0xb55, 0xb57, 0xb62, 0xb63, 0xb82, 0xb82, 0xbbe, 0xbc2, 0xbc6, 0xbc8, 0xbca, 0xbcd, 0xbd7, 0xbd7, 0xc00, 0xc04, 0xc3c, 0xc3c,
        0xc3e, 0xc44, 0xc46, 0xc48, 0xc4a, 0xc4d, 0xc55, 0xc56, 0xc62, 0xc63, 0xc81, 0xc83, 0xcbc, 0xcbc, 0xcbe, 0xcc4, 0xcc6, 0xcc8,
        0xcca, 0xccd, 0xcd5, 0xcd6, 0xce2, 0xce3, 0xcf3, 0xcf3, 0xd00, 0xd03, 0xd3b, 0xd3c, 0xd3e, 0xd44, 0xd46, 0xd48, 0xd4a, 0xd4d,
        0xd57, 0xd57, 0xd62, 0xd63, 0xd81, 0xd83, 0xdca, 0xdca, 0xdcf, 0xdd4, 0xdd6, 0xdd6, 0xdd8, 0xddf, 0xdf2, 0xdf3, 0xe31, 0xe31,
        0xe34, 0xe3a, 0xe47, 0xe4e, 0xeb1, 0xeb1, 0xeb4, 0xebc, 0xec8, 0xece, 0xf18, 0xf19, 0xf35, 0xf35, 0xf37, 0xf37, 0xf39, 0xf39,
        0xf3e, 0xf3f, 0xf71, 0xf84, 0xf86, 0xf87, 0xf8d, 0xf97, 0xf99, 0xfbc, 0xfc6, 0xfc6, 0x102b, 0x103e, 0x1056, 0x1059, 0x105e, 0x1060,
        0x1062, 0x1064, 0x1067, 0x106d, 0x1071, 0x1074, 0x1082, 0x108d, 0x108f, 0x108f, 0x109a, 0x109d, 0x115f, 0x1160, 0x135d, 0x135f, 0x1712, 0x1715,
        0x1732, 0x1734, 0x1752, 0x1753, 0x1772, 0x1773, 0x17b4, 0x17d3, 0x17dd, 0x17dd, 0x180b, 0x180f, 0x1885, 0x1886, 0x18a9, 0x18a9, 0x1920, 0x192b,
        0x1930, 0x193b, 0x1a17, 0x1a1b, 0x1a55, 0x1a5e, 0x1a60, 0x1a7c, 0x1a7f, 0x1a7f, 0x1ab0, 0x1add, 0x1ae0, 0x1aeb, 0x1b00, 0x1b04, 0x1b34, 0x1b44,
        0x1b6b, 0x1b73, 0x1b80, 0x1b82, 0x1ba1, 0x1bad, 0x1be6, 0x1bf3, 0x1c24, 0x1c37, 0x1cd0, 0x1cd2, 0x1cd4, 0x1ce8, 0x1ced, 0x1ced, 0x1cf4, 0x1cf4,
        0x1cf7, 0x1cf9, 0x1dc0, 0x1dff, 0x200b, 0x200f, 0x202a, 0x202e, 0x2060, 0x206f, 0x20d0, 0x20f0, 0x2cef, 0x2cf1, 0x2d7f, 0x2d7f, 0x2de0, 0x2dff,
        0x302a, 0x302f, 0x3099, 0x309a, 0x3164, 0x3164, 0xa66f, 0xa672, 0xa674, 0xa67d, 0xa69e, 0xa69f, 0xa6f0, 0xa6f1, 0xa802, 0xa802, 0xa806, 0xa806,
        0xa80b, 0xa80b, 0xa823, 0xa827, 0xa82c, 0xa82c, 0xa880, 0xa881, 0xa8b4, 0xa8c5, 0xa8e0, 0xa8f1, 0xa8ff, 0xa8ff, 0xa926, 0xa92d, 0xa947, 0xa953,
        0xa980, 0xa983, 0xa9b3, 0xa9c0, 0xa9e5, 0xa9e5, 0xaa29, 0xaa36, 0xaa43, 0xaa43, 0xaa4c, 0xaa4d, 0xaa7b, 0xaa7d, 0xaab0, 0xaab0, 0xaab2, 0xaab4,
        0xaab7, 0xaab8, 0xaabe, 0xaabf, 0xaac1, 0xaac1, 0xaaeb, 0xaaef, 0xaaf5, 0xaaf6, 0xabe3, 0xabea, 0xabec, 0xabed, 0xd800, 0xdfff, 0xfb1e, 0xfb1e,
        0xfe00, 0xfe0f, 0xfe20, 0xfe2f, 0xfeff, 0xfeff, 0xffa0, 0xffa0, 0xfff0, 0xfff8, 0x101fd, 0x101fd, 0x102e0, 0x102e0, 0x10376, 0x1037a, 0x10a01, 0x10a03,
        0x10a05, 0x10a06, 0x10a0c, 0x10a0f, 0x10a38, 0x10a3a, 0x10a3f, 0x10a3f, 0x10ae5, 0x10ae6, 0x10d24, 0x10d27, 0x10d69, 0x10d6d, 0x10eab, 0x10eac, 0x10efa, 0x10eff,
        0x10f46, 0x10f50, 0x10f82, 0x10f85, 0x11000, 0x11002, 0x11038, 0x11046, 0x11070, 0x11070, 0x11073, 0x11074, 0x1107f, 0x11082, 0x110b0, 0x110ba, 0x110c2, 0x110c2,
        0x11100, 0x11102, 0x11127, 0x11134, 0x11145, 0x11146, 0x11173, 0x11173, 0x11180, 0x11182, 0x111b3, 0x111c0, 0x111c9, 0x111cc, 0x111ce, 0x111cf, 0x1122c, 0x11237,
        0x1123e, 0x1123e, 0x11241, 0x11241, 0x112df, 0x112ea, 0x11300, 0x11303, 0x1133b, 0x1133c, 0x1133e, 0x11344, 0x11347, 0x11348, 0x1134b, 0x1134d, 0x11357, 0x11357,
        0x11362, 0x11363, 0x11366, 0x1136c, 0x11370, 0x11374, 0x113b8, 0x113c0, 0x113c2, 0x113c2, 0x113c5, 0x113c5, 0x113c7, 0x113ca, 0x113cc, 0x113d0, 0x113d2, 0x113d2,
        0x113e1, 0x113e2, 0x11435, 0x11446, 0x1145e, 0x1145e, 0x114b0, 0x114c3, 0x115af, 0x115b5, 0x115b8, 0x115c0, 0x115dc, 0x115dd, 0x11630, 0x11640, 0x116ab, 0x116b7,
        0x1171d, 0x1172b, 0x1182c, 0x1183a, 0x11930, 0x11935, 0x11937, 0x11938, 0x1193b, 0x1193e, 0x11940, 0x11940, 0x11942, 0x11943, 0x119d1, 0x119d7, 0x119da, 0x119e0,
        0x119e4, 0x119e4, 0x11a01, 0x11a0a, 0x11a33, 0x11a39, 0x11a3b, 0x11a3e, 0x11a47, 0x11a47, 0x11a51, 0x11a5b, 0x11a8a, 0x11a99, 0x11b60, 0x11b67, 0x11c2f, 0x11c36,
        0x11c38, 0x11c3f, 0x11c92, 0x11ca7, 0x11ca9, 0x11cb6, 0x11d31, 0x11d36, 0x11d3a, 0x11d3a, 0x11d3c, 0x11d3d, 0x11d3f, 0x11d45, 0x11d47, 0x11d47, 0x11d8a, 0x11d8e,
        0x11d90, 0x11d91, 0x11d93, 0x11d97, 0x11ef3, 0x11ef6, 0x11f00, 0x11f01, 0x11f03, 0x11f03, 0x11f34, 0x11f3a, 0x11f3e, 0x11f42, 0x11f5a, 0x11f5a, 0x13440, 0x13440,
        0x13447, 0x13455, 0x1611e, 0x1612f, 0x16af0, 0x16af4, 0x16b30, 0x16b36, 0x16f4f, 0x16f4f, 0x16f51, 0x16f87, 0x16f8f, 0x16f92, 0x16fe4, 0x16fe4, 0x16ff0, 0x16ff1,
        0x1bc9d, 0x1bc9e, 0x1bca0, 0x1bca3, 0x1cf00, 0x1cf2d, 0x1cf30, 0x1cf46, 0x1d165, 0x1d169, 0x1d16d, 0x1d182, 0x1d185, 0x1d18b, 0x1d1aa, 0x1d1ad, 0x1d242, 0x1d244,
        0x1da00, 0x1da36, 0x1da3b, 0x1da6c, 0x1da75, 0x1da75, 0x1da84, 0x1da84, 0x1da9b, 0x1da9f, 0x1daa1, 0x1daaf, 0x1e000, 0x1e006, 0x1e008, 0x1e018, 0x1e01b, 0x1e021,
        0x1e023, 0x1e024, 0x1e026, 0x1e02a, 0x1e08f, 0x1e08f, 0x1e130, 0x1e136, 0x1e2ae, 0x1e2ae, 0x1e2ec, 0x1e2ef, 0x1e4ec, 0x1e4ef, 0x1e5ee, 0x1e5ef, 0x1e6e3, 0x1e6e3,
        0x1e6e6, 0x1e6e6, 0x1e6ee, 0x1e6ef, 0x1e6f5, 0x1e6f5, 0x1e8d0, 0x1e8d6, 0x1e944, 0x1e94a, 0xe0000, 0xe0fff,
    ];
    private static readonly int[] NonPrintingRanges = [
        0x0, 0x1f, 0x7f, 0x9f, 0xad, 0xad, 0x300, 0x36f, 0x483, 0x489, 0x591, 0x5bd, 0x5bf, 0x5bf, 0x5c1, 0x5c2, 0x5c4, 0x5c5,
        0x5c7, 0x5c7, 0x600, 0x605, 0x610, 0x61a, 0x61c, 0x61c, 0x64b, 0x65f, 0x670, 0x670, 0x6d6, 0x6dd, 0x6df, 0x6e4, 0x6e7, 0x6e8,
        0x6ea, 0x6ed, 0x70f, 0x70f, 0x711, 0x711, 0x730, 0x74a, 0x7a6, 0x7b0, 0x7eb, 0x7f3, 0x7fd, 0x7fd, 0x816, 0x819, 0x81b, 0x823,
        0x825, 0x827, 0x829, 0x82d, 0x859, 0x85b, 0x890, 0x891, 0x897, 0x89f, 0x8ca, 0x903, 0x93a, 0x93c, 0x93e, 0x94f, 0x951, 0x957,
        0x962, 0x963, 0x981, 0x983, 0x9bc, 0x9bc, 0x9be, 0x9c4, 0x9c7, 0x9c8, 0x9cb, 0x9cd, 0x9d7, 0x9d7, 0x9e2, 0x9e3, 0x9fe, 0x9fe,
        0xa01, 0xa03, 0xa3c, 0xa3c, 0xa3e, 0xa42, 0xa47, 0xa48, 0xa4b, 0xa4d, 0xa51, 0xa51, 0xa70, 0xa71, 0xa75, 0xa75, 0xa81, 0xa83,
        0xabc, 0xabc, 0xabe, 0xac5, 0xac7, 0xac9, 0xacb, 0xacd, 0xae2, 0xae3, 0xafa, 0xaff, 0xb01, 0xb03, 0xb3c, 0xb3c, 0xb3e, 0xb44,
        0xb47, 0xb48, 0xb4b, 0xb4d, 0xb55, 0xb57, 0xb62, 0xb63, 0xb82, 0xb82, 0xbbe, 0xbc2, 0xbc6, 0xbc8, 0xbca, 0xbcd, 0xbd7, 0xbd7,
        0xc00, 0xc04, 0xc3c, 0xc3c, 0xc3e, 0xc44, 0xc46, 0xc48, 0xc4a, 0xc4d, 0xc55, 0xc56, 0xc62, 0xc63, 0xc81, 0xc83, 0xcbc, 0xcbc,
        0xcbe, 0xcc4, 0xcc6, 0xcc8, 0xcca, 0xccd, 0xcd5, 0xcd6, 0xce2, 0xce3, 0xcf3, 0xcf3, 0xd00, 0xd03, 0xd3b, 0xd3c, 0xd3e, 0xd44,
        0xd46, 0xd48, 0xd4a, 0xd4d, 0xd57, 0xd57, 0xd62, 0xd63, 0xd81, 0xd83, 0xdca, 0xdca, 0xdcf, 0xdd4, 0xdd6, 0xdd6, 0xdd8, 0xddf,
        0xdf2, 0xdf3, 0xe31, 0xe31, 0xe34, 0xe3a, 0xe47, 0xe4e, 0xeb1, 0xeb1, 0xeb4, 0xebc, 0xec8, 0xece, 0xf18, 0xf19, 0xf35, 0xf35,
        0xf37, 0xf37, 0xf39, 0xf39, 0xf3e, 0xf3f, 0xf71, 0xf84, 0xf86, 0xf87, 0xf8d, 0xf97, 0xf99, 0xfbc, 0xfc6, 0xfc6, 0x102b, 0x103e,
        0x1056, 0x1059, 0x105e, 0x1060, 0x1062, 0x1064, 0x1067, 0x106d, 0x1071, 0x1074, 0x1082, 0x108d, 0x108f, 0x108f, 0x109a, 0x109d, 0x115f, 0x1160,
        0x135d, 0x135f, 0x1712, 0x1715, 0x1732, 0x1734, 0x1752, 0x1753, 0x1772, 0x1773, 0x17b4, 0x17d3, 0x17dd, 0x17dd, 0x180b, 0x180f, 0x1885, 0x1886,
        0x18a9, 0x18a9, 0x1920, 0x192b, 0x1930, 0x193b, 0x1a17, 0x1a1b, 0x1a55, 0x1a5e, 0x1a60, 0x1a7c, 0x1a7f, 0x1a7f, 0x1ab0, 0x1add, 0x1ae0, 0x1aeb,
        0x1b00, 0x1b04, 0x1b34, 0x1b44, 0x1b6b, 0x1b73, 0x1b80, 0x1b82, 0x1ba1, 0x1bad, 0x1be6, 0x1bf3, 0x1c24, 0x1c37, 0x1cd0, 0x1cd2, 0x1cd4, 0x1ce8,
        0x1ced, 0x1ced, 0x1cf4, 0x1cf4, 0x1cf7, 0x1cf9, 0x1dc0, 0x1dff, 0x200b, 0x200f, 0x202a, 0x202e, 0x2060, 0x206f, 0x20d0, 0x20f0, 0x2cef, 0x2cf1,
        0x2d7f, 0x2d7f, 0x2de0, 0x2dff, 0x302a, 0x302f, 0x3099, 0x309a, 0x3164, 0x3164, 0xa66f, 0xa672, 0xa674, 0xa67d, 0xa69e, 0xa69f, 0xa6f0, 0xa6f1,
        0xa802, 0xa802, 0xa806, 0xa806, 0xa80b, 0xa80b, 0xa823, 0xa827, 0xa82c, 0xa82c, 0xa880, 0xa881, 0xa8b4, 0xa8c5, 0xa8e0, 0xa8f1, 0xa8ff, 0xa8ff,
        0xa926, 0xa92d, 0xa947, 0xa953, 0xa980, 0xa983, 0xa9b3, 0xa9c0, 0xa9e5, 0xa9e5, 0xaa29, 0xaa36, 0xaa43, 0xaa43, 0xaa4c, 0xaa4d, 0xaa7b, 0xaa7d,
        0xaab0, 0xaab0, 0xaab2, 0xaab4, 0xaab7, 0xaab8, 0xaabe, 0xaabf, 0xaac1, 0xaac1, 0xaaeb, 0xaaef, 0xaaf5, 0xaaf6, 0xabe3, 0xabea, 0xabec, 0xabed,
        0xd800, 0xdfff, 0xfb1e, 0xfb1e, 0xfe00, 0xfe0f, 0xfe20, 0xfe2f, 0xfeff, 0xfeff, 0xffa0, 0xffa0, 0xfff0, 0xfffb, 0x101fd, 0x101fd, 0x102e0, 0x102e0,
        0x10376, 0x1037a, 0x10a01, 0x10a03, 0x10a05, 0x10a06, 0x10a0c, 0x10a0f, 0x10a38, 0x10a3a, 0x10a3f, 0x10a3f, 0x10ae5, 0x10ae6, 0x10d24, 0x10d27, 0x10d69, 0x10d6d,
        0x10eab, 0x10eac, 0x10efa, 0x10eff, 0x10f46, 0x10f50, 0x10f82, 0x10f85, 0x11000, 0x11002, 0x11038, 0x11046, 0x11070, 0x11070, 0x11073, 0x11074, 0x1107f, 0x11082,
        0x110b0, 0x110ba, 0x110bd, 0x110bd, 0x110c2, 0x110c2, 0x110cd, 0x110cd, 0x11100, 0x11102, 0x11127, 0x11134, 0x11145, 0x11146, 0x11173, 0x11173, 0x11180, 0x11182,
        0x111b3, 0x111c0, 0x111c9, 0x111cc, 0x111ce, 0x111cf, 0x1122c, 0x11237, 0x1123e, 0x1123e, 0x11241, 0x11241, 0x112df, 0x112ea, 0x11300, 0x11303, 0x1133b, 0x1133c,
        0x1133e, 0x11344, 0x11347, 0x11348, 0x1134b, 0x1134d, 0x11357, 0x11357, 0x11362, 0x11363, 0x11366, 0x1136c, 0x11370, 0x11374, 0x113b8, 0x113c0, 0x113c2, 0x113c2,
        0x113c5, 0x113c5, 0x113c7, 0x113ca, 0x113cc, 0x113d0, 0x113d2, 0x113d2, 0x113e1, 0x113e2, 0x11435, 0x11446, 0x1145e, 0x1145e, 0x114b0, 0x114c3, 0x115af, 0x115b5,
        0x115b8, 0x115c0, 0x115dc, 0x115dd, 0x11630, 0x11640, 0x116ab, 0x116b7, 0x1171d, 0x1172b, 0x1182c, 0x1183a, 0x11930, 0x11935, 0x11937, 0x11938, 0x1193b, 0x1193e,
        0x11940, 0x11940, 0x11942, 0x11943, 0x119d1, 0x119d7, 0x119da, 0x119e0, 0x119e4, 0x119e4, 0x11a01, 0x11a0a, 0x11a33, 0x11a39, 0x11a3b, 0x11a3e, 0x11a47, 0x11a47,
        0x11a51, 0x11a5b, 0x11a8a, 0x11a99, 0x11b60, 0x11b67, 0x11c2f, 0x11c36, 0x11c38, 0x11c3f, 0x11c92, 0x11ca7, 0x11ca9, 0x11cb6, 0x11d31, 0x11d36, 0x11d3a, 0x11d3a,
        0x11d3c, 0x11d3d, 0x11d3f, 0x11d45, 0x11d47, 0x11d47, 0x11d8a, 0x11d8e, 0x11d90, 0x11d91, 0x11d93, 0x11d97, 0x11ef3, 0x11ef6, 0x11f00, 0x11f01, 0x11f03, 0x11f03,
        0x11f34, 0x11f3a, 0x11f3e, 0x11f42, 0x11f5a, 0x11f5a, 0x13430, 0x13440, 0x13447, 0x13455, 0x1611e, 0x1612f, 0x16af0, 0x16af4, 0x16b30, 0x16b36, 0x16f4f, 0x16f4f,
        0x16f51, 0x16f87, 0x16f8f, 0x16f92, 0x16fe4, 0x16fe4, 0x16ff0, 0x16ff1, 0x1bc9d, 0x1bc9e, 0x1bca0, 0x1bca3, 0x1cf00, 0x1cf2d, 0x1cf30, 0x1cf46, 0x1d165, 0x1d169,
        0x1d16d, 0x1d182, 0x1d185, 0x1d18b, 0x1d1aa, 0x1d1ad, 0x1d242, 0x1d244, 0x1da00, 0x1da36, 0x1da3b, 0x1da6c, 0x1da75, 0x1da75, 0x1da84, 0x1da84, 0x1da9b, 0x1da9f,
        0x1daa1, 0x1daaf, 0x1e000, 0x1e006, 0x1e008, 0x1e018, 0x1e01b, 0x1e021, 0x1e023, 0x1e024, 0x1e026, 0x1e02a, 0x1e08f, 0x1e08f, 0x1e130, 0x1e136, 0x1e2ae, 0x1e2ae,
        0x1e2ec, 0x1e2ef, 0x1e4ec, 0x1e4ef, 0x1e5ee, 0x1e5ef, 0x1e6e3, 0x1e6e3, 0x1e6e6, 0x1e6e6, 0x1e6ee, 0x1e6ef, 0x1e6f5, 0x1e6f5, 0x1e8d0, 0x1e8d6, 0x1e944, 0x1e94a,
        0xe0000, 0xe0fff,
    ];
    private static readonly int[] MarkRanges = [
        0x300, 0x36f, 0x483, 0x489, 0x591, 0x5bd, 0x5bf, 0x5bf, 0x5c1, 0x5c2, 0x5c4, 0x5c5, 0x5c7, 0x5c7, 0x610, 0x61a, 0x64b, 0x65f,
        0x670, 0x670, 0x6d6, 0x6dc, 0x6df, 0x6e4, 0x6e7, 0x6e8, 0x6ea, 0x6ed, 0x711, 0x711, 0x730, 0x74a, 0x7a6, 0x7b0, 0x7eb, 0x7f3,
        0x7fd, 0x7fd, 0x816, 0x819, 0x81b, 0x823, 0x825, 0x827, 0x829, 0x82d, 0x859, 0x85b, 0x897, 0x89f, 0x8ca, 0x8e1, 0x8e3, 0x903,
        0x93a, 0x93c, 0x93e, 0x94f, 0x951, 0x957, 0x962, 0x963, 0x981, 0x983, 0x9bc, 0x9bc, 0x9be, 0x9c4, 0x9c7, 0x9c8, 0x9cb, 0x9cd,
        0x9d7, 0x9d7, 0x9e2, 0x9e3, 0x9fe, 0x9fe, 0xa01, 0xa03, 0xa3c, 0xa3c, 0xa3e, 0xa42, 0xa47, 0xa48, 0xa4b, 0xa4d, 0xa51, 0xa51,
        0xa70, 0xa71, 0xa75, 0xa75, 0xa81, 0xa83, 0xabc, 0xabc, 0xabe, 0xac5, 0xac7, 0xac9, 0xacb, 0xacd, 0xae2, 0xae3, 0xafa, 0xaff,
        0xb01, 0xb03, 0xb3c, 0xb3c, 0xb3e, 0xb44, 0xb47, 0xb48, 0xb4b, 0xb4d, 0xb55, 0xb57, 0xb62, 0xb63, 0xb82, 0xb82, 0xbbe, 0xbc2,
        0xbc6, 0xbc8, 0xbca, 0xbcd, 0xbd7, 0xbd7, 0xc00, 0xc04, 0xc3c, 0xc3c, 0xc3e, 0xc44, 0xc46, 0xc48, 0xc4a, 0xc4d, 0xc55, 0xc56,
        0xc62, 0xc63, 0xc81, 0xc83, 0xcbc, 0xcbc, 0xcbe, 0xcc4, 0xcc6, 0xcc8, 0xcca, 0xccd, 0xcd5, 0xcd6, 0xce2, 0xce3, 0xcf3, 0xcf3,
        0xd00, 0xd03, 0xd3b, 0xd3c, 0xd3e, 0xd44, 0xd46, 0xd48, 0xd4a, 0xd4d, 0xd57, 0xd57, 0xd62, 0xd63, 0xd81, 0xd83, 0xdca, 0xdca,
        0xdcf, 0xdd4, 0xdd6, 0xdd6, 0xdd8, 0xddf, 0xdf2, 0xdf3, 0xe31, 0xe31, 0xe34, 0xe3a, 0xe47, 0xe4e, 0xeb1, 0xeb1, 0xeb4, 0xebc,
        0xec8, 0xece, 0xf18, 0xf19, 0xf35, 0xf35, 0xf37, 0xf37, 0xf39, 0xf39, 0xf3e, 0xf3f, 0xf71, 0xf84, 0xf86, 0xf87, 0xf8d, 0xf97,
        0xf99, 0xfbc, 0xfc6, 0xfc6, 0x102b, 0x103e, 0x1056, 0x1059, 0x105e, 0x1060, 0x1062, 0x1064, 0x1067, 0x106d, 0x1071, 0x1074, 0x1082, 0x108d,
        0x108f, 0x108f, 0x109a, 0x109d, 0x135d, 0x135f, 0x1712, 0x1715, 0x1732, 0x1734, 0x1752, 0x1753, 0x1772, 0x1773, 0x17b4, 0x17d3, 0x17dd, 0x17dd,
        0x180b, 0x180d, 0x180f, 0x180f, 0x1885, 0x1886, 0x18a9, 0x18a9, 0x1920, 0x192b, 0x1930, 0x193b, 0x1a17, 0x1a1b, 0x1a55, 0x1a5e, 0x1a60, 0x1a7c,
        0x1a7f, 0x1a7f, 0x1ab0, 0x1add, 0x1ae0, 0x1aeb, 0x1b00, 0x1b04, 0x1b34, 0x1b44, 0x1b6b, 0x1b73, 0x1b80, 0x1b82, 0x1ba1, 0x1bad, 0x1be6, 0x1bf3,
        0x1c24, 0x1c37, 0x1cd0, 0x1cd2, 0x1cd4, 0x1ce8, 0x1ced, 0x1ced, 0x1cf4, 0x1cf4, 0x1cf7, 0x1cf9, 0x1dc0, 0x1dff, 0x20d0, 0x20f0, 0x2cef, 0x2cf1,
        0x2d7f, 0x2d7f, 0x2de0, 0x2dff, 0x302a, 0x302f, 0x3099, 0x309a, 0xa66f, 0xa672, 0xa674, 0xa67d, 0xa69e, 0xa69f, 0xa6f0, 0xa6f1, 0xa802, 0xa802,
        0xa806, 0xa806, 0xa80b, 0xa80b, 0xa823, 0xa827, 0xa82c, 0xa82c, 0xa880, 0xa881, 0xa8b4, 0xa8c5, 0xa8e0, 0xa8f1, 0xa8ff, 0xa8ff, 0xa926, 0xa92d,
        0xa947, 0xa953, 0xa980, 0xa983, 0xa9b3, 0xa9c0, 0xa9e5, 0xa9e5, 0xaa29, 0xaa36, 0xaa43, 0xaa43, 0xaa4c, 0xaa4d, 0xaa7b, 0xaa7d, 0xaab0, 0xaab0,
        0xaab2, 0xaab4, 0xaab7, 0xaab8, 0xaabe, 0xaabf, 0xaac1, 0xaac1, 0xaaeb, 0xaaef, 0xaaf5, 0xaaf6, 0xabe3, 0xabea, 0xabec, 0xabed, 0xfb1e, 0xfb1e,
        0xfe00, 0xfe0f, 0xfe20, 0xfe2f, 0x101fd, 0x101fd, 0x102e0, 0x102e0, 0x10376, 0x1037a, 0x10a01, 0x10a03, 0x10a05, 0x10a06, 0x10a0c, 0x10a0f, 0x10a38, 0x10a3a,
        0x10a3f, 0x10a3f, 0x10ae5, 0x10ae6, 0x10d24, 0x10d27, 0x10d69, 0x10d6d, 0x10eab, 0x10eac, 0x10efa, 0x10eff, 0x10f46, 0x10f50, 0x10f82, 0x10f85, 0x11000, 0x11002,
        0x11038, 0x11046, 0x11070, 0x11070, 0x11073, 0x11074, 0x1107f, 0x11082, 0x110b0, 0x110ba, 0x110c2, 0x110c2, 0x11100, 0x11102, 0x11127, 0x11134, 0x11145, 0x11146,
        0x11173, 0x11173, 0x11180, 0x11182, 0x111b3, 0x111c0, 0x111c9, 0x111cc, 0x111ce, 0x111cf, 0x1122c, 0x11237, 0x1123e, 0x1123e, 0x11241, 0x11241, 0x112df, 0x112ea,
        0x11300, 0x11303, 0x1133b, 0x1133c, 0x1133e, 0x11344, 0x11347, 0x11348, 0x1134b, 0x1134d, 0x11357, 0x11357, 0x11362, 0x11363, 0x11366, 0x1136c, 0x11370, 0x11374,
        0x113b8, 0x113c0, 0x113c2, 0x113c2, 0x113c5, 0x113c5, 0x113c7, 0x113ca, 0x113cc, 0x113d0, 0x113d2, 0x113d2, 0x113e1, 0x113e2, 0x11435, 0x11446, 0x1145e, 0x1145e,
        0x114b0, 0x114c3, 0x115af, 0x115b5, 0x115b8, 0x115c0, 0x115dc, 0x115dd, 0x11630, 0x11640, 0x116ab, 0x116b7, 0x1171d, 0x1172b, 0x1182c, 0x1183a, 0x11930, 0x11935,
        0x11937, 0x11938, 0x1193b, 0x1193e, 0x11940, 0x11940, 0x11942, 0x11943, 0x119d1, 0x119d7, 0x119da, 0x119e0, 0x119e4, 0x119e4, 0x11a01, 0x11a0a, 0x11a33, 0x11a39,
        0x11a3b, 0x11a3e, 0x11a47, 0x11a47, 0x11a51, 0x11a5b, 0x11a8a, 0x11a99, 0x11b60, 0x11b67, 0x11c2f, 0x11c36, 0x11c38, 0x11c3f, 0x11c92, 0x11ca7, 0x11ca9, 0x11cb6,
        0x11d31, 0x11d36, 0x11d3a, 0x11d3a, 0x11d3c, 0x11d3d, 0x11d3f, 0x11d45, 0x11d47, 0x11d47, 0x11d8a, 0x11d8e, 0x11d90, 0x11d91, 0x11d93, 0x11d97, 0x11ef3, 0x11ef6,
        0x11f00, 0x11f01, 0x11f03, 0x11f03, 0x11f34, 0x11f3a, 0x11f3e, 0x11f42, 0x11f5a, 0x11f5a, 0x13440, 0x13440, 0x13447, 0x13455, 0x1611e, 0x1612f, 0x16af0, 0x16af4,
        0x16b30, 0x16b36, 0x16f4f, 0x16f4f, 0x16f51, 0x16f87, 0x16f8f, 0x16f92, 0x16fe4, 0x16fe4, 0x16ff0, 0x16ff1, 0x1bc9d, 0x1bc9e, 0x1cf00, 0x1cf2d, 0x1cf30, 0x1cf46,
        0x1d165, 0x1d169, 0x1d16d, 0x1d172, 0x1d17b, 0x1d182, 0x1d185, 0x1d18b, 0x1d1aa, 0x1d1ad, 0x1d242, 0x1d244, 0x1da00, 0x1da36, 0x1da3b, 0x1da6c, 0x1da75, 0x1da75,
        0x1da84, 0x1da84, 0x1da9b, 0x1da9f, 0x1daa1, 0x1daaf, 0x1e000, 0x1e006, 0x1e008, 0x1e018, 0x1e01b, 0x1e021, 0x1e023, 0x1e024, 0x1e026, 0x1e02a, 0x1e08f, 0x1e08f,
        0x1e130, 0x1e136, 0x1e2ae, 0x1e2ae, 0x1e2ec, 0x1e2ef, 0x1e4ec, 0x1e4ef, 0x1e5ee, 0x1e5ef, 0x1e6e3, 0x1e6e3, 0x1e6e6, 0x1e6e6, 0x1e6ee, 0x1e6ef, 0x1e6f5, 0x1e6f5,
        0x1e8d0, 0x1e8d6, 0x1e944, 0x1e94a, 0xe0100, 0xe01ef,
    ];
    private static readonly int[] SpacingRanges = [
        0x65f, 0x65f, 0x903, 0x903, 0x93b, 0x93b, 0x93e, 0x940, 0x949, 0x94c, 0x94e, 0x94f, 0x982, 0x983, 0x9be, 0x9c0, 0x9c7, 0x9c8,
        0x9cb, 0x9cc, 0x9d7, 0x9d7, 0xa03, 0xa03, 0xa3e, 0xa40, 0xa83, 0xa83, 0xabe, 0xac0, 0xac9, 0xac9, 0xacb, 0xacc, 0xb02, 0xb03,
        0xb3e, 0xb3e, 0xb40, 0xb40, 0xb47, 0xb48, 0xb4b, 0xb4c, 0xb57, 0xb57, 0xbbe, 0xbbf, 0xbc1, 0xbc2, 0xbc6, 0xbc8, 0xbca, 0xbcc,
        0xbd7, 0xbd7, 0xc01, 0xc03, 0xc41, 0xc44, 0xc82, 0xc83, 0xcbe, 0xcbe, 0xcc0, 0xcc4, 0xcc7, 0xcc8, 0xcca, 0xccb, 0xcd5, 0xcd6,
        0xcf3, 0xcf3, 0xd02, 0xd03, 0xd3e, 0xd40, 0xd46, 0xd48, 0xd4a, 0xd4c, 0xd57, 0xd57, 0xd82, 0xd83, 0xdcf, 0xdd1, 0xdd8, 0xddf,
        0xdf2, 0xdf3, 0xf3e, 0xf3f, 0xf7f, 0xf7f, 0x102b, 0x102c, 0x1031, 0x1031, 0x1033, 0x1035, 0x1038, 0x1038, 0x103a, 0x103e, 0x1056, 0x1057,
        0x1062, 0x1064, 0x1067, 0x106d, 0x1083, 0x1084, 0x1087, 0x108c, 0x108f, 0x108f, 0x109a, 0x109c, 0x1715, 0x1715, 0x17b6, 0x17b6, 0x17be, 0x17c5,
        0x17c7, 0x17c8, 0x1923, 0x1926, 0x1929, 0x192b, 0x1930, 0x1931, 0x1933, 0x1938, 0x1a19, 0x1a1a, 0x1a55, 0x1a55, 0x1a57, 0x1a57, 0x1a61, 0x1a61,
        0x1a63, 0x1a64, 0x1a6d, 0x1a72, 0x1b04, 0x1b04, 0x1b35, 0x1b35, 0x1b3b, 0x1b3b, 0x1b3d, 0x1b41, 0x1b43, 0x1b44, 0x1b82, 0x1b82, 0x1ba1, 0x1ba1,
        0x1ba6, 0x1ba7, 0x1baa, 0x1baa, 0x1be7, 0x1be7, 0x1bea, 0x1bec, 0x1bee, 0x1bee, 0x1bf2, 0x1bf3, 0x1c24, 0x1c2b, 0x1c34, 0x1c35, 0x1ce1, 0x1ce1,
        0x1cf7, 0x1cf7, 0xa823, 0xa824, 0xa827, 0xa827, 0xa880, 0xa881, 0xa8b4, 0xa8c3, 0xa952, 0xa953, 0xa983, 0xa983, 0xa9b4, 0xa9b5, 0xa9ba, 0xa9bb,
        0xa9be, 0xa9c0, 0xaa2f, 0xaa30, 0xaa33, 0xaa34, 0xaa4d, 0xaa4d, 0xaa7b, 0xaa7b, 0xaa7d, 0xaa7d, 0xaaeb, 0xaaeb, 0xaaee, 0xaaef, 0xaaf5, 0xaaf5,
        0xabe3, 0xabe4, 0xabe6, 0xabe7, 0xabe9, 0xabea, 0xabec, 0xabec, 0x11000, 0x11000, 0x11002, 0x11002, 0x11082, 0x11082, 0x110b0, 0x110b2, 0x110b7, 0x110b8,
        0x1112c, 0x1112c, 0x11145, 0x11146, 0x11182, 0x11182, 0x111b3, 0x111b5, 0x111bf, 0x111c0, 0x111ce, 0x111ce, 0x1122c, 0x1122e, 0x11232, 0x11233, 0x11235, 0x11235,
        0x112e0, 0x112e2, 0x11302, 0x11303, 0x1133e, 0x1133f, 0x11341, 0x11344, 0x11347, 0x11348, 0x1134b, 0x1134d, 0x11357, 0x11357, 0x11362, 0x11363, 0x113b8, 0x113ba,
        0x113c2, 0x113c2, 0x113c5, 0x113c5, 0x113c7, 0x113ca, 0x113cc, 0x113cd, 0x113cf, 0x113cf, 0x11435, 0x11437, 0x11440, 0x11441, 0x11445, 0x11445, 0x114b0, 0x114b2,
        0x114b9, 0x114b9, 0x114bb, 0x114be, 0x114c1, 0x114c1, 0x115af, 0x115b1, 0x115b8, 0x115bb, 0x115be, 0x115be, 0x11630, 0x11632, 0x1163b, 0x1163c, 0x1163e, 0x1163e,
        0x116ac, 0x116ac, 0x116ae, 0x116af, 0x116b6, 0x116b6, 0x1171e, 0x1171e, 0x11720, 0x11721, 0x11726, 0x11726, 0x1182c, 0x1182e, 0x11838, 0x11838, 0x11930, 0x11935,
        0x11937, 0x11938, 0x1193d, 0x1193d, 0x11940, 0x11940, 0x11942, 0x11942, 0x119d1, 0x119d3, 0x119dc, 0x119df, 0x119e4, 0x119e4, 0x11a39, 0x11a39, 0x11a57, 0x11a58,
        0x11a97, 0x11a97, 0x11b61, 0x11b61, 0x11b65, 0x11b65, 0x11b67, 0x11b67, 0x11c2f, 0x11c2f, 0x11c3e, 0x11c3e, 0x11ca9, 0x11ca9, 0x11cb1, 0x11cb1, 0x11cb4, 0x11cb4,
        0x11d8a, 0x11d8e, 0x11d93, 0x11d94, 0x11d96, 0x11d96, 0x11ef5, 0x11ef6, 0x11f03, 0x11f03, 0x11f34, 0x11f35, 0x11f3e, 0x11f3f, 0x11f41, 0x11f41, 0x1612a, 0x1612c,
        0x16f51, 0x16f87, 0x16ff0, 0x16ff1, 0x1d165, 0x1d166, 0x1d16d, 0x1d172,
    ];
    private static readonly int[] RgiSingleRanges = [
        0x231a, 0x231b, 0x23e9, 0x23ec, 0x23f0, 0x23f0, 0x23f3, 0x23f3, 0x25fd, 0x25fe, 0x2614, 0x2615, 0x2648, 0x2653, 0x267f, 0x267f, 0x2693, 0x2693,
        0x26a1, 0x26a1, 0x26aa, 0x26ab, 0x26bd, 0x26be, 0x26c4, 0x26c5, 0x26ce, 0x26ce, 0x26d4, 0x26d4, 0x26ea, 0x26ea, 0x26f2, 0x26f3, 0x26f5, 0x26f5,
        0x26fa, 0x26fa, 0x26fd, 0x26fd, 0x2705, 0x2705, 0x270a, 0x270b, 0x2728, 0x2728, 0x274c, 0x274c, 0x274e, 0x274e, 0x2753, 0x2755, 0x2757, 0x2757,
        0x2795, 0x2797, 0x27b0, 0x27b0, 0x27bf, 0x27bf, 0x2b1b, 0x2b1c, 0x2b50, 0x2b50, 0x2b55, 0x2b55, 0x1f004, 0x1f004, 0x1f0cf, 0x1f0cf, 0x1f18e, 0x1f18e,
        0x1f191, 0x1f19a, 0x1f201, 0x1f201, 0x1f21a, 0x1f21a, 0x1f22f, 0x1f22f, 0x1f232, 0x1f236, 0x1f238, 0x1f23a, 0x1f250, 0x1f251, 0x1f300, 0x1f320, 0x1f32d, 0x1f335,
        0x1f337, 0x1f37c, 0x1f37e, 0x1f393, 0x1f3a0, 0x1f3ca, 0x1f3cf, 0x1f3d3, 0x1f3e0, 0x1f3f0, 0x1f3f4, 0x1f3f4, 0x1f3f8, 0x1f43e, 0x1f440, 0x1f440, 0x1f442, 0x1f4fc,
        0x1f4ff, 0x1f53d, 0x1f54b, 0x1f54e, 0x1f550, 0x1f567, 0x1f57a, 0x1f57a, 0x1f595, 0x1f596, 0x1f5a4, 0x1f5a4, 0x1f5fb, 0x1f64f, 0x1f680, 0x1f6c5, 0x1f6cc, 0x1f6cc,
        0x1f6d0, 0x1f6d2, 0x1f6d5, 0x1f6d8, 0x1f6dc, 0x1f6df, 0x1f6eb, 0x1f6ec, 0x1f6f4, 0x1f6fc, 0x1f7e0, 0x1f7eb, 0x1f7f0, 0x1f7f0, 0x1f90c, 0x1f93a, 0x1f93c, 0x1f945,
        0x1f947, 0x1f9ff, 0x1fa70, 0x1fa7c, 0x1fa80, 0x1fa8a, 0x1fa8e, 0x1fac6, 0x1fac8, 0x1fac8, 0x1facd, 0x1fadc, 0x1fadf, 0x1faea, 0x1faef, 0x1faf8,
    ];
    private static readonly HashSet<int> RgiVs16 = [0xa9,0xae,0x203c,0x2049,0x2122,0x2139,0x2194,0x2195,0x2196,0x2197,0x2198,0x2199,0x21a9,0x21aa,0x2328,0x23cf,0x23ed,0x23ee,0x23ef,0x23f1,0x23f2,0x23f8,0x23f9,0x23fa,0x24c2,0x25aa,0x25ab,0x25b6,0x25c0,0x25fb,0x25fc,0x2600,0x2601,0x2602,0x2603,0x2604,0x260e,0x2611,0x2618,0x261d,0x2620,0x2622,0x2623,0x2626,0x262a,0x262e,0x262f,0x2638,0x2639,0x263a,0x2640,0x2642,0x265f,0x2660,0x2663,0x2665,0x2666,0x2668,0x267b,0x267e,0x2692,0x2694,0x2695,0x2696,0x2697,0x2699,0x269b,0x269c,0x26a0,0x26a7,0x26b0,0x26b1,0x26c8,0x26cf,0x26d1,0x26d3,0x26e9,0x26f0,0x26f1,0x26f4,0x26f7,0x26f8,0x26f9,0x2702,0x2708,0x2709,0x270c,0x270d,0x270f,0x2712,0x2714,0x2716,0x271d,0x2721,0x2733,0x2734,0x2744,0x2747,0x2763,0x2764,0x27a1,0x2934,0x2935,0x2b05,0x2b06,0x2b07,0x3030,0x303d,0x3297,0x3299,0x1f170,0x1f171,0x1f17e,0x1f17f,0x1f202,0x1f237,0x1f321,0x1f324,0x1f325,0x1f326,0x1f327,0x1f328,0x1f329,0x1f32a,0x1f32b,0x1f32c,0x1f336,0x1f37d,0x1f396,0x1f397,0x1f399,0x1f39a,0x1f39b,0x1f39e,0x1f39f,0x1f3cb,0x1f3cc,0x1f3cd,0x1f3ce,0x1f3d4,0x1f3d5,0x1f3d6,0x1f3d7,0x1f3d8,0x1f3d9,0x1f3da,0x1f3db,0x1f3dc,0x1f3dd,0x1f3de,0x1f3df,0x1f3f3,0x1f3f5,0x1f3f7,0x1f43f,0x1f441,0x1f4fd,0x1f549,0x1f54a,0x1f56f,0x1f570,0x1f573,0x1f574,0x1f575,0x1f576,0x1f577,0x1f578,0x1f579,0x1f587,0x1f58a,0x1f58b,0x1f58c,0x1f58d,0x1f590,0x1f5a5,0x1f5a8,0x1f5b1,0x1f5b2,0x1f5bc,0x1f5c2,0x1f5c3,0x1f5c4,0x1f5d1,0x1f5d2,0x1f5d3,0x1f5dc,0x1f5dd,0x1f5de,0x1f5e1,0x1f5e3,0x1f5e8,0x1f5ef,0x1f5f3,0x1f5fa,0x1f6cb,0x1f6cd,0x1f6ce,0x1f6cf,0x1f6e0,0x1f6e1,0x1f6e2,0x1f6e3,0x1f6e4,0x1f6e5,0x1f6e9,0x1f6f0,0x1f6f3];
}

// Generated from the complete official Unicode 17 RGI sequence union; see tests width-data/emoji17.
// Unicode data license is retained in full. No input-dependent cache or alternate width profile.
internal static partial class TerminalEditorSourceWidth
{
    private static bool IsRgiEmoji(string value)
    {
        var runes = value.EnumerateRunes();
        if (!runes.MoveNext()) return false;
        var first = runes.Current.Value;
        if (!runes.MoveNext()) return In(first, RgiSingleRanges);
        var second = runes.Current.Value;
        if (second == 0xfe0f && !runes.MoveNext()) return RgiVs16.Contains(first);
        var offsets = RgiMultiOffsets; var data = RgiMultiData.AsSpan();
        var low = 0; var high = offsets.Length - 2;
        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            var comparison = value.AsSpan().SequenceCompareTo(data[offsets[mid]..offsets[mid + 1]]);
            if (comparison < 0) high = mid - 1;
            else if (comparison > 0) low = mid + 1;
            else return true;
        }
        return false;
    }

    private static ReadOnlySpan<int> RgiMultiOffsets => [
        0, 3, 6, 9, 12, 15, 18, 21, 24, 27, 30, 33, 36, 39, 42, 45,
        48, 51, 56, 59, 65, 71, 74, 80, 86, 89, 95, 101, 104, 110, 116, 119,
        125, 131, 136, 141, 144, 147, 150, 153, 156, 159, 162, 165, 168, 171, 174, 177,
        180, 183, 186, 189, 192, 195, 198, 201, 206, 211, 215, 219, 223, 227, 231, 235,
        239, 243, 247, 251, 255, 259, 263, 267, 271, 275, 279, 283, 287, 291, 295, 299,
        303, 307, 311, 315, 319, 323, 327, 331, 335, 339, 343, 347, 351, 355, 359, 363,
        367, 371, 375, 379, 383, 387, 391, 395, 399, 403, 407, 411, 415, 419, 423, 427,
        431, 435, 439, 443, 447, 451, 455, 459, 463, 467, 471, 475, 479, 483, 487, 491,
        495, 499, 503, 507, 511, 515, 519, 523, 527, 531, 535, 539, 543, 547, 551, 555,
        559, 563, 567, 571, 575, 579, 583, 587, 591, 595, 599, 603, 607, 611, 615, 619,
        623, 627, 631, 635, 639, 643, 647, 651, 655, 659, 663, 667, 671, 675, 679, 683,
        687, 691, 695, 699, 703, 707, 711, 715, 719, 723, 727, 731, 735, 739, 743, 747,
        751, 755, 759, 763, 767, 771, 775, 779, 783, 787, 791, 795, 799, 803, 807, 811,
        815, 819, 823, 827, 831, 835, 839, 843, 847, 851, 855, 859, 863, 867, 871, 875,
        879, 883, 887, 891, 895, 899, 903, 907, 911, 915, 919, 923, 927, 931, 935, 939,
        943, 947, 951, 955, 959, 963, 967, 971, 975, 979, 983, 987, 991, 995, 999, 1003,
        1007, 1011, 1015, 1019, 1023, 1027, 1031, 1035, 1039, 1043, 1047, 1051, 1055, 1059, 1063, 1067,
        1071, 1075, 1079, 1083, 1087, 1091, 1095, 1099, 1103, 1107, 1111, 1115, 1119, 1123, 1127, 1131,
        1135, 1139, 1143, 1147, 1151, 1155, 1159, 1163, 1167, 1171, 1175, 1179, 1183, 1187, 1191, 1195,
        1199, 1203, 1207, 1211, 1215, 1219, 1223, 1227, 1231, 1235, 1239, 1243, 1247, 1252, 1257, 1261,
        1265, 1269, 1273, 1277, 1281, 1285, 1289, 1293, 1297, 1302, 1310, 1315, 1323, 1328, 1332, 1339,
        1349, 1356, 1366, 1373, 1377, 1384, 1394, 1401, 1411, 1418, 1422, 1429, 1439, 1446, 1456, 1463,
        1467, 1474, 1484, 1491, 1501, 1508, 1512, 1519, 1529, 1536, 1546, 1553, 1558, 1563, 1567, 1574,
        1581, 1585, 1592, 1599, 1603, 1610, 1617, 1621, 1628, 1635, 1639, 1646, 1653, 1657, 1661, 1665,
        1669, 1673, 1678, 1683, 1687, 1694, 1701, 1705, 1712, 1719, 1723, 1730, 1737, 1741, 1748, 1755,
        1759, 1766, 1773, 1777, 1784, 1791, 1795, 1802, 1809, 1813, 1820, 1827, 1831, 1838, 1845, 1849,
        1856, 1863, 1869, 1875, 1879, 1886, 1893, 1897, 1904, 1911, 1915, 1922, 1929, 1933, 1940, 1947,
        1951, 1958, 1965, 1971, 1977, 1983, 1989, 1994, 2008, 2022, 2036, 2040, 2045, 2049, 2054, 2059,
        2066, 2070, 2074, 2078, 2082, 2086, 2090, 2094, 2098, 2102, 2106, 2110, 2114, 2118, 2122, 2126,
        2130, 2134, 2138, 2142, 2146, 2150, 2154, 2158, 2162, 2166, 2170, 2174, 2178, 2182, 2186, 2190,
        2194, 2198, 2202, 2206, 2210, 2214, 2218, 2222, 2226, 2230, 2234, 2238, 2242, 2246, 2250, 2254,
        2258, 2262, 2266, 2270, 2274, 2278, 2282, 2286, 2290, 2294, 2298, 2302, 2306, 2310, 2314, 2318,
        2322, 2326, 2330, 2334, 2338, 2342, 2346, 2350, 2354, 2358, 2362, 2366, 2371, 2376, 2381, 2389,
        2400, 2405, 2410, 2415, 2420, 2425, 2430, 2435, 2440, 2445, 2453, 2458, 2466, 2474, 2482, 2493,
        2501, 2512, 2523, 2531, 2542, 2550, 2561, 2572, 2577, 2582, 2587, 2592, 2597, 2602, 2607, 2615,
        2620, 2625, 2630, 2635, 2640, 2648, 2653, 2661, 2665, 2672, 2679, 2686, 2698, 2710, 2722, 2734,
        2746, 2761, 2776, 2791, 2806, 2821, 2828, 2835, 2842, 2849, 2856, 2863, 2870, 2877, 2889, 2901,
        2913, 2925, 2932, 2939, 2946, 2953, 2960, 2967, 2979, 2991, 3003, 3015, 3022, 3032, 3039, 3046,
        3053, 3060, 3067, 3077, 3084, 3094, 3106, 3118, 3130, 3142, 3146, 3153, 3160, 3167, 3179, 3191,
        3203, 3215, 3227, 3242, 3257, 3272, 3287, 3302, 3309, 3316, 3323, 3330, 3337, 3344, 3351, 3358,
        3370, 3382, 3394, 3406, 3413, 3420, 3427, 3434, 3441, 3448, 3460, 3472, 3484, 3496, 3503, 3513,
        3520, 3527, 3534, 3541, 3548, 3558, 3565, 3575, 3587, 3599, 3611, 3623, 3627, 3634, 3641, 3648,
        3660, 3672, 3684, 3696, 3708, 3723, 3738, 3753, 3768, 3783, 3790, 3797, 3804, 3811, 3818, 3825,
        3832, 3839, 3851, 3863, 3875, 3887, 3894, 3901, 3908, 3915, 3922, 3929, 3941, 3953, 3965, 3977,
        3984, 3994, 4001, 4008, 4015, 4022, 4029, 4039, 4046, 4056, 4068, 4080, 4092, 4104, 4108, 4115,
        4122, 4129, 4141, 4153, 4165, 4177, 4189, 4204, 4219, 4234, 4249, 4264, 4271, 4278, 4285, 4292,
        4299, 4306, 4313, 4320, 4332, 4344, 4356, 4368, 4375, 4382, 4389, 4396, 4403, 4410, 4422, 4434,
        4446, 4458, 4465, 4475, 4482, 4489, 4496, 4503, 4510, 4520, 4527, 4537, 4549, 4561, 4573, 4585,
        4589, 4596, 4603, 4610, 4622, 4634, 4646, 4658, 4670, 4685, 4700, 4715, 4730, 4745, 4752, 4759,
        4766, 4773, 4780, 4787, 4794, 4801, 4813, 4825, 4837, 4849, 4856, 4863, 4870, 4877, 4884, 4891,
        4903, 4915, 4927, 4939, 4946, 4956, 4963, 4970, 4977, 4984, 4991, 5001, 5008, 5018, 5030, 5042,
        5054, 5066, 5071, 5076, 5081, 5089, 5097, 5108, 5119, 5124, 5129, 5134, 5139, 5144, 5149, 5154,
        5159, 5164, 5172, 5177, 5185, 5193, 5201, 5212, 5220, 5231, 5242, 5247, 5252, 5257, 5262, 5267,
        5272, 5277, 5285, 5290, 5295, 5300, 5305, 5310, 5318, 5323, 5331, 5335, 5342, 5349, 5356, 5368,
        5380, 5392, 5404, 5416, 5428, 5440, 5452, 5464, 5476, 5491, 5506, 5521, 5536, 5551, 5566, 5581,
        5596, 5611, 5626, 5633, 5640, 5647, 5654, 5661, 5668, 5675, 5682, 5694, 5706, 5718, 5730, 5737,
        5744, 5751, 5758, 5765, 5772, 5784, 5796, 5808, 5820, 5832, 5844, 5856, 5868, 5875, 5885, 5892,
        5899, 5906, 5913, 5920, 5930, 5937, 5947, 5959, 5971, 5983, 5995, 5999, 6006, 6013, 6020, 6032,
        6044, 6056, 6068, 6080, 6092, 6104, 6116, 6128, 6140, 6155, 6170, 6185, 6200, 6215, 6230, 6245,
        6260, 6275, 6290, 6297, 6304, 6311, 6318, 6325, 6332, 6339, 6346, 6358, 6370, 6382, 6394, 6401,
        6408, 6415, 6422, 6429, 6436, 6448, 6460, 6472, 6484, 6496, 6508, 6520, 6532, 6539, 6549, 6556,
        6563, 6570, 6577, 6584, 6594, 6601, 6611, 6623, 6635, 6647, 6659, 6663, 6670, 6677, 6684, 6696,
        6708, 6720, 6732, 6744, 6756, 6768, 6780, 6792, 6804, 6819, 6834, 6849, 6864, 6879, 6894, 6909,
        6924, 6939, 6954, 6961, 6968, 6975, 6982, 6989, 6996, 7003, 7010, 7022, 7034, 7046, 7058, 7065,
        7072, 7079, 7086, 7093, 7100, 7112, 7124, 7136, 7148, 7160, 7172, 7184, 7196, 7203, 7213, 7220,
        7227, 7234, 7241, 7248, 7258, 7265, 7275, 7287, 7299, 7311, 7323, 7327, 7334, 7341, 7348, 7360,
        7372, 7384, 7396, 7408, 7420, 7432, 7444, 7456, 7468, 7483, 7498, 7513, 7528, 7543, 7558, 7573,
        7588, 7603, 7618, 7625, 7632, 7639, 7646, 7653, 7660, 7667, 7674, 7686, 7698, 7710, 7722, 7729,
        7736, 7743, 7750, 7757, 7764, 7776, 7788, 7800, 7812, 7824, 7836, 7848, 7860, 7867, 7877, 7884,
        7891, 7898, 7905, 7912, 7922, 7929, 7939, 7951, 7963, 7975, 7987, 7991, 7998, 8005, 8012, 8024,
        8036, 8048, 8060, 8072, 8084, 8096, 8108, 8120, 8132, 8147, 8162, 8177, 8192, 8207, 8222, 8237,
        8252, 8267, 8282, 8289, 8296, 8303, 8310, 8317, 8324, 8331, 8338, 8350, 8362, 8374, 8386, 8393,
        8400, 8407, 8414, 8421, 8428, 8440, 8452, 8464, 8476, 8488, 8500, 8512, 8524, 8531, 8541, 8548,
        8555, 8562, 8569, 8576, 8586, 8593, 8603, 8615, 8627, 8639, 8651, 8655, 8659, 8663, 8667, 8671,
        8675, 8679, 8683, 8687, 8691, 8695, 8699, 8703, 8707, 8711, 8716, 8721, 8725, 8732, 8739, 8743,
        8750, 8757, 8761, 8768, 8775, 8779, 8786, 8793, 8797, 8804, 8811, 8816, 8821, 8825, 8832, 8839,
        8843, 8850, 8857, 8861, 8868, 8875, 8879, 8886, 8893, 8897, 8904, 8911, 8916, 8921, 8925, 8932,
        8939, 8943, 8950, 8957, 8961, 8968, 8975, 8979, 8986, 8993, 8997, 9004, 9011, 9016, 9021, 9025,
        9032, 9039, 9043, 9050, 9057, 9061, 9068, 9075, 9079, 9086, 9093, 9097, 9104, 9111, 9115, 9119,
        9123, 9127, 9131, 9136, 9141, 9145, 9152, 9159, 9163, 9170, 9177, 9181, 9188, 9195, 9199, 9206,
        9213, 9217, 9224, 9231, 9235, 9239, 9243, 9247, 9251, 9255, 9259, 9263, 9267, 9271, 9275, 9279,
        9283, 9287, 9291, 9296, 9301, 9305, 9312, 9319, 9323, 9330, 9337, 9341, 9348, 9355, 9359, 9366,
        9373, 9377, 9384, 9391, 9395, 9399, 9403, 9407, 9411, 9415, 9419, 9423, 9427, 9431, 9436, 9441,
        9445, 9452, 9459, 9463, 9470, 9477, 9481, 9488, 9495, 9499, 9506, 9513, 9517, 9524, 9531, 9536,
        9541, 9545, 9552, 9559, 9563, 9570, 9577, 9581, 9588, 9595, 9599, 9606, 9613, 9617, 9624, 9631,
        9635, 9639, 9643, 9647, 9651, 9655, 9659, 9663, 9667, 9671, 9676, 9681, 9685, 9692, 9699, 9703,
        9710, 9717, 9721, 9728, 9735, 9739, 9746, 9753, 9757, 9764, 9771, 9776, 9781, 9785, 9792, 9799,
        9803, 9810, 9817, 9821, 9828, 9835, 9839, 9846, 9853, 9857, 9864, 9871, 9875, 9879, 9883, 9887,
        9891, 9895, 9899, 9903, 9907, 9911, 9915, 9919, 9923, 9927, 9931, 9935, 9939, 9943, 9947, 9951,
        9955, 9962, 9969, 9973, 9980, 9987, 9991, 9998, 10005, 10009, 10016, 10023, 10027, 10034, 10041, 10047,
        10053, 10057, 10061, 10065, 10069, 10073, 10077, 10081, 10085, 10089, 10093, 10097, 10101, 10105, 10109, 10113,
        10117, 10121, 10125, 10129, 10133, 10138, 10143, 10149, 10154, 10159, 10164, 10169, 10173, 10180, 10187, 10191,
        10198, 10205, 10209, 10216, 10223, 10227, 10234, 10241, 10245, 10252, 10259, 10264, 10269, 10273, 10280, 10287,
        10291, 10298, 10305, 10309, 10316, 10323, 10327, 10334, 10341, 10345, 10352, 10359, 10364, 10369, 10373, 10380,
        10387, 10391, 10398, 10405, 10409, 10416, 10423, 10427, 10434, 10441, 10445, 10452, 10459, 10464, 10469, 10473,
        10480, 10487, 10491, 10498, 10505, 10509, 10516, 10523, 10527, 10534, 10541, 10545, 10552, 10559, 10563, 10567,
        10571, 10575, 10579, 10584, 10589, 10593, 10600, 10607, 10611, 10618, 10625, 10629, 10636, 10643, 10647, 10654,
        10661, 10665, 10672, 10679, 10684, 10689, 10693, 10700, 10707, 10711, 10718, 10725, 10729, 10736, 10743, 10747,
        10754, 10761, 10765, 10772, 10779, 10783, 10787, 10791, 10795, 10799, 10804, 10809, 10813, 10820, 10827, 10831,
        10838, 10845, 10849, 10856, 10863, 10867, 10874, 10881, 10885, 10892, 10899, 10904, 10909, 10913, 10920, 10927,
        10931, 10938, 10945, 10949, 10956, 10963, 10967, 10974, 10981, 10985, 10992, 10999, 11004, 11009, 11013, 11020,
        11027, 11031, 11038, 11045, 11049, 11056, 11063, 11067, 11074, 11081, 11085, 11092, 11099, 11104, 11112, 11117,
        11125, 11130, 11134, 11141, 11151, 11158, 11168, 11175, 11179, 11186, 11196, 11203, 11213, 11220, 11224, 11231,
        11241, 11248, 11258, 11265, 11269, 11276, 11286, 11293, 11303, 11310, 11314, 11321, 11331, 11338, 11348, 11355,
        11359, 11363, 11367, 11371, 11375, 11379, 11383, 11387, 11391, 11395, 11399, 11403, 11407, 11411, 11415, 11419,
        11423, 11427, 11431, 11435, 11439, 11443, 11447, 11451, 11455, 11459, 11463, 11467, 11471, 11475, 11479, 11483,
        11487, 11491, 11495, 11499, 11503, 11507, 11511, 11515, 11519, 11523, 11527, 11531, 11535, 11539, 11543, 11547,
        11551, 11555, 11559, 11563, 11567, 11571, 11575, 11579, 11583, 11587, 11591, 11595, 11600, 11605, 11609, 11616,
        11623, 11627, 11634, 11641, 11645, 11652, 11659, 11663, 11670, 11677, 11681, 11688, 11695, 11699, 11703, 11707,
        11711, 11715, 11719, 11723, 11727, 11731, 11735, 11739, 11743, 11747, 11751, 11755, 11759, 11763, 11767, 11771,
        11775, 11779, 11783, 11787, 11791, 11795, 11800, 11805, 11809, 11816, 11823, 11827, 11834, 11841, 11845, 11852,
        11859, 11863, 11870, 11877, 11881, 11888, 11895, 11899, 11903, 11907, 11911, 11915, 11920, 11925, 11929, 11936,
        11943, 11947, 11954, 11961, 11965, 11972, 11979, 11983, 11990, 11997, 12001, 12008, 12015, 12020, 12025, 12029,
        12036, 12043, 12047, 12054, 12061, 12065, 12072, 12079, 12083, 12090, 12097, 12101, 12108, 12115, 12120, 12125,
        12129, 12136, 12143, 12147, 12154, 12161, 12165, 12172, 12179, 12183, 12190, 12197, 12201, 12208, 12215, 12220,
        12225, 12229, 12236, 12243, 12247, 12254, 12261, 12265, 12272, 12279, 12283, 12290, 12297, 12301, 12308, 12315,
        12320, 12325, 12329, 12336, 12343, 12347, 12354, 12361, 12365, 12372, 12379, 12383, 12390, 12397, 12401, 12408,
        12415, 12420, 12425, 12429, 12436, 12443, 12447, 12454, 12461, 12465, 12472, 12479, 12483, 12490, 12497, 12501,
        12508, 12515, 12519, 12523, 12527, 12531, 12535, 12539, 12543, 12547, 12551, 12555, 12559, 12563, 12567, 12571,
        12575, 12580, 12585, 12589, 12596, 12603, 12607, 12614, 12621, 12625, 12632, 12639, 12643, 12650, 12657, 12661,
        12668, 12675, 12680, 12685, 12689, 12696, 12703, 12707, 12714, 12721, 12725, 12732, 12739, 12743, 12750, 12757,
        12761, 12768, 12775, 12779, 12783, 12787, 12791, 12795, 12800, 12805, 12809, 12816, 12823, 12827, 12834, 12841,
        12845, 12852, 12859, 12863, 12870, 12877, 12881, 12888, 12895, 12900, 12908, 12913, 12921, 12926, 12930, 12937,
        12947, 12954, 12964, 12971, 12975, 12982, 12992, 12999, 13009, 13016, 13020, 13027, 13037, 13044, 13054, 13061,
        13065, 13072, 13082, 13089, 13099, 13106, 13110, 13117, 13127, 13134, 13144, 13151, 13156, 13161, 13165, 13172,
        13179, 13183, 13190, 13197, 13201, 13208, 13215, 13219, 13226, 13233, 13237, 13244, 13251, 13256, 13261, 13266,
        13271, 13276, 13281, 13286, 13291, 13296, 13301, 13306, 13311, 13316, 13321, 13326, 13331, 13336, 13341, 13349,
        13354, 13362, 13367, 13372, 13377, 13382, 13387, 13395, 13400, 13408, 13416, 13427, 13432, 13440, 13445, 13449,
        13456, 13463, 13470, 13485, 13500, 13515, 13530, 13542, 13554, 13566, 13578, 13585, 13592, 13599, 13606, 13613,
        13620, 13627, 13634, 13641, 13653, 13665, 13677, 13689, 13696, 13703, 13710, 13717, 13724, 13731, 13743, 13755,
        13767, 13779, 13791, 13798, 13808, 13815, 13822, 13829, 13836, 13843, 13853, 13860, 13870, 13877, 13889, 13901,
        13913, 13925, 13929, 13936, 13943, 13950, 13965, 13980, 13995, 14010, 14022, 14034, 14046, 14058, 14065, 14072,
        14079, 14086, 14093, 14100, 14107, 14114, 14121, 14133, 14145, 14157, 14169, 14176, 14183, 14190, 14197, 14204,
        14211, 14223, 14235, 14247, 14259, 14271, 14278, 14288, 14295, 14302, 14309, 14316, 14323, 14333, 14340, 14350,
        14357, 14369, 14381, 14393, 14405, 14409, 14416, 14423, 14430, 14445, 14460, 14475, 14490, 14502, 14514, 14526,
        14538, 14545, 14552, 14559, 14566, 14573, 14580, 14587, 14594, 14601, 14613, 14625, 14637, 14649, 14656, 14663,
        14670, 14677, 14684, 14691, 14703, 14715, 14727, 14739, 14751, 14758, 14768, 14775, 14782, 14789, 14796, 14803,
        14813, 14820, 14830, 14837, 14849, 14861, 14873, 14885, 14889, 14896, 14903, 14910, 14925, 14940, 14955, 14970,
        14982, 14994, 15006, 15018, 15025, 15032, 15039, 15046, 15053, 15060, 15067, 15074, 15081, 15093, 15105, 15117,
        15129, 15136, 15143, 15150, 15157, 15164, 15171, 15183, 15195, 15207, 15219, 15231, 15238, 15248, 15255, 15262,
        15269, 15276, 15283, 15293, 15300, 15310, 15317, 15329, 15341, 15353, 15365, 15369, 15376, 15383, 15390, 15405,
        15420, 15435, 15450, 15462, 15474, 15486, 15498, 15505, 15512, 15519, 15526, 15533, 15540, 15547, 15554, 15561,
        15573, 15585, 15597, 15609, 15616, 15623, 15630, 15637, 15644, 15651, 15663, 15675, 15687, 15699, 15711, 15718,
        15728, 15735, 15742, 15749, 15756, 15763, 15773, 15780, 15790, 15797, 15809, 15821, 15833, 15845, 15849, 15853,
        15857, 15861, 15865, 15869, 15873, 15877, 15881, 15885, 15890, 15895, 15899, 15906, 15913, 15917, 15924, 15931,
        15935, 15942, 15949, 15953, 15960, 15967, 15971, 15978, 15985, 15989, 15993, 15997, 16001, 16005, 16010, 16015,
        16019, 16026, 16033, 16037, 16044, 16051, 16055, 16062, 16069, 16073, 16080, 16087, 16091, 16098, 16105, 16110,
        16115, 16119, 16126, 16133, 16137, 16144, 16151, 16155, 16162, 16169, 16173, 16180, 16187, 16191, 16198, 16205,
        16210, 16215, 16219, 16226, 16233, 16237, 16244, 16251, 16255, 16262, 16269, 16273, 16280, 16287, 16291, 16298,
        16305, 16310, 16315, 16319, 16326, 16333, 16337, 16344, 16351, 16355, 16362, 16369, 16373, 16380, 16387, 16391,
        16398, 16405, 16410, 16415, 16419, 16426, 16433, 16437, 16444, 16451, 16455, 16462, 16469, 16473, 16480, 16487,
        16491, 16498, 16505, 16510, 16515, 16519, 16526, 16533, 16537, 16544, 16551, 16555, 16562, 16569, 16573, 16580,
        16587, 16591, 16598, 16605, 16610, 16615, 16619, 16626, 16633, 16637, 16644, 16651, 16655, 16662, 16669, 16673,
        16680, 16687, 16691, 16698, 16705, 16710, 16715, 16719, 16726, 16733, 16737, 16744, 16751, 16755, 16762, 16769,
        16773, 16780, 16787, 16791, 16798, 16805, 16810, 16815, 16820, 16825, 16829, 16833, 16837, 16841, 16845, 16849,
        16853, 16857, 16861, 16865, 16869, 16873, 16877, 16881, 16885, 16889, 16893, 16897, 16901, 16905, 16909, 16918,
        16927, 16936, 16945, 16949, 16958, 16967, 16976, 16985, 16989, 16998, 17007, 17016, 17025, 17029, 17038, 17047,
        17056, 17065, 17069, 17078, 17087, 17096, 17105, 17109, 17113, 17117, 17121, 17125, 17129, 17133, 17137, 17141,
        17145, 17149, 17153, 17157, 17161, 17165, 17169, 17173, 17177, 17181, 17185, 17189, 17193, 17197, 17201, 17205,
        17209, 17213, 17217, 17221, 17225, 17229, 17233, 17237, 17241, 17245,
    ];
    private static string RgiMultiData =>
        "\u0023\ufe0f\u20e3\u002a\ufe0f\u20e3\u0030\ufe0f\u20e3\u0031\ufe0f\u20e3\u0032\ufe0f\u20e3\u0033\ufe0f\u20e3\u0034\ufe0f\u20e3\u0035\ufe0f\u20e3\u0036\ufe0f\u20e3\u0037\ufe0f\u20e3\u0038\ufe0f\u20e3\u0039\ufe0f\u20e3\u261d\ud83c\udffb\u261d\ud83c\udffc\u261d\ud83c\udffd\u261d\ud83c\udffe\u261d\ud83c\udfff\u26d3\ufe0f\u200d\ud83d\udca5\u26f9\ud83c\udffb\u26f9\ud83c\udffb\u200d\u2640" +
        "\ufe0f\u26f9\ud83c\udffb\u200d\u2642\ufe0f\u26f9\ud83c\udffc\u26f9\ud83c\udffc\u200d\u2640\ufe0f\u26f9\ud83c\udffc\u200d\u2642\ufe0f\u26f9\ud83c\udffd\u26f9\ud83c\udffd\u200d\u2640\ufe0f\u26f9\ud83c\udffd\u200d\u2642\ufe0f\u26f9\ud83c\udffe\u26f9\ud83c\udffe\u200d\u2640\ufe0f\u26f9\ud83c\udffe\u200d\u2642\ufe0f\u26f9\ud83c\udfff\u26f9\ud83c\udfff\u200d\u2640\ufe0f\u26f9\ud83c\udfff" +
        "\u200d\u2642\ufe0f\u26f9\ufe0f\u200d\u2640\ufe0f\u26f9\ufe0f\u200d\u2642\ufe0f\u270a\ud83c\udffb\u270a\ud83c\udffc\u270a\ud83c\udffd\u270a\ud83c\udffe\u270a\ud83c\udfff\u270b\ud83c\udffb\u270b\ud83c\udffc\u270b\ud83c\udffd\u270b\ud83c\udffe\u270b\ud83c\udfff\u270c\ud83c\udffb\u270c\ud83c\udffc\u270c\ud83c\udffd\u270c\ud83c\udffe\u270c\ud83c\udfff\u270d\ud83c\udffb\u270d\ud83c\udffc" +
        "\u270d\ud83c\udffd\u270d\ud83c\udffe\u270d\ud83c\udfff\u2764\ufe0f\u200d\ud83d\udd25\u2764\ufe0f\u200d\ud83e\ude79\ud83c\udde6\ud83c\udde8\ud83c\udde6\ud83c\udde9\ud83c\udde6\ud83c\uddea\ud83c\udde6\ud83c\uddeb\ud83c\udde6\ud83c\uddec\ud83c\udde6\ud83c\uddee\ud83c\udde6\ud83c\uddf1\ud83c\udde6\ud83c\uddf2\ud83c\udde6\ud83c\uddf4\ud83c\udde6\ud83c\uddf6\ud83c\udde6\ud83c\uddf7\ud83c" +
        "\udde6\ud83c\uddf8\ud83c\udde6\ud83c\uddf9\ud83c\udde6\ud83c\uddfa\ud83c\udde6\ud83c\uddfc\ud83c\udde6\ud83c\uddfd\ud83c\udde6\ud83c\uddff\ud83c\udde7\ud83c\udde6\ud83c\udde7\ud83c\udde7\ud83c\udde7\ud83c\udde9\ud83c\udde7\ud83c\uddea\ud83c\udde7\ud83c\uddeb\ud83c\udde7\ud83c\uddec\ud83c\udde7\ud83c\udded\ud83c\udde7\ud83c\uddee\ud83c\udde7\ud83c\uddef\ud83c\udde7\ud83c\uddf1\ud83c" +
        "\udde7\ud83c\uddf2\ud83c\udde7\ud83c\uddf3\ud83c\udde7\ud83c\uddf4\ud83c\udde7\ud83c\uddf6\ud83c\udde7\ud83c\uddf7\ud83c\udde7\ud83c\uddf8\ud83c\udde7\ud83c\uddf9\ud83c\udde7\ud83c\uddfb\ud83c\udde7\ud83c\uddfc\ud83c\udde7\ud83c\uddfe\ud83c\udde7\ud83c\uddff\ud83c\udde8\ud83c\udde6\ud83c\udde8\ud83c\udde8\ud83c\udde8\ud83c\udde9\ud83c\udde8\ud83c\uddeb\ud83c\udde8\ud83c\uddec\ud83c" +
        "\udde8\ud83c\udded\ud83c\udde8\ud83c\uddee\ud83c\udde8\ud83c\uddf0\ud83c\udde8\ud83c\uddf1\ud83c\udde8\ud83c\uddf2\ud83c\udde8\ud83c\uddf3\ud83c\udde8\ud83c\uddf4\ud83c\udde8\ud83c\uddf5\ud83c\udde8\ud83c\uddf6\ud83c\udde8\ud83c\uddf7\ud83c\udde8\ud83c\uddfa\ud83c\udde8\ud83c\uddfb\ud83c\udde8\ud83c\uddfc\ud83c\udde8\ud83c\uddfd\ud83c\udde8\ud83c\uddfe\ud83c\udde8\ud83c\uddff\ud83c" +
        "\udde9\ud83c\uddea\ud83c\udde9\ud83c\uddec\ud83c\udde9\ud83c\uddef\ud83c\udde9\ud83c\uddf0\ud83c\udde9\ud83c\uddf2\ud83c\udde9\ud83c\uddf4\ud83c\udde9\ud83c\uddff\ud83c\uddea\ud83c\udde6\ud83c\uddea\ud83c\udde8\ud83c\uddea\ud83c\uddea\ud83c\uddea\ud83c\uddec\ud83c\uddea\ud83c\udded\ud83c\uddea\ud83c\uddf7\ud83c\uddea\ud83c\uddf8\ud83c\uddea\ud83c\uddf9\ud83c\uddea\ud83c\uddfa\ud83c" +
        "\uddeb\ud83c\uddee\ud83c\uddeb\ud83c\uddef\ud83c\uddeb\ud83c\uddf0\ud83c\uddeb\ud83c\uddf2\ud83c\uddeb\ud83c\uddf4\ud83c\uddeb\ud83c\uddf7\ud83c\uddec\ud83c\udde6\ud83c\uddec\ud83c\udde7\ud83c\uddec\ud83c\udde9\ud83c\uddec\ud83c\uddea\ud83c\uddec\ud83c\uddeb\ud83c\uddec\ud83c\uddec\ud83c\uddec\ud83c\udded\ud83c\uddec\ud83c\uddee\ud83c\uddec\ud83c\uddf1\ud83c\uddec\ud83c\uddf2\ud83c" +
        "\uddec\ud83c\uddf3\ud83c\uddec\ud83c\uddf5\ud83c\uddec\ud83c\uddf6\ud83c\uddec\ud83c\uddf7\ud83c\uddec\ud83c\uddf8\ud83c\uddec\ud83c\uddf9\ud83c\uddec\ud83c\uddfa\ud83c\uddec\ud83c\uddfc\ud83c\uddec\ud83c\uddfe\ud83c\udded\ud83c\uddf0\ud83c\udded\ud83c\uddf2\ud83c\udded\ud83c\uddf3\ud83c\udded\ud83c\uddf7\ud83c\udded\ud83c\uddf9\ud83c\udded\ud83c\uddfa\ud83c\uddee\ud83c\udde8\ud83c" +
        "\uddee\ud83c\udde9\ud83c\uddee\ud83c\uddea\ud83c\uddee\ud83c\uddf1\ud83c\uddee\ud83c\uddf2\ud83c\uddee\ud83c\uddf3\ud83c\uddee\ud83c\uddf4\ud83c\uddee\ud83c\uddf6\ud83c\uddee\ud83c\uddf7\ud83c\uddee\ud83c\uddf8\ud83c\uddee\ud83c\uddf9\ud83c\uddef\ud83c\uddea\ud83c\uddef\ud83c\uddf2\ud83c\uddef\ud83c\uddf4\ud83c\uddef\ud83c\uddf5\ud83c\uddf0\ud83c\uddea\ud83c\uddf0\ud83c\uddec\ud83c" +
        "\uddf0\ud83c\udded\ud83c\uddf0\ud83c\uddee\ud83c\uddf0\ud83c\uddf2\ud83c\uddf0\ud83c\uddf3\ud83c\uddf0\ud83c\uddf5\ud83c\uddf0\ud83c\uddf7\ud83c\uddf0\ud83c\uddfc\ud83c\uddf0\ud83c\uddfe\ud83c\uddf0\ud83c\uddff\ud83c\uddf1\ud83c\udde6\ud83c\uddf1\ud83c\udde7\ud83c\uddf1\ud83c\udde8\ud83c\uddf1\ud83c\uddee\ud83c\uddf1\ud83c\uddf0\ud83c\uddf1\ud83c\uddf7\ud83c\uddf1\ud83c\uddf8\ud83c" +
        "\uddf1\ud83c\uddf9\ud83c\uddf1\ud83c\uddfa\ud83c\uddf1\ud83c\uddfb\ud83c\uddf1\ud83c\uddfe\ud83c\uddf2\ud83c\udde6\ud83c\uddf2\ud83c\udde8\ud83c\uddf2\ud83c\udde9\ud83c\uddf2\ud83c\uddea\ud83c\uddf2\ud83c\uddeb\ud83c\uddf2\ud83c\uddec\ud83c\uddf2\ud83c\udded\ud83c\uddf2\ud83c\uddf0\ud83c\uddf2\ud83c\uddf1\ud83c\uddf2\ud83c\uddf2\ud83c\uddf2\ud83c\uddf3\ud83c\uddf2\ud83c\uddf4\ud83c" +
        "\uddf2\ud83c\uddf5\ud83c\uddf2\ud83c\uddf6\ud83c\uddf2\ud83c\uddf7\ud83c\uddf2\ud83c\uddf8\ud83c\uddf2\ud83c\uddf9\ud83c\uddf2\ud83c\uddfa\ud83c\uddf2\ud83c\uddfb\ud83c\uddf2\ud83c\uddfc\ud83c\uddf2\ud83c\uddfd\ud83c\uddf2\ud83c\uddfe\ud83c\uddf2\ud83c\uddff\ud83c\uddf3\ud83c\udde6\ud83c\uddf3\ud83c\udde8\ud83c\uddf3\ud83c\uddea\ud83c\uddf3\ud83c\uddeb\ud83c\uddf3\ud83c\uddec\ud83c" +
        "\uddf3\ud83c\uddee\ud83c\uddf3\ud83c\uddf1\ud83c\uddf3\ud83c\uddf4\ud83c\uddf3\ud83c\uddf5\ud83c\uddf3\ud83c\uddf7\ud83c\uddf3\ud83c\uddfa\ud83c\uddf3\ud83c\uddff\ud83c\uddf4\ud83c\uddf2\ud83c\uddf5\ud83c\udde6\ud83c\uddf5\ud83c\uddea\ud83c\uddf5\ud83c\uddeb\ud83c\uddf5\ud83c\uddec\ud83c\uddf5\ud83c\udded\ud83c\uddf5\ud83c\uddf0\ud83c\uddf5\ud83c\uddf1\ud83c\uddf5\ud83c\uddf2\ud83c" +
        "\uddf5\ud83c\uddf3\ud83c\uddf5\ud83c\uddf7\ud83c\uddf5\ud83c\uddf8\ud83c\uddf5\ud83c\uddf9\ud83c\uddf5\ud83c\uddfc\ud83c\uddf5\ud83c\uddfe\ud83c\uddf6\ud83c\udde6\ud83c\uddf7\ud83c\uddea\ud83c\uddf7\ud83c\uddf4\ud83c\uddf7\ud83c\uddf8\ud83c\uddf7\ud83c\uddfa\ud83c\uddf7\ud83c\uddfc\ud83c\uddf8\ud83c\udde6\ud83c\uddf8\ud83c\udde7\ud83c\uddf8\ud83c\udde8\ud83c\uddf8\ud83c\udde9\ud83c" +
        "\uddf8\ud83c\uddea\ud83c\uddf8\ud83c\uddec\ud83c\uddf8\ud83c\udded\ud83c\uddf8\ud83c\uddee\ud83c\uddf8\ud83c\uddef\ud83c\uddf8\ud83c\uddf0\ud83c\uddf8\ud83c\uddf1\ud83c\uddf8\ud83c\uddf2\ud83c\uddf8\ud83c\uddf3\ud83c\uddf8\ud83c\uddf4\ud83c\uddf8\ud83c\uddf7\ud83c\uddf8\ud83c\uddf8\ud83c\uddf8\ud83c\uddf9\ud83c\uddf8\ud83c\uddfb\ud83c\uddf8\ud83c\uddfd\ud83c\uddf8\ud83c\uddfe\ud83c" +
        "\uddf8\ud83c\uddff\ud83c\uddf9\ud83c\udde6\ud83c\uddf9\ud83c\udde8\ud83c\uddf9\ud83c\udde9\ud83c\uddf9\ud83c\uddeb\ud83c\uddf9\ud83c\uddec\ud83c\uddf9\ud83c\udded\ud83c\uddf9\ud83c\uddef\ud83c\uddf9\ud83c\uddf0\ud83c\uddf9\ud83c\uddf1\ud83c\uddf9\ud83c\uddf2\ud83c\uddf9\ud83c\uddf3\ud83c\uddf9\ud83c\uddf4\ud83c\uddf9\ud83c\uddf7\ud83c\uddf9\ud83c\uddf9\ud83c\uddf9\ud83c\uddfb\ud83c" +
        "\uddf9\ud83c\uddfc\ud83c\uddf9\ud83c\uddff\ud83c\uddfa\ud83c\udde6\ud83c\uddfa\ud83c\uddec\ud83c\uddfa\ud83c\uddf2\ud83c\uddfa\ud83c\uddf3\ud83c\uddfa\ud83c\uddf8\ud83c\uddfa\ud83c\uddfe\ud83c\uddfa\ud83c\uddff\ud83c\uddfb\ud83c\udde6\ud83c\uddfb\ud83c\udde8\ud83c\uddfb\ud83c\uddea\ud83c\uddfb\ud83c\uddec\ud83c\uddfb\ud83c\uddee\ud83c\uddfb\ud83c\uddf3\ud83c\uddfb\ud83c\uddfa\ud83c" +
        "\uddfc\ud83c\uddeb\ud83c\uddfc\ud83c\uddf8\ud83c\uddfd\ud83c\uddf0\ud83c\uddfe\ud83c\uddea\ud83c\uddfe\ud83c\uddf9\ud83c\uddff\ud83c\udde6\ud83c\uddff\ud83c\uddf2\ud83c\uddff\ud83c\uddfc\ud83c\udf44\u200d\ud83d\udfeb\ud83c\udf4b\u200d\ud83d\udfe9\ud83c\udf85\ud83c\udffb\ud83c\udf85\ud83c\udffc\ud83c\udf85\ud83c\udffd\ud83c\udf85\ud83c\udffe\ud83c\udf85\ud83c\udfff\ud83c\udfc2\ud83c" +
        "\udffb\ud83c\udfc2\ud83c\udffc\ud83c\udfc2\ud83c\udffd\ud83c\udfc2\ud83c\udffe\ud83c\udfc2\ud83c\udfff\ud83c\udfc3\u200d\u2640\ufe0f\ud83c\udfc3\u200d\u2640\ufe0f\u200d\u27a1\ufe0f\ud83c\udfc3\u200d\u2642\ufe0f\ud83c\udfc3\u200d\u2642\ufe0f\u200d\u27a1\ufe0f\ud83c\udfc3\u200d\u27a1\ufe0f\ud83c\udfc3\ud83c\udffb\ud83c\udfc3\ud83c\udffb\u200d\u2640\ufe0f\ud83c\udfc3\ud83c\udffb\u200d" +
        "\u2640\ufe0f\u200d\u27a1\ufe0f\ud83c\udfc3\ud83c\udffb\u200d\u2642\ufe0f\ud83c\udfc3\ud83c\udffb\u200d\u2642\ufe0f\u200d\u27a1\ufe0f\ud83c\udfc3\ud83c\udffb\u200d\u27a1\ufe0f\ud83c\udfc3\ud83c\udffc\ud83c\udfc3\ud83c\udffc\u200d\u2640\ufe0f\ud83c\udfc3\ud83c\udffc\u200d\u2640\ufe0f\u200d\u27a1\ufe0f\ud83c\udfc3\ud83c\udffc\u200d\u2642\ufe0f\ud83c\udfc3\ud83c\udffc\u200d\u2642\ufe0f" +
        "\u200d\u27a1\ufe0f\ud83c\udfc3\ud83c\udffc\u200d\u27a1\ufe0f\ud83c\udfc3\ud83c\udffd\ud83c\udfc3\ud83c\udffd\u200d\u2640\ufe0f\ud83c\udfc3\ud83c\udffd\u200d\u2640\ufe0f\u200d\u27a1\ufe0f\ud83c\udfc3\ud83c\udffd\u200d\u2642\ufe0f\ud83c\udfc3\ud83c\udffd\u200d\u2642\ufe0f\u200d\u27a1\ufe0f\ud83c\udfc3\ud83c\udffd\u200d\u27a1\ufe0f\ud83c\udfc3\ud83c\udffe\ud83c\udfc3\ud83c\udffe\u200d" +
        "\u2640\ufe0f\ud83c\udfc3\ud83c\udffe\u200d\u2640\ufe0f\u200d\u27a1\ufe0f\ud83c\udfc3\ud83c\udffe\u200d\u2642\ufe0f\ud83c\udfc3\ud83c\udffe\u200d\u2642\ufe0f\u200d\u27a1\ufe0f\ud83c\udfc3\ud83c\udffe\u200d\u27a1\ufe0f\ud83c\udfc3\ud83c\udfff\ud83c\udfc3\ud83c\udfff\u200d\u2640\ufe0f\ud83c\udfc3\ud83c\udfff\u200d\u2640\ufe0f\u200d\u27a1\ufe0f\ud83c\udfc3\ud83c\udfff\u200d\u2642\ufe0f" +
        "\ud83c\udfc3\ud83c\udfff\u200d\u2642\ufe0f\u200d\u27a1\ufe0f\ud83c\udfc3\ud83c\udfff\u200d\u27a1\ufe0f\ud83c\udfc4\u200d\u2640\ufe0f\ud83c\udfc4\u200d\u2642\ufe0f\ud83c\udfc4\ud83c\udffb\ud83c\udfc4\ud83c\udffb\u200d\u2640\ufe0f\ud83c\udfc4\ud83c\udffb\u200d\u2642\ufe0f\ud83c\udfc4\ud83c\udffc\ud83c\udfc4\ud83c\udffc\u200d\u2640\ufe0f\ud83c\udfc4\ud83c\udffc\u200d\u2642\ufe0f\ud83c" +
        "\udfc4\ud83c\udffd\ud83c\udfc4\ud83c\udffd\u200d\u2640\ufe0f\ud83c\udfc4\ud83c\udffd\u200d\u2642\ufe0f\ud83c\udfc4\ud83c\udffe\ud83c\udfc4\ud83c\udffe\u200d\u2640\ufe0f\ud83c\udfc4\ud83c\udffe\u200d\u2642\ufe0f\ud83c\udfc4\ud83c\udfff\ud83c\udfc4\ud83c\udfff\u200d\u2640\ufe0f\ud83c\udfc4\ud83c\udfff\u200d\u2642\ufe0f\ud83c\udfc7\ud83c\udffb\ud83c\udfc7\ud83c\udffc\ud83c\udfc7\ud83c" +
        "\udffd\ud83c\udfc7\ud83c\udffe\ud83c\udfc7\ud83c\udfff\ud83c\udfca\u200d\u2640\ufe0f\ud83c\udfca\u200d\u2642\ufe0f\ud83c\udfca\ud83c\udffb\ud83c\udfca\ud83c\udffb\u200d\u2640\ufe0f\ud83c\udfca\ud83c\udffb\u200d\u2642\ufe0f\ud83c\udfca\ud83c\udffc\ud83c\udfca\ud83c\udffc\u200d\u2640\ufe0f\ud83c\udfca\ud83c\udffc\u200d\u2642\ufe0f\ud83c\udfca\ud83c\udffd\ud83c\udfca\ud83c\udffd\u200d" +
        "\u2640\ufe0f\ud83c\udfca\ud83c\udffd\u200d\u2642\ufe0f\ud83c\udfca\ud83c\udffe\ud83c\udfca\ud83c\udffe\u200d\u2640\ufe0f\ud83c\udfca\ud83c\udffe\u200d\u2642\ufe0f\ud83c\udfca\ud83c\udfff\ud83c\udfca\ud83c\udfff\u200d\u2640\ufe0f\ud83c\udfca\ud83c\udfff\u200d\u2642\ufe0f\ud83c\udfcb\ud83c\udffb\ud83c\udfcb\ud83c\udffb\u200d\u2640\ufe0f\ud83c\udfcb\ud83c\udffb\u200d\u2642\ufe0f\ud83c" +
        "\udfcb\ud83c\udffc\ud83c\udfcb\ud83c\udffc\u200d\u2640\ufe0f\ud83c\udfcb\ud83c\udffc\u200d\u2642\ufe0f\ud83c\udfcb\ud83c\udffd\ud83c\udfcb\ud83c\udffd\u200d\u2640\ufe0f\ud83c\udfcb\ud83c\udffd\u200d\u2642\ufe0f\ud83c\udfcb\ud83c\udffe\ud83c\udfcb\ud83c\udffe\u200d\u2640\ufe0f\ud83c\udfcb\ud83c\udffe\u200d\u2642\ufe0f\ud83c\udfcb\ud83c\udfff\ud83c\udfcb\ud83c\udfff\u200d\u2640\ufe0f" +
        "\ud83c\udfcb\ud83c\udfff\u200d\u2642\ufe0f\ud83c\udfcb\ufe0f\u200d\u2640\ufe0f\ud83c\udfcb\ufe0f\u200d\u2642\ufe0f\ud83c\udfcc\ud83c\udffb\ud83c\udfcc\ud83c\udffb\u200d\u2640\ufe0f\ud83c\udfcc\ud83c\udffb\u200d\u2642\ufe0f\ud83c\udfcc\ud83c\udffc\ud83c\udfcc\ud83c\udffc\u200d\u2640\ufe0f\ud83c\udfcc\ud83c\udffc\u200d\u2642\ufe0f\ud83c\udfcc\ud83c\udffd\ud83c\udfcc\ud83c\udffd\u200d" +
        "\u2640\ufe0f\ud83c\udfcc\ud83c\udffd\u200d\u2642\ufe0f\ud83c\udfcc\ud83c\udffe\ud83c\udfcc\ud83c\udffe\u200d\u2640\ufe0f\ud83c\udfcc\ud83c\udffe\u200d\u2642\ufe0f\ud83c\udfcc\ud83c\udfff\ud83c\udfcc\ud83c\udfff\u200d\u2640\ufe0f\ud83c\udfcc\ud83c\udfff\u200d\u2642\ufe0f\ud83c\udfcc\ufe0f\u200d\u2640\ufe0f\ud83c\udfcc\ufe0f\u200d\u2642\ufe0f\ud83c\udff3\ufe0f\u200d\u26a7\ufe0f\ud83c" +
        "\udff3\ufe0f\u200d\ud83c\udf08\ud83c\udff4\u200d\u2620\ufe0f\ud83c\udff4\udb40\udc67\udb40\udc62\udb40\udc65\udb40\udc6e\udb40\udc67\udb40\udc7f\ud83c\udff4\udb40\udc67\udb40\udc62\udb40\udc73\udb40\udc63\udb40\udc74\udb40\udc7f\ud83c\udff4\udb40\udc67\udb40\udc62\udb40\udc77\udb40\udc6c\udb40\udc73\udb40\udc7f\ud83d\udc08\u200d\u2b1b\ud83d\udc15\u200d\ud83e\uddba\ud83d\udc26\u200d" +
        "\u2b1b\ud83d\udc26\u200d\ud83d\udd25\ud83d\udc3b\u200d\u2744\ufe0f\ud83d\udc41\ufe0f\u200d\ud83d\udde8\ufe0f\ud83d\udc42\ud83c\udffb\ud83d\udc42\ud83c\udffc\ud83d\udc42\ud83c\udffd\ud83d\udc42\ud83c\udffe\ud83d\udc42\ud83c\udfff\ud83d\udc43\ud83c\udffb\ud83d\udc43\ud83c\udffc\ud83d\udc43\ud83c\udffd\ud83d\udc43\ud83c\udffe\ud83d\udc43\ud83c\udfff\ud83d\udc46\ud83c\udffb\ud83d\udc46" +
        "\ud83c\udffc\ud83d\udc46\ud83c\udffd\ud83d\udc46\ud83c\udffe\ud83d\udc46\ud83c\udfff\ud83d\udc47\ud83c\udffb\ud83d\udc47\ud83c\udffc\ud83d\udc47\ud83c\udffd\ud83d\udc47\ud83c\udffe\ud83d\udc47\ud83c\udfff\ud83d\udc48\ud83c\udffb\ud83d\udc48\ud83c\udffc\ud83d\udc48\ud83c\udffd\ud83d\udc48\ud83c\udffe\ud83d\udc48\ud83c\udfff\ud83d\udc49\ud83c\udffb\ud83d\udc49\ud83c\udffc\ud83d\udc49" +
        "\ud83c\udffd\ud83d\udc49\ud83c\udffe\ud83d\udc49\ud83c\udfff\ud83d\udc4a\ud83c\udffb\ud83d\udc4a\ud83c\udffc\ud83d\udc4a\ud83c\udffd\ud83d\udc4a\ud83c\udffe\ud83d\udc4a\ud83c\udfff\ud83d\udc4b\ud83c\udffb\ud83d\udc4b\ud83c\udffc\ud83d\udc4b\ud83c\udffd\ud83d\udc4b\ud83c\udffe\ud83d\udc4b\ud83c\udfff\ud83d\udc4c\ud83c\udffb\ud83d\udc4c\ud83c\udffc\ud83d\udc4c\ud83c\udffd\ud83d\udc4c" +
        "\ud83c\udffe\ud83d\udc4c\ud83c\udfff\ud83d\udc4d\ud83c\udffb\ud83d\udc4d\ud83c\udffc\ud83d\udc4d\ud83c\udffd\ud83d\udc4d\ud83c\udffe\ud83d\udc4d\ud83c\udfff\ud83d\udc4e\ud83c\udffb\ud83d\udc4e\ud83c\udffc\ud83d\udc4e\ud83c\udffd\ud83d\udc4e\ud83c\udffe\ud83d\udc4e\ud83c\udfff\ud83d\udc4f\ud83c\udffb\ud83d\udc4f\ud83c\udffc\ud83d\udc4f\ud83c\udffd\ud83d\udc4f\ud83c\udffe\ud83d\udc4f" +
        "\ud83c\udfff\ud83d\udc50\ud83c\udffb\ud83d\udc50\ud83c\udffc\ud83d\udc50\ud83c\udffd\ud83d\udc50\ud83c\udffe\ud83d\udc50\ud83c\udfff\ud83d\udc66\ud83c\udffb\ud83d\udc66\ud83c\udffc\ud83d\udc66\ud83c\udffd\ud83d\udc66\ud83c\udffe\ud83d\udc66\ud83c\udfff\ud83d\udc67\ud83c\udffb\ud83d\udc67\ud83c\udffc\ud83d\udc67\ud83c\udffd\ud83d\udc67\ud83c\udffe\ud83d\udc67\ud83c\udfff\ud83d\udc68" +
        "\u200d\u2695\ufe0f\ud83d\udc68\u200d\u2696\ufe0f\ud83d\udc68\u200d\u2708\ufe0f\ud83d\udc68\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83d\udc68\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83d\udc68\u200d\ud83c\udf3e\ud83d\udc68\u200d\ud83c\udf73\ud83d\udc68\u200d\ud83c\udf7c\ud83d\udc68\u200d\ud83c\udf93\ud83d\udc68\u200d\ud83c\udfa4\ud83d\udc68\u200d\ud83c\udfa8\ud83d\udc68" +
        "\u200d\ud83c\udfeb\ud83d\udc68\u200d\ud83c\udfed\ud83d\udc68\u200d\ud83d\udc66\ud83d\udc68\u200d\ud83d\udc66\u200d\ud83d\udc66\ud83d\udc68\u200d\ud83d\udc67\ud83d\udc68\u200d\ud83d\udc67\u200d\ud83d\udc66\ud83d\udc68\u200d\ud83d\udc67\u200d\ud83d\udc67\ud83d\udc68\u200d\ud83d\udc68\u200d\ud83d\udc66\ud83d\udc68\u200d\ud83d\udc68\u200d\ud83d\udc66\u200d\ud83d\udc66\ud83d\udc68\u200d" +
        "\ud83d\udc68\u200d\ud83d\udc67\ud83d\udc68\u200d\ud83d\udc68\u200d\ud83d\udc67\u200d\ud83d\udc66\ud83d\udc68\u200d\ud83d\udc68\u200d\ud83d\udc67\u200d\ud83d\udc67\ud83d\udc68\u200d\ud83d\udc69\u200d\ud83d\udc66\ud83d\udc68\u200d\ud83d\udc69\u200d\ud83d\udc66\u200d\ud83d\udc66\ud83d\udc68\u200d\ud83d\udc69\u200d\ud83d\udc67\ud83d\udc68\u200d\ud83d\udc69\u200d\ud83d\udc67\u200d\ud83d" +
        "\udc66\ud83d\udc68\u200d\ud83d\udc69\u200d\ud83d\udc67\u200d\ud83d\udc67\ud83d\udc68\u200d\ud83d\udcbb\ud83d\udc68\u200d\ud83d\udcbc\ud83d\udc68\u200d\ud83d\udd27\ud83d\udc68\u200d\ud83d\udd2c\ud83d\udc68\u200d\ud83d\ude80\ud83d\udc68\u200d\ud83d\ude92\ud83d\udc68\u200d\ud83e\uddaf\ud83d\udc68\u200d\ud83e\uddaf\u200d\u27a1\ufe0f\ud83d\udc68\u200d\ud83e\uddb0\ud83d\udc68\u200d\ud83e" +
        "\uddb1\ud83d\udc68\u200d\ud83e\uddb2\ud83d\udc68\u200d\ud83e\uddb3\ud83d\udc68\u200d\ud83e\uddbc\ud83d\udc68\u200d\ud83e\uddbc\u200d\u27a1\ufe0f\ud83d\udc68\u200d\ud83e\uddbd\ud83d\udc68\u200d\ud83e\uddbd\u200d\u27a1\ufe0f\ud83d\udc68\ud83c\udffb\ud83d\udc68\ud83c\udffb\u200d\u2695\ufe0f\ud83d\udc68\ud83c\udffb\u200d\u2696\ufe0f\ud83d\udc68\ud83c\udffb\u200d\u2708\ufe0f\ud83d\udc68" +
        "\ud83c\udffb\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udffb\ud83d\udc68\ud83c\udffb\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udffc\ud83d\udc68\ud83c\udffb\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udffd\ud83d\udc68\ud83c\udffb\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udffe\ud83d\udc68\ud83c\udffb\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udfff\ud83d\udc68\ud83c\udffb\u200d\u2764" +
        "\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udffb\ud83d\udc68\ud83c\udffb\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udffc\ud83d\udc68\ud83c\udffb\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udffd\ud83d\udc68\ud83c\udffb\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udffe\ud83d\udc68\ud83c\udffb\u200d\u2764\ufe0f\u200d\ud83d\udc8b" +
        "\u200d\ud83d\udc68\ud83c\udfff\ud83d\udc68\ud83c\udffb\u200d\ud83c\udf3e\ud83d\udc68\ud83c\udffb\u200d\ud83c\udf73\ud83d\udc68\ud83c\udffb\u200d\ud83c\udf7c\ud83d\udc68\ud83c\udffb\u200d\ud83c\udf93\ud83d\udc68\ud83c\udffb\u200d\ud83c\udfa4\ud83d\udc68\ud83c\udffb\u200d\ud83c\udfa8\ud83d\udc68\ud83c\udffb\u200d\ud83c\udfeb\ud83d\udc68\ud83c\udffb\u200d\ud83c\udfed\ud83d\udc68\ud83c" +
        "\udffb\u200d\ud83d\udc30\u200d\ud83d\udc68\ud83c\udffc\ud83d\udc68\ud83c\udffb\u200d\ud83d\udc30\u200d\ud83d\udc68\ud83c\udffd\ud83d\udc68\ud83c\udffb\u200d\ud83d\udc30\u200d\ud83d\udc68\ud83c\udffe\ud83d\udc68\ud83c\udffb\u200d\ud83d\udc30\u200d\ud83d\udc68\ud83c\udfff\ud83d\udc68\ud83c\udffb\u200d\ud83d\udcbb\ud83d\udc68\ud83c\udffb\u200d\ud83d\udcbc\ud83d\udc68\ud83c\udffb\u200d" +
        "\ud83d\udd27\ud83d\udc68\ud83c\udffb\u200d\ud83d\udd2c\ud83d\udc68\ud83c\udffb\u200d\ud83d\ude80\ud83d\udc68\ud83c\udffb\u200d\ud83d\ude92\ud83d\udc68\ud83c\udffb\u200d\ud83e\udd1d\u200d\ud83d\udc68\ud83c\udffc\ud83d\udc68\ud83c\udffb\u200d\ud83e\udd1d\u200d\ud83d\udc68\ud83c\udffd\ud83d\udc68\ud83c\udffb\u200d\ud83e\udd1d\u200d\ud83d\udc68\ud83c\udffe\ud83d\udc68\ud83c\udffb\u200d" +
        "\ud83e\udd1d\u200d\ud83d\udc68\ud83c\udfff\ud83d\udc68\ud83c\udffb\u200d\ud83e\uddaf\ud83d\udc68\ud83c\udffb\u200d\ud83e\uddaf\u200d\u27a1\ufe0f\ud83d\udc68\ud83c\udffb\u200d\ud83e\uddb0\ud83d\udc68\ud83c\udffb\u200d\ud83e\uddb1\ud83d\udc68\ud83c\udffb\u200d\ud83e\uddb2\ud83d\udc68\ud83c\udffb\u200d\ud83e\uddb3\ud83d\udc68\ud83c\udffb\u200d\ud83e\uddbc\ud83d\udc68\ud83c\udffb\u200d" +
        "\ud83e\uddbc\u200d\u27a1\ufe0f\ud83d\udc68\ud83c\udffb\u200d\ud83e\uddbd\ud83d\udc68\ud83c\udffb\u200d\ud83e\uddbd\u200d\u27a1\ufe0f\ud83d\udc68\ud83c\udffb\u200d\ud83e\udeef\u200d\ud83d\udc68\ud83c\udffc\ud83d\udc68\ud83c\udffb\u200d\ud83e\udeef\u200d\ud83d\udc68\ud83c\udffd\ud83d\udc68\ud83c\udffb\u200d\ud83e\udeef\u200d\ud83d\udc68\ud83c\udffe\ud83d\udc68\ud83c\udffb\u200d\ud83e" +
        "\udeef\u200d\ud83d\udc68\ud83c\udfff\ud83d\udc68\ud83c\udffc\ud83d\udc68\ud83c\udffc\u200d\u2695\ufe0f\ud83d\udc68\ud83c\udffc\u200d\u2696\ufe0f\ud83d\udc68\ud83c\udffc\u200d\u2708\ufe0f\ud83d\udc68\ud83c\udffc\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udffb\ud83d\udc68\ud83c\udffc\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udffc\ud83d\udc68\ud83c\udffc\u200d\u2764\ufe0f\u200d\ud83d" +
        "\udc68\ud83c\udffd\ud83d\udc68\ud83c\udffc\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udffe\ud83d\udc68\ud83c\udffc\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udfff\ud83d\udc68\ud83c\udffc\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udffb\ud83d\udc68\ud83c\udffc\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udffc\ud83d\udc68\ud83c\udffc\u200d\u2764\ufe0f" +
        "\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udffd\ud83d\udc68\ud83c\udffc\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udffe\ud83d\udc68\ud83c\udffc\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udfff\ud83d\udc68\ud83c\udffc\u200d\ud83c\udf3e\ud83d\udc68\ud83c\udffc\u200d\ud83c\udf73\ud83d\udc68\ud83c\udffc\u200d\ud83c\udf7c\ud83d\udc68\ud83c\udffc\u200d" +
        "\ud83c\udf93\ud83d\udc68\ud83c\udffc\u200d\ud83c\udfa4\ud83d\udc68\ud83c\udffc\u200d\ud83c\udfa8\ud83d\udc68\ud83c\udffc\u200d\ud83c\udfeb\ud83d\udc68\ud83c\udffc\u200d\ud83c\udfed\ud83d\udc68\ud83c\udffc\u200d\ud83d\udc30\u200d\ud83d\udc68\ud83c\udffb\ud83d\udc68\ud83c\udffc\u200d\ud83d\udc30\u200d\ud83d\udc68\ud83c\udffd\ud83d\udc68\ud83c\udffc\u200d\ud83d\udc30\u200d\ud83d\udc68" +
        "\ud83c\udffe\ud83d\udc68\ud83c\udffc\u200d\ud83d\udc30\u200d\ud83d\udc68\ud83c\udfff\ud83d\udc68\ud83c\udffc\u200d\ud83d\udcbb\ud83d\udc68\ud83c\udffc\u200d\ud83d\udcbc\ud83d\udc68\ud83c\udffc\u200d\ud83d\udd27\ud83d\udc68\ud83c\udffc\u200d\ud83d\udd2c\ud83d\udc68\ud83c\udffc\u200d\ud83d\ude80\ud83d\udc68\ud83c\udffc\u200d\ud83d\ude92\ud83d\udc68\ud83c\udffc\u200d\ud83e\udd1d\u200d" +
        "\ud83d\udc68\ud83c\udffb\ud83d\udc68\ud83c\udffc\u200d\ud83e\udd1d\u200d\ud83d\udc68\ud83c\udffd\ud83d\udc68\ud83c\udffc\u200d\ud83e\udd1d\u200d\ud83d\udc68\ud83c\udffe\ud83d\udc68\ud83c\udffc\u200d\ud83e\udd1d\u200d\ud83d\udc68\ud83c\udfff\ud83d\udc68\ud83c\udffc\u200d\ud83e\uddaf\ud83d\udc68\ud83c\udffc\u200d\ud83e\uddaf\u200d\u27a1\ufe0f\ud83d\udc68\ud83c\udffc\u200d\ud83e\uddb0" +
        "\ud83d\udc68\ud83c\udffc\u200d\ud83e\uddb1\ud83d\udc68\ud83c\udffc\u200d\ud83e\uddb2\ud83d\udc68\ud83c\udffc\u200d\ud83e\uddb3\ud83d\udc68\ud83c\udffc\u200d\ud83e\uddbc\ud83d\udc68\ud83c\udffc\u200d\ud83e\uddbc\u200d\u27a1\ufe0f\ud83d\udc68\ud83c\udffc\u200d\ud83e\uddbd\ud83d\udc68\ud83c\udffc\u200d\ud83e\uddbd\u200d\u27a1\ufe0f\ud83d\udc68\ud83c\udffc\u200d\ud83e\udeef\u200d\ud83d" +
        "\udc68\ud83c\udffb\ud83d\udc68\ud83c\udffc\u200d\ud83e\udeef\u200d\ud83d\udc68\ud83c\udffd\ud83d\udc68\ud83c\udffc\u200d\ud83e\udeef\u200d\ud83d\udc68\ud83c\udffe\ud83d\udc68\ud83c\udffc\u200d\ud83e\udeef\u200d\ud83d\udc68\ud83c\udfff\ud83d\udc68\ud83c\udffd\ud83d\udc68\ud83c\udffd\u200d\u2695\ufe0f\ud83d\udc68\ud83c\udffd\u200d\u2696\ufe0f\ud83d\udc68\ud83c\udffd\u200d\u2708\ufe0f" +
        "\ud83d\udc68\ud83c\udffd\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udffb\ud83d\udc68\ud83c\udffd\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udffc\ud83d\udc68\ud83c\udffd\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udffd\ud83d\udc68\ud83c\udffd\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udffe\ud83d\udc68\ud83c\udffd\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udfff\ud83d\udc68\ud83c\udffd" +
        "\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udffb\ud83d\udc68\ud83c\udffd\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udffc\ud83d\udc68\ud83c\udffd\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udffd\ud83d\udc68\ud83c\udffd\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udffe\ud83d\udc68\ud83c\udffd\u200d\u2764\ufe0f\u200d" +
        "\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udfff\ud83d\udc68\ud83c\udffd\u200d\ud83c\udf3e\ud83d\udc68\ud83c\udffd\u200d\ud83c\udf73\ud83d\udc68\ud83c\udffd\u200d\ud83c\udf7c\ud83d\udc68\ud83c\udffd\u200d\ud83c\udf93\ud83d\udc68\ud83c\udffd\u200d\ud83c\udfa4\ud83d\udc68\ud83c\udffd\u200d\ud83c\udfa8\ud83d\udc68\ud83c\udffd\u200d\ud83c\udfeb\ud83d\udc68\ud83c\udffd\u200d\ud83c\udfed\ud83d" +
        "\udc68\ud83c\udffd\u200d\ud83d\udc30\u200d\ud83d\udc68\ud83c\udffb\ud83d\udc68\ud83c\udffd\u200d\ud83d\udc30\u200d\ud83d\udc68\ud83c\udffc\ud83d\udc68\ud83c\udffd\u200d\ud83d\udc30\u200d\ud83d\udc68\ud83c\udffe\ud83d\udc68\ud83c\udffd\u200d\ud83d\udc30\u200d\ud83d\udc68\ud83c\udfff\ud83d\udc68\ud83c\udffd\u200d\ud83d\udcbb\ud83d\udc68\ud83c\udffd\u200d\ud83d\udcbc\ud83d\udc68\ud83c" +
        "\udffd\u200d\ud83d\udd27\ud83d\udc68\ud83c\udffd\u200d\ud83d\udd2c\ud83d\udc68\ud83c\udffd\u200d\ud83d\ude80\ud83d\udc68\ud83c\udffd\u200d\ud83d\ude92\ud83d\udc68\ud83c\udffd\u200d\ud83e\udd1d\u200d\ud83d\udc68\ud83c\udffb\ud83d\udc68\ud83c\udffd\u200d\ud83e\udd1d\u200d\ud83d\udc68\ud83c\udffc\ud83d\udc68\ud83c\udffd\u200d\ud83e\udd1d\u200d\ud83d\udc68\ud83c\udffe\ud83d\udc68\ud83c" +
        "\udffd\u200d\ud83e\udd1d\u200d\ud83d\udc68\ud83c\udfff\ud83d\udc68\ud83c\udffd\u200d\ud83e\uddaf\ud83d\udc68\ud83c\udffd\u200d\ud83e\uddaf\u200d\u27a1\ufe0f\ud83d\udc68\ud83c\udffd\u200d\ud83e\uddb0\ud83d\udc68\ud83c\udffd\u200d\ud83e\uddb1\ud83d\udc68\ud83c\udffd\u200d\ud83e\uddb2\ud83d\udc68\ud83c\udffd\u200d\ud83e\uddb3\ud83d\udc68\ud83c\udffd\u200d\ud83e\uddbc\ud83d\udc68\ud83c" +
        "\udffd\u200d\ud83e\uddbc\u200d\u27a1\ufe0f\ud83d\udc68\ud83c\udffd\u200d\ud83e\uddbd\ud83d\udc68\ud83c\udffd\u200d\ud83e\uddbd\u200d\u27a1\ufe0f\ud83d\udc68\ud83c\udffd\u200d\ud83e\udeef\u200d\ud83d\udc68\ud83c\udffb\ud83d\udc68\ud83c\udffd\u200d\ud83e\udeef\u200d\ud83d\udc68\ud83c\udffc\ud83d\udc68\ud83c\udffd\u200d\ud83e\udeef\u200d\ud83d\udc68\ud83c\udffe\ud83d\udc68\ud83c\udffd" +
        "\u200d\ud83e\udeef\u200d\ud83d\udc68\ud83c\udfff\ud83d\udc68\ud83c\udffe\ud83d\udc68\ud83c\udffe\u200d\u2695\ufe0f\ud83d\udc68\ud83c\udffe\u200d\u2696\ufe0f\ud83d\udc68\ud83c\udffe\u200d\u2708\ufe0f\ud83d\udc68\ud83c\udffe\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udffb\ud83d\udc68\ud83c\udffe\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udffc\ud83d\udc68\ud83c\udffe\u200d\u2764\ufe0f" +
        "\u200d\ud83d\udc68\ud83c\udffd\ud83d\udc68\ud83c\udffe\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udffe\ud83d\udc68\ud83c\udffe\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udfff\ud83d\udc68\ud83c\udffe\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udffb\ud83d\udc68\ud83c\udffe\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udffc\ud83d\udc68\ud83c\udffe\u200d" +
        "\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udffd\ud83d\udc68\ud83c\udffe\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udffe\ud83d\udc68\ud83c\udffe\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udfff\ud83d\udc68\ud83c\udffe\u200d\ud83c\udf3e\ud83d\udc68\ud83c\udffe\u200d\ud83c\udf73\ud83d\udc68\ud83c\udffe\u200d\ud83c\udf7c\ud83d\udc68\ud83c" +
        "\udffe\u200d\ud83c\udf93\ud83d\udc68\ud83c\udffe\u200d\ud83c\udfa4\ud83d\udc68\ud83c\udffe\u200d\ud83c\udfa8\ud83d\udc68\ud83c\udffe\u200d\ud83c\udfeb\ud83d\udc68\ud83c\udffe\u200d\ud83c\udfed\ud83d\udc68\ud83c\udffe\u200d\ud83d\udc30\u200d\ud83d\udc68\ud83c\udffb\ud83d\udc68\ud83c\udffe\u200d\ud83d\udc30\u200d\ud83d\udc68\ud83c\udffc\ud83d\udc68\ud83c\udffe\u200d\ud83d\udc30\u200d" +
        "\ud83d\udc68\ud83c\udffd\ud83d\udc68\ud83c\udffe\u200d\ud83d\udc30\u200d\ud83d\udc68\ud83c\udfff\ud83d\udc68\ud83c\udffe\u200d\ud83d\udcbb\ud83d\udc68\ud83c\udffe\u200d\ud83d\udcbc\ud83d\udc68\ud83c\udffe\u200d\ud83d\udd27\ud83d\udc68\ud83c\udffe\u200d\ud83d\udd2c\ud83d\udc68\ud83c\udffe\u200d\ud83d\ude80\ud83d\udc68\ud83c\udffe\u200d\ud83d\ude92\ud83d\udc68\ud83c\udffe\u200d\ud83e" +
        "\udd1d\u200d\ud83d\udc68\ud83c\udffb\ud83d\udc68\ud83c\udffe\u200d\ud83e\udd1d\u200d\ud83d\udc68\ud83c\udffc\ud83d\udc68\ud83c\udffe\u200d\ud83e\udd1d\u200d\ud83d\udc68\ud83c\udffd\ud83d\udc68\ud83c\udffe\u200d\ud83e\udd1d\u200d\ud83d\udc68\ud83c\udfff\ud83d\udc68\ud83c\udffe\u200d\ud83e\uddaf\ud83d\udc68\ud83c\udffe\u200d\ud83e\uddaf\u200d\u27a1\ufe0f\ud83d\udc68\ud83c\udffe\u200d" +
        "\ud83e\uddb0\ud83d\udc68\ud83c\udffe\u200d\ud83e\uddb1\ud83d\udc68\ud83c\udffe\u200d\ud83e\uddb2\ud83d\udc68\ud83c\udffe\u200d\ud83e\uddb3\ud83d\udc68\ud83c\udffe\u200d\ud83e\uddbc\ud83d\udc68\ud83c\udffe\u200d\ud83e\uddbc\u200d\u27a1\ufe0f\ud83d\udc68\ud83c\udffe\u200d\ud83e\uddbd\ud83d\udc68\ud83c\udffe\u200d\ud83e\uddbd\u200d\u27a1\ufe0f\ud83d\udc68\ud83c\udffe\u200d\ud83e\udeef" +
        "\u200d\ud83d\udc68\ud83c\udffb\ud83d\udc68\ud83c\udffe\u200d\ud83e\udeef\u200d\ud83d\udc68\ud83c\udffc\ud83d\udc68\ud83c\udffe\u200d\ud83e\udeef\u200d\ud83d\udc68\ud83c\udffd\ud83d\udc68\ud83c\udffe\u200d\ud83e\udeef\u200d\ud83d\udc68\ud83c\udfff\ud83d\udc68\ud83c\udfff\ud83d\udc68\ud83c\udfff\u200d\u2695\ufe0f\ud83d\udc68\ud83c\udfff\u200d\u2696\ufe0f\ud83d\udc68\ud83c\udfff\u200d" +
        "\u2708\ufe0f\ud83d\udc68\ud83c\udfff\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udffb\ud83d\udc68\ud83c\udfff\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udffc\ud83d\udc68\ud83c\udfff\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udffd\ud83d\udc68\ud83c\udfff\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udffe\ud83d\udc68\ud83c\udfff\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udfff\ud83d\udc68" +
        "\ud83c\udfff\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udffb\ud83d\udc68\ud83c\udfff\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udffc\ud83d\udc68\ud83c\udfff\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udffd\ud83d\udc68\ud83c\udfff\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udffe\ud83d\udc68\ud83c\udfff\u200d\u2764" +
        "\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udfff\ud83d\udc68\ud83c\udfff\u200d\ud83c\udf3e\ud83d\udc68\ud83c\udfff\u200d\ud83c\udf73\ud83d\udc68\ud83c\udfff\u200d\ud83c\udf7c\ud83d\udc68\ud83c\udfff\u200d\ud83c\udf93\ud83d\udc68\ud83c\udfff\u200d\ud83c\udfa4\ud83d\udc68\ud83c\udfff\u200d\ud83c\udfa8\ud83d\udc68\ud83c\udfff\u200d\ud83c\udfeb\ud83d\udc68\ud83c\udfff\u200d\ud83c" +
        "\udfed\ud83d\udc68\ud83c\udfff\u200d\ud83d\udc30\u200d\ud83d\udc68\ud83c\udffb\ud83d\udc68\ud83c\udfff\u200d\ud83d\udc30\u200d\ud83d\udc68\ud83c\udffc\ud83d\udc68\ud83c\udfff\u200d\ud83d\udc30\u200d\ud83d\udc68\ud83c\udffd\ud83d\udc68\ud83c\udfff\u200d\ud83d\udc30\u200d\ud83d\udc68\ud83c\udffe\ud83d\udc68\ud83c\udfff\u200d\ud83d\udcbb\ud83d\udc68\ud83c\udfff\u200d\ud83d\udcbc\ud83d" +
        "\udc68\ud83c\udfff\u200d\ud83d\udd27\ud83d\udc68\ud83c\udfff\u200d\ud83d\udd2c\ud83d\udc68\ud83c\udfff\u200d\ud83d\ude80\ud83d\udc68\ud83c\udfff\u200d\ud83d\ude92\ud83d\udc68\ud83c\udfff\u200d\ud83e\udd1d\u200d\ud83d\udc68\ud83c\udffb\ud83d\udc68\ud83c\udfff\u200d\ud83e\udd1d\u200d\ud83d\udc68\ud83c\udffc\ud83d\udc68\ud83c\udfff\u200d\ud83e\udd1d\u200d\ud83d\udc68\ud83c\udffd\ud83d" +
        "\udc68\ud83c\udfff\u200d\ud83e\udd1d\u200d\ud83d\udc68\ud83c\udffe\ud83d\udc68\ud83c\udfff\u200d\ud83e\uddaf\ud83d\udc68\ud83c\udfff\u200d\ud83e\uddaf\u200d\u27a1\ufe0f\ud83d\udc68\ud83c\udfff\u200d\ud83e\uddb0\ud83d\udc68\ud83c\udfff\u200d\ud83e\uddb1\ud83d\udc68\ud83c\udfff\u200d\ud83e\uddb2\ud83d\udc68\ud83c\udfff\u200d\ud83e\uddb3\ud83d\udc68\ud83c\udfff\u200d\ud83e\uddbc\ud83d" +
        "\udc68\ud83c\udfff\u200d\ud83e\uddbc\u200d\u27a1\ufe0f\ud83d\udc68\ud83c\udfff\u200d\ud83e\uddbd\ud83d\udc68\ud83c\udfff\u200d\ud83e\uddbd\u200d\u27a1\ufe0f\ud83d\udc68\ud83c\udfff\u200d\ud83e\udeef\u200d\ud83d\udc68\ud83c\udffb\ud83d\udc68\ud83c\udfff\u200d\ud83e\udeef\u200d\ud83d\udc68\ud83c\udffc\ud83d\udc68\ud83c\udfff\u200d\ud83e\udeef\u200d\ud83d\udc68\ud83c\udffd\ud83d\udc68" +
        "\ud83c\udfff\u200d\ud83e\udeef\u200d\ud83d\udc68\ud83c\udffe\ud83d\udc69\u200d\u2695\ufe0f\ud83d\udc69\u200d\u2696\ufe0f\ud83d\udc69\u200d\u2708\ufe0f\ud83d\udc69\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83d\udc69\u200d\u2764\ufe0f\u200d\ud83d\udc69\ud83d\udc69\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83d\udc69\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc69\ud83d" +
        "\udc69\u200d\ud83c\udf3e\ud83d\udc69\u200d\ud83c\udf73\ud83d\udc69\u200d\ud83c\udf7c\ud83d\udc69\u200d\ud83c\udf93\ud83d\udc69\u200d\ud83c\udfa4\ud83d\udc69\u200d\ud83c\udfa8\ud83d\udc69\u200d\ud83c\udfeb\ud83d\udc69\u200d\ud83c\udfed\ud83d\udc69\u200d\ud83d\udc66\ud83d\udc69\u200d\ud83d\udc66\u200d\ud83d\udc66\ud83d\udc69\u200d\ud83d\udc67\ud83d\udc69\u200d\ud83d\udc67\u200d\ud83d" +
        "\udc66\ud83d\udc69\u200d\ud83d\udc67\u200d\ud83d\udc67\ud83d\udc69\u200d\ud83d\udc69\u200d\ud83d\udc66\ud83d\udc69\u200d\ud83d\udc69\u200d\ud83d\udc66\u200d\ud83d\udc66\ud83d\udc69\u200d\ud83d\udc69\u200d\ud83d\udc67\ud83d\udc69\u200d\ud83d\udc69\u200d\ud83d\udc67\u200d\ud83d\udc66\ud83d\udc69\u200d\ud83d\udc69\u200d\ud83d\udc67\u200d\ud83d\udc67\ud83d\udc69\u200d\ud83d\udcbb\ud83d" +
        "\udc69\u200d\ud83d\udcbc\ud83d\udc69\u200d\ud83d\udd27\ud83d\udc69\u200d\ud83d\udd2c\ud83d\udc69\u200d\ud83d\ude80\ud83d\udc69\u200d\ud83d\ude92\ud83d\udc69\u200d\ud83e\uddaf\ud83d\udc69\u200d\ud83e\uddaf\u200d\u27a1\ufe0f\ud83d\udc69\u200d\ud83e\uddb0\ud83d\udc69\u200d\ud83e\uddb1\ud83d\udc69\u200d\ud83e\uddb2\ud83d\udc69\u200d\ud83e\uddb3\ud83d\udc69\u200d\ud83e\uddbc\ud83d\udc69" +
        "\u200d\ud83e\uddbc\u200d\u27a1\ufe0f\ud83d\udc69\u200d\ud83e\uddbd\ud83d\udc69\u200d\ud83e\uddbd\u200d\u27a1\ufe0f\ud83d\udc69\ud83c\udffb\ud83d\udc69\ud83c\udffb\u200d\u2695\ufe0f\ud83d\udc69\ud83c\udffb\u200d\u2696\ufe0f\ud83d\udc69\ud83c\udffb\u200d\u2708\ufe0f\ud83d\udc69\ud83c\udffb\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udffb\ud83d\udc69\ud83c\udffb\u200d\u2764\ufe0f\u200d" +
        "\ud83d\udc68\ud83c\udffc\ud83d\udc69\ud83c\udffb\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udffd\ud83d\udc69\ud83c\udffb\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udffe\ud83d\udc69\ud83c\udffb\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udfff\ud83d\udc69\ud83c\udffb\u200d\u2764\ufe0f\u200d\ud83d\udc69\ud83c\udffb\ud83d\udc69\ud83c\udffb\u200d\u2764\ufe0f\u200d\ud83d\udc69\ud83c\udffc" +
        "\ud83d\udc69\ud83c\udffb\u200d\u2764\ufe0f\u200d\ud83d\udc69\ud83c\udffd\ud83d\udc69\ud83c\udffb\u200d\u2764\ufe0f\u200d\ud83d\udc69\ud83c\udffe\ud83d\udc69\ud83c\udffb\u200d\u2764\ufe0f\u200d\ud83d\udc69\ud83c\udfff\ud83d\udc69\ud83c\udffb\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udffb\ud83d\udc69\ud83c\udffb\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68" +
        "\ud83c\udffc\ud83d\udc69\ud83c\udffb\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udffd\ud83d\udc69\ud83c\udffb\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udffe\ud83d\udc69\ud83c\udffb\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udfff\ud83d\udc69\ud83c\udffb\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc69\ud83c\udffb\ud83d\udc69" +
        "\ud83c\udffb\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc69\ud83c\udffc\ud83d\udc69\ud83c\udffb\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc69\ud83c\udffd\ud83d\udc69\ud83c\udffb\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc69\ud83c\udffe\ud83d\udc69\ud83c\udffb\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc69\ud83c\udfff\ud83d\udc69\ud83c\udffb\u200d\ud83c" +
        "\udf3e\ud83d\udc69\ud83c\udffb\u200d\ud83c\udf73\ud83d\udc69\ud83c\udffb\u200d\ud83c\udf7c\ud83d\udc69\ud83c\udffb\u200d\ud83c\udf93\ud83d\udc69\ud83c\udffb\u200d\ud83c\udfa4\ud83d\udc69\ud83c\udffb\u200d\ud83c\udfa8\ud83d\udc69\ud83c\udffb\u200d\ud83c\udfeb\ud83d\udc69\ud83c\udffb\u200d\ud83c\udfed\ud83d\udc69\ud83c\udffb\u200d\ud83d\udc30\u200d\ud83d\udc69\ud83c\udffc\ud83d\udc69" +
        "\ud83c\udffb\u200d\ud83d\udc30\u200d\ud83d\udc69\ud83c\udffd\ud83d\udc69\ud83c\udffb\u200d\ud83d\udc30\u200d\ud83d\udc69\ud83c\udffe\ud83d\udc69\ud83c\udffb\u200d\ud83d\udc30\u200d\ud83d\udc69\ud83c\udfff\ud83d\udc69\ud83c\udffb\u200d\ud83d\udcbb\ud83d\udc69\ud83c\udffb\u200d\ud83d\udcbc\ud83d\udc69\ud83c\udffb\u200d\ud83d\udd27\ud83d\udc69\ud83c\udffb\u200d\ud83d\udd2c\ud83d\udc69" +
        "\ud83c\udffb\u200d\ud83d\ude80\ud83d\udc69\ud83c\udffb\u200d\ud83d\ude92\ud83d\udc69\ud83c\udffb\u200d\ud83e\udd1d\u200d\ud83d\udc68\ud83c\udffc\ud83d\udc69\ud83c\udffb\u200d\ud83e\udd1d\u200d\ud83d\udc68\ud83c\udffd\ud83d\udc69\ud83c\udffb\u200d\ud83e\udd1d\u200d\ud83d\udc68\ud83c\udffe\ud83d\udc69\ud83c\udffb\u200d\ud83e\udd1d\u200d\ud83d\udc68\ud83c\udfff\ud83d\udc69\ud83c\udffb" +
        "\u200d\ud83e\udd1d\u200d\ud83d\udc69\ud83c\udffc\ud83d\udc69\ud83c\udffb\u200d\ud83e\udd1d\u200d\ud83d\udc69\ud83c\udffd\ud83d\udc69\ud83c\udffb\u200d\ud83e\udd1d\u200d\ud83d\udc69\ud83c\udffe\ud83d\udc69\ud83c\udffb\u200d\ud83e\udd1d\u200d\ud83d\udc69\ud83c\udfff\ud83d\udc69\ud83c\udffb\u200d\ud83e\uddaf\ud83d\udc69\ud83c\udffb\u200d\ud83e\uddaf\u200d\u27a1\ufe0f\ud83d\udc69\ud83c" +
        "\udffb\u200d\ud83e\uddb0\ud83d\udc69\ud83c\udffb\u200d\ud83e\uddb1\ud83d\udc69\ud83c\udffb\u200d\ud83e\uddb2\ud83d\udc69\ud83c\udffb\u200d\ud83e\uddb3\ud83d\udc69\ud83c\udffb\u200d\ud83e\uddbc\ud83d\udc69\ud83c\udffb\u200d\ud83e\uddbc\u200d\u27a1\ufe0f\ud83d\udc69\ud83c\udffb\u200d\ud83e\uddbd\ud83d\udc69\ud83c\udffb\u200d\ud83e\uddbd\u200d\u27a1\ufe0f\ud83d\udc69\ud83c\udffb\u200d" +
        "\ud83e\udeef\u200d\ud83d\udc69\ud83c\udffc\ud83d\udc69\ud83c\udffb\u200d\ud83e\udeef\u200d\ud83d\udc69\ud83c\udffd\ud83d\udc69\ud83c\udffb\u200d\ud83e\udeef\u200d\ud83d\udc69\ud83c\udffe\ud83d\udc69\ud83c\udffb\u200d\ud83e\udeef\u200d\ud83d\udc69\ud83c\udfff\ud83d\udc69\ud83c\udffc\ud83d\udc69\ud83c\udffc\u200d\u2695\ufe0f\ud83d\udc69\ud83c\udffc\u200d\u2696\ufe0f\ud83d\udc69\ud83c" +
        "\udffc\u200d\u2708\ufe0f\ud83d\udc69\ud83c\udffc\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udffb\ud83d\udc69\ud83c\udffc\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udffc\ud83d\udc69\ud83c\udffc\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udffd\ud83d\udc69\ud83c\udffc\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udffe\ud83d\udc69\ud83c\udffc\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udfff" +
        "\ud83d\udc69\ud83c\udffc\u200d\u2764\ufe0f\u200d\ud83d\udc69\ud83c\udffb\ud83d\udc69\ud83c\udffc\u200d\u2764\ufe0f\u200d\ud83d\udc69\ud83c\udffc\ud83d\udc69\ud83c\udffc\u200d\u2764\ufe0f\u200d\ud83d\udc69\ud83c\udffd\ud83d\udc69\ud83c\udffc\u200d\u2764\ufe0f\u200d\ud83d\udc69\ud83c\udffe\ud83d\udc69\ud83c\udffc\u200d\u2764\ufe0f\u200d\ud83d\udc69\ud83c\udfff\ud83d\udc69\ud83c\udffc" +
        "\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udffb\ud83d\udc69\ud83c\udffc\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udffc\ud83d\udc69\ud83c\udffc\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udffd\ud83d\udc69\ud83c\udffc\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udffe\ud83d\udc69\ud83c\udffc\u200d\u2764\ufe0f\u200d" +
        "\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udfff\ud83d\udc69\ud83c\udffc\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc69\ud83c\udffb\ud83d\udc69\ud83c\udffc\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc69\ud83c\udffc\ud83d\udc69\ud83c\udffc\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc69\ud83c\udffd\ud83d\udc69\ud83c\udffc\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d" +
        "\udc69\ud83c\udffe\ud83d\udc69\ud83c\udffc\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc69\ud83c\udfff\ud83d\udc69\ud83c\udffc\u200d\ud83c\udf3e\ud83d\udc69\ud83c\udffc\u200d\ud83c\udf73\ud83d\udc69\ud83c\udffc\u200d\ud83c\udf7c\ud83d\udc69\ud83c\udffc\u200d\ud83c\udf93\ud83d\udc69\ud83c\udffc\u200d\ud83c\udfa4\ud83d\udc69\ud83c\udffc\u200d\ud83c\udfa8\ud83d\udc69\ud83c\udffc" +
        "\u200d\ud83c\udfeb\ud83d\udc69\ud83c\udffc\u200d\ud83c\udfed\ud83d\udc69\ud83c\udffc\u200d\ud83d\udc30\u200d\ud83d\udc69\ud83c\udffb\ud83d\udc69\ud83c\udffc\u200d\ud83d\udc30\u200d\ud83d\udc69\ud83c\udffd\ud83d\udc69\ud83c\udffc\u200d\ud83d\udc30\u200d\ud83d\udc69\ud83c\udffe\ud83d\udc69\ud83c\udffc\u200d\ud83d\udc30\u200d\ud83d\udc69\ud83c\udfff\ud83d\udc69\ud83c\udffc\u200d\ud83d" +
        "\udcbb\ud83d\udc69\ud83c\udffc\u200d\ud83d\udcbc\ud83d\udc69\ud83c\udffc\u200d\ud83d\udd27\ud83d\udc69\ud83c\udffc\u200d\ud83d\udd2c\ud83d\udc69\ud83c\udffc\u200d\ud83d\ude80\ud83d\udc69\ud83c\udffc\u200d\ud83d\ude92\ud83d\udc69\ud83c\udffc\u200d\ud83e\udd1d\u200d\ud83d\udc68\ud83c\udffb\ud83d\udc69\ud83c\udffc\u200d\ud83e\udd1d\u200d\ud83d\udc68\ud83c\udffd\ud83d\udc69\ud83c\udffc" +
        "\u200d\ud83e\udd1d\u200d\ud83d\udc68\ud83c\udffe\ud83d\udc69\ud83c\udffc\u200d\ud83e\udd1d\u200d\ud83d\udc68\ud83c\udfff\ud83d\udc69\ud83c\udffc\u200d\ud83e\udd1d\u200d\ud83d\udc69\ud83c\udffb\ud83d\udc69\ud83c\udffc\u200d\ud83e\udd1d\u200d\ud83d\udc69\ud83c\udffd\ud83d\udc69\ud83c\udffc\u200d\ud83e\udd1d\u200d\ud83d\udc69\ud83c\udffe\ud83d\udc69\ud83c\udffc\u200d\ud83e\udd1d\u200d" +
        "\ud83d\udc69\ud83c\udfff\ud83d\udc69\ud83c\udffc\u200d\ud83e\uddaf\ud83d\udc69\ud83c\udffc\u200d\ud83e\uddaf\u200d\u27a1\ufe0f\ud83d\udc69\ud83c\udffc\u200d\ud83e\uddb0\ud83d\udc69\ud83c\udffc\u200d\ud83e\uddb1\ud83d\udc69\ud83c\udffc\u200d\ud83e\uddb2\ud83d\udc69\ud83c\udffc\u200d\ud83e\uddb3\ud83d\udc69\ud83c\udffc\u200d\ud83e\uddbc\ud83d\udc69\ud83c\udffc\u200d\ud83e\uddbc\u200d" +
        "\u27a1\ufe0f\ud83d\udc69\ud83c\udffc\u200d\ud83e\uddbd\ud83d\udc69\ud83c\udffc\u200d\ud83e\uddbd\u200d\u27a1\ufe0f\ud83d\udc69\ud83c\udffc\u200d\ud83e\udeef\u200d\ud83d\udc69\ud83c\udffb\ud83d\udc69\ud83c\udffc\u200d\ud83e\udeef\u200d\ud83d\udc69\ud83c\udffd\ud83d\udc69\ud83c\udffc\u200d\ud83e\udeef\u200d\ud83d\udc69\ud83c\udffe\ud83d\udc69\ud83c\udffc\u200d\ud83e\udeef\u200d\ud83d" +
        "\udc69\ud83c\udfff\ud83d\udc69\ud83c\udffd\ud83d\udc69\ud83c\udffd\u200d\u2695\ufe0f\ud83d\udc69\ud83c\udffd\u200d\u2696\ufe0f\ud83d\udc69\ud83c\udffd\u200d\u2708\ufe0f\ud83d\udc69\ud83c\udffd\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udffb\ud83d\udc69\ud83c\udffd\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udffc\ud83d\udc69\ud83c\udffd\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udffd" +
        "\ud83d\udc69\ud83c\udffd\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udffe\ud83d\udc69\ud83c\udffd\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udfff\ud83d\udc69\ud83c\udffd\u200d\u2764\ufe0f\u200d\ud83d\udc69\ud83c\udffb\ud83d\udc69\ud83c\udffd\u200d\u2764\ufe0f\u200d\ud83d\udc69\ud83c\udffc\ud83d\udc69\ud83c\udffd\u200d\u2764\ufe0f\u200d\ud83d\udc69\ud83c\udffd\ud83d\udc69\ud83c\udffd" +
        "\u200d\u2764\ufe0f\u200d\ud83d\udc69\ud83c\udffe\ud83d\udc69\ud83c\udffd\u200d\u2764\ufe0f\u200d\ud83d\udc69\ud83c\udfff\ud83d\udc69\ud83c\udffd\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udffb\ud83d\udc69\ud83c\udffd\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udffc\ud83d\udc69\ud83c\udffd\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c" +
        "\udffd\ud83d\udc69\ud83c\udffd\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udffe\ud83d\udc69\ud83c\udffd\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udfff\ud83d\udc69\ud83c\udffd\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc69\ud83c\udffb\ud83d\udc69\ud83c\udffd\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc69\ud83c\udffc\ud83d\udc69\ud83c" +
        "\udffd\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc69\ud83c\udffd\ud83d\udc69\ud83c\udffd\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc69\ud83c\udffe\ud83d\udc69\ud83c\udffd\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc69\ud83c\udfff\ud83d\udc69\ud83c\udffd\u200d\ud83c\udf3e\ud83d\udc69\ud83c\udffd\u200d\ud83c\udf73\ud83d\udc69\ud83c\udffd\u200d\ud83c\udf7c\ud83d" +
        "\udc69\ud83c\udffd\u200d\ud83c\udf93\ud83d\udc69\ud83c\udffd\u200d\ud83c\udfa4\ud83d\udc69\ud83c\udffd\u200d\ud83c\udfa8\ud83d\udc69\ud83c\udffd\u200d\ud83c\udfeb\ud83d\udc69\ud83c\udffd\u200d\ud83c\udfed\ud83d\udc69\ud83c\udffd\u200d\ud83d\udc30\u200d\ud83d\udc69\ud83c\udffb\ud83d\udc69\ud83c\udffd\u200d\ud83d\udc30\u200d\ud83d\udc69\ud83c\udffc\ud83d\udc69\ud83c\udffd\u200d\ud83d" +
        "\udc30\u200d\ud83d\udc69\ud83c\udffe\ud83d\udc69\ud83c\udffd\u200d\ud83d\udc30\u200d\ud83d\udc69\ud83c\udfff\ud83d\udc69\ud83c\udffd\u200d\ud83d\udcbb\ud83d\udc69\ud83c\udffd\u200d\ud83d\udcbc\ud83d\udc69\ud83c\udffd\u200d\ud83d\udd27\ud83d\udc69\ud83c\udffd\u200d\ud83d\udd2c\ud83d\udc69\ud83c\udffd\u200d\ud83d\ude80\ud83d\udc69\ud83c\udffd\u200d\ud83d\ude92\ud83d\udc69\ud83c\udffd" +
        "\u200d\ud83e\udd1d\u200d\ud83d\udc68\ud83c\udffb\ud83d\udc69\ud83c\udffd\u200d\ud83e\udd1d\u200d\ud83d\udc68\ud83c\udffc\ud83d\udc69\ud83c\udffd\u200d\ud83e\udd1d\u200d\ud83d\udc68\ud83c\udffe\ud83d\udc69\ud83c\udffd\u200d\ud83e\udd1d\u200d\ud83d\udc68\ud83c\udfff\ud83d\udc69\ud83c\udffd\u200d\ud83e\udd1d\u200d\ud83d\udc69\ud83c\udffb\ud83d\udc69\ud83c\udffd\u200d\ud83e\udd1d\u200d" +
        "\ud83d\udc69\ud83c\udffc\ud83d\udc69\ud83c\udffd\u200d\ud83e\udd1d\u200d\ud83d\udc69\ud83c\udffe\ud83d\udc69\ud83c\udffd\u200d\ud83e\udd1d\u200d\ud83d\udc69\ud83c\udfff\ud83d\udc69\ud83c\udffd\u200d\ud83e\uddaf\ud83d\udc69\ud83c\udffd\u200d\ud83e\uddaf\u200d\u27a1\ufe0f\ud83d\udc69\ud83c\udffd\u200d\ud83e\uddb0\ud83d\udc69\ud83c\udffd\u200d\ud83e\uddb1\ud83d\udc69\ud83c\udffd\u200d" +
        "\ud83e\uddb2\ud83d\udc69\ud83c\udffd\u200d\ud83e\uddb3\ud83d\udc69\ud83c\udffd\u200d\ud83e\uddbc\ud83d\udc69\ud83c\udffd\u200d\ud83e\uddbc\u200d\u27a1\ufe0f\ud83d\udc69\ud83c\udffd\u200d\ud83e\uddbd\ud83d\udc69\ud83c\udffd\u200d\ud83e\uddbd\u200d\u27a1\ufe0f\ud83d\udc69\ud83c\udffd\u200d\ud83e\udeef\u200d\ud83d\udc69\ud83c\udffb\ud83d\udc69\ud83c\udffd\u200d\ud83e\udeef\u200d\ud83d" +
        "\udc69\ud83c\udffc\ud83d\udc69\ud83c\udffd\u200d\ud83e\udeef\u200d\ud83d\udc69\ud83c\udffe\ud83d\udc69\ud83c\udffd\u200d\ud83e\udeef\u200d\ud83d\udc69\ud83c\udfff\ud83d\udc69\ud83c\udffe\ud83d\udc69\ud83c\udffe\u200d\u2695\ufe0f\ud83d\udc69\ud83c\udffe\u200d\u2696\ufe0f\ud83d\udc69\ud83c\udffe\u200d\u2708\ufe0f\ud83d\udc69\ud83c\udffe\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udffb" +
        "\ud83d\udc69\ud83c\udffe\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udffc\ud83d\udc69\ud83c\udffe\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udffd\ud83d\udc69\ud83c\udffe\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udffe\ud83d\udc69\ud83c\udffe\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udfff\ud83d\udc69\ud83c\udffe\u200d\u2764\ufe0f\u200d\ud83d\udc69\ud83c\udffb\ud83d\udc69\ud83c\udffe" +
        "\u200d\u2764\ufe0f\u200d\ud83d\udc69\ud83c\udffc\ud83d\udc69\ud83c\udffe\u200d\u2764\ufe0f\u200d\ud83d\udc69\ud83c\udffd\ud83d\udc69\ud83c\udffe\u200d\u2764\ufe0f\u200d\ud83d\udc69\ud83c\udffe\ud83d\udc69\ud83c\udffe\u200d\u2764\ufe0f\u200d\ud83d\udc69\ud83c\udfff\ud83d\udc69\ud83c\udffe\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udffb\ud83d\udc69\ud83c\udffe\u200d" +
        "\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udffc\ud83d\udc69\ud83c\udffe\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udffd\ud83d\udc69\ud83c\udffe\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udffe\ud83d\udc69\ud83c\udffe\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udfff\ud83d\udc69\ud83c\udffe\u200d\u2764\ufe0f\u200d\ud83d" +
        "\udc8b\u200d\ud83d\udc69\ud83c\udffb\ud83d\udc69\ud83c\udffe\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc69\ud83c\udffc\ud83d\udc69\ud83c\udffe\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc69\ud83c\udffd\ud83d\udc69\ud83c\udffe\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc69\ud83c\udffe\ud83d\udc69\ud83c\udffe\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc69" +
        "\ud83c\udfff\ud83d\udc69\ud83c\udffe\u200d\ud83c\udf3e\ud83d\udc69\ud83c\udffe\u200d\ud83c\udf73\ud83d\udc69\ud83c\udffe\u200d\ud83c\udf7c\ud83d\udc69\ud83c\udffe\u200d\ud83c\udf93\ud83d\udc69\ud83c\udffe\u200d\ud83c\udfa4\ud83d\udc69\ud83c\udffe\u200d\ud83c\udfa8\ud83d\udc69\ud83c\udffe\u200d\ud83c\udfeb\ud83d\udc69\ud83c\udffe\u200d\ud83c\udfed\ud83d\udc69\ud83c\udffe\u200d\ud83d" +
        "\udc30\u200d\ud83d\udc69\ud83c\udffb\ud83d\udc69\ud83c\udffe\u200d\ud83d\udc30\u200d\ud83d\udc69\ud83c\udffc\ud83d\udc69\ud83c\udffe\u200d\ud83d\udc30\u200d\ud83d\udc69\ud83c\udffd\ud83d\udc69\ud83c\udffe\u200d\ud83d\udc30\u200d\ud83d\udc69\ud83c\udfff\ud83d\udc69\ud83c\udffe\u200d\ud83d\udcbb\ud83d\udc69\ud83c\udffe\u200d\ud83d\udcbc\ud83d\udc69\ud83c\udffe\u200d\ud83d\udd27\ud83d" +
        "\udc69\ud83c\udffe\u200d\ud83d\udd2c\ud83d\udc69\ud83c\udffe\u200d\ud83d\ude80\ud83d\udc69\ud83c\udffe\u200d\ud83d\ude92\ud83d\udc69\ud83c\udffe\u200d\ud83e\udd1d\u200d\ud83d\udc68\ud83c\udffb\ud83d\udc69\ud83c\udffe\u200d\ud83e\udd1d\u200d\ud83d\udc68\ud83c\udffc\ud83d\udc69\ud83c\udffe\u200d\ud83e\udd1d\u200d\ud83d\udc68\ud83c\udffd\ud83d\udc69\ud83c\udffe\u200d\ud83e\udd1d\u200d" +
        "\ud83d\udc68\ud83c\udfff\ud83d\udc69\ud83c\udffe\u200d\ud83e\udd1d\u200d\ud83d\udc69\ud83c\udffb\ud83d\udc69\ud83c\udffe\u200d\ud83e\udd1d\u200d\ud83d\udc69\ud83c\udffc\ud83d\udc69\ud83c\udffe\u200d\ud83e\udd1d\u200d\ud83d\udc69\ud83c\udffd\ud83d\udc69\ud83c\udffe\u200d\ud83e\udd1d\u200d\ud83d\udc69\ud83c\udfff\ud83d\udc69\ud83c\udffe\u200d\ud83e\uddaf\ud83d\udc69\ud83c\udffe\u200d" +
        "\ud83e\uddaf\u200d\u27a1\ufe0f\ud83d\udc69\ud83c\udffe\u200d\ud83e\uddb0\ud83d\udc69\ud83c\udffe\u200d\ud83e\uddb1\ud83d\udc69\ud83c\udffe\u200d\ud83e\uddb2\ud83d\udc69\ud83c\udffe\u200d\ud83e\uddb3\ud83d\udc69\ud83c\udffe\u200d\ud83e\uddbc\ud83d\udc69\ud83c\udffe\u200d\ud83e\uddbc\u200d\u27a1\ufe0f\ud83d\udc69\ud83c\udffe\u200d\ud83e\uddbd\ud83d\udc69\ud83c\udffe\u200d\ud83e\uddbd" +
        "\u200d\u27a1\ufe0f\ud83d\udc69\ud83c\udffe\u200d\ud83e\udeef\u200d\ud83d\udc69\ud83c\udffb\ud83d\udc69\ud83c\udffe\u200d\ud83e\udeef\u200d\ud83d\udc69\ud83c\udffc\ud83d\udc69\ud83c\udffe\u200d\ud83e\udeef\u200d\ud83d\udc69\ud83c\udffd\ud83d\udc69\ud83c\udffe\u200d\ud83e\udeef\u200d\ud83d\udc69\ud83c\udfff\ud83d\udc69\ud83c\udfff\ud83d\udc69\ud83c\udfff\u200d\u2695\ufe0f\ud83d\udc69" +
        "\ud83c\udfff\u200d\u2696\ufe0f\ud83d\udc69\ud83c\udfff\u200d\u2708\ufe0f\ud83d\udc69\ud83c\udfff\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udffb\ud83d\udc69\ud83c\udfff\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udffc\ud83d\udc69\ud83c\udfff\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udffd\ud83d\udc69\ud83c\udfff\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udffe\ud83d\udc69\ud83c\udfff" +
        "\u200d\u2764\ufe0f\u200d\ud83d\udc68\ud83c\udfff\ud83d\udc69\ud83c\udfff\u200d\u2764\ufe0f\u200d\ud83d\udc69\ud83c\udffb\ud83d\udc69\ud83c\udfff\u200d\u2764\ufe0f\u200d\ud83d\udc69\ud83c\udffc\ud83d\udc69\ud83c\udfff\u200d\u2764\ufe0f\u200d\ud83d\udc69\ud83c\udffd\ud83d\udc69\ud83c\udfff\u200d\u2764\ufe0f\u200d\ud83d\udc69\ud83c\udffe\ud83d\udc69\ud83c\udfff\u200d\u2764\ufe0f\u200d" +
        "\ud83d\udc69\ud83c\udfff\ud83d\udc69\ud83c\udfff\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udffb\ud83d\udc69\ud83c\udfff\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udffc\ud83d\udc69\ud83c\udfff\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udffd\ud83d\udc69\ud83c\udfff\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udffe" +
        "\ud83d\udc69\ud83c\udfff\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc68\ud83c\udfff\ud83d\udc69\ud83c\udfff\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc69\ud83c\udffb\ud83d\udc69\ud83c\udfff\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc69\ud83c\udffc\ud83d\udc69\ud83c\udfff\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc69\ud83c\udffd\ud83d\udc69\ud83c\udfff" +
        "\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc69\ud83c\udffe\ud83d\udc69\ud83c\udfff\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83d\udc69\ud83c\udfff\ud83d\udc69\ud83c\udfff\u200d\ud83c\udf3e\ud83d\udc69\ud83c\udfff\u200d\ud83c\udf73\ud83d\udc69\ud83c\udfff\u200d\ud83c\udf7c\ud83d\udc69\ud83c\udfff\u200d\ud83c\udf93\ud83d\udc69\ud83c\udfff\u200d\ud83c\udfa4\ud83d\udc69\ud83c" +
        "\udfff\u200d\ud83c\udfa8\ud83d\udc69\ud83c\udfff\u200d\ud83c\udfeb\ud83d\udc69\ud83c\udfff\u200d\ud83c\udfed\ud83d\udc69\ud83c\udfff\u200d\ud83d\udc30\u200d\ud83d\udc69\ud83c\udffb\ud83d\udc69\ud83c\udfff\u200d\ud83d\udc30\u200d\ud83d\udc69\ud83c\udffc\ud83d\udc69\ud83c\udfff\u200d\ud83d\udc30\u200d\ud83d\udc69\ud83c\udffd\ud83d\udc69\ud83c\udfff\u200d\ud83d\udc30\u200d\ud83d\udc69" +
        "\ud83c\udffe\ud83d\udc69\ud83c\udfff\u200d\ud83d\udcbb\ud83d\udc69\ud83c\udfff\u200d\ud83d\udcbc\ud83d\udc69\ud83c\udfff\u200d\ud83d\udd27\ud83d\udc69\ud83c\udfff\u200d\ud83d\udd2c\ud83d\udc69\ud83c\udfff\u200d\ud83d\ude80\ud83d\udc69\ud83c\udfff\u200d\ud83d\ude92\ud83d\udc69\ud83c\udfff\u200d\ud83e\udd1d\u200d\ud83d\udc68\ud83c\udffb\ud83d\udc69\ud83c\udfff\u200d\ud83e\udd1d\u200d" +
        "\ud83d\udc68\ud83c\udffc\ud83d\udc69\ud83c\udfff\u200d\ud83e\udd1d\u200d\ud83d\udc68\ud83c\udffd\ud83d\udc69\ud83c\udfff\u200d\ud83e\udd1d\u200d\ud83d\udc68\ud83c\udffe\ud83d\udc69\ud83c\udfff\u200d\ud83e\udd1d\u200d\ud83d\udc69\ud83c\udffb\ud83d\udc69\ud83c\udfff\u200d\ud83e\udd1d\u200d\ud83d\udc69\ud83c\udffc\ud83d\udc69\ud83c\udfff\u200d\ud83e\udd1d\u200d\ud83d\udc69\ud83c\udffd" +
        "\ud83d\udc69\ud83c\udfff\u200d\ud83e\udd1d\u200d\ud83d\udc69\ud83c\udffe\ud83d\udc69\ud83c\udfff\u200d\ud83e\uddaf\ud83d\udc69\ud83c\udfff\u200d\ud83e\uddaf\u200d\u27a1\ufe0f\ud83d\udc69\ud83c\udfff\u200d\ud83e\uddb0\ud83d\udc69\ud83c\udfff\u200d\ud83e\uddb1\ud83d\udc69\ud83c\udfff\u200d\ud83e\uddb2\ud83d\udc69\ud83c\udfff\u200d\ud83e\uddb3\ud83d\udc69\ud83c\udfff\u200d\ud83e\uddbc" +
        "\ud83d\udc69\ud83c\udfff\u200d\ud83e\uddbc\u200d\u27a1\ufe0f\ud83d\udc69\ud83c\udfff\u200d\ud83e\uddbd\ud83d\udc69\ud83c\udfff\u200d\ud83e\uddbd\u200d\u27a1\ufe0f\ud83d\udc69\ud83c\udfff\u200d\ud83e\udeef\u200d\ud83d\udc69\ud83c\udffb\ud83d\udc69\ud83c\udfff\u200d\ud83e\udeef\u200d\ud83d\udc69\ud83c\udffc\ud83d\udc69\ud83c\udfff\u200d\ud83e\udeef\u200d\ud83d\udc69\ud83c\udffd\ud83d" +
        "\udc69\ud83c\udfff\u200d\ud83e\udeef\u200d\ud83d\udc69\ud83c\udffe\ud83d\udc6b\ud83c\udffb\ud83d\udc6b\ud83c\udffc\ud83d\udc6b\ud83c\udffd\ud83d\udc6b\ud83c\udffe\ud83d\udc6b\ud83c\udfff\ud83d\udc6c\ud83c\udffb\ud83d\udc6c\ud83c\udffc\ud83d\udc6c\ud83c\udffd\ud83d\udc6c\ud83c\udffe\ud83d\udc6c\ud83c\udfff\ud83d\udc6d\ud83c\udffb\ud83d\udc6d\ud83c\udffc\ud83d\udc6d\ud83c\udffd\ud83d" +
        "\udc6d\ud83c\udffe\ud83d\udc6d\ud83c\udfff\ud83d\udc6e\u200d\u2640\ufe0f\ud83d\udc6e\u200d\u2642\ufe0f\ud83d\udc6e\ud83c\udffb\ud83d\udc6e\ud83c\udffb\u200d\u2640\ufe0f\ud83d\udc6e\ud83c\udffb\u200d\u2642\ufe0f\ud83d\udc6e\ud83c\udffc\ud83d\udc6e\ud83c\udffc\u200d\u2640\ufe0f\ud83d\udc6e\ud83c\udffc\u200d\u2642\ufe0f\ud83d\udc6e\ud83c\udffd\ud83d\udc6e\ud83c\udffd\u200d\u2640\ufe0f" +
        "\ud83d\udc6e\ud83c\udffd\u200d\u2642\ufe0f\ud83d\udc6e\ud83c\udffe\ud83d\udc6e\ud83c\udffe\u200d\u2640\ufe0f\ud83d\udc6e\ud83c\udffe\u200d\u2642\ufe0f\ud83d\udc6e\ud83c\udfff\ud83d\udc6e\ud83c\udfff\u200d\u2640\ufe0f\ud83d\udc6e\ud83c\udfff\u200d\u2642\ufe0f\ud83d\udc6f\u200d\u2640\ufe0f\ud83d\udc6f\u200d\u2642\ufe0f\ud83d\udc6f\ud83c\udffb\ud83d\udc6f\ud83c\udffb\u200d\u2640\ufe0f" +
        "\ud83d\udc6f\ud83c\udffb\u200d\u2642\ufe0f\ud83d\udc6f\ud83c\udffc\ud83d\udc6f\ud83c\udffc\u200d\u2640\ufe0f\ud83d\udc6f\ud83c\udffc\u200d\u2642\ufe0f\ud83d\udc6f\ud83c\udffd\ud83d\udc6f\ud83c\udffd\u200d\u2640\ufe0f\ud83d\udc6f\ud83c\udffd\u200d\u2642\ufe0f\ud83d\udc6f\ud83c\udffe\ud83d\udc6f\ud83c\udffe\u200d\u2640\ufe0f\ud83d\udc6f\ud83c\udffe\u200d\u2642\ufe0f\ud83d\udc6f\ud83c" +
        "\udfff\ud83d\udc6f\ud83c\udfff\u200d\u2640\ufe0f\ud83d\udc6f\ud83c\udfff\u200d\u2642\ufe0f\ud83d\udc70\u200d\u2640\ufe0f\ud83d\udc70\u200d\u2642\ufe0f\ud83d\udc70\ud83c\udffb\ud83d\udc70\ud83c\udffb\u200d\u2640\ufe0f\ud83d\udc70\ud83c\udffb\u200d\u2642\ufe0f\ud83d\udc70\ud83c\udffc\ud83d\udc70\ud83c\udffc\u200d\u2640\ufe0f\ud83d\udc70\ud83c\udffc\u200d\u2642\ufe0f\ud83d\udc70\ud83c" +
        "\udffd\ud83d\udc70\ud83c\udffd\u200d\u2640\ufe0f\ud83d\udc70\ud83c\udffd\u200d\u2642\ufe0f\ud83d\udc70\ud83c\udffe\ud83d\udc70\ud83c\udffe\u200d\u2640\ufe0f\ud83d\udc70\ud83c\udffe\u200d\u2642\ufe0f\ud83d\udc70\ud83c\udfff\ud83d\udc70\ud83c\udfff\u200d\u2640\ufe0f\ud83d\udc70\ud83c\udfff\u200d\u2642\ufe0f\ud83d\udc71\u200d\u2640\ufe0f\ud83d\udc71\u200d\u2642\ufe0f\ud83d\udc71\ud83c" +
        "\udffb\ud83d\udc71\ud83c\udffb\u200d\u2640\ufe0f\ud83d\udc71\ud83c\udffb\u200d\u2642\ufe0f\ud83d\udc71\ud83c\udffc\ud83d\udc71\ud83c\udffc\u200d\u2640\ufe0f\ud83d\udc71\ud83c\udffc\u200d\u2642\ufe0f\ud83d\udc71\ud83c\udffd\ud83d\udc71\ud83c\udffd\u200d\u2640\ufe0f\ud83d\udc71\ud83c\udffd\u200d\u2642\ufe0f\ud83d\udc71\ud83c\udffe\ud83d\udc71\ud83c\udffe\u200d\u2640\ufe0f\ud83d\udc71" +
        "\ud83c\udffe\u200d\u2642\ufe0f\ud83d\udc71\ud83c\udfff\ud83d\udc71\ud83c\udfff\u200d\u2640\ufe0f\ud83d\udc71\ud83c\udfff\u200d\u2642\ufe0f\ud83d\udc72\ud83c\udffb\ud83d\udc72\ud83c\udffc\ud83d\udc72\ud83c\udffd\ud83d\udc72\ud83c\udffe\ud83d\udc72\ud83c\udfff\ud83d\udc73\u200d\u2640\ufe0f\ud83d\udc73\u200d\u2642\ufe0f\ud83d\udc73\ud83c\udffb\ud83d\udc73\ud83c\udffb\u200d\u2640\ufe0f" +
        "\ud83d\udc73\ud83c\udffb\u200d\u2642\ufe0f\ud83d\udc73\ud83c\udffc\ud83d\udc73\ud83c\udffc\u200d\u2640\ufe0f\ud83d\udc73\ud83c\udffc\u200d\u2642\ufe0f\ud83d\udc73\ud83c\udffd\ud83d\udc73\ud83c\udffd\u200d\u2640\ufe0f\ud83d\udc73\ud83c\udffd\u200d\u2642\ufe0f\ud83d\udc73\ud83c\udffe\ud83d\udc73\ud83c\udffe\u200d\u2640\ufe0f\ud83d\udc73\ud83c\udffe\u200d\u2642\ufe0f\ud83d\udc73\ud83c" +
        "\udfff\ud83d\udc73\ud83c\udfff\u200d\u2640\ufe0f\ud83d\udc73\ud83c\udfff\u200d\u2642\ufe0f\ud83d\udc74\ud83c\udffb\ud83d\udc74\ud83c\udffc\ud83d\udc74\ud83c\udffd\ud83d\udc74\ud83c\udffe\ud83d\udc74\ud83c\udfff\ud83d\udc75\ud83c\udffb\ud83d\udc75\ud83c\udffc\ud83d\udc75\ud83c\udffd\ud83d\udc75\ud83c\udffe\ud83d\udc75\ud83c\udfff\ud83d\udc76\ud83c\udffb\ud83d\udc76\ud83c\udffc\ud83d" +
        "\udc76\ud83c\udffd\ud83d\udc76\ud83c\udffe\ud83d\udc76\ud83c\udfff\ud83d\udc77\u200d\u2640\ufe0f\ud83d\udc77\u200d\u2642\ufe0f\ud83d\udc77\ud83c\udffb\ud83d\udc77\ud83c\udffb\u200d\u2640\ufe0f\ud83d\udc77\ud83c\udffb\u200d\u2642\ufe0f\ud83d\udc77\ud83c\udffc\ud83d\udc77\ud83c\udffc\u200d\u2640\ufe0f\ud83d\udc77\ud83c\udffc\u200d\u2642\ufe0f\ud83d\udc77\ud83c\udffd\ud83d\udc77\ud83c" +
        "\udffd\u200d\u2640\ufe0f\ud83d\udc77\ud83c\udffd\u200d\u2642\ufe0f\ud83d\udc77\ud83c\udffe\ud83d\udc77\ud83c\udffe\u200d\u2640\ufe0f\ud83d\udc77\ud83c\udffe\u200d\u2642\ufe0f\ud83d\udc77\ud83c\udfff\ud83d\udc77\ud83c\udfff\u200d\u2640\ufe0f\ud83d\udc77\ud83c\udfff\u200d\u2642\ufe0f\ud83d\udc78\ud83c\udffb\ud83d\udc78\ud83c\udffc\ud83d\udc78\ud83c\udffd\ud83d\udc78\ud83c\udffe\ud83d" +
        "\udc78\ud83c\udfff\ud83d\udc7c\ud83c\udffb\ud83d\udc7c\ud83c\udffc\ud83d\udc7c\ud83c\udffd\ud83d\udc7c\ud83c\udffe\ud83d\udc7c\ud83c\udfff\ud83d\udc81\u200d\u2640\ufe0f\ud83d\udc81\u200d\u2642\ufe0f\ud83d\udc81\ud83c\udffb\ud83d\udc81\ud83c\udffb\u200d\u2640\ufe0f\ud83d\udc81\ud83c\udffb\u200d\u2642\ufe0f\ud83d\udc81\ud83c\udffc\ud83d\udc81\ud83c\udffc\u200d\u2640\ufe0f\ud83d\udc81" +
        "\ud83c\udffc\u200d\u2642\ufe0f\ud83d\udc81\ud83c\udffd\ud83d\udc81\ud83c\udffd\u200d\u2640\ufe0f\ud83d\udc81\ud83c\udffd\u200d\u2642\ufe0f\ud83d\udc81\ud83c\udffe\ud83d\udc81\ud83c\udffe\u200d\u2640\ufe0f\ud83d\udc81\ud83c\udffe\u200d\u2642\ufe0f\ud83d\udc81\ud83c\udfff\ud83d\udc81\ud83c\udfff\u200d\u2640\ufe0f\ud83d\udc81\ud83c\udfff\u200d\u2642\ufe0f\ud83d\udc82\u200d\u2640\ufe0f" +
        "\ud83d\udc82\u200d\u2642\ufe0f\ud83d\udc82\ud83c\udffb\ud83d\udc82\ud83c\udffb\u200d\u2640\ufe0f\ud83d\udc82\ud83c\udffb\u200d\u2642\ufe0f\ud83d\udc82\ud83c\udffc\ud83d\udc82\ud83c\udffc\u200d\u2640\ufe0f\ud83d\udc82\ud83c\udffc\u200d\u2642\ufe0f\ud83d\udc82\ud83c\udffd\ud83d\udc82\ud83c\udffd\u200d\u2640\ufe0f\ud83d\udc82\ud83c\udffd\u200d\u2642\ufe0f\ud83d\udc82\ud83c\udffe\ud83d" +
        "\udc82\ud83c\udffe\u200d\u2640\ufe0f\ud83d\udc82\ud83c\udffe\u200d\u2642\ufe0f\ud83d\udc82\ud83c\udfff\ud83d\udc82\ud83c\udfff\u200d\u2640\ufe0f\ud83d\udc82\ud83c\udfff\u200d\u2642\ufe0f\ud83d\udc83\ud83c\udffb\ud83d\udc83\ud83c\udffc\ud83d\udc83\ud83c\udffd\ud83d\udc83\ud83c\udffe\ud83d\udc83\ud83c\udfff\ud83d\udc85\ud83c\udffb\ud83d\udc85\ud83c\udffc\ud83d\udc85\ud83c\udffd\ud83d" +
        "\udc85\ud83c\udffe\ud83d\udc85\ud83c\udfff\ud83d\udc86\u200d\u2640\ufe0f\ud83d\udc86\u200d\u2642\ufe0f\ud83d\udc86\ud83c\udffb\ud83d\udc86\ud83c\udffb\u200d\u2640\ufe0f\ud83d\udc86\ud83c\udffb\u200d\u2642\ufe0f\ud83d\udc86\ud83c\udffc\ud83d\udc86\ud83c\udffc\u200d\u2640\ufe0f\ud83d\udc86\ud83c\udffc\u200d\u2642\ufe0f\ud83d\udc86\ud83c\udffd\ud83d\udc86\ud83c\udffd\u200d\u2640\ufe0f" +
        "\ud83d\udc86\ud83c\udffd\u200d\u2642\ufe0f\ud83d\udc86\ud83c\udffe\ud83d\udc86\ud83c\udffe\u200d\u2640\ufe0f\ud83d\udc86\ud83c\udffe\u200d\u2642\ufe0f\ud83d\udc86\ud83c\udfff\ud83d\udc86\ud83c\udfff\u200d\u2640\ufe0f\ud83d\udc86\ud83c\udfff\u200d\u2642\ufe0f\ud83d\udc87\u200d\u2640\ufe0f\ud83d\udc87\u200d\u2642\ufe0f\ud83d\udc87\ud83c\udffb\ud83d\udc87\ud83c\udffb\u200d\u2640\ufe0f" +
        "\ud83d\udc87\ud83c\udffb\u200d\u2642\ufe0f\ud83d\udc87\ud83c\udffc\ud83d\udc87\ud83c\udffc\u200d\u2640\ufe0f\ud83d\udc87\ud83c\udffc\u200d\u2642\ufe0f\ud83d\udc87\ud83c\udffd\ud83d\udc87\ud83c\udffd\u200d\u2640\ufe0f\ud83d\udc87\ud83c\udffd\u200d\u2642\ufe0f\ud83d\udc87\ud83c\udffe\ud83d\udc87\ud83c\udffe\u200d\u2640\ufe0f\ud83d\udc87\ud83c\udffe\u200d\u2642\ufe0f\ud83d\udc87\ud83c" +
        "\udfff\ud83d\udc87\ud83c\udfff\u200d\u2640\ufe0f\ud83d\udc87\ud83c\udfff\u200d\u2642\ufe0f\ud83d\udc8f\ud83c\udffb\ud83d\udc8f\ud83c\udffc\ud83d\udc8f\ud83c\udffd\ud83d\udc8f\ud83c\udffe\ud83d\udc8f\ud83c\udfff\ud83d\udc91\ud83c\udffb\ud83d\udc91\ud83c\udffc\ud83d\udc91\ud83c\udffd\ud83d\udc91\ud83c\udffe\ud83d\udc91\ud83c\udfff\ud83d\udcaa\ud83c\udffb\ud83d\udcaa\ud83c\udffc\ud83d" +
        "\udcaa\ud83c\udffd\ud83d\udcaa\ud83c\udffe\ud83d\udcaa\ud83c\udfff\ud83d\udd74\ud83c\udffb\ud83d\udd74\ud83c\udffc\ud83d\udd74\ud83c\udffd\ud83d\udd74\ud83c\udffe\ud83d\udd74\ud83c\udfff\ud83d\udd75\ud83c\udffb\ud83d\udd75\ud83c\udffb\u200d\u2640\ufe0f\ud83d\udd75\ud83c\udffb\u200d\u2642\ufe0f\ud83d\udd75\ud83c\udffc\ud83d\udd75\ud83c\udffc\u200d\u2640\ufe0f\ud83d\udd75\ud83c\udffc" +
        "\u200d\u2642\ufe0f\ud83d\udd75\ud83c\udffd\ud83d\udd75\ud83c\udffd\u200d\u2640\ufe0f\ud83d\udd75\ud83c\udffd\u200d\u2642\ufe0f\ud83d\udd75\ud83c\udffe\ud83d\udd75\ud83c\udffe\u200d\u2640\ufe0f\ud83d\udd75\ud83c\udffe\u200d\u2642\ufe0f\ud83d\udd75\ud83c\udfff\ud83d\udd75\ud83c\udfff\u200d\u2640\ufe0f\ud83d\udd75\ud83c\udfff\u200d\u2642\ufe0f\ud83d\udd75\ufe0f\u200d\u2640\ufe0f\ud83d" +
        "\udd75\ufe0f\u200d\u2642\ufe0f\ud83d\udd7a\ud83c\udffb\ud83d\udd7a\ud83c\udffc\ud83d\udd7a\ud83c\udffd\ud83d\udd7a\ud83c\udffe\ud83d\udd7a\ud83c\udfff\ud83d\udd90\ud83c\udffb\ud83d\udd90\ud83c\udffc\ud83d\udd90\ud83c\udffd\ud83d\udd90\ud83c\udffe\ud83d\udd90\ud83c\udfff\ud83d\udd95\ud83c\udffb\ud83d\udd95\ud83c\udffc\ud83d\udd95\ud83c\udffd\ud83d\udd95\ud83c\udffe\ud83d\udd95\ud83c" +
        "\udfff\ud83d\udd96\ud83c\udffb\ud83d\udd96\ud83c\udffc\ud83d\udd96\ud83c\udffd\ud83d\udd96\ud83c\udffe\ud83d\udd96\ud83c\udfff\ud83d\ude2e\u200d\ud83d\udca8\ud83d\ude35\u200d\ud83d\udcab\ud83d\ude36\u200d\ud83c\udf2b\ufe0f\ud83d\ude42\u200d\u2194\ufe0f\ud83d\ude42\u200d\u2195\ufe0f\ud83d\ude45\u200d\u2640\ufe0f\ud83d\ude45\u200d\u2642\ufe0f\ud83d\ude45\ud83c\udffb\ud83d\ude45\ud83c" +
        "\udffb\u200d\u2640\ufe0f\ud83d\ude45\ud83c\udffb\u200d\u2642\ufe0f\ud83d\ude45\ud83c\udffc\ud83d\ude45\ud83c\udffc\u200d\u2640\ufe0f\ud83d\ude45\ud83c\udffc\u200d\u2642\ufe0f\ud83d\ude45\ud83c\udffd\ud83d\ude45\ud83c\udffd\u200d\u2640\ufe0f\ud83d\ude45\ud83c\udffd\u200d\u2642\ufe0f\ud83d\ude45\ud83c\udffe\ud83d\ude45\ud83c\udffe\u200d\u2640\ufe0f\ud83d\ude45\ud83c\udffe\u200d\u2642" +
        "\ufe0f\ud83d\ude45\ud83c\udfff\ud83d\ude45\ud83c\udfff\u200d\u2640\ufe0f\ud83d\ude45\ud83c\udfff\u200d\u2642\ufe0f\ud83d\ude46\u200d\u2640\ufe0f\ud83d\ude46\u200d\u2642\ufe0f\ud83d\ude46\ud83c\udffb\ud83d\ude46\ud83c\udffb\u200d\u2640\ufe0f\ud83d\ude46\ud83c\udffb\u200d\u2642\ufe0f\ud83d\ude46\ud83c\udffc\ud83d\ude46\ud83c\udffc\u200d\u2640\ufe0f\ud83d\ude46\ud83c\udffc\u200d\u2642" +
        "\ufe0f\ud83d\ude46\ud83c\udffd\ud83d\ude46\ud83c\udffd\u200d\u2640\ufe0f\ud83d\ude46\ud83c\udffd\u200d\u2642\ufe0f\ud83d\ude46\ud83c\udffe\ud83d\ude46\ud83c\udffe\u200d\u2640\ufe0f\ud83d\ude46\ud83c\udffe\u200d\u2642\ufe0f\ud83d\ude46\ud83c\udfff\ud83d\ude46\ud83c\udfff\u200d\u2640\ufe0f\ud83d\ude46\ud83c\udfff\u200d\u2642\ufe0f\ud83d\ude47\u200d\u2640\ufe0f\ud83d\ude47\u200d\u2642" +
        "\ufe0f\ud83d\ude47\ud83c\udffb\ud83d\ude47\ud83c\udffb\u200d\u2640\ufe0f\ud83d\ude47\ud83c\udffb\u200d\u2642\ufe0f\ud83d\ude47\ud83c\udffc\ud83d\ude47\ud83c\udffc\u200d\u2640\ufe0f\ud83d\ude47\ud83c\udffc\u200d\u2642\ufe0f\ud83d\ude47\ud83c\udffd\ud83d\ude47\ud83c\udffd\u200d\u2640\ufe0f\ud83d\ude47\ud83c\udffd\u200d\u2642\ufe0f\ud83d\ude47\ud83c\udffe\ud83d\ude47\ud83c\udffe\u200d" +
        "\u2640\ufe0f\ud83d\ude47\ud83c\udffe\u200d\u2642\ufe0f\ud83d\ude47\ud83c\udfff\ud83d\ude47\ud83c\udfff\u200d\u2640\ufe0f\ud83d\ude47\ud83c\udfff\u200d\u2642\ufe0f\ud83d\ude4b\u200d\u2640\ufe0f\ud83d\ude4b\u200d\u2642\ufe0f\ud83d\ude4b\ud83c\udffb\ud83d\ude4b\ud83c\udffb\u200d\u2640\ufe0f\ud83d\ude4b\ud83c\udffb\u200d\u2642\ufe0f\ud83d\ude4b\ud83c\udffc\ud83d\ude4b\ud83c\udffc\u200d" +
        "\u2640\ufe0f\ud83d\ude4b\ud83c\udffc\u200d\u2642\ufe0f\ud83d\ude4b\ud83c\udffd\ud83d\ude4b\ud83c\udffd\u200d\u2640\ufe0f\ud83d\ude4b\ud83c\udffd\u200d\u2642\ufe0f\ud83d\ude4b\ud83c\udffe\ud83d\ude4b\ud83c\udffe\u200d\u2640\ufe0f\ud83d\ude4b\ud83c\udffe\u200d\u2642\ufe0f\ud83d\ude4b\ud83c\udfff\ud83d\ude4b\ud83c\udfff\u200d\u2640\ufe0f\ud83d\ude4b\ud83c\udfff\u200d\u2642\ufe0f\ud83d" +
        "\ude4c\ud83c\udffb\ud83d\ude4c\ud83c\udffc\ud83d\ude4c\ud83c\udffd\ud83d\ude4c\ud83c\udffe\ud83d\ude4c\ud83c\udfff\ud83d\ude4d\u200d\u2640\ufe0f\ud83d\ude4d\u200d\u2642\ufe0f\ud83d\ude4d\ud83c\udffb\ud83d\ude4d\ud83c\udffb\u200d\u2640\ufe0f\ud83d\ude4d\ud83c\udffb\u200d\u2642\ufe0f\ud83d\ude4d\ud83c\udffc\ud83d\ude4d\ud83c\udffc\u200d\u2640\ufe0f\ud83d\ude4d\ud83c\udffc\u200d\u2642" +
        "\ufe0f\ud83d\ude4d\ud83c\udffd\ud83d\ude4d\ud83c\udffd\u200d\u2640\ufe0f\ud83d\ude4d\ud83c\udffd\u200d\u2642\ufe0f\ud83d\ude4d\ud83c\udffe\ud83d\ude4d\ud83c\udffe\u200d\u2640\ufe0f\ud83d\ude4d\ud83c\udffe\u200d\u2642\ufe0f\ud83d\ude4d\ud83c\udfff\ud83d\ude4d\ud83c\udfff\u200d\u2640\ufe0f\ud83d\ude4d\ud83c\udfff\u200d\u2642\ufe0f\ud83d\ude4e\u200d\u2640\ufe0f\ud83d\ude4e\u200d\u2642" +
        "\ufe0f\ud83d\ude4e\ud83c\udffb\ud83d\ude4e\ud83c\udffb\u200d\u2640\ufe0f\ud83d\ude4e\ud83c\udffb\u200d\u2642\ufe0f\ud83d\ude4e\ud83c\udffc\ud83d\ude4e\ud83c\udffc\u200d\u2640\ufe0f\ud83d\ude4e\ud83c\udffc\u200d\u2642\ufe0f\ud83d\ude4e\ud83c\udffd\ud83d\ude4e\ud83c\udffd\u200d\u2640\ufe0f\ud83d\ude4e\ud83c\udffd\u200d\u2642\ufe0f\ud83d\ude4e\ud83c\udffe\ud83d\ude4e\ud83c\udffe\u200d" +
        "\u2640\ufe0f\ud83d\ude4e\ud83c\udffe\u200d\u2642\ufe0f\ud83d\ude4e\ud83c\udfff\ud83d\ude4e\ud83c\udfff\u200d\u2640\ufe0f\ud83d\ude4e\ud83c\udfff\u200d\u2642\ufe0f\ud83d\ude4f\ud83c\udffb\ud83d\ude4f\ud83c\udffc\ud83d\ude4f\ud83c\udffd\ud83d\ude4f\ud83c\udffe\ud83d\ude4f\ud83c\udfff\ud83d\udea3\u200d\u2640\ufe0f\ud83d\udea3\u200d\u2642\ufe0f\ud83d\udea3\ud83c\udffb\ud83d\udea3\ud83c" +
        "\udffb\u200d\u2640\ufe0f\ud83d\udea3\ud83c\udffb\u200d\u2642\ufe0f\ud83d\udea3\ud83c\udffc\ud83d\udea3\ud83c\udffc\u200d\u2640\ufe0f\ud83d\udea3\ud83c\udffc\u200d\u2642\ufe0f\ud83d\udea3\ud83c\udffd\ud83d\udea3\ud83c\udffd\u200d\u2640\ufe0f\ud83d\udea3\ud83c\udffd\u200d\u2642\ufe0f\ud83d\udea3\ud83c\udffe\ud83d\udea3\ud83c\udffe\u200d\u2640\ufe0f\ud83d\udea3\ud83c\udffe\u200d\u2642" +
        "\ufe0f\ud83d\udea3\ud83c\udfff\ud83d\udea3\ud83c\udfff\u200d\u2640\ufe0f\ud83d\udea3\ud83c\udfff\u200d\u2642\ufe0f\ud83d\udeb4\u200d\u2640\ufe0f\ud83d\udeb4\u200d\u2642\ufe0f\ud83d\udeb4\ud83c\udffb\ud83d\udeb4\ud83c\udffb\u200d\u2640\ufe0f\ud83d\udeb4\ud83c\udffb\u200d\u2642\ufe0f\ud83d\udeb4\ud83c\udffc\ud83d\udeb4\ud83c\udffc\u200d\u2640\ufe0f\ud83d\udeb4\ud83c\udffc\u200d\u2642" +
        "\ufe0f\ud83d\udeb4\ud83c\udffd\ud83d\udeb4\ud83c\udffd\u200d\u2640\ufe0f\ud83d\udeb4\ud83c\udffd\u200d\u2642\ufe0f\ud83d\udeb4\ud83c\udffe\ud83d\udeb4\ud83c\udffe\u200d\u2640\ufe0f\ud83d\udeb4\ud83c\udffe\u200d\u2642\ufe0f\ud83d\udeb4\ud83c\udfff\ud83d\udeb4\ud83c\udfff\u200d\u2640\ufe0f\ud83d\udeb4\ud83c\udfff\u200d\u2642\ufe0f\ud83d\udeb5\u200d\u2640\ufe0f\ud83d\udeb5\u200d\u2642" +
        "\ufe0f\ud83d\udeb5\ud83c\udffb\ud83d\udeb5\ud83c\udffb\u200d\u2640\ufe0f\ud83d\udeb5\ud83c\udffb\u200d\u2642\ufe0f\ud83d\udeb5\ud83c\udffc\ud83d\udeb5\ud83c\udffc\u200d\u2640\ufe0f\ud83d\udeb5\ud83c\udffc\u200d\u2642\ufe0f\ud83d\udeb5\ud83c\udffd\ud83d\udeb5\ud83c\udffd\u200d\u2640\ufe0f\ud83d\udeb5\ud83c\udffd\u200d\u2642\ufe0f\ud83d\udeb5\ud83c\udffe\ud83d\udeb5\ud83c\udffe\u200d" +
        "\u2640\ufe0f\ud83d\udeb5\ud83c\udffe\u200d\u2642\ufe0f\ud83d\udeb5\ud83c\udfff\ud83d\udeb5\ud83c\udfff\u200d\u2640\ufe0f\ud83d\udeb5\ud83c\udfff\u200d\u2642\ufe0f\ud83d\udeb6\u200d\u2640\ufe0f\ud83d\udeb6\u200d\u2640\ufe0f\u200d\u27a1\ufe0f\ud83d\udeb6\u200d\u2642\ufe0f\ud83d\udeb6\u200d\u2642\ufe0f\u200d\u27a1\ufe0f\ud83d\udeb6\u200d\u27a1\ufe0f\ud83d\udeb6\ud83c\udffb\ud83d\udeb6" +
        "\ud83c\udffb\u200d\u2640\ufe0f\ud83d\udeb6\ud83c\udffb\u200d\u2640\ufe0f\u200d\u27a1\ufe0f\ud83d\udeb6\ud83c\udffb\u200d\u2642\ufe0f\ud83d\udeb6\ud83c\udffb\u200d\u2642\ufe0f\u200d\u27a1\ufe0f\ud83d\udeb6\ud83c\udffb\u200d\u27a1\ufe0f\ud83d\udeb6\ud83c\udffc\ud83d\udeb6\ud83c\udffc\u200d\u2640\ufe0f\ud83d\udeb6\ud83c\udffc\u200d\u2640\ufe0f\u200d\u27a1\ufe0f\ud83d\udeb6\ud83c\udffc" +
        "\u200d\u2642\ufe0f\ud83d\udeb6\ud83c\udffc\u200d\u2642\ufe0f\u200d\u27a1\ufe0f\ud83d\udeb6\ud83c\udffc\u200d\u27a1\ufe0f\ud83d\udeb6\ud83c\udffd\ud83d\udeb6\ud83c\udffd\u200d\u2640\ufe0f\ud83d\udeb6\ud83c\udffd\u200d\u2640\ufe0f\u200d\u27a1\ufe0f\ud83d\udeb6\ud83c\udffd\u200d\u2642\ufe0f\ud83d\udeb6\ud83c\udffd\u200d\u2642\ufe0f\u200d\u27a1\ufe0f\ud83d\udeb6\ud83c\udffd\u200d\u27a1" +
        "\ufe0f\ud83d\udeb6\ud83c\udffe\ud83d\udeb6\ud83c\udffe\u200d\u2640\ufe0f\ud83d\udeb6\ud83c\udffe\u200d\u2640\ufe0f\u200d\u27a1\ufe0f\ud83d\udeb6\ud83c\udffe\u200d\u2642\ufe0f\ud83d\udeb6\ud83c\udffe\u200d\u2642\ufe0f\u200d\u27a1\ufe0f\ud83d\udeb6\ud83c\udffe\u200d\u27a1\ufe0f\ud83d\udeb6\ud83c\udfff\ud83d\udeb6\ud83c\udfff\u200d\u2640\ufe0f\ud83d\udeb6\ud83c\udfff\u200d\u2640\ufe0f" +
        "\u200d\u27a1\ufe0f\ud83d\udeb6\ud83c\udfff\u200d\u2642\ufe0f\ud83d\udeb6\ud83c\udfff\u200d\u2642\ufe0f\u200d\u27a1\ufe0f\ud83d\udeb6\ud83c\udfff\u200d\u27a1\ufe0f\ud83d\udec0\ud83c\udffb\ud83d\udec0\ud83c\udffc\ud83d\udec0\ud83c\udffd\ud83d\udec0\ud83c\udffe\ud83d\udec0\ud83c\udfff\ud83d\udecc\ud83c\udffb\ud83d\udecc\ud83c\udffc\ud83d\udecc\ud83c\udffd\ud83d\udecc\ud83c\udffe\ud83d" +
        "\udecc\ud83c\udfff\ud83e\udd0c\ud83c\udffb\ud83e\udd0c\ud83c\udffc\ud83e\udd0c\ud83c\udffd\ud83e\udd0c\ud83c\udffe\ud83e\udd0c\ud83c\udfff\ud83e\udd0f\ud83c\udffb\ud83e\udd0f\ud83c\udffc\ud83e\udd0f\ud83c\udffd\ud83e\udd0f\ud83c\udffe\ud83e\udd0f\ud83c\udfff\ud83e\udd18\ud83c\udffb\ud83e\udd18\ud83c\udffc\ud83e\udd18\ud83c\udffd\ud83e\udd18\ud83c\udffe\ud83e\udd18\ud83c\udfff\ud83e" +
        "\udd19\ud83c\udffb\ud83e\udd19\ud83c\udffc\ud83e\udd19\ud83c\udffd\ud83e\udd19\ud83c\udffe\ud83e\udd19\ud83c\udfff\ud83e\udd1a\ud83c\udffb\ud83e\udd1a\ud83c\udffc\ud83e\udd1a\ud83c\udffd\ud83e\udd1a\ud83c\udffe\ud83e\udd1a\ud83c\udfff\ud83e\udd1b\ud83c\udffb\ud83e\udd1b\ud83c\udffc\ud83e\udd1b\ud83c\udffd\ud83e\udd1b\ud83c\udffe\ud83e\udd1b\ud83c\udfff\ud83e\udd1c\ud83c\udffb\ud83e" +
        "\udd1c\ud83c\udffc\ud83e\udd1c\ud83c\udffd\ud83e\udd1c\ud83c\udffe\ud83e\udd1c\ud83c\udfff\ud83e\udd1d\ud83c\udffb\ud83e\udd1d\ud83c\udffc\ud83e\udd1d\ud83c\udffd\ud83e\udd1d\ud83c\udffe\ud83e\udd1d\ud83c\udfff\ud83e\udd1e\ud83c\udffb\ud83e\udd1e\ud83c\udffc\ud83e\udd1e\ud83c\udffd\ud83e\udd1e\ud83c\udffe\ud83e\udd1e\ud83c\udfff\ud83e\udd1f\ud83c\udffb\ud83e\udd1f\ud83c\udffc\ud83e" +
        "\udd1f\ud83c\udffd\ud83e\udd1f\ud83c\udffe\ud83e\udd1f\ud83c\udfff\ud83e\udd26\u200d\u2640\ufe0f\ud83e\udd26\u200d\u2642\ufe0f\ud83e\udd26\ud83c\udffb\ud83e\udd26\ud83c\udffb\u200d\u2640\ufe0f\ud83e\udd26\ud83c\udffb\u200d\u2642\ufe0f\ud83e\udd26\ud83c\udffc\ud83e\udd26\ud83c\udffc\u200d\u2640\ufe0f\ud83e\udd26\ud83c\udffc\u200d\u2642\ufe0f\ud83e\udd26\ud83c\udffd\ud83e\udd26\ud83c" +
        "\udffd\u200d\u2640\ufe0f\ud83e\udd26\ud83c\udffd\u200d\u2642\ufe0f\ud83e\udd26\ud83c\udffe\ud83e\udd26\ud83c\udffe\u200d\u2640\ufe0f\ud83e\udd26\ud83c\udffe\u200d\u2642\ufe0f\ud83e\udd26\ud83c\udfff\ud83e\udd26\ud83c\udfff\u200d\u2640\ufe0f\ud83e\udd26\ud83c\udfff\u200d\u2642\ufe0f\ud83e\udd30\ud83c\udffb\ud83e\udd30\ud83c\udffc\ud83e\udd30\ud83c\udffd\ud83e\udd30\ud83c\udffe\ud83e" +
        "\udd30\ud83c\udfff\ud83e\udd31\ud83c\udffb\ud83e\udd31\ud83c\udffc\ud83e\udd31\ud83c\udffd\ud83e\udd31\ud83c\udffe\ud83e\udd31\ud83c\udfff\ud83e\udd32\ud83c\udffb\ud83e\udd32\ud83c\udffc\ud83e\udd32\ud83c\udffd\ud83e\udd32\ud83c\udffe\ud83e\udd32\ud83c\udfff\ud83e\udd33\ud83c\udffb\ud83e\udd33\ud83c\udffc\ud83e\udd33\ud83c\udffd\ud83e\udd33\ud83c\udffe\ud83e\udd33\ud83c\udfff\ud83e" +
        "\udd34\ud83c\udffb\ud83e\udd34\ud83c\udffc\ud83e\udd34\ud83c\udffd\ud83e\udd34\ud83c\udffe\ud83e\udd34\ud83c\udfff\ud83e\udd35\u200d\u2640\ufe0f\ud83e\udd35\u200d\u2642\ufe0f\ud83e\udd35\ud83c\udffb\ud83e\udd35\ud83c\udffb\u200d\u2640\ufe0f\ud83e\udd35\ud83c\udffb\u200d\u2642\ufe0f\ud83e\udd35\ud83c\udffc\ud83e\udd35\ud83c\udffc\u200d\u2640\ufe0f\ud83e\udd35\ud83c\udffc\u200d\u2642" +
        "\ufe0f\ud83e\udd35\ud83c\udffd\ud83e\udd35\ud83c\udffd\u200d\u2640\ufe0f\ud83e\udd35\ud83c\udffd\u200d\u2642\ufe0f\ud83e\udd35\ud83c\udffe\ud83e\udd35\ud83c\udffe\u200d\u2640\ufe0f\ud83e\udd35\ud83c\udffe\u200d\u2642\ufe0f\ud83e\udd35\ud83c\udfff\ud83e\udd35\ud83c\udfff\u200d\u2640\ufe0f\ud83e\udd35\ud83c\udfff\u200d\u2642\ufe0f\ud83e\udd36\ud83c\udffb\ud83e\udd36\ud83c\udffc\ud83e" +
        "\udd36\ud83c\udffd\ud83e\udd36\ud83c\udffe\ud83e\udd36\ud83c\udfff\ud83e\udd37\u200d\u2640\ufe0f\ud83e\udd37\u200d\u2642\ufe0f\ud83e\udd37\ud83c\udffb\ud83e\udd37\ud83c\udffb\u200d\u2640\ufe0f\ud83e\udd37\ud83c\udffb\u200d\u2642\ufe0f\ud83e\udd37\ud83c\udffc\ud83e\udd37\ud83c\udffc\u200d\u2640\ufe0f\ud83e\udd37\ud83c\udffc\u200d\u2642\ufe0f\ud83e\udd37\ud83c\udffd\ud83e\udd37\ud83c" +
        "\udffd\u200d\u2640\ufe0f\ud83e\udd37\ud83c\udffd\u200d\u2642\ufe0f\ud83e\udd37\ud83c\udffe\ud83e\udd37\ud83c\udffe\u200d\u2640\ufe0f\ud83e\udd37\ud83c\udffe\u200d\u2642\ufe0f\ud83e\udd37\ud83c\udfff\ud83e\udd37\ud83c\udfff\u200d\u2640\ufe0f\ud83e\udd37\ud83c\udfff\u200d\u2642\ufe0f\ud83e\udd38\u200d\u2640\ufe0f\ud83e\udd38\u200d\u2642\ufe0f\ud83e\udd38\ud83c\udffb\ud83e\udd38\ud83c" +
        "\udffb\u200d\u2640\ufe0f\ud83e\udd38\ud83c\udffb\u200d\u2642\ufe0f\ud83e\udd38\ud83c\udffc\ud83e\udd38\ud83c\udffc\u200d\u2640\ufe0f\ud83e\udd38\ud83c\udffc\u200d\u2642\ufe0f\ud83e\udd38\ud83c\udffd\ud83e\udd38\ud83c\udffd\u200d\u2640\ufe0f\ud83e\udd38\ud83c\udffd\u200d\u2642\ufe0f\ud83e\udd38\ud83c\udffe\ud83e\udd38\ud83c\udffe\u200d\u2640\ufe0f\ud83e\udd38\ud83c\udffe\u200d\u2642" +
        "\ufe0f\ud83e\udd38\ud83c\udfff\ud83e\udd38\ud83c\udfff\u200d\u2640\ufe0f\ud83e\udd38\ud83c\udfff\u200d\u2642\ufe0f\ud83e\udd39\u200d\u2640\ufe0f\ud83e\udd39\u200d\u2642\ufe0f\ud83e\udd39\ud83c\udffb\ud83e\udd39\ud83c\udffb\u200d\u2640\ufe0f\ud83e\udd39\ud83c\udffb\u200d\u2642\ufe0f\ud83e\udd39\ud83c\udffc\ud83e\udd39\ud83c\udffc\u200d\u2640\ufe0f\ud83e\udd39\ud83c\udffc\u200d\u2642" +
        "\ufe0f\ud83e\udd39\ud83c\udffd\ud83e\udd39\ud83c\udffd\u200d\u2640\ufe0f\ud83e\udd39\ud83c\udffd\u200d\u2642\ufe0f\ud83e\udd39\ud83c\udffe\ud83e\udd39\ud83c\udffe\u200d\u2640\ufe0f\ud83e\udd39\ud83c\udffe\u200d\u2642\ufe0f\ud83e\udd39\ud83c\udfff\ud83e\udd39\ud83c\udfff\u200d\u2640\ufe0f\ud83e\udd39\ud83c\udfff\u200d\u2642\ufe0f\ud83e\udd3c\u200d\u2640\ufe0f\ud83e\udd3c\u200d\u2642" +
        "\ufe0f\ud83e\udd3c\ud83c\udffb\ud83e\udd3c\ud83c\udffb\u200d\u2640\ufe0f\ud83e\udd3c\ud83c\udffb\u200d\u2642\ufe0f\ud83e\udd3c\ud83c\udffc\ud83e\udd3c\ud83c\udffc\u200d\u2640\ufe0f\ud83e\udd3c\ud83c\udffc\u200d\u2642\ufe0f\ud83e\udd3c\ud83c\udffd\ud83e\udd3c\ud83c\udffd\u200d\u2640\ufe0f\ud83e\udd3c\ud83c\udffd\u200d\u2642\ufe0f\ud83e\udd3c\ud83c\udffe\ud83e\udd3c\ud83c\udffe\u200d" +
        "\u2640\ufe0f\ud83e\udd3c\ud83c\udffe\u200d\u2642\ufe0f\ud83e\udd3c\ud83c\udfff\ud83e\udd3c\ud83c\udfff\u200d\u2640\ufe0f\ud83e\udd3c\ud83c\udfff\u200d\u2642\ufe0f\ud83e\udd3d\u200d\u2640\ufe0f\ud83e\udd3d\u200d\u2642\ufe0f\ud83e\udd3d\ud83c\udffb\ud83e\udd3d\ud83c\udffb\u200d\u2640\ufe0f\ud83e\udd3d\ud83c\udffb\u200d\u2642\ufe0f\ud83e\udd3d\ud83c\udffc\ud83e\udd3d\ud83c\udffc\u200d" +
        "\u2640\ufe0f\ud83e\udd3d\ud83c\udffc\u200d\u2642\ufe0f\ud83e\udd3d\ud83c\udffd\ud83e\udd3d\ud83c\udffd\u200d\u2640\ufe0f\ud83e\udd3d\ud83c\udffd\u200d\u2642\ufe0f\ud83e\udd3d\ud83c\udffe\ud83e\udd3d\ud83c\udffe\u200d\u2640\ufe0f\ud83e\udd3d\ud83c\udffe\u200d\u2642\ufe0f\ud83e\udd3d\ud83c\udfff\ud83e\udd3d\ud83c\udfff\u200d\u2640\ufe0f\ud83e\udd3d\ud83c\udfff\u200d\u2642\ufe0f\ud83e" +
        "\udd3e\u200d\u2640\ufe0f\ud83e\udd3e\u200d\u2642\ufe0f\ud83e\udd3e\ud83c\udffb\ud83e\udd3e\ud83c\udffb\u200d\u2640\ufe0f\ud83e\udd3e\ud83c\udffb\u200d\u2642\ufe0f\ud83e\udd3e\ud83c\udffc\ud83e\udd3e\ud83c\udffc\u200d\u2640\ufe0f\ud83e\udd3e\ud83c\udffc\u200d\u2642\ufe0f\ud83e\udd3e\ud83c\udffd\ud83e\udd3e\ud83c\udffd\u200d\u2640\ufe0f\ud83e\udd3e\ud83c\udffd\u200d\u2642\ufe0f\ud83e" +
        "\udd3e\ud83c\udffe\ud83e\udd3e\ud83c\udffe\u200d\u2640\ufe0f\ud83e\udd3e\ud83c\udffe\u200d\u2642\ufe0f\ud83e\udd3e\ud83c\udfff\ud83e\udd3e\ud83c\udfff\u200d\u2640\ufe0f\ud83e\udd3e\ud83c\udfff\u200d\u2642\ufe0f\ud83e\udd77\ud83c\udffb\ud83e\udd77\ud83c\udffc\ud83e\udd77\ud83c\udffd\ud83e\udd77\ud83c\udffe\ud83e\udd77\ud83c\udfff\ud83e\uddb5\ud83c\udffb\ud83e\uddb5\ud83c\udffc\ud83e" +
        "\uddb5\ud83c\udffd\ud83e\uddb5\ud83c\udffe\ud83e\uddb5\ud83c\udfff\ud83e\uddb6\ud83c\udffb\ud83e\uddb6\ud83c\udffc\ud83e\uddb6\ud83c\udffd\ud83e\uddb6\ud83c\udffe\ud83e\uddb6\ud83c\udfff\ud83e\uddb8\u200d\u2640\ufe0f\ud83e\uddb8\u200d\u2642\ufe0f\ud83e\uddb8\ud83c\udffb\ud83e\uddb8\ud83c\udffb\u200d\u2640\ufe0f\ud83e\uddb8\ud83c\udffb\u200d\u2642\ufe0f\ud83e\uddb8\ud83c\udffc\ud83e" +
        "\uddb8\ud83c\udffc\u200d\u2640\ufe0f\ud83e\uddb8\ud83c\udffc\u200d\u2642\ufe0f\ud83e\uddb8\ud83c\udffd\ud83e\uddb8\ud83c\udffd\u200d\u2640\ufe0f\ud83e\uddb8\ud83c\udffd\u200d\u2642\ufe0f\ud83e\uddb8\ud83c\udffe\ud83e\uddb8\ud83c\udffe\u200d\u2640\ufe0f\ud83e\uddb8\ud83c\udffe\u200d\u2642\ufe0f\ud83e\uddb8\ud83c\udfff\ud83e\uddb8\ud83c\udfff\u200d\u2640\ufe0f\ud83e\uddb8\ud83c\udfff" +
        "\u200d\u2642\ufe0f\ud83e\uddb9\u200d\u2640\ufe0f\ud83e\uddb9\u200d\u2642\ufe0f\ud83e\uddb9\ud83c\udffb\ud83e\uddb9\ud83c\udffb\u200d\u2640\ufe0f\ud83e\uddb9\ud83c\udffb\u200d\u2642\ufe0f\ud83e\uddb9\ud83c\udffc\ud83e\uddb9\ud83c\udffc\u200d\u2640\ufe0f\ud83e\uddb9\ud83c\udffc\u200d\u2642\ufe0f\ud83e\uddb9\ud83c\udffd\ud83e\uddb9\ud83c\udffd\u200d\u2640\ufe0f\ud83e\uddb9\ud83c\udffd" +
        "\u200d\u2642\ufe0f\ud83e\uddb9\ud83c\udffe\ud83e\uddb9\ud83c\udffe\u200d\u2640\ufe0f\ud83e\uddb9\ud83c\udffe\u200d\u2642\ufe0f\ud83e\uddb9\ud83c\udfff\ud83e\uddb9\ud83c\udfff\u200d\u2640\ufe0f\ud83e\uddb9\ud83c\udfff\u200d\u2642\ufe0f\ud83e\uddbb\ud83c\udffb\ud83e\uddbb\ud83c\udffc\ud83e\uddbb\ud83c\udffd\ud83e\uddbb\ud83c\udffe\ud83e\uddbb\ud83c\udfff\ud83e\uddcd\u200d\u2640\ufe0f" +
        "\ud83e\uddcd\u200d\u2642\ufe0f\ud83e\uddcd\ud83c\udffb\ud83e\uddcd\ud83c\udffb\u200d\u2640\ufe0f\ud83e\uddcd\ud83c\udffb\u200d\u2642\ufe0f\ud83e\uddcd\ud83c\udffc\ud83e\uddcd\ud83c\udffc\u200d\u2640\ufe0f\ud83e\uddcd\ud83c\udffc\u200d\u2642\ufe0f\ud83e\uddcd\ud83c\udffd\ud83e\uddcd\ud83c\udffd\u200d\u2640\ufe0f\ud83e\uddcd\ud83c\udffd\u200d\u2642\ufe0f\ud83e\uddcd\ud83c\udffe\ud83e" +
        "\uddcd\ud83c\udffe\u200d\u2640\ufe0f\ud83e\uddcd\ud83c\udffe\u200d\u2642\ufe0f\ud83e\uddcd\ud83c\udfff\ud83e\uddcd\ud83c\udfff\u200d\u2640\ufe0f\ud83e\uddcd\ud83c\udfff\u200d\u2642\ufe0f\ud83e\uddce\u200d\u2640\ufe0f\ud83e\uddce\u200d\u2640\ufe0f\u200d\u27a1\ufe0f\ud83e\uddce\u200d\u2642\ufe0f\ud83e\uddce\u200d\u2642\ufe0f\u200d\u27a1\ufe0f\ud83e\uddce\u200d\u27a1\ufe0f\ud83e\uddce" +
        "\ud83c\udffb\ud83e\uddce\ud83c\udffb\u200d\u2640\ufe0f\ud83e\uddce\ud83c\udffb\u200d\u2640\ufe0f\u200d\u27a1\ufe0f\ud83e\uddce\ud83c\udffb\u200d\u2642\ufe0f\ud83e\uddce\ud83c\udffb\u200d\u2642\ufe0f\u200d\u27a1\ufe0f\ud83e\uddce\ud83c\udffb\u200d\u27a1\ufe0f\ud83e\uddce\ud83c\udffc\ud83e\uddce\ud83c\udffc\u200d\u2640\ufe0f\ud83e\uddce\ud83c\udffc\u200d\u2640\ufe0f\u200d\u27a1\ufe0f" +
        "\ud83e\uddce\ud83c\udffc\u200d\u2642\ufe0f\ud83e\uddce\ud83c\udffc\u200d\u2642\ufe0f\u200d\u27a1\ufe0f\ud83e\uddce\ud83c\udffc\u200d\u27a1\ufe0f\ud83e\uddce\ud83c\udffd\ud83e\uddce\ud83c\udffd\u200d\u2640\ufe0f\ud83e\uddce\ud83c\udffd\u200d\u2640\ufe0f\u200d\u27a1\ufe0f\ud83e\uddce\ud83c\udffd\u200d\u2642\ufe0f\ud83e\uddce\ud83c\udffd\u200d\u2642\ufe0f\u200d\u27a1\ufe0f\ud83e\uddce" +
        "\ud83c\udffd\u200d\u27a1\ufe0f\ud83e\uddce\ud83c\udffe\ud83e\uddce\ud83c\udffe\u200d\u2640\ufe0f\ud83e\uddce\ud83c\udffe\u200d\u2640\ufe0f\u200d\u27a1\ufe0f\ud83e\uddce\ud83c\udffe\u200d\u2642\ufe0f\ud83e\uddce\ud83c\udffe\u200d\u2642\ufe0f\u200d\u27a1\ufe0f\ud83e\uddce\ud83c\udffe\u200d\u27a1\ufe0f\ud83e\uddce\ud83c\udfff\ud83e\uddce\ud83c\udfff\u200d\u2640\ufe0f\ud83e\uddce\ud83c" +
        "\udfff\u200d\u2640\ufe0f\u200d\u27a1\ufe0f\ud83e\uddce\ud83c\udfff\u200d\u2642\ufe0f\ud83e\uddce\ud83c\udfff\u200d\u2642\ufe0f\u200d\u27a1\ufe0f\ud83e\uddce\ud83c\udfff\u200d\u27a1\ufe0f\ud83e\uddcf\u200d\u2640\ufe0f\ud83e\uddcf\u200d\u2642\ufe0f\ud83e\uddcf\ud83c\udffb\ud83e\uddcf\ud83c\udffb\u200d\u2640\ufe0f\ud83e\uddcf\ud83c\udffb\u200d\u2642\ufe0f\ud83e\uddcf\ud83c\udffc\ud83e" +
        "\uddcf\ud83c\udffc\u200d\u2640\ufe0f\ud83e\uddcf\ud83c\udffc\u200d\u2642\ufe0f\ud83e\uddcf\ud83c\udffd\ud83e\uddcf\ud83c\udffd\u200d\u2640\ufe0f\ud83e\uddcf\ud83c\udffd\u200d\u2642\ufe0f\ud83e\uddcf\ud83c\udffe\ud83e\uddcf\ud83c\udffe\u200d\u2640\ufe0f\ud83e\uddcf\ud83c\udffe\u200d\u2642\ufe0f\ud83e\uddcf\ud83c\udfff\ud83e\uddcf\ud83c\udfff\u200d\u2640\ufe0f\ud83e\uddcf\ud83c\udfff" +
        "\u200d\u2642\ufe0f\ud83e\uddd1\u200d\u2695\ufe0f\ud83e\uddd1\u200d\u2696\ufe0f\ud83e\uddd1\u200d\u2708\ufe0f\ud83e\uddd1\u200d\ud83c\udf3e\ud83e\uddd1\u200d\ud83c\udf73\ud83e\uddd1\u200d\ud83c\udf7c\ud83e\uddd1\u200d\ud83c\udf84\ud83e\uddd1\u200d\ud83c\udf93\ud83e\uddd1\u200d\ud83c\udfa4\ud83e\uddd1\u200d\ud83c\udfa8\ud83e\uddd1\u200d\ud83c\udfeb\ud83e\uddd1\u200d\ud83c\udfed\ud83e" +
        "\uddd1\u200d\ud83d\udcbb\ud83e\uddd1\u200d\ud83d\udcbc\ud83e\uddd1\u200d\ud83d\udd27\ud83e\uddd1\u200d\ud83d\udd2c\ud83e\uddd1\u200d\ud83d\ude80\ud83e\uddd1\u200d\ud83d\ude92\ud83e\uddd1\u200d\ud83e\udd1d\u200d\ud83e\uddd1\ud83e\uddd1\u200d\ud83e\uddaf\ud83e\uddd1\u200d\ud83e\uddaf\u200d\u27a1\ufe0f\ud83e\uddd1\u200d\ud83e\uddb0\ud83e\uddd1\u200d\ud83e\uddb1\ud83e\uddd1\u200d\ud83e" +
        "\uddb2\ud83e\uddd1\u200d\ud83e\uddb3\ud83e\uddd1\u200d\ud83e\uddbc\ud83e\uddd1\u200d\ud83e\uddbc\u200d\u27a1\ufe0f\ud83e\uddd1\u200d\ud83e\uddbd\ud83e\uddd1\u200d\ud83e\uddbd\u200d\u27a1\ufe0f\ud83e\uddd1\u200d\ud83e\uddd1\u200d\ud83e\uddd2\ud83e\uddd1\u200d\ud83e\uddd1\u200d\ud83e\uddd2\u200d\ud83e\uddd2\ud83e\uddd1\u200d\ud83e\uddd2\ud83e\uddd1\u200d\ud83e\uddd2\u200d\ud83e\uddd2" +
        "\ud83e\uddd1\u200d\ud83e\ude70\ud83e\uddd1\ud83c\udffb\ud83e\uddd1\ud83c\udffb\u200d\u2695\ufe0f\ud83e\uddd1\ud83c\udffb\u200d\u2696\ufe0f\ud83e\uddd1\ud83c\udffb\u200d\u2708\ufe0f\ud83e\uddd1\ud83c\udffb\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83e\uddd1\ud83c\udffc\ud83e\uddd1\ud83c\udffb\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83e\uddd1\ud83c\udffd\ud83e\uddd1\ud83c\udffb" +
        "\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83e\uddd1\ud83c\udffe\ud83e\uddd1\ud83c\udffb\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83e\uddd1\ud83c\udfff\ud83e\uddd1\ud83c\udffb\u200d\u2764\ufe0f\u200d\ud83e\uddd1\ud83c\udffc\ud83e\uddd1\ud83c\udffb\u200d\u2764\ufe0f\u200d\ud83e\uddd1\ud83c\udffd\ud83e\uddd1\ud83c\udffb\u200d\u2764\ufe0f\u200d\ud83e\uddd1\ud83c\udffe\ud83e\uddd1" +
        "\ud83c\udffb\u200d\u2764\ufe0f\u200d\ud83e\uddd1\ud83c\udfff\ud83e\uddd1\ud83c\udffb\u200d\ud83c\udf3e\ud83e\uddd1\ud83c\udffb\u200d\ud83c\udf73\ud83e\uddd1\ud83c\udffb\u200d\ud83c\udf7c\ud83e\uddd1\ud83c\udffb\u200d\ud83c\udf84\ud83e\uddd1\ud83c\udffb\u200d\ud83c\udf93\ud83e\uddd1\ud83c\udffb\u200d\ud83c\udfa4\ud83e\uddd1\ud83c\udffb\u200d\ud83c\udfa8\ud83e\uddd1\ud83c\udffb\u200d" +
        "\ud83c\udfeb\ud83e\uddd1\ud83c\udffb\u200d\ud83c\udfed\ud83e\uddd1\ud83c\udffb\u200d\ud83d\udc30\u200d\ud83e\uddd1\ud83c\udffc\ud83e\uddd1\ud83c\udffb\u200d\ud83d\udc30\u200d\ud83e\uddd1\ud83c\udffd\ud83e\uddd1\ud83c\udffb\u200d\ud83d\udc30\u200d\ud83e\uddd1\ud83c\udffe\ud83e\uddd1\ud83c\udffb\u200d\ud83d\udc30\u200d\ud83e\uddd1\ud83c\udfff\ud83e\uddd1\ud83c\udffb\u200d\ud83d\udcbb" +
        "\ud83e\uddd1\ud83c\udffb\u200d\ud83d\udcbc\ud83e\uddd1\ud83c\udffb\u200d\ud83d\udd27\ud83e\uddd1\ud83c\udffb\u200d\ud83d\udd2c\ud83e\uddd1\ud83c\udffb\u200d\ud83d\ude80\ud83e\uddd1\ud83c\udffb\u200d\ud83d\ude92\ud83e\uddd1\ud83c\udffb\u200d\ud83e\udd1d\u200d\ud83e\uddd1\ud83c\udffb\ud83e\uddd1\ud83c\udffb\u200d\ud83e\udd1d\u200d\ud83e\uddd1\ud83c\udffc\ud83e\uddd1\ud83c\udffb\u200d" +
        "\ud83e\udd1d\u200d\ud83e\uddd1\ud83c\udffd\ud83e\uddd1\ud83c\udffb\u200d\ud83e\udd1d\u200d\ud83e\uddd1\ud83c\udffe\ud83e\uddd1\ud83c\udffb\u200d\ud83e\udd1d\u200d\ud83e\uddd1\ud83c\udfff\ud83e\uddd1\ud83c\udffb\u200d\ud83e\uddaf\ud83e\uddd1\ud83c\udffb\u200d\ud83e\uddaf\u200d\u27a1\ufe0f\ud83e\uddd1\ud83c\udffb\u200d\ud83e\uddb0\ud83e\uddd1\ud83c\udffb\u200d\ud83e\uddb1\ud83e\uddd1" +
        "\ud83c\udffb\u200d\ud83e\uddb2\ud83e\uddd1\ud83c\udffb\u200d\ud83e\uddb3\ud83e\uddd1\ud83c\udffb\u200d\ud83e\uddbc\ud83e\uddd1\ud83c\udffb\u200d\ud83e\uddbc\u200d\u27a1\ufe0f\ud83e\uddd1\ud83c\udffb\u200d\ud83e\uddbd\ud83e\uddd1\ud83c\udffb\u200d\ud83e\uddbd\u200d\u27a1\ufe0f\ud83e\uddd1\ud83c\udffb\u200d\ud83e\ude70\ud83e\uddd1\ud83c\udffb\u200d\ud83e\udeef\u200d\ud83e\uddd1\ud83c" +
        "\udffc\ud83e\uddd1\ud83c\udffb\u200d\ud83e\udeef\u200d\ud83e\uddd1\ud83c\udffd\ud83e\uddd1\ud83c\udffb\u200d\ud83e\udeef\u200d\ud83e\uddd1\ud83c\udffe\ud83e\uddd1\ud83c\udffb\u200d\ud83e\udeef\u200d\ud83e\uddd1\ud83c\udfff\ud83e\uddd1\ud83c\udffc\ud83e\uddd1\ud83c\udffc\u200d\u2695\ufe0f\ud83e\uddd1\ud83c\udffc\u200d\u2696\ufe0f\ud83e\uddd1\ud83c\udffc\u200d\u2708\ufe0f\ud83e\uddd1" +
        "\ud83c\udffc\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83e\uddd1\ud83c\udffb\ud83e\uddd1\ud83c\udffc\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83e\uddd1\ud83c\udffd\ud83e\uddd1\ud83c\udffc\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83e\uddd1\ud83c\udffe\ud83e\uddd1\ud83c\udffc\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83e\uddd1\ud83c\udfff\ud83e\uddd1\ud83c\udffc\u200d\u2764" +
        "\ufe0f\u200d\ud83e\uddd1\ud83c\udffb\ud83e\uddd1\ud83c\udffc\u200d\u2764\ufe0f\u200d\ud83e\uddd1\ud83c\udffd\ud83e\uddd1\ud83c\udffc\u200d\u2764\ufe0f\u200d\ud83e\uddd1\ud83c\udffe\ud83e\uddd1\ud83c\udffc\u200d\u2764\ufe0f\u200d\ud83e\uddd1\ud83c\udfff\ud83e\uddd1\ud83c\udffc\u200d\ud83c\udf3e\ud83e\uddd1\ud83c\udffc\u200d\ud83c\udf73\ud83e\uddd1\ud83c\udffc\u200d\ud83c\udf7c\ud83e" +
        "\uddd1\ud83c\udffc\u200d\ud83c\udf84\ud83e\uddd1\ud83c\udffc\u200d\ud83c\udf93\ud83e\uddd1\ud83c\udffc\u200d\ud83c\udfa4\ud83e\uddd1\ud83c\udffc\u200d\ud83c\udfa8\ud83e\uddd1\ud83c\udffc\u200d\ud83c\udfeb\ud83e\uddd1\ud83c\udffc\u200d\ud83c\udfed\ud83e\uddd1\ud83c\udffc\u200d\ud83d\udc30\u200d\ud83e\uddd1\ud83c\udffb\ud83e\uddd1\ud83c\udffc\u200d\ud83d\udc30\u200d\ud83e\uddd1\ud83c" +
        "\udffd\ud83e\uddd1\ud83c\udffc\u200d\ud83d\udc30\u200d\ud83e\uddd1\ud83c\udffe\ud83e\uddd1\ud83c\udffc\u200d\ud83d\udc30\u200d\ud83e\uddd1\ud83c\udfff\ud83e\uddd1\ud83c\udffc\u200d\ud83d\udcbb\ud83e\uddd1\ud83c\udffc\u200d\ud83d\udcbc\ud83e\uddd1\ud83c\udffc\u200d\ud83d\udd27\ud83e\uddd1\ud83c\udffc\u200d\ud83d\udd2c\ud83e\uddd1\ud83c\udffc\u200d\ud83d\ude80\ud83e\uddd1\ud83c\udffc" +
        "\u200d\ud83d\ude92\ud83e\uddd1\ud83c\udffc\u200d\ud83e\udd1d\u200d\ud83e\uddd1\ud83c\udffb\ud83e\uddd1\ud83c\udffc\u200d\ud83e\udd1d\u200d\ud83e\uddd1\ud83c\udffc\ud83e\uddd1\ud83c\udffc\u200d\ud83e\udd1d\u200d\ud83e\uddd1\ud83c\udffd\ud83e\uddd1\ud83c\udffc\u200d\ud83e\udd1d\u200d\ud83e\uddd1\ud83c\udffe\ud83e\uddd1\ud83c\udffc\u200d\ud83e\udd1d\u200d\ud83e\uddd1\ud83c\udfff\ud83e" +
        "\uddd1\ud83c\udffc\u200d\ud83e\uddaf\ud83e\uddd1\ud83c\udffc\u200d\ud83e\uddaf\u200d\u27a1\ufe0f\ud83e\uddd1\ud83c\udffc\u200d\ud83e\uddb0\ud83e\uddd1\ud83c\udffc\u200d\ud83e\uddb1\ud83e\uddd1\ud83c\udffc\u200d\ud83e\uddb2\ud83e\uddd1\ud83c\udffc\u200d\ud83e\uddb3\ud83e\uddd1\ud83c\udffc\u200d\ud83e\uddbc\ud83e\uddd1\ud83c\udffc\u200d\ud83e\uddbc\u200d\u27a1\ufe0f\ud83e\uddd1\ud83c" +
        "\udffc\u200d\ud83e\uddbd\ud83e\uddd1\ud83c\udffc\u200d\ud83e\uddbd\u200d\u27a1\ufe0f\ud83e\uddd1\ud83c\udffc\u200d\ud83e\ude70\ud83e\uddd1\ud83c\udffc\u200d\ud83e\udeef\u200d\ud83e\uddd1\ud83c\udffb\ud83e\uddd1\ud83c\udffc\u200d\ud83e\udeef\u200d\ud83e\uddd1\ud83c\udffd\ud83e\uddd1\ud83c\udffc\u200d\ud83e\udeef\u200d\ud83e\uddd1\ud83c\udffe\ud83e\uddd1\ud83c\udffc\u200d\ud83e\udeef" +
        "\u200d\ud83e\uddd1\ud83c\udfff\ud83e\uddd1\ud83c\udffd\ud83e\uddd1\ud83c\udffd\u200d\u2695\ufe0f\ud83e\uddd1\ud83c\udffd\u200d\u2696\ufe0f\ud83e\uddd1\ud83c\udffd\u200d\u2708\ufe0f\ud83e\uddd1\ud83c\udffd\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83e\uddd1\ud83c\udffb\ud83e\uddd1\ud83c\udffd\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83e\uddd1\ud83c\udffc\ud83e\uddd1\ud83c\udffd" +
        "\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83e\uddd1\ud83c\udffe\ud83e\uddd1\ud83c\udffd\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83e\uddd1\ud83c\udfff\ud83e\uddd1\ud83c\udffd\u200d\u2764\ufe0f\u200d\ud83e\uddd1\ud83c\udffb\ud83e\uddd1\ud83c\udffd\u200d\u2764\ufe0f\u200d\ud83e\uddd1\ud83c\udffc\ud83e\uddd1\ud83c\udffd\u200d\u2764\ufe0f\u200d\ud83e\uddd1\ud83c\udffe\ud83e\uddd1" +
        "\ud83c\udffd\u200d\u2764\ufe0f\u200d\ud83e\uddd1\ud83c\udfff\ud83e\uddd1\ud83c\udffd\u200d\ud83c\udf3e\ud83e\uddd1\ud83c\udffd\u200d\ud83c\udf73\ud83e\uddd1\ud83c\udffd\u200d\ud83c\udf7c\ud83e\uddd1\ud83c\udffd\u200d\ud83c\udf84\ud83e\uddd1\ud83c\udffd\u200d\ud83c\udf93\ud83e\uddd1\ud83c\udffd\u200d\ud83c\udfa4\ud83e\uddd1\ud83c\udffd\u200d\ud83c\udfa8\ud83e\uddd1\ud83c\udffd\u200d" +
        "\ud83c\udfeb\ud83e\uddd1\ud83c\udffd\u200d\ud83c\udfed\ud83e\uddd1\ud83c\udffd\u200d\ud83d\udc30\u200d\ud83e\uddd1\ud83c\udffb\ud83e\uddd1\ud83c\udffd\u200d\ud83d\udc30\u200d\ud83e\uddd1\ud83c\udffc\ud83e\uddd1\ud83c\udffd\u200d\ud83d\udc30\u200d\ud83e\uddd1\ud83c\udffe\ud83e\uddd1\ud83c\udffd\u200d\ud83d\udc30\u200d\ud83e\uddd1\ud83c\udfff\ud83e\uddd1\ud83c\udffd\u200d\ud83d\udcbb" +
        "\ud83e\uddd1\ud83c\udffd\u200d\ud83d\udcbc\ud83e\uddd1\ud83c\udffd\u200d\ud83d\udd27\ud83e\uddd1\ud83c\udffd\u200d\ud83d\udd2c\ud83e\uddd1\ud83c\udffd\u200d\ud83d\ude80\ud83e\uddd1\ud83c\udffd\u200d\ud83d\ude92\ud83e\uddd1\ud83c\udffd\u200d\ud83e\udd1d\u200d\ud83e\uddd1\ud83c\udffb\ud83e\uddd1\ud83c\udffd\u200d\ud83e\udd1d\u200d\ud83e\uddd1\ud83c\udffc\ud83e\uddd1\ud83c\udffd\u200d" +
        "\ud83e\udd1d\u200d\ud83e\uddd1\ud83c\udffd\ud83e\uddd1\ud83c\udffd\u200d\ud83e\udd1d\u200d\ud83e\uddd1\ud83c\udffe\ud83e\uddd1\ud83c\udffd\u200d\ud83e\udd1d\u200d\ud83e\uddd1\ud83c\udfff\ud83e\uddd1\ud83c\udffd\u200d\ud83e\uddaf\ud83e\uddd1\ud83c\udffd\u200d\ud83e\uddaf\u200d\u27a1\ufe0f\ud83e\uddd1\ud83c\udffd\u200d\ud83e\uddb0\ud83e\uddd1\ud83c\udffd\u200d\ud83e\uddb1\ud83e\uddd1" +
        "\ud83c\udffd\u200d\ud83e\uddb2\ud83e\uddd1\ud83c\udffd\u200d\ud83e\uddb3\ud83e\uddd1\ud83c\udffd\u200d\ud83e\uddbc\ud83e\uddd1\ud83c\udffd\u200d\ud83e\uddbc\u200d\u27a1\ufe0f\ud83e\uddd1\ud83c\udffd\u200d\ud83e\uddbd\ud83e\uddd1\ud83c\udffd\u200d\ud83e\uddbd\u200d\u27a1\ufe0f\ud83e\uddd1\ud83c\udffd\u200d\ud83e\ude70\ud83e\uddd1\ud83c\udffd\u200d\ud83e\udeef\u200d\ud83e\uddd1\ud83c" +
        "\udffb\ud83e\uddd1\ud83c\udffd\u200d\ud83e\udeef\u200d\ud83e\uddd1\ud83c\udffc\ud83e\uddd1\ud83c\udffd\u200d\ud83e\udeef\u200d\ud83e\uddd1\ud83c\udffe\ud83e\uddd1\ud83c\udffd\u200d\ud83e\udeef\u200d\ud83e\uddd1\ud83c\udfff\ud83e\uddd1\ud83c\udffe\ud83e\uddd1\ud83c\udffe\u200d\u2695\ufe0f\ud83e\uddd1\ud83c\udffe\u200d\u2696\ufe0f\ud83e\uddd1\ud83c\udffe\u200d\u2708\ufe0f\ud83e\uddd1" +
        "\ud83c\udffe\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83e\uddd1\ud83c\udffb\ud83e\uddd1\ud83c\udffe\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83e\uddd1\ud83c\udffc\ud83e\uddd1\ud83c\udffe\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83e\uddd1\ud83c\udffd\ud83e\uddd1\ud83c\udffe\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83e\uddd1\ud83c\udfff\ud83e\uddd1\ud83c\udffe\u200d\u2764" +
        "\ufe0f\u200d\ud83e\uddd1\ud83c\udffb\ud83e\uddd1\ud83c\udffe\u200d\u2764\ufe0f\u200d\ud83e\uddd1\ud83c\udffc\ud83e\uddd1\ud83c\udffe\u200d\u2764\ufe0f\u200d\ud83e\uddd1\ud83c\udffd\ud83e\uddd1\ud83c\udffe\u200d\u2764\ufe0f\u200d\ud83e\uddd1\ud83c\udfff\ud83e\uddd1\ud83c\udffe\u200d\ud83c\udf3e\ud83e\uddd1\ud83c\udffe\u200d\ud83c\udf73\ud83e\uddd1\ud83c\udffe\u200d\ud83c\udf7c\ud83e" +
        "\uddd1\ud83c\udffe\u200d\ud83c\udf84\ud83e\uddd1\ud83c\udffe\u200d\ud83c\udf93\ud83e\uddd1\ud83c\udffe\u200d\ud83c\udfa4\ud83e\uddd1\ud83c\udffe\u200d\ud83c\udfa8\ud83e\uddd1\ud83c\udffe\u200d\ud83c\udfeb\ud83e\uddd1\ud83c\udffe\u200d\ud83c\udfed\ud83e\uddd1\ud83c\udffe\u200d\ud83d\udc30\u200d\ud83e\uddd1\ud83c\udffb\ud83e\uddd1\ud83c\udffe\u200d\ud83d\udc30\u200d\ud83e\uddd1\ud83c" +
        "\udffc\ud83e\uddd1\ud83c\udffe\u200d\ud83d\udc30\u200d\ud83e\uddd1\ud83c\udffd\ud83e\uddd1\ud83c\udffe\u200d\ud83d\udc30\u200d\ud83e\uddd1\ud83c\udfff\ud83e\uddd1\ud83c\udffe\u200d\ud83d\udcbb\ud83e\uddd1\ud83c\udffe\u200d\ud83d\udcbc\ud83e\uddd1\ud83c\udffe\u200d\ud83d\udd27\ud83e\uddd1\ud83c\udffe\u200d\ud83d\udd2c\ud83e\uddd1\ud83c\udffe\u200d\ud83d\ude80\ud83e\uddd1\ud83c\udffe" +
        "\u200d\ud83d\ude92\ud83e\uddd1\ud83c\udffe\u200d\ud83e\udd1d\u200d\ud83e\uddd1\ud83c\udffb\ud83e\uddd1\ud83c\udffe\u200d\ud83e\udd1d\u200d\ud83e\uddd1\ud83c\udffc\ud83e\uddd1\ud83c\udffe\u200d\ud83e\udd1d\u200d\ud83e\uddd1\ud83c\udffd\ud83e\uddd1\ud83c\udffe\u200d\ud83e\udd1d\u200d\ud83e\uddd1\ud83c\udffe\ud83e\uddd1\ud83c\udffe\u200d\ud83e\udd1d\u200d\ud83e\uddd1\ud83c\udfff\ud83e" +
        "\uddd1\ud83c\udffe\u200d\ud83e\uddaf\ud83e\uddd1\ud83c\udffe\u200d\ud83e\uddaf\u200d\u27a1\ufe0f\ud83e\uddd1\ud83c\udffe\u200d\ud83e\uddb0\ud83e\uddd1\ud83c\udffe\u200d\ud83e\uddb1\ud83e\uddd1\ud83c\udffe\u200d\ud83e\uddb2\ud83e\uddd1\ud83c\udffe\u200d\ud83e\uddb3\ud83e\uddd1\ud83c\udffe\u200d\ud83e\uddbc\ud83e\uddd1\ud83c\udffe\u200d\ud83e\uddbc\u200d\u27a1\ufe0f\ud83e\uddd1\ud83c" +
        "\udffe\u200d\ud83e\uddbd\ud83e\uddd1\ud83c\udffe\u200d\ud83e\uddbd\u200d\u27a1\ufe0f\ud83e\uddd1\ud83c\udffe\u200d\ud83e\ude70\ud83e\uddd1\ud83c\udffe\u200d\ud83e\udeef\u200d\ud83e\uddd1\ud83c\udffb\ud83e\uddd1\ud83c\udffe\u200d\ud83e\udeef\u200d\ud83e\uddd1\ud83c\udffc\ud83e\uddd1\ud83c\udffe\u200d\ud83e\udeef\u200d\ud83e\uddd1\ud83c\udffd\ud83e\uddd1\ud83c\udffe\u200d\ud83e\udeef" +
        "\u200d\ud83e\uddd1\ud83c\udfff\ud83e\uddd1\ud83c\udfff\ud83e\uddd1\ud83c\udfff\u200d\u2695\ufe0f\ud83e\uddd1\ud83c\udfff\u200d\u2696\ufe0f\ud83e\uddd1\ud83c\udfff\u200d\u2708\ufe0f\ud83e\uddd1\ud83c\udfff\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83e\uddd1\ud83c\udffb\ud83e\uddd1\ud83c\udfff\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83e\uddd1\ud83c\udffc\ud83e\uddd1\ud83c\udfff" +
        "\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83e\uddd1\ud83c\udffd\ud83e\uddd1\ud83c\udfff\u200d\u2764\ufe0f\u200d\ud83d\udc8b\u200d\ud83e\uddd1\ud83c\udffe\ud83e\uddd1\ud83c\udfff\u200d\u2764\ufe0f\u200d\ud83e\uddd1\ud83c\udffb\ud83e\uddd1\ud83c\udfff\u200d\u2764\ufe0f\u200d\ud83e\uddd1\ud83c\udffc\ud83e\uddd1\ud83c\udfff\u200d\u2764\ufe0f\u200d\ud83e\uddd1\ud83c\udffd\ud83e\uddd1" +
        "\ud83c\udfff\u200d\u2764\ufe0f\u200d\ud83e\uddd1\ud83c\udffe\ud83e\uddd1\ud83c\udfff\u200d\ud83c\udf3e\ud83e\uddd1\ud83c\udfff\u200d\ud83c\udf73\ud83e\uddd1\ud83c\udfff\u200d\ud83c\udf7c\ud83e\uddd1\ud83c\udfff\u200d\ud83c\udf84\ud83e\uddd1\ud83c\udfff\u200d\ud83c\udf93\ud83e\uddd1\ud83c\udfff\u200d\ud83c\udfa4\ud83e\uddd1\ud83c\udfff\u200d\ud83c\udfa8\ud83e\uddd1\ud83c\udfff\u200d" +
        "\ud83c\udfeb\ud83e\uddd1\ud83c\udfff\u200d\ud83c\udfed\ud83e\uddd1\ud83c\udfff\u200d\ud83d\udc30\u200d\ud83e\uddd1\ud83c\udffb\ud83e\uddd1\ud83c\udfff\u200d\ud83d\udc30\u200d\ud83e\uddd1\ud83c\udffc\ud83e\uddd1\ud83c\udfff\u200d\ud83d\udc30\u200d\ud83e\uddd1\ud83c\udffd\ud83e\uddd1\ud83c\udfff\u200d\ud83d\udc30\u200d\ud83e\uddd1\ud83c\udffe\ud83e\uddd1\ud83c\udfff\u200d\ud83d\udcbb" +
        "\ud83e\uddd1\ud83c\udfff\u200d\ud83d\udcbc\ud83e\uddd1\ud83c\udfff\u200d\ud83d\udd27\ud83e\uddd1\ud83c\udfff\u200d\ud83d\udd2c\ud83e\uddd1\ud83c\udfff\u200d\ud83d\ude80\ud83e\uddd1\ud83c\udfff\u200d\ud83d\ude92\ud83e\uddd1\ud83c\udfff\u200d\ud83e\udd1d\u200d\ud83e\uddd1\ud83c\udffb\ud83e\uddd1\ud83c\udfff\u200d\ud83e\udd1d\u200d\ud83e\uddd1\ud83c\udffc\ud83e\uddd1\ud83c\udfff\u200d" +
        "\ud83e\udd1d\u200d\ud83e\uddd1\ud83c\udffd\ud83e\uddd1\ud83c\udfff\u200d\ud83e\udd1d\u200d\ud83e\uddd1\ud83c\udffe\ud83e\uddd1\ud83c\udfff\u200d\ud83e\udd1d\u200d\ud83e\uddd1\ud83c\udfff\ud83e\uddd1\ud83c\udfff\u200d\ud83e\uddaf\ud83e\uddd1\ud83c\udfff\u200d\ud83e\uddaf\u200d\u27a1\ufe0f\ud83e\uddd1\ud83c\udfff\u200d\ud83e\uddb0\ud83e\uddd1\ud83c\udfff\u200d\ud83e\uddb1\ud83e\uddd1" +
        "\ud83c\udfff\u200d\ud83e\uddb2\ud83e\uddd1\ud83c\udfff\u200d\ud83e\uddb3\ud83e\uddd1\ud83c\udfff\u200d\ud83e\uddbc\ud83e\uddd1\ud83c\udfff\u200d\ud83e\uddbc\u200d\u27a1\ufe0f\ud83e\uddd1\ud83c\udfff\u200d\ud83e\uddbd\ud83e\uddd1\ud83c\udfff\u200d\ud83e\uddbd\u200d\u27a1\ufe0f\ud83e\uddd1\ud83c\udfff\u200d\ud83e\ude70\ud83e\uddd1\ud83c\udfff\u200d\ud83e\udeef\u200d\ud83e\uddd1\ud83c" +
        "\udffb\ud83e\uddd1\ud83c\udfff\u200d\ud83e\udeef\u200d\ud83e\uddd1\ud83c\udffc\ud83e\uddd1\ud83c\udfff\u200d\ud83e\udeef\u200d\ud83e\uddd1\ud83c\udffd\ud83e\uddd1\ud83c\udfff\u200d\ud83e\udeef\u200d\ud83e\uddd1\ud83c\udffe\ud83e\uddd2\ud83c\udffb\ud83e\uddd2\ud83c\udffc\ud83e\uddd2\ud83c\udffd\ud83e\uddd2\ud83c\udffe\ud83e\uddd2\ud83c\udfff\ud83e\uddd3\ud83c\udffb\ud83e\uddd3\ud83c" +
        "\udffc\ud83e\uddd3\ud83c\udffd\ud83e\uddd3\ud83c\udffe\ud83e\uddd3\ud83c\udfff\ud83e\uddd4\u200d\u2640\ufe0f\ud83e\uddd4\u200d\u2642\ufe0f\ud83e\uddd4\ud83c\udffb\ud83e\uddd4\ud83c\udffb\u200d\u2640\ufe0f\ud83e\uddd4\ud83c\udffb\u200d\u2642\ufe0f\ud83e\uddd4\ud83c\udffc\ud83e\uddd4\ud83c\udffc\u200d\u2640\ufe0f\ud83e\uddd4\ud83c\udffc\u200d\u2642\ufe0f\ud83e\uddd4\ud83c\udffd\ud83e" +
        "\uddd4\ud83c\udffd\u200d\u2640\ufe0f\ud83e\uddd4\ud83c\udffd\u200d\u2642\ufe0f\ud83e\uddd4\ud83c\udffe\ud83e\uddd4\ud83c\udffe\u200d\u2640\ufe0f\ud83e\uddd4\ud83c\udffe\u200d\u2642\ufe0f\ud83e\uddd4\ud83c\udfff\ud83e\uddd4\ud83c\udfff\u200d\u2640\ufe0f\ud83e\uddd4\ud83c\udfff\u200d\u2642\ufe0f\ud83e\uddd5\ud83c\udffb\ud83e\uddd5\ud83c\udffc\ud83e\uddd5\ud83c\udffd\ud83e\uddd5\ud83c" +
        "\udffe\ud83e\uddd5\ud83c\udfff\ud83e\uddd6\u200d\u2640\ufe0f\ud83e\uddd6\u200d\u2642\ufe0f\ud83e\uddd6\ud83c\udffb\ud83e\uddd6\ud83c\udffb\u200d\u2640\ufe0f\ud83e\uddd6\ud83c\udffb\u200d\u2642\ufe0f\ud83e\uddd6\ud83c\udffc\ud83e\uddd6\ud83c\udffc\u200d\u2640\ufe0f\ud83e\uddd6\ud83c\udffc\u200d\u2642\ufe0f\ud83e\uddd6\ud83c\udffd\ud83e\uddd6\ud83c\udffd\u200d\u2640\ufe0f\ud83e\uddd6" +
        "\ud83c\udffd\u200d\u2642\ufe0f\ud83e\uddd6\ud83c\udffe\ud83e\uddd6\ud83c\udffe\u200d\u2640\ufe0f\ud83e\uddd6\ud83c\udffe\u200d\u2642\ufe0f\ud83e\uddd6\ud83c\udfff\ud83e\uddd6\ud83c\udfff\u200d\u2640\ufe0f\ud83e\uddd6\ud83c\udfff\u200d\u2642\ufe0f\ud83e\uddd7\u200d\u2640\ufe0f\ud83e\uddd7\u200d\u2642\ufe0f\ud83e\uddd7\ud83c\udffb\ud83e\uddd7\ud83c\udffb\u200d\u2640\ufe0f\ud83e\uddd7" +
        "\ud83c\udffb\u200d\u2642\ufe0f\ud83e\uddd7\ud83c\udffc\ud83e\uddd7\ud83c\udffc\u200d\u2640\ufe0f\ud83e\uddd7\ud83c\udffc\u200d\u2642\ufe0f\ud83e\uddd7\ud83c\udffd\ud83e\uddd7\ud83c\udffd\u200d\u2640\ufe0f\ud83e\uddd7\ud83c\udffd\u200d\u2642\ufe0f\ud83e\uddd7\ud83c\udffe\ud83e\uddd7\ud83c\udffe\u200d\u2640\ufe0f\ud83e\uddd7\ud83c\udffe\u200d\u2642\ufe0f\ud83e\uddd7\ud83c\udfff\ud83e" +
        "\uddd7\ud83c\udfff\u200d\u2640\ufe0f\ud83e\uddd7\ud83c\udfff\u200d\u2642\ufe0f\ud83e\uddd8\u200d\u2640\ufe0f\ud83e\uddd8\u200d\u2642\ufe0f\ud83e\uddd8\ud83c\udffb\ud83e\uddd8\ud83c\udffb\u200d\u2640\ufe0f\ud83e\uddd8\ud83c\udffb\u200d\u2642\ufe0f\ud83e\uddd8\ud83c\udffc\ud83e\uddd8\ud83c\udffc\u200d\u2640\ufe0f\ud83e\uddd8\ud83c\udffc\u200d\u2642\ufe0f\ud83e\uddd8\ud83c\udffd\ud83e" +
        "\uddd8\ud83c\udffd\u200d\u2640\ufe0f\ud83e\uddd8\ud83c\udffd\u200d\u2642\ufe0f\ud83e\uddd8\ud83c\udffe\ud83e\uddd8\ud83c\udffe\u200d\u2640\ufe0f\ud83e\uddd8\ud83c\udffe\u200d\u2642\ufe0f\ud83e\uddd8\ud83c\udfff\ud83e\uddd8\ud83c\udfff\u200d\u2640\ufe0f\ud83e\uddd8\ud83c\udfff\u200d\u2642\ufe0f\ud83e\uddd9\u200d\u2640\ufe0f\ud83e\uddd9\u200d\u2642\ufe0f\ud83e\uddd9\ud83c\udffb\ud83e" +
        "\uddd9\ud83c\udffb\u200d\u2640\ufe0f\ud83e\uddd9\ud83c\udffb\u200d\u2642\ufe0f\ud83e\uddd9\ud83c\udffc\ud83e\uddd9\ud83c\udffc\u200d\u2640\ufe0f\ud83e\uddd9\ud83c\udffc\u200d\u2642\ufe0f\ud83e\uddd9\ud83c\udffd\ud83e\uddd9\ud83c\udffd\u200d\u2640\ufe0f\ud83e\uddd9\ud83c\udffd\u200d\u2642\ufe0f\ud83e\uddd9\ud83c\udffe\ud83e\uddd9\ud83c\udffe\u200d\u2640\ufe0f\ud83e\uddd9\ud83c\udffe" +
        "\u200d\u2642\ufe0f\ud83e\uddd9\ud83c\udfff\ud83e\uddd9\ud83c\udfff\u200d\u2640\ufe0f\ud83e\uddd9\ud83c\udfff\u200d\u2642\ufe0f\ud83e\uddda\u200d\u2640\ufe0f\ud83e\uddda\u200d\u2642\ufe0f\ud83e\uddda\ud83c\udffb\ud83e\uddda\ud83c\udffb\u200d\u2640\ufe0f\ud83e\uddda\ud83c\udffb\u200d\u2642\ufe0f\ud83e\uddda\ud83c\udffc\ud83e\uddda\ud83c\udffc\u200d\u2640\ufe0f\ud83e\uddda\ud83c\udffc" +
        "\u200d\u2642\ufe0f\ud83e\uddda\ud83c\udffd\ud83e\uddda\ud83c\udffd\u200d\u2640\ufe0f\ud83e\uddda\ud83c\udffd\u200d\u2642\ufe0f\ud83e\uddda\ud83c\udffe\ud83e\uddda\ud83c\udffe\u200d\u2640\ufe0f\ud83e\uddda\ud83c\udffe\u200d\u2642\ufe0f\ud83e\uddda\ud83c\udfff\ud83e\uddda\ud83c\udfff\u200d\u2640\ufe0f\ud83e\uddda\ud83c\udfff\u200d\u2642\ufe0f\ud83e\udddb\u200d\u2640\ufe0f\ud83e\udddb" +
        "\u200d\u2642\ufe0f\ud83e\udddb\ud83c\udffb\ud83e\udddb\ud83c\udffb\u200d\u2640\ufe0f\ud83e\udddb\ud83c\udffb\u200d\u2642\ufe0f\ud83e\udddb\ud83c\udffc\ud83e\udddb\ud83c\udffc\u200d\u2640\ufe0f\ud83e\udddb\ud83c\udffc\u200d\u2642\ufe0f\ud83e\udddb\ud83c\udffd\ud83e\udddb\ud83c\udffd\u200d\u2640\ufe0f\ud83e\udddb\ud83c\udffd\u200d\u2642\ufe0f\ud83e\udddb\ud83c\udffe\ud83e\udddb\ud83c" +
        "\udffe\u200d\u2640\ufe0f\ud83e\udddb\ud83c\udffe\u200d\u2642\ufe0f\ud83e\udddb\ud83c\udfff\ud83e\udddb\ud83c\udfff\u200d\u2640\ufe0f\ud83e\udddb\ud83c\udfff\u200d\u2642\ufe0f\ud83e\udddc\u200d\u2640\ufe0f\ud83e\udddc\u200d\u2642\ufe0f\ud83e\udddc\ud83c\udffb\ud83e\udddc\ud83c\udffb\u200d\u2640\ufe0f\ud83e\udddc\ud83c\udffb\u200d\u2642\ufe0f\ud83e\udddc\ud83c\udffc\ud83e\udddc\ud83c" +
        "\udffc\u200d\u2640\ufe0f\ud83e\udddc\ud83c\udffc\u200d\u2642\ufe0f\ud83e\udddc\ud83c\udffd\ud83e\udddc\ud83c\udffd\u200d\u2640\ufe0f\ud83e\udddc\ud83c\udffd\u200d\u2642\ufe0f\ud83e\udddc\ud83c\udffe\ud83e\udddc\ud83c\udffe\u200d\u2640\ufe0f\ud83e\udddc\ud83c\udffe\u200d\u2642\ufe0f\ud83e\udddc\ud83c\udfff\ud83e\udddc\ud83c\udfff\u200d\u2640\ufe0f\ud83e\udddc\ud83c\udfff\u200d\u2642" +
        "\ufe0f\ud83e\udddd\u200d\u2640\ufe0f\ud83e\udddd\u200d\u2642\ufe0f\ud83e\udddd\ud83c\udffb\ud83e\udddd\ud83c\udffb\u200d\u2640\ufe0f\ud83e\udddd\ud83c\udffb\u200d\u2642\ufe0f\ud83e\udddd\ud83c\udffc\ud83e\udddd\ud83c\udffc\u200d\u2640\ufe0f\ud83e\udddd\ud83c\udffc\u200d\u2642\ufe0f\ud83e\udddd\ud83c\udffd\ud83e\udddd\ud83c\udffd\u200d\u2640\ufe0f\ud83e\udddd\ud83c\udffd\u200d\u2642" +
        "\ufe0f\ud83e\udddd\ud83c\udffe\ud83e\udddd\ud83c\udffe\u200d\u2640\ufe0f\ud83e\udddd\ud83c\udffe\u200d\u2642\ufe0f\ud83e\udddd\ud83c\udfff\ud83e\udddd\ud83c\udfff\u200d\u2640\ufe0f\ud83e\udddd\ud83c\udfff\u200d\u2642\ufe0f\ud83e\uddde\u200d\u2640\ufe0f\ud83e\uddde\u200d\u2642\ufe0f\ud83e\udddf\u200d\u2640\ufe0f\ud83e\udddf\u200d\u2642\ufe0f\ud83e\udec3\ud83c\udffb\ud83e\udec3\ud83c" +
        "\udffc\ud83e\udec3\ud83c\udffd\ud83e\udec3\ud83c\udffe\ud83e\udec3\ud83c\udfff\ud83e\udec4\ud83c\udffb\ud83e\udec4\ud83c\udffc\ud83e\udec4\ud83c\udffd\ud83e\udec4\ud83c\udffe\ud83e\udec4\ud83c\udfff\ud83e\udec5\ud83c\udffb\ud83e\udec5\ud83c\udffc\ud83e\udec5\ud83c\udffd\ud83e\udec5\ud83c\udffe\ud83e\udec5\ud83c\udfff\ud83e\udef0\ud83c\udffb\ud83e\udef0\ud83c\udffc\ud83e\udef0\ud83c" +
        "\udffd\ud83e\udef0\ud83c\udffe\ud83e\udef0\ud83c\udfff\ud83e\udef1\ud83c\udffb\ud83e\udef1\ud83c\udffb\u200d\ud83e\udef2\ud83c\udffc\ud83e\udef1\ud83c\udffb\u200d\ud83e\udef2\ud83c\udffd\ud83e\udef1\ud83c\udffb\u200d\ud83e\udef2\ud83c\udffe\ud83e\udef1\ud83c\udffb\u200d\ud83e\udef2\ud83c\udfff\ud83e\udef1\ud83c\udffc\ud83e\udef1\ud83c\udffc\u200d\ud83e\udef2\ud83c\udffb\ud83e\udef1" +
        "\ud83c\udffc\u200d\ud83e\udef2\ud83c\udffd\ud83e\udef1\ud83c\udffc\u200d\ud83e\udef2\ud83c\udffe\ud83e\udef1\ud83c\udffc\u200d\ud83e\udef2\ud83c\udfff\ud83e\udef1\ud83c\udffd\ud83e\udef1\ud83c\udffd\u200d\ud83e\udef2\ud83c\udffb\ud83e\udef1\ud83c\udffd\u200d\ud83e\udef2\ud83c\udffc\ud83e\udef1\ud83c\udffd\u200d\ud83e\udef2\ud83c\udffe\ud83e\udef1\ud83c\udffd\u200d\ud83e\udef2\ud83c" +
        "\udfff\ud83e\udef1\ud83c\udffe\ud83e\udef1\ud83c\udffe\u200d\ud83e\udef2\ud83c\udffb\ud83e\udef1\ud83c\udffe\u200d\ud83e\udef2\ud83c\udffc\ud83e\udef1\ud83c\udffe\u200d\ud83e\udef2\ud83c\udffd\ud83e\udef1\ud83c\udffe\u200d\ud83e\udef2\ud83c\udfff\ud83e\udef1\ud83c\udfff\ud83e\udef1\ud83c\udfff\u200d\ud83e\udef2\ud83c\udffb\ud83e\udef1\ud83c\udfff\u200d\ud83e\udef2\ud83c\udffc\ud83e" +
        "\udef1\ud83c\udfff\u200d\ud83e\udef2\ud83c\udffd\ud83e\udef1\ud83c\udfff\u200d\ud83e\udef2\ud83c\udffe\ud83e\udef2\ud83c\udffb\ud83e\udef2\ud83c\udffc\ud83e\udef2\ud83c\udffd\ud83e\udef2\ud83c\udffe\ud83e\udef2\ud83c\udfff\ud83e\udef3\ud83c\udffb\ud83e\udef3\ud83c\udffc\ud83e\udef3\ud83c\udffd\ud83e\udef3\ud83c\udffe\ud83e\udef3\ud83c\udfff\ud83e\udef4\ud83c\udffb\ud83e\udef4\ud83c" +
        "\udffc\ud83e\udef4\ud83c\udffd\ud83e\udef4\ud83c\udffe\ud83e\udef4\ud83c\udfff\ud83e\udef5\ud83c\udffb\ud83e\udef5\ud83c\udffc\ud83e\udef5\ud83c\udffd\ud83e\udef5\ud83c\udffe\ud83e\udef5\ud83c\udfff\ud83e\udef6\ud83c\udffb\ud83e\udef6\ud83c\udffc\ud83e\udef6\ud83c\udffd\ud83e\udef6\ud83c\udffe\ud83e\udef6\ud83c\udfff\ud83e\udef7\ud83c\udffb\ud83e\udef7\ud83c\udffc\ud83e\udef7\ud83c" +
        "\udffd\ud83e\udef7\ud83c\udffe\ud83e\udef7\ud83c\udfff\ud83e\udef8\ud83c\udffb\ud83e\udef8\ud83c\udffc\ud83e\udef8\ud83c\udffd\ud83e\udef8\ud83c\udffe\ud83e\udef8\ud83c\udfff";
}
