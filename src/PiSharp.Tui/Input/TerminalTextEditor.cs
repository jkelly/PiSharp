using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Tui.Input;

public enum TerminalTextEditorFailure { InvalidOptions, ResourceLimit, InvalidUnicode, InvalidCursor }

public sealed class TerminalTextEditorException : Exception
{
    public TerminalTextEditorFailure Failure { get; }

    public TerminalTextEditorException(TerminalTextEditorFailure failure) : base(failure switch
    {
        TerminalTextEditorFailure.InvalidOptions => "Terminal editor limits are invalid.",
        TerminalTextEditorFailure.ResourceLimit => "Terminal draft exceeds its character bound.",
        TerminalTextEditorFailure.InvalidUnicode => "Terminal draft contains invalid UTF-16.",
        _ => "Terminal draft cursor is invalid for this editor."
    }) => Failure = failure;
}

/// <summary>Draft data only. Cursor offsets count UTF-16 units, independently of terminal display cells.</summary>
public sealed record TerminalTextEditorSnapshot(string Text, int CursorUtf16Offset);

/// <summary>
/// A bounded, single-consumer multiline draft editor. StringInfo segmentation is an explicit .NET runtime
/// policy, not pinned Pi Unicode parity. No terminal, session, submission or agent queue is owned here.
/// </summary>
public sealed class TerminalTextEditor
{
    public const int MaximumSupportedCharacters = 65_536;
    public const string SegmentationPolicyId = "dotnet-stringinfo-runtime-v1";
    private readonly int maximumCharacters;
    private int[] boundaries = [];
    private TerminalTextEditorSnapshot snapshot = new("", 0);

    public TerminalTextEditor(int maximumCharacters = MaximumSupportedCharacters)
    {
        if (maximumCharacters is < 1 or > MaximumSupportedCharacters)
            throw Error(TerminalTextEditorFailure.InvalidOptions);
        this.maximumCharacters = maximumCharacters;
    }

    public TerminalTextEditorSnapshot Snapshot => snapshot;

    /// <summary>Replaces the draft atomically. An omitted cursor selects the end; explicit cursors must be boundaries.</summary>
    public bool SetText(string text, int? cursorUtf16Offset = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var nextBoundaries = Parse(text);
        var cursor = cursorUtf16Offset ?? text.Length;
        if (!IsBoundary(text, nextBoundaries, cursor)) throw Error(TerminalTextEditorFailure.InvalidCursor);
        return Commit(text, nextBoundaries, cursor);
    }

    public bool SetCursor(int cursorUtf16Offset)
    {
        if (!IsBoundary(snapshot.Text, boundaries, cursorUtf16Offset)) throw Error(TerminalTextEditorFailure.InvalidCursor);
        return Move(cursorUtf16Offset);
    }

    /// <summary>
    /// Inserts validated literal data, including newlines and paste controls. A host must sanitize it for display.
    /// If insertion merges clusters across the edit, the cursor advances to the end of the merged cluster.
    /// </summary>
    public bool Insert(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > maximumCharacters - snapshot.Text.Length) throw Error(TerminalTextEditorFailure.ResourceLimit);
        ValidateUnicode(text);
        if (text.Length == 0) return false;
        var cursor = snapshot.CursorUtf16Offset;
        var value = snapshot.Text.Insert(cursor, text);
        var nextBoundaries = Parse(value);
        return Commit(value, nextBoundaries, BoundaryAtOrAfter(value, nextBoundaries, cursor + text.Length));
    }

    public bool Backspace()
    {
        var cursor = snapshot.CursorUtf16Offset;
        if (cursor == 0) return false;
        var start = PreviousBoundary(cursor);
        return Remove(start, cursor - start);
    }

    public bool Delete()
    {
        var cursor = snapshot.CursorUtf16Offset;
        if (cursor == snapshot.Text.Length) return false;
        return Remove(cursor, NextBoundary(cursor) - cursor);
    }

    public bool MoveLeft() => snapshot.CursorUtf16Offset != 0 && Move(PreviousBoundary(snapshot.CursorUtf16Offset));
    public bool MoveRight() => snapshot.CursorUtf16Offset != snapshot.Text.Length && Move(NextBoundary(snapshot.CursorUtf16Offset));
    public bool MoveDocumentStart() => Move(0);
    public bool MoveDocumentEnd() => Move(snapshot.Text.Length);

    /// <summary>Moves to the start of the logical CR, LF or CRLF-delimited line containing the cursor.</summary>
    public bool MoveLineStart()
    {
        var cursor = snapshot.CursorUtf16Offset;
        var start = cursor;
        while (start > 0 && snapshot.Text[start - 1] is not ('\r' or '\n')) start--;
        return Move(start);
    }

    /// <summary>Moves before the next logical line break, or to the end of the document.</summary>
    public bool MoveLineEnd()
    {
        var end = snapshot.CursorUtf16Offset;
        while (end < snapshot.Text.Length && snapshot.Text[end] is not ('\r' or '\n')) end++;
        return Move(end);
    }

    public bool Clear() => Commit("", [], 0);

    private bool Remove(int start, int length)
    {
        var text = snapshot.Text.Remove(start, length);
        var nextBoundaries = Parse(text);
        // Deleting a separator may join two old clusters, including regional-indicator pairs.
        return Commit(text, nextBoundaries, BoundaryAtOrAfter(text, nextBoundaries, start));
    }

    private bool Move(int cursor)
    {
        if (snapshot.CursorUtf16Offset == cursor) return false;
        snapshot = snapshot with { CursorUtf16Offset = cursor };
        return true;
    }

    private bool Commit(string text, int[] nextBoundaries, int cursor)
    {
        if (snapshot.Text == text && snapshot.CursorUtf16Offset == cursor) return false;
        var next = new TerminalTextEditorSnapshot(text, cursor);
        boundaries = nextBoundaries;
        snapshot = next;
        return true;
    }

    private int[] Parse(string text)
    {
        if (text.Length > maximumCharacters) throw Error(TerminalTextEditorFailure.ResourceLimit);
        ValidateUnicode(text);
        return StringInfo.ParseCombiningCharacters(text);
    }

    internal static void ValidateUnicode(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]))
            {
                if (++index >= text.Length || !char.IsLowSurrogate(text[index])) throw Error(TerminalTextEditorFailure.InvalidUnicode);
            }
            else if (char.IsLowSurrogate(text[index])) throw Error(TerminalTextEditorFailure.InvalidUnicode);
        }
    }

    private int PreviousBoundary(int cursor)
    {
        var index = cursor == snapshot.Text.Length ? boundaries.Length : Array.BinarySearch(boundaries, cursor);
        return boundaries[index - 1];
    }

    private int NextBoundary(int cursor)
    {
        var index = Array.BinarySearch(boundaries, cursor) + 1;
        return index == boundaries.Length ? snapshot.Text.Length : boundaries[index];
    }

    private static bool IsBoundary(string text, int[] positions, int cursor) => cursor >= 0 && cursor <= text.Length &&
        (cursor == text.Length || Array.BinarySearch(positions, cursor) >= 0);

    private static int BoundaryAtOrAfter(string text, int[] positions, int cursor)
    {
        if (cursor == text.Length) return cursor;
        var index = Array.BinarySearch(positions, cursor);
        if (index >= 0) return cursor;
        index = ~index;
        return index == positions.Length ? text.Length : positions[index];
    }

    private static TerminalTextEditorException Error(TerminalTextEditorFailure failure) => new(failure);
}

public enum TerminalEditorInputDisposition { Ignored, Handled, LiteralLargePaste }

/// <summary>
/// Native host semantics for the bounded editor: source-compatible text normalization, paste preparation
/// and synchronous change notifications. Logical UTF-16 edit seams follow the source even when inside
/// a newly joined grapheme; movement and deletion segment the current line before or after that seam.
/// History, autocomplete, undo and upstream large-paste marker expansion are not implemented here.
/// </summary>
public sealed class TerminalTextEditorController
{
    private static readonly Regex EncodedPasteControl = new("\u001b\\[([0-9]+);5u",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
    private readonly int maximumCharacters;
    private TerminalTextEditorSnapshot snapshot = new("", 0);

    public TerminalTextEditorController(int maximumCharacters = TerminalTextEditor.MaximumSupportedCharacters)
    {
        if (maximumCharacters is < 1 or > TerminalTextEditor.MaximumSupportedCharacters)
            throw new TerminalTextEditorException(TerminalTextEditorFailure.InvalidOptions);
        this.maximumCharacters = maximumCharacters;
    }

    public TerminalTextEditorSnapshot Snapshot => snapshot;
    public const bool PasteMarkerExpansionSupported = false;

    /// <summary>
    /// Fires after successful edits, including no-op SetText, Backspace and Delete. Subscriber failures
    /// propagate after the committed draft mutation; the caller owns callback reentrancy and rendering.
    /// </summary>
    public event Action<string>? TextChanged;

    public bool SetText(string text)
    {
        ValidateInput(text);
        var value = Normalize(text);
        var changed = Commit(value, value.Length);
        Notify(); return changed;
    }

    public bool SetCursor(int cursorUtf16Offset) => Commit(snapshot.Text, cursorUtf16Offset);
    public bool Clear() => SetText("");
    internal bool SetSnapshotForHost(TerminalTextEditorSnapshot value) => Commit(value.Text, value.CursorUtf16Offset);

    /// <summary>Programmatic insertion normalizes CR/LF and tabs, independently of paste filtering.</summary>
    public bool InsertText(string text)
    {
        ValidateInput(text);
        var normalized = Normalize(text);
        if (normalized.Length == 0) return false;
        var changed = Insert(normalized);
        Notify(); return changed;
    }

    public TerminalEditorInputDisposition HandleInput(TerminalInputEvent input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input is TerminalText text)
        {
            ValidateInput(text.Text);
            if (text.Text.Length != 0) { Insert(text.Text); Notify(); }
            return TerminalEditorInputDisposition.Handled;
        }
        if (input is TerminalPaste paste) return Paste(paste.Text);
        if (input is not TerminalKey { Action: not TerminalKeyAction.Release } key) return TerminalEditorInputDisposition.Ignored;
        if (key.Key == "Enter" && key.Modifiers == TerminalModifiers.Shift)
        { Insert("\n"); Notify(); return TerminalEditorInputDisposition.Handled; }
        if (key.Modifiers != TerminalModifiers.None) return TerminalEditorInputDisposition.Ignored;
        switch (key.Key)
        {
            case "Backspace": RemovePrevious(); Notify(); break;
            case "Delete": RemoveNext(); Notify(); break;
            case "Left": SetCursor(snapshot.CursorUtf16Offset - PreviousLength()); break;
            case "Right": SetCursor(snapshot.CursorUtf16Offset + NextLength()); break;
            case "Home": SetCursor(LineStart()); break;
            case "End": SetCursor(LineEnd()); break;
            default: return TerminalEditorInputDisposition.Ignored;
        }
        return TerminalEditorInputDisposition.Handled;
    }

    private TerminalEditorInputDisposition Paste(string text)
    {
        ValidateInput(text);
        var value = PreparePaste(text, snapshot.Text, snapshot.CursorUtf16Offset);
        var large = value.Length > 1000 || value.Count(character => character == '\n') >= 10;
        // Existing native literal large-paste behavior remains available, but its source marker mismatch is explicit.
        if (value.Length != 0) { Insert(value); Notify(); }
        return large ? TerminalEditorInputDisposition.LiteralLargePaste : TerminalEditorInputDisposition.Handled;
    }

    internal static string PreparePaste(string text, string draft, int cursor)
    {
        var decoded = EncodedPasteControl.Replace(text, match =>
        {
            if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return match.Value;
            return value switch { >= 97 and <= 122 => ((char)(value - 96)).ToString(), >= 65 and <= 90 => ((char)(value - 64)).ToString(), _ => match.Value };
        });
        var clean = Normalize(decoded); var filtered = new StringBuilder(clean.Length);
        foreach (var character in clean) if (character == '\n' || character >= 32) filtered.Append(character);
        var value = filtered.ToString();
        if (value.Length != 0 && value[0] is '/' or '~' or '.' && cursor > 0 && IsAsciiWord(draft[cursor - 1])) value = " " + value;
        return value;
    }

    private void ValidateInput(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > maximumCharacters) throw new TerminalTextEditorException(TerminalTextEditorFailure.ResourceLimit);
        TerminalTextEditor.ValidateUnicode(text);
    }

    internal static string Normalize(string text)
    {
        // The input bound limits this intermediate to at most four times the draft bound; final admission is atomic.
        var value = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
            if (text[index] == '\r') { value.Append('\n'); if (index + 1 < text.Length && text[index + 1] == '\n') index++; }
            else if (text[index] == '\t') value.Append("    ");
            else value.Append(text[index]);
        return value.ToString();
    }

    private static bool IsAsciiWord(char value) => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_';
    private void Notify() => TextChanged?.Invoke(snapshot.Text);

    private bool Commit(string text, int cursor)
    {
        if (text.Length > maximumCharacters) throw new TerminalTextEditorException(TerminalTextEditorFailure.ResourceLimit);
        TerminalTextEditor.ValidateUnicode(text);
        // The supported source seam may be inside a grapheme, but never inside a UTF-16 scalar pair.
        if (cursor < 0 || cursor > text.Length || cursor > 0 && cursor < text.Length && char.IsHighSurrogate(text[cursor - 1]) && char.IsLowSurrogate(text[cursor]))
            throw new TerminalTextEditorException(TerminalTextEditorFailure.InvalidCursor);
        if (snapshot.Text == text && snapshot.CursorUtf16Offset == cursor) return false;
        snapshot = new(text, cursor); return true;
    }

    private bool Insert(string text)
    {
        if (text.Length > maximumCharacters - snapshot.Text.Length) throw new TerminalTextEditorException(TerminalTextEditorFailure.ResourceLimit);
        var cursor = snapshot.CursorUtf16Offset;
        return Commit(snapshot.Text.Insert(cursor, text), cursor + text.Length);
    }

    private void RemovePrevious()
    {
        var length = PreviousLength(); var cursor = snapshot.CursorUtf16Offset;
        if (length != 0) Commit(snapshot.Text.Remove(cursor - length, length), cursor - length);
    }

    private void RemoveNext()
    {
        var cursor = snapshot.CursorUtf16Offset; var length = NextLength();
        if (length != 0) Commit(snapshot.Text.Remove(cursor, length), cursor);
    }

    private int PreviousLength()
    {
        var cursor = snapshot.CursorUtf16Offset; var start = LineStart();
        if (cursor == start) return cursor == 0 ? 0 : 1;
        var prefix = snapshot.Text[start..cursor];
        return prefix.Length - StringInfo.ParseCombiningCharacters(prefix)[^1];
    }

    private int NextLength()
    {
        var cursor = snapshot.CursorUtf16Offset; var end = LineEnd();
        if (cursor == end) return cursor == snapshot.Text.Length ? 0 : 1;
        var suffix = snapshot.Text[cursor..end]; var positions = StringInfo.ParseCombiningCharacters(suffix);
        return positions.Length == 1 ? suffix.Length : positions[1];
    }

    private int LineStart()
    {
        var position = snapshot.CursorUtf16Offset;
        while (position > 0 && snapshot.Text[position - 1] != '\n') position--;
        return position;
    }

    private int LineEnd()
    {
        var position = snapshot.CursorUtf16Offset;
        while (position < snapshot.Text.Length && snapshot.Text[position] != '\n') position++;
        return position;
    }
}

/// <summary>Escaped ASCII draft viewport; the source cursor and displayed cell position remain separate.</summary>
public sealed record TerminalTextEditorProjection(ImmutableArray<string> Rows, int CursorRow, int CursorColumn,
    int SourceCursorUtf16Offset, int FirstVisibleRow, int TotalRows)
{
    public const int MaximumColumns = 256, MaximumVisibleRows = 3;
    private static readonly TerminalEditorLayoutIdentity ProjectionIdentity = new(new Guid("2a2a5d2a-e1a0-4e80-8312-a612b8f10265"), 0);
    private static readonly TerminalEditorGeometryIdentity ProjectionGeometry = new(new Guid("90bc52e1-0e47-491f-a4b5-934307cd6266"), 0, TerminalEditorVisualMapBuilder.PolicyId);

    /// <summary>
    /// Maps every valid scalar seam, including a source caret inside a grapheme, into a bounded ASCII view.
    /// Escapes are indivisible tokens; tokens wider than the viewport use the existing '?' fallback.
    /// No stored draft text, logical cursor or terminal-width data is changed.
    /// </summary>
    public static TerminalTextEditorProjection Create(TerminalTextEditorSnapshot draft, int columns, int maximumVisibleRows)
    {
        ArgumentNullException.ThrowIfNull(draft); ArgumentNullException.ThrowIfNull(draft.Text);
        if (columns is < 1 or > MaximumColumns || maximumVisibleRows is < 1 or > MaximumVisibleRows)
            throw new TerminalTextEditorException(TerminalTextEditorFailure.InvalidOptions);
        if (draft.Text.Length > TerminalTextEditor.MaximumSupportedCharacters)
            throw new TerminalTextEditorException(TerminalTextEditorFailure.ResourceLimit);
        TerminalTextEditor.ValidateUnicode(draft.Text);
        var cursor = draft.CursorUtf16Offset;
        if (cursor < 0 || cursor > draft.Text.Length || cursor > 0 && cursor < draft.Text.Length && char.IsHighSurrogate(draft.Text[cursor - 1]) && char.IsLowSurrogate(draft.Text[cursor]))
            throw new TerminalTextEditorException(TerminalTextEditorFailure.InvalidCursor);
        return new TerminalEditorVisualMapBuilder().Build(new(draft, [], ProjectionIdentity),
            new(ProjectionGeometry, columns, 24)).Project(maximumVisibleRows);
    }
}
