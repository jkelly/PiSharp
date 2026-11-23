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
