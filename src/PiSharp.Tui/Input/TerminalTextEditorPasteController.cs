using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Tui.Input;

/// <summary>
/// Explicit opt-in editor with bounded source-style large-paste storage, marker editing and expansion.
/// Snapshot.Text is display data; hosts must use GetExpandedText() for full submission/external-editor data.
/// The existing TerminalTextEditorController keeps its literal-paste behavior.
/// </summary>
public sealed class TerminalTextEditorPasteController
{
    public const int MaximumPasteEntries = 64;
    public const int MaximumUndoEntries = 64;
    public const int MaximumRetainedUndoCharacters = 1_048_576;
    public const int MaximumKillEntries = 64;
    public const int MaximumRetainedKillCharacters = 1_048_576;
    public const int MaximumHistoryEntries = 100;
    public const int MaximumRetainedHistoryCharacters = 1_048_576;
    private static readonly Regex Marker = new(@"\[paste #([0-9]+)( (\+[0-9]+ lines|[0-9]+ chars))?\]",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
    private static readonly Regex SingleMarker = new(@"^\[paste #([0-9]+)( (\+[0-9]+ lines|[0-9]+ chars))?\]$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
    private sealed record PasteEntry(int Id, string Text);
    private sealed record UndoFrame(TerminalTextEditorSnapshot Snapshot, ImmutableArray<PasteEntry> Pastes,
        int Counter, int RetainedPasteCharacters, string ExpandedText, int Weight);
    private readonly TerminalTextEditorController editor;
    private readonly int maximumCharacters, maximumPasteEntries;
    private readonly TerminalKeybindings? keybindings;
    private ImmutableArray<PasteEntry> pastes = [];
    private int pasteCounter, retainedPasteCharacters;
    private string expandedText = "";
    private ImmutableArray<UndoFrame> undo = [];
    private int retainedUndoCharacters;
    private enum EditorAction { None, TypeWord, Kill, Yank }
    private EditorAction lastAction;
    private ImmutableArray<string> killRing = [];
    private int retainedKillCharacters;
    private readonly Guid editorLifetime = Guid.NewGuid();
    private long editorRevision, scrollResetRevision;
    private ImmutableArray<string> history = [];
    private int retainedHistoryCharacters, historyIndex = -1;
    private TerminalTextEditorSnapshot? historyDraft;
    private int? preferredUtf16Offset, snappedUtf16Offset;
    private bool deliveringObserver;
    private enum CharacterJump { None, Forward, Backward }
    private CharacterJump characterJump;
    // An input-local admission stage, cleared before observers or on rejected dispatch.
    private bool cancelJumpOnAdmission;

    public TerminalTextEditorPasteController(int maximumCharacters = TerminalTextEditor.MaximumSupportedCharacters,
        int maximumPasteEntries = MaximumPasteEntries, TerminalKeybindings? keybindings = null)
    {
        editor = new(maximumCharacters);
        if (maximumPasteEntries is < 1 or > MaximumPasteEntries)
            throw new TerminalTextEditorException(TerminalTextEditorFailure.InvalidOptions);
        this.maximumCharacters = maximumCharacters; this.maximumPasteEntries = maximumPasteEntries;
        this.keybindings = keybindings;
    }

    public bool HasConfiguredKeybindings => keybindings is not null;
    public bool DisableSubmit { get; set; }
    private bool Matches(TerminalInputEvent input, string action) => keybindings is not null &&
        keybindings.GetKeys(action).Any(key => TerminalInputDecoder.MatchesKey(input, key));

    /// <summary>Contextual editor decision from the original event, before default wire projection.</summary>
    public string? ResolveConfiguredAction(TerminalInputEvent input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return HandlesPendingCharacterJump(input) ? null : ResolveConfiguredOrdinaryAction(input);
    }

    public bool HandlesPendingCharacterJump(TerminalInputEvent input) => keybindings is not null && IsCharacterJumpPending &&
        (Matches(input, "tui.editor.jumpForward") || Matches(input, "tui.editor.jumpBackward") || PendingTarget(input) is not null);

    private static string? PendingTarget(TerminalInputEvent original) => TerminalInputDecoder.ForEditorInput(original, true) switch
    {
        TerminalText text when text.Text.Length > 0 && text.Text[0] >= 32 => text.Text,
        TerminalKey key when key.Modifiers is TerminalModifiers.None or TerminalModifiers.Shift &&
            !string.IsNullOrEmpty(key.Key) && Rune.TryGetRuneAt(key.Key, 0, out var rune) && rune.Value >= 32 && rune.Utf16SequenceLength == key.Key.Length => key.Key,
        _ => null
    };

    private string? ResolveConfiguredOrdinaryAction(TerminalInputEvent input)
    {
        if (keybindings is null || input is TerminalPaste or TerminalProtocol or TerminalUnknownSequence) return null;
        if (TerminalInputDecoder.IsNonLatinAlternateKeyRelease(input)) return null;
        foreach (var action in new[] { "tui.input.copy", "tui.editor.undo", "tui.input.tab", "tui.editor.deleteToLineEnd",
            "tui.editor.deleteToLineStart", "tui.editor.deleteWordBackward", "tui.editor.deleteWordForward",
            "tui.editor.deleteCharBackward", "tui.editor.deleteCharForward", "tui.editor.yank", "tui.editor.yankPop",
            "tui.editor.historyPrevious", "tui.editor.historyNext", "tui.editor.cursorLineStart", "tui.editor.cursorLineEnd",
            "tui.editor.cursorWordLeft", "tui.editor.cursorWordRight" })
        {
            if (Matches(input, action)) return action;
            if (action == "tui.editor.deleteCharBackward" && TerminalInputDecoder.MatchesKey(input, "shift+backspace")) return action;
            if (action == "tui.editor.deleteCharForward" && TerminalInputDecoder.MatchesKey(input, "shift+delete")) return action;
        }
        TerminalInputDecoder.TryGetOriginalInput(input, out var raw, out _);
        if (Matches(input, "tui.input.newLine") || raw == "\n" || raw == "\u001b\r" || raw == "\u001b[13;2~" ||
            raw.Length > 1 && (raw[0] == '\n' || raw.Contains('\u001b') && raw.Contains('\r'))) return "tui.input.newLine";
        foreach (var action in new[] { "tui.input.submit", "tui.editor.cursorUp", "tui.editor.cursorDown",
            "tui.editor.cursorRight", "tui.editor.cursorLeft", "tui.editor.pageUp", "tui.editor.pageDown",
            "tui.editor.jumpForward", "tui.editor.jumpBackward" })
            if (Matches(input, action)) return action;
        return TerminalInputDecoder.MatchesKey(input, "shift+space") ? "direct.shiftSpace" : null;
    }

    public bool RequiresConfiguredLayout(TerminalInputEvent input) => keybindings is null ? RequiresLayout(input) :
        input is not TerminalKey { Action: TerminalKeyAction.Release } &&
        ResolveConfiguredAction(input) is "tui.editor.cursorUp" or "tui.editor.cursorDown" or "tui.editor.pageUp" or "tui.editor.pageDown";

    /// <summary>Prepare Source submit/backslash behavior; the host owns submission and receipts.</summary>
    public bool TryPrepareConfiguredSubmit(TerminalInputEvent input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (DisableSubmit || keybindings is null || input is TerminalKey { Action: TerminalKeyAction.Release }) return false;
        var action = ResolveConfiguredAction(input);
        var slash = Snapshot.CursorUtf16Offset > LineStart() && Snapshot.Text[Snapshot.CursorUtf16Offset - 1] == '\\';
        if (action == "tui.input.newLine" && slash && TerminalInputDecoder.MatchesKey(input, "enter") &&
            keybindings.GetKeys("tui.input.submit").Any(key => key is "shift+enter" or "shift+return"))
        { DispatchCancelingJump(() => { RemovePrevious(); Notify(); return TerminalEditorInputDisposition.Handled; }); return true; }
        if (action != "tui.input.submit") return false;
        if (!slash)
        {
            DispatchCancelingJump(() => TerminalEditorInputDisposition.Handled); return true;
        }
        DispatchCancelingJump(() => { RemovePrevious(); Notify(); InsertText("\n"); return TerminalEditorInputDisposition.Handled; });
        return false;
    }

    public TerminalTextEditorSnapshot Snapshot => editor.Snapshot;
    /// <summary>The same editor owner awaits a Source printable character-jump target.</summary>
    public bool IsCharacterJumpPending => characterJump != CharacterJump.None;
    public int RegisteredPasteCount => pastes.Length;
    public int RetainedPasteCharacters => retainedPasteCharacters;
    public int UndoDepth => undo.Length;
    public int RetainedUndoCharacters => retainedUndoCharacters;
    public int KillRingCount => killRing.Length;
    public int RetainedKillCharacters => retainedKillCharacters;
    public string GetExpandedText() => expandedText;
    public TerminalEditorLayoutIdentity LayoutIdentity => new(editorLifetime, editorRevision);
    public long ScrollResetRevision => scrollResetRevision;
    public ImmutableArray<string> History => history;
    public int HistoryCount => history.Length;
    public int RetainedHistoryCharacters => retainedHistoryCharacters;
    public int HistoryIndex => historyIndex;
    public int SavedHistoryDraftCharacters => historyDraft?.Text.Length ?? 0;
    public TerminalTextEditorSnapshot? SavedHistoryDraft => historyDraft;
    public int? PreferredUtf16Offset => preferredUtf16Offset;
    public int? SnappedUtf16Offset => snappedUtf16Offset;

    public TerminalEditorLayoutInput CaptureLayoutInput()
    {
        var validIds = pastes.Select(entry => entry.Id).ToHashSet();
        var ranges = Marker.Matches(Snapshot.Text).Cast<Match>().Where(match => int.TryParse(match.Groups[1].Value,
            NumberStyles.None, CultureInfo.InvariantCulture, out var id) && validIds.Contains(id))
            .Select(match => new TerminalEditorAtomicRange(match.Index, match.Length)).ToImmutableArray();
        return new(Snapshot, ranges, LayoutIdentity) { ScrollResetRevision = scrollResetRevision };
    }

    /// <summary>Explicit accepted-user-input host call; continue/quit callbacks do not imply acknowledgment.</summary>
    public bool AddToHistory(string text)
    {
        ValidateInput(text);
        var start = 0; var end = text.Length;
        while (start < end && IsSourceWhitespace(text[start])) start++;
        while (end > start && IsSourceWhitespace(text[end - 1])) end--;
        var value = text[start..end];
        if (value.Length == 0 || !history.IsEmpty && history[0] == value) return false;
        EnsureRevisionAvailable();
        var existing = history.IndexOf(value);
        var next = (existing < 0 ? history : history.RemoveAt(existing)).Insert(0, value);
        var retained = retainedHistoryCharacters + (existing < 0 ? value.Length : 0);
        while (next.Length > MaximumHistoryEntries || retained > MaximumRetainedHistoryCharacters)
        { retained -= next[^1].Length; next = next.RemoveAt(next.Length - 1); }
        history = next; retainedHistoryCharacters = retained; AdvanceRevision(); return true;
    }

    /// <summary>Actual synchronous marker-text notifications; subscriber errors propagate after commit.</summary>
    public event Action<string>? TextChanged;

    public bool SetText(string text)
    {
        ValidateInput(text);
        var value = TerminalTextEditorController.Normalize(text);
        var changed = Commit(value, value.Length, [], 0, Snapshot.Text != value, false, resetScroll: true);
        ExitHistoryBrowsing(); ClearPreferences();
        Notify(); return changed;
    }

    public bool Clear() => SetText("");
    /// <summary>Host boundary after submission, cancellation or teardown; releases draft, pastes and undo.</summary>
    public bool Reset()
    {
        var changed = Commit("", 0, [], 0, nextTypingWord: false, resetScroll: true);
        undo = []; retainedUndoCharacters = 0;
        characterJump = CharacterJump.None; cancelJumpOnAdmission = false;
        ExitHistoryBrowsing(); ClearPreferences();
        Notify(); return changed;
    }

    public bool SetCursor(int cursorUtf16Offset)
    {
        EnsureRevisionAvailable();
        var changed = editor.SetCursor(cursorUtf16Offset);
        lastAction = EditorAction.None; ClearPreferences(); AdvanceRevision(); return changed;
    }

    public bool InsertText(string text)
    {
        ValidateInput(text);
        var value = TerminalTextEditorController.Normalize(text);
        if (value.Length == 0) return false;
        var changed = Insert(value, pastes, pasteCounter, true, false);
        Notify(); return changed;
    }

    public TerminalEditorInputDisposition HandleInput(TerminalInputEvent input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (keybindings is not null) return HandleConfiguredInput(input);
        input = TerminalInputDecoder.ForEditorInput(input, IsCharacterJumpPending);
        if (input is TerminalKey { Key: "]" } jump && jump.Modifiers is
            TerminalModifiers.Control or (TerminalModifiers.Control | TerminalModifiers.Alt))
        {
            EnsureRevisionAvailable();
            characterJump = characterJump != CharacterJump.None ? CharacterJump.None :
                jump.Modifiers == TerminalModifiers.Control ? CharacterJump.Forward : CharacterJump.Backward;
            AdvanceRevision(); return TerminalEditorInputDisposition.Handled;
        }
        if (characterJump != CharacterJump.None)
        {
            // The pinned source's printable CSI-u decoding includes event releases. Ordinary
            // releases remain ignored below when no original jump is awaiting a target.
            var target = input switch
            {
                TerminalText text when text.Text is { Length: > 0 } && text.Text[0] >= 32 => text.Text,
                TerminalKey key when key.Modifiers is TerminalModifiers.None or TerminalModifiers.Shift &&
                    !string.IsNullOrEmpty(key.Key) && Rune.TryGetRuneAt(key.Key, 0, out var rune) &&
                    rune.Value >= 32 && rune.Utf16SequenceLength == key.Key.Length => key.Key,
                _ => null
            };
            if (target is not null) { CompleteCharacterJump(target); return TerminalEditorInputDisposition.Handled; }
            return DispatchCancelingJump(() => HandleOrdinaryInput(input));
        }
        return HandleOrdinaryInput(input);
    }

    private TerminalEditorInputDisposition HandleConfiguredInput(TerminalInputEvent original)
    {
        if (IsCharacterJumpPending)
        {
            if (Matches(original, "tui.editor.jumpForward") || Matches(original, "tui.editor.jumpBackward"))
            { EnsureRevisionAvailable(); characterJump = CharacterJump.None; AdvanceRevision(); return TerminalEditorInputDisposition.Handled; }
            var target = PendingTarget(original);
            if (target is not null) { CompleteCharacterJump(target); return TerminalEditorInputDisposition.Handled; }
            return DispatchCancelingJump(() => HandleConfiguredOrdinary(original));
        }
        return HandleConfiguredOrdinary(original);
    }

    private TerminalEditorInputDisposition HandleConfiguredOrdinary(TerminalInputEvent input)
    {
        if (input is TerminalPaste paste) { Paste(paste.Text); return TerminalEditorInputDisposition.Handled; }
        // Decide without mutating the pending jump; cancellation remains transactional.
        var action = ResolveConfiguredOrdinaryAction(input);
        if (input is TerminalKey { Action: TerminalKeyAction.Release } && action is not
            ("tui.editor.cursorWordLeft" or "tui.editor.cursorWordRight" or "tui.editor.deleteWordBackward" or "tui.editor.deleteWordForward"))
            return TerminalEditorInputDisposition.Ignored;
        switch (action)
        {
            case "tui.input.copy": return TerminalEditorInputDisposition.Handled;
            case "tui.editor.undo": Undo(); break;
            case "tui.editor.deleteToLineEnd": KillLine(false); break;
            case "tui.editor.deleteToLineStart": KillLine(true); break;
            case "tui.editor.deleteWordBackward": KillWord(true); break;
            case "tui.editor.deleteWordForward": KillWord(false); break;
            case "tui.editor.deleteCharBackward": RemovePrevious(); Notify(); break;
            case "tui.editor.deleteCharForward": RemoveNext(); Notify(); break;
            case "tui.editor.yank": Yank(); break;
            case "tui.editor.yankPop": YankPop(); break;
            case "tui.editor.historyPrevious": NavigateHistory(-1); break;
            case "tui.editor.historyNext": NavigateHistory(1); break;
            case "tui.editor.cursorLineStart": SetCursor(LineStart()); break;
            case "tui.editor.cursorLineEnd": SetCursor(LineEnd()); break;
            case "tui.editor.cursorWordLeft": MoveWord(true); break;
            case "tui.editor.cursorWordRight": MoveWord(false); break;
            case "tui.input.newLine": Insert("\n", pastes, pasteCounter, true, false); Notify(); break;
            case "direct.shiftSpace": InsertText(" "); break;
            case "tui.editor.cursorRight": SetCursor(Snapshot.CursorUtf16Offset + NextLength()); break;
            case "tui.editor.cursorLeft": SetCursor(Snapshot.CursorUtf16Offset - PreviousLength()); break;
            case "tui.editor.jumpForward": case "tui.editor.jumpBackward":
                EnsureRevisionAvailable(); characterJump = action == "tui.editor.jumpForward" ? CharacterJump.Forward : CharacterJump.Backward;
                AdvanceRevision(); break;
            case "tui.input.submit": case "tui.input.tab":
            case "tui.editor.cursorUp": case "tui.editor.cursorDown": case "tui.editor.pageUp": case "tui.editor.pageDown":
                return TerminalEditorInputDisposition.Ignored; // Host or fresh layout overload owns these actions.
            default:
                var printable = TerminalInputDecoder.GetPrintableText(input);
                return printable is null ? TerminalEditorInputDisposition.Ignored : HandleOrdinaryInput(new TerminalText(printable));
        }
        return TerminalEditorInputDisposition.Handled;
    }

    private TerminalEditorInputDisposition HandleOrdinaryInput(TerminalInputEvent input)
    {
        input = TerminalInputDecoder.ForEditorInput(input, false);
        if (input is TerminalText text)
        {
            ValidateInput(text.Text);
            if (text.Text.Length != 0)
            { Insert(text.Text, pastes, pasteCounter, lastAction != EditorAction.TypeWord || ContainsSourceWhitespace(text.Text), true); Notify(); }
            return TerminalEditorInputDisposition.Handled;
        }
        if (input is TerminalPaste paste) { Paste(paste.Text); return TerminalEditorInputDisposition.Handled; }
        // The pinned key matcher accepts Kitty releases for these original bindings.
        // Keep ordinary/unbound releases subject to the existing filter below.
        if (input is TerminalKey wordKey && !TerminalInputDecoder.IsNonLatinAlternateKeyRelease(input))
        {
            var wordAction = WordAction(wordKey);
            if (wordAction != 0)
            {
                if (Math.Abs(wordAction) == 1) MoveWord(wordAction < 0); else KillWord(wordAction < 0);
                return TerminalEditorInputDisposition.Handled;
            }
        }
        if (input is not TerminalKey { Action: not TerminalKeyAction.Release } key) return TerminalEditorInputDisposition.Ignored;
        if (key.Key == "-" && key.Modifiers == TerminalModifiers.Control)
        { Undo(); return TerminalEditorInputDisposition.Handled; }
        if (key.Key == "Enter" && key.Modifiers is TerminalModifiers.Shift or TerminalModifiers.Alt ||
            key.Key == "j" && key.Modifiers == TerminalModifiers.Control)
        { Insert("\n", pastes, pasteCounter, true, false); Notify(); return TerminalEditorInputDisposition.Handled; }
        if (key.Modifiers == TerminalModifiers.Control && key.Key is "u" or "k" or "y")
        {
            if (key.Key == "y") Yank(); else KillLine(key.Key == "u");
            return TerminalEditorInputDisposition.Handled;
        }
        if (key.Key == "y" && key.Modifiers == TerminalModifiers.Alt)
        { YankPop(); return TerminalEditorInputDisposition.Handled; }
        if (key.Modifiers == TerminalModifiers.Control)
        {
            var alias = key.Key switch { "a" or "Home" => "Home", "e" or "End" => "End", "b" => "Left", "f" => "Right", "d" => "Delete", _ => null };
            if (alias is not null) key = key with { Key = alias, Modifiers = TerminalModifiers.None };
        }
        if (key.Modifiers == TerminalModifiers.Shift && key.Key is "Backspace" or "Delete")
            key = key with { Modifiers = TerminalModifiers.None };
        if (key.Modifiers is TerminalModifiers.None or TerminalModifiers.Shift && !string.IsNullOrEmpty(key.Key) &&
            Rune.TryGetRuneAt(key.Key, 0, out var printable) && printable.Value >= 32 && printable.Utf16SequenceLength == key.Key.Length)
            return HandleOrdinaryInput(new TerminalText(key.Key));
        if (key.Modifiers != TerminalModifiers.None) return TerminalEditorInputDisposition.Ignored;
        switch (key.Key)
        {
            case "Backspace": RemovePrevious(); Notify(); break;
            case "Delete": RemoveNext(); Notify(); break;
            case "Left": SetCursor(Snapshot.CursorUtf16Offset - PreviousLength()); break;
            case "Right": SetCursor(Snapshot.CursorUtf16Offset + NextLength()); break;
            case "Home": SetCursor(LineStart()); break;
            case "End": SetCursor(LineEnd()); break;
            default: return TerminalEditorInputDisposition.Ignored;
        }
        return TerminalEditorInputDisposition.Handled;
    }

    public static bool RequiresLayout(TerminalInputEvent input) => input is TerminalKey { Action: not TerminalKeyAction.Release } key &&
        (key.Modifiers == TerminalModifiers.None && key.Key is "Up" or "Down" or "PageUp" or "PageDown" ||
         key.Modifiers == TerminalModifiers.Control && key.Key is "PageUp" or "PageDown");

    public TerminalEditorInputDisposition HandleInput(TerminalInputEvent input, TerminalEditorVisualMap map,
        TerminalEditorGeometryIdentity expectedGeometryIdentity)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateMap(map, expectedGeometryIdentity);
        if (keybindings is not null)
        {
            if (input is TerminalKey { Action: TerminalKeyAction.Release }) return HandleInput(input);
            var action = ResolveConfiguredAction(input);
            var name = action switch { "tui.editor.cursorUp" => "Up", "tui.editor.cursorDown" => "Down",
                "tui.editor.pageUp" => "PageUp", "tui.editor.pageDown" => "PageDown", _ => null };
            if (name is null) return HandleInput(input);
            return DispatchCancelingJump(() => HandleLayoutKey(new TerminalKey(name), map));
        }
        if (!RequiresLayout(input)) return HandleInput(input);
        return DispatchCancelingJump(() => HandleLayoutKey((TerminalKey)input, map));
    }

    private TerminalEditorInputDisposition HandleLayoutKey(TerminalKey key, TerminalEditorVisualMap map)
    {
        var current = FindVisualRow(map.Rows, Snapshot.CursorUtf16Offset);
        if (key.Key == "Up")
        {
            if (current == 0 && (Snapshot.Text.Length == 0 || historyIndex >= 0 || Snapshot.CursorUtf16Offset == LineStart())) NavigateHistory(-1);
            else if (current == 0) SetCursor(LineStart());
            else MoveToVisualRow(map, current, current - 1);
        }
        else if (key.Key == "Down")
        {
            if (historyIndex >= 0 && current == map.Rows.Length - 1) NavigateHistory(1);
            else if (current == map.Rows.Length - 1) SetCursor(LineEnd());
            else MoveToVisualRow(map, current, current + 1);
        }
        else
        {
            var page = Math.Max(5, (int)Math.Floor(map.ViewportRows * 0.3));
            var target = Math.Clamp(current + (key.Key == "PageUp" ? -page : page), 0, map.Rows.Length - 1);
            MoveToVisualRow(map, current, target);
        }
        return TerminalEditorInputDisposition.Handled;
    }

    private void Paste(string text)
    {
        ValidateInput(text);
        if (text.Length == 0) return;
        var value = TerminalTextEditorController.PreparePaste(text, Snapshot.Text, Snapshot.CursorUtf16Offset);
        if (value.Length == 0)
        { Commit(Snapshot.Text, Snapshot.CursorUtf16Offset, pastes, pasteCounter, true, false); ExitHistoryBrowsing(); ClearPreferences(); return; }
        var lines = value.Count(character => character == '\n') + 1;
        if (value.Length > 1000 || lines > 10)
        {
            var id = checked(pasteCounter + 1);
            var registry = Upsert(pastes, id, value);
            var label = lines > 10 ? $"[paste #{id} +{lines} lines]" : $"[paste #{id} {value.Length} chars]";
            Insert(label, registry, id, true, false);
        }
        else Insert(value, pastes, pasteCounter, true, false);
        Notify();
    }

    private void ValidateInput(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > maximumCharacters) throw new TerminalTextEditorException(TerminalTextEditorFailure.ResourceLimit);
        TerminalTextEditor.ValidateUnicode(text);
    }

    private bool Insert(string value, ImmutableArray<PasteEntry> registry, int counter, bool pushUndo, bool nextTypingWord)
    {
        var snapshot = Snapshot;
        if (value.Length > maximumCharacters - snapshot.Text.Length) throw new TerminalTextEditorException(TerminalTextEditorFailure.ResourceLimit);
        var changed = Commit(snapshot.Text.Insert(snapshot.CursorUtf16Offset, value), snapshot.CursorUtf16Offset + value.Length,
            registry, counter, pushUndo, nextTypingWord);
        ExitHistoryBrowsing(); ClearPreferences(); return changed;
    }

    private bool Commit(string text, int cursor, ImmutableArray<PasteEntry> registry, int counter,
        bool pushUndo = false, bool? nextTypingWord = null, bool resetScroll = false)
    {
        EnsureRevisionAvailable();
        if (resetScroll && scrollResetRevision == long.MaxValue)
            throw new TerminalTextEditorException(TerminalTextEditorFailure.ResourceLimit);
        var prepared = PrepareCommit(text, cursor, registry);
        var history = undo; var historyCharacters = retainedUndoCharacters;
        if (pushUndo)
        {
            var weight = Snapshot.Text.Length + retainedPasteCharacters + expandedText.Length;
            history = history.Add(new(Snapshot, pastes, pasteCounter, retainedPasteCharacters, expandedText, weight));
            historyCharacters += weight;
            while (history.Length > MaximumUndoEntries || historyCharacters > MaximumRetainedUndoCharacters)
            { historyCharacters -= history[0].Weight; history = history.RemoveAt(0); }
        }
        // Validate and commit the draft before publishing metadata. No underlying observer is subscribed.
        var changed = editor.SetSnapshotForHost(new(text, cursor));
        pastes = registry; pasteCounter = counter; retainedPasteCharacters = prepared.Retained; expandedText = prepared.Expanded;
        undo = history; retainedUndoCharacters = historyCharacters;
        if (nextTypingWord is not null) lastAction = nextTypingWord.Value ? EditorAction.TypeWord : EditorAction.None;
        if (resetScroll) scrollResetRevision++;
        AdvanceRevision();
        return changed;
    }

    private (int Retained, string Expanded) PrepareCommit(string text, int cursor, ImmutableArray<PasteEntry> registry)
    {
        if (text.Length > maximumCharacters || registry.Length > maximumPasteEntries)
            throw new TerminalTextEditorException(TerminalTextEditorFailure.ResourceLimit);
        TerminalTextEditor.ValidateUnicode(text);
        if (cursor < 0 || cursor > text.Length || cursor > 0 && cursor < text.Length &&
            char.IsHighSurrogate(text[cursor - 1]) && char.IsLowSurrogate(text[cursor]))
            throw new TerminalTextEditorException(TerminalTextEditorFailure.InvalidCursor);
        var retained = 0;
        foreach (var entry in registry)
        {
            if (entry.Text.Length > maximumCharacters - retained) throw new TerminalTextEditorException(TerminalTextEditorFailure.ResourceLimit);
            retained += entry.Text.Length;
        }
        return (retained, Expand(text, registry));
    }

    private static int WordAction(TerminalKey key) => (key.Modifiers, key.Key) switch
    {
        (TerminalModifiers.Alt, "Left" or "b") or (TerminalModifiers.Control, "Left") => -1,
        (TerminalModifiers.Alt, "Right" or "f") or (TerminalModifiers.Control, "Right") => 1,
        (TerminalModifiers.Alt, "Backspace") or (TerminalModifiers.Control, "w") => -2,
        (TerminalModifiers.Alt, "Delete" or "d") => 2,
        _ => 0
    };

    private void MoveWord(bool backward)
    {
        EnsureRevisionAvailable();
        var cursor = Snapshot.CursorUtf16Offset;
        var edge = backward ? LineStart() : LineEnd();
        var globalEdge = backward ? cursor == 0 : cursor == Snapshot.Text.Length;
        if (cursor != edge || !globalEdge)
        {
            editor.SetCursor(WordTarget(backward)); ClearPreferences();
        }
        lastAction = EditorAction.None; AdvanceRevision();
    }

    private int WordTarget(bool backward)
    {
        var text = Snapshot.Text; var cursor = Snapshot.CursorUtf16Offset;
        var edge = backward ? LineStart() : LineEnd();
        if (cursor == edge) return backward ? Math.Max(0, cursor - 1) : Math.Min(text.Length, cursor + 1);
        var fragment = backward ? text[edge..cursor] : text[cursor..edge];
        var segments = WordSegments(fragment);
        var at = backward ? segments.Count - 1 : 0; var position = backward ? fragment.Length : 0;
        var direction = backward ? -1 : 1;
        bool InRange() => at >= 0 && at < segments.Count;
        while (InRange() && !segments[at].Atomic && ContainsSourceWhitespace(fragment.Substring(segments[at].Start, segments[at].Length)))
        { position += direction * segments[at].Length; at += direction; }
        if (InRange())
        {
            var segment = segments[at];
            if (segment.Atomic) position += direction * segment.Length;
            else if (segment.Wordlike)
            {
                var value = fragment.AsSpan(segment.Start, segment.Length); var punctuation = -1;
                if (backward) { for (var i = value.Length - 1; i >= 0; i--) if (IsWordPunctuation(value[i])) { punctuation = i; break; } }
                else { for (var i = 0; i < value.Length; i++) if (IsWordPunctuation(value[i])) { punctuation = i; break; } }
                position += direction * (punctuation < 0 ? segment.Length : backward ? segment.Length - punctuation - 1 : punctuation);
            }
            else
            {
                while (InRange() && !segments[at].Atomic && !segments[at].Wordlike &&
                    !ContainsSourceWhitespace(fragment.Substring(segments[at].Start, segments[at].Length)))
                { position += direction * segments[at].Length; at += direction; }
            }
        }
        return (backward ? edge : cursor) + position;
    }

    private static bool IsWordPunctuation(char value) => "(){}[]<>.,;:'\"!?+-=*/\\|&%^$#@~`".Contains(value, StringComparison.Ordinal);
    private readonly record struct WordSegment(int Start, int Length, bool Wordlike, bool Atomic = false);

    private List<WordSegment> WordSegments(string text)
    {
        var boundaries = WordDictionary.Apply(text, ComputeWordBoundaries(text, sourceRules: true));
        var ids = pastes.Select(entry => entry.Id).ToHashSet();
        var markers = Marker.Matches(text).Cast<Match>().Where(match => int.TryParse(match.Groups[1].Value,
            NumberStyles.None, CultureInfo.InvariantCulture, out var id) && ids.Contains(id)).ToArray();
        var result = new List<WordSegment>(boundaries.Count - 1); var markerIndex = 0;
        for (var i = 0; i + 1 < boundaries.Count; i++)
        {
            var start = boundaries[i]; var end = boundaries[i + 1];
            while (markerIndex < markers.Length && markers[markerIndex].Index + markers[markerIndex].Length <= start) markerIndex++;
            var marker = markerIndex < markers.Length ? markers[markerIndex] : null;
            if (marker is not null && start >= marker.Index && start < marker.Index + marker.Length)
            { if (start == marker.Index) result.Add(new(start, marker.Length, false, true)); }
            else
            {
                var wordlike = false;
                foreach (var rune in text.AsSpan(start, end - start).EnumerateRunes()) wordlike |= WordData.IsWordlike(rune.Value);
                result.Add(new(start, end - start, wordlike));
            }
        }
        return result;
    }

    // Keep default UAX #29 conformance separate from pinned ICU78.3 source rules.
    private static List<int> WordBoundaryOffsets(string text) => ComputeWordBoundaries(text, sourceRules: false);

    private static List<int> ComputeWordBoundaries(string text, bool sourceRules)
    {
        var scalars = new List<(int Start, WordData.Kind Kind, bool Pictograph, int Flags)>(); var position = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var flags = sourceRules ? WordDictionary.Flags(rune.Value) : 0;
            var kind = WordData.Property(rune.Value);
            if (sourceRules)
            {
                if ((flags & 2) != 0 && kind == WordData.Kind.Extend || (flags & 6) != 0 && kind == WordData.Kind.ALetter) kind = WordData.Kind.Other;
                if ((flags & 8) != 0 && !Ignored(kind) && !Newline(kind)) kind = WordData.Kind.ALetter;
            }
            scalars.Add((position, kind, WordData.IsPictograph(rune.Value), flags)); position += rune.Utf16SequenceLength;
        }
        var count = scalars.Count; var previous = new int[count]; var next = new int[count]; var riRun = new int[count]; var significant = -1;
        for (var i = 0; i < count; i++)
        {
            previous[i] = significant;
            if (!Ignored(scalars[i].Kind))
            { riRun[i] = scalars[i].Kind == WordData.Kind.Regional_Indicator ? 1 + (significant >= 0 ? riRun[significant] : 0) : 0; significant = i; }
        }
        significant = -1;
        for (var i = count - 1; i >= 0; i--) { next[i] = significant; if (!Ignored(scalars[i].Kind)) significant = i; }
        var result = new List<int> { 0 };
        for (var i = 1; i < count; i++) if (Break(i)) result.Add(scalars[i].Start);
        if (text.Length != 0) result.Add(text.Length);
        return result;

        bool Break(int i)
        {
            var raw = scalars[i - 1].Kind; var b = scalars[i].Kind;
            if (raw == WordData.Kind.CR && b == WordData.Kind.LF) return false;
            if (Newline(raw) || Newline(b)) return true;
            if (raw == WordData.Kind.ZWJ && scalars[i].Pictograph) return false;
            if (raw == WordData.Kind.WSegSpace && b == WordData.Kind.WSegSpace) return false;
            if (Ignored(b)) return false;
            if (sourceRules && ((scalars[i - 1].Flags & scalars[i].Flags & 2) != 0 || (scalars[i - 1].Flags & scalars[i].Flags & 4) != 0)) return false;
            var aIndex = previous[i]; if (aIndex < 0) return true;
            var a = scalars[aIndex].Kind;
            var pi = previous[aIndex]; var ni = next[i];
            var p = pi >= 0 ? scalars[pi].Kind : WordData.Kind.Other;
            var n = ni >= 0 ? scalars[ni].Kind : WordData.Kind.Other;
            if (AH(a) && AH(b)) return false;
            if (AH(a) && LetterMiddle(b) && AH(n) || LetterMiddle(a) && AH(p) && AH(b)) return false;
            if (a == WordData.Kind.Hebrew_Letter && b == WordData.Kind.Single_Quote) return false;
            if (a == WordData.Kind.Hebrew_Letter && b == WordData.Kind.Double_Quote && n == WordData.Kind.Hebrew_Letter ||
                a == WordData.Kind.Double_Quote && p == WordData.Kind.Hebrew_Letter && b == WordData.Kind.Hebrew_Letter) return false;
            if (a == WordData.Kind.Numeric && b == WordData.Kind.Numeric || AH(a) && b == WordData.Kind.Numeric || a == WordData.Kind.Numeric && AH(b)) return false;
            if (a == WordData.Kind.Numeric && NumberMiddle(b) && n == WordData.Kind.Numeric || NumberMiddle(a) && p == WordData.Kind.Numeric && b == WordData.Kind.Numeric) return false;
            if (a == WordData.Kind.Katakana && b == WordData.Kind.Katakana) return false;
            if (Extended(a) && b == WordData.Kind.ExtendNumLet || a == WordData.Kind.ExtendNumLet && Extended(b)) return false;
            if (a == WordData.Kind.Regional_Indicator && b == WordData.Kind.Regional_Indicator && riRun[aIndex] % 2 == 1) return false;
            return true;
        }
        static bool Ignored(WordData.Kind kind) => kind is WordData.Kind.Extend or WordData.Kind.Format or WordData.Kind.ZWJ;
        static bool Newline(WordData.Kind kind) => kind is WordData.Kind.CR or WordData.Kind.LF or WordData.Kind.Newline;
        static bool AH(WordData.Kind kind) => kind is WordData.Kind.ALetter or WordData.Kind.Hebrew_Letter;
        static bool LetterMiddle(WordData.Kind kind) => kind is WordData.Kind.MidLetter or WordData.Kind.MidNumLet or WordData.Kind.Single_Quote;
        static bool NumberMiddle(WordData.Kind kind) => kind is WordData.Kind.MidNum or WordData.Kind.MidNumLet or WordData.Kind.Single_Quote;
        static bool Extended(WordData.Kind kind) => AH(kind) || kind is WordData.Kind.Numeric or WordData.Kind.Katakana or WordData.Kind.ExtendNumLet;
    }

    private void KillWord(bool backward)
    {
        var snapshot = Snapshot; var cursor = snapshot.CursorUtf16Offset;
        var target = WordTarget(backward); var start = Math.Min(cursor, target); var length = Math.Abs(cursor - target);
        if (length == 0 && cursor == (backward ? LineStart() : LineEnd())) { ExitHistoryWithRevision(); Notify(); return; }
        var killed = snapshot.Text.Substring(start, length); var ring = killRing; var retained = retainedKillCharacters;
        if (killed.Length != 0)
        {
            if (lastAction == EditorAction.Kill && !ring.IsEmpty)
            {
                if (killed.Length > maximumCharacters - ring[^1].Length) throw new TerminalTextEditorException(TerminalTextEditorFailure.ResourceLimit);
                ring = ring.SetItem(ring.Length - 1, backward ? killed + ring[^1] : ring[^1] + killed);
            }
            else ring = ring.Add(killed);
            retained += killed.Length;
            while (ring.Length > MaximumKillEntries || retained > MaximumRetainedKillCharacters)
            { retained -= ring[0].Length; ring = ring.RemoveAt(0); }
        }
        Commit(snapshot.Text.Remove(start, length), start, pastes, pasteCounter, true);
        killRing = ring; retainedKillCharacters = retained; lastAction = EditorAction.Kill;
        ExitHistoryBrowsing(); ClearPreferences(); Notify();
    }

    private void KillLine(bool backward)
    {
        var snapshot = Snapshot; var cursor = snapshot.CursorUtf16Offset;
        var length = backward ? cursor - LineStart() : LineEnd() - cursor;
        if (length == 0 && (backward ? cursor > 0 : cursor < snapshot.Text.Length)) length = 1;
        if (length == 0) { ExitHistoryWithRevision(); Notify(); return; }
        var start = backward ? cursor - length : cursor;
        var killed = snapshot.Text.Substring(start, length);
        var ring = killRing; var retained = retainedKillCharacters;
        if (lastAction == EditorAction.Kill && !ring.IsEmpty)
        {
            if (killed.Length > maximumCharacters - ring[^1].Length)
                throw new TerminalTextEditorException(TerminalTextEditorFailure.ResourceLimit);
            ring = ring.SetItem(ring.Length - 1, backward ? killed + ring[^1] : ring[^1] + killed);
        }
        else ring = ring.Add(killed);
        retained += killed.Length;
        while (ring.Length > MaximumKillEntries || retained > MaximumRetainedKillCharacters)
        { retained -= ring[0].Length; ring = ring.RemoveAt(0); }
        Commit(snapshot.Text.Remove(start, length), start, pastes, pasteCounter, true);
        killRing = ring; retainedKillCharacters = retained; lastAction = EditorAction.Kill;
        ExitHistoryBrowsing(); ClearPreferences();
        Notify();
    }

    private void Yank()
    {
        if (killRing.IsEmpty) return;
        EnsureRevisionAvailable(2);
        var snapshot = Snapshot; var value = killRing[^1];
        Commit(snapshot.Text.Insert(snapshot.CursorUtf16Offset, value), snapshot.CursorUtf16Offset + value.Length, pastes, pasteCounter, true);
        ExitHistoryBrowsing(); ClearPreferences(); Notify(); lastAction = EditorAction.Yank; AdvanceRevision();
    }

    private void YankPop()
    {
        if (lastAction != EditorAction.Yank || killRing.Length <= 1) return;
        EnsureRevisionAvailable(3);
        var snapshot = Snapshot; var start = snapshot.CursorUtf16Offset - killRing[^1].Length;
        var withoutYank = snapshot.Text.Remove(start, killRing[^1].Length);
        var rotated = killRing.RemoveAt(killRing.Length - 1).Insert(0, killRing[^1]);
        var replacement = rotated[^1]; var finalText = withoutYank.Insert(start, replacement);
        // Admission is atomic even though source observers see two distinct committed phases.
        PrepareCommit(withoutYank, start, pastes); PrepareCommit(finalText, start + replacement.Length, pastes);
        Commit(withoutYank, start, pastes, pasteCounter, true); ExitHistoryBrowsing(); ClearPreferences(); Notify();
        Commit(finalText, start + replacement.Length, pastes, pasteCounter);
        killRing = rotated; Notify(); lastAction = EditorAction.Yank; AdvanceRevision();
    }

    private void Undo()
    {
        if (undo.IsEmpty) { ExitHistoryWithRevision(); return; }
        var frame = undo[^1];
        Commit(frame.Snapshot.Text, frame.Snapshot.CursorUtf16Offset, frame.Pastes, frame.Counter, nextTypingWord: false);
        undo = undo.RemoveAt(undo.Length - 1); retainedUndoCharacters -= frame.Weight;
        ExitHistoryBrowsing(); ClearPreferences();
        Notify();
    }

    // ECMAScript /\s/ differs from Char.IsWhiteSpace: BOM is whitespace, U+0085 is not.
    private static bool ContainsSourceWhitespace(string text) => text.Any(IsSourceWhitespace);
    private static bool IsSourceWhitespace(char value) => value is
        >= '\u0009' and <= '\u000d' or '\u0020' or '\u00a0' or '\u1680' or
        >= '\u2000' and <= '\u200a' or '\u2028' or '\u2029' or '\u202f' or '\u205f' or '\u3000' or '\ufeff';

    private string Expand(string text, ImmutableArray<PasteEntry> registry)
    {
        var result = text;
        foreach (var entry in registry)
        {
            // Source expansion is one ordered replacement pass per canonical numeric ID, not recursion.
            var id = entry.Id.ToString(CultureInfo.InvariantCulture);
            var matches = Marker.Matches(result).Cast<Match>().Where(match => match.Groups[1].Value == id).ToArray();
            var size = (long)result.Length;
            foreach (var match in matches) size += entry.Text.Length - match.Length;
            if (size > maximumCharacters) throw new TerminalTextEditorException(TerminalTextEditorFailure.ResourceLimit);
            if (matches.Length == 0) continue;
            var expanded = new StringBuilder((int)size); var position = 0;
            foreach (var match in matches)
            { expanded.Append(result.AsSpan(position, match.Index - position)); expanded.Append(entry.Text); position = match.Index + match.Length; }
            expanded.Append(result.AsSpan(position)); result = expanded.ToString();
        }
        return result;
    }

    private static ImmutableArray<PasteEntry> Upsert(ImmutableArray<PasteEntry> registry, int id, string text)
    {
        for (var index = 0; index < registry.Length; index++)
            if (registry[index].Id == id) return registry.SetItem(index, new(id, text));
        return registry.Add(new(id, text));
    }

    private void RemovePrevious()
    {
        var snapshot = Snapshot; var cursor = snapshot.CursorUtf16Offset; var start = LineStart();
        var length = PreviousLength(); if (length == 0) { ExitHistoryWithRevision(resetAction: true); return; }
        var text = snapshot.Text; var registry = pastes; var counter = pasteCounter;
        var prefix = text[start..cursor]; var segments = Segment(prefix);
        var lastSegment = segments.Count == 0 ? "" : prefix.Substring(segments[^1].Start, segments[^1].Length);
        var match = SingleMarker.Match(lastSegment);
        if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var targetId))
        {
            registry = registry.Where(entry => entry.Id != targetId).ToImmutableArray(); counter--;
            foreach (var id in registry.Select(entry => entry.Id).Where(id => id > targetId).Order().ToArray())
            {
                var content = registry.Single(entry => entry.Id == id).Text;
                registry = Upsert(registry, id - 1, content).Where(entry => entry.Id != id).ToImmutableArray();
            }
            text = Marker.Replace(text, marker =>
            {
                if (!int.TryParse(marker.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= targetId) return marker.Value;
                // Preserve the pinned source's literal undefined suffix when renumbering a bare alias.
                return "[paste #" + (id - 1).ToString(CultureInfo.InvariantCulture) + marker.Groups[2].Value + "]";
            });
            var lineNumber = snapshot.Text.AsSpan(0, start).Count('\n'); var newStart = 0;
            for (var index = 0; index < lineNumber; index++) newStart = text.IndexOf('\n', newStart) + 1;
            var newEnd = text.IndexOf('\n', newStart); if (newEnd < 0) newEnd = text.Length;
            var line = text[newStart..newEnd]; var column = cursor - start;
            var before = Math.Min(column - length, line.Length); var after = Math.Min(column, line.Length);
            text = text[..newStart] + line[..before] + line[after..] + text[newEnd..];
            cursor = newStart + column - length;
        }
        else { text = text.Remove(cursor - length, length); cursor -= length; }
        Commit(text, cursor, registry, counter, true, false);
        ExitHistoryBrowsing(); ClearPreferences();
    }

    private void RemoveNext()
    {
        var snapshot = Snapshot; var length = NextLength();
        // Source forward deletion removes display text without pruning/renumbering the registry.
        if (length != 0)
        { Commit(snapshot.Text.Remove(snapshot.CursorUtf16Offset, length), snapshot.CursorUtf16Offset, pastes, pasteCounter, true, false); ExitHistoryBrowsing(); ClearPreferences(); }
        else ExitHistoryWithRevision(resetAction: true);
    }

    private int PreviousLength()
    {
        var cursor = Snapshot.CursorUtf16Offset; var start = LineStart();
        if (cursor == start) return cursor == 0 ? 0 : 1;
        var segments = Segment(Snapshot.Text[start..cursor]);
        return segments.Count == 0 ? 1 : segments[^1].Length;
    }

    private int NextLength()
    {
        var cursor = Snapshot.CursorUtf16Offset; var end = LineEnd();
        if (cursor == end) return cursor == Snapshot.Text.Length ? 0 : 1;
        var segments = Segment(Snapshot.Text[cursor..end]);
        return segments.Count == 0 ? 1 : segments[0].Length;
    }

    private List<(int Start, int Length)> Segment(string text)
    {
        var positions = TerminalSourceGraphemeSegmenter.GetOffsets(text); var ids = pastes.Select(entry => entry.Id).ToHashSet();
        var markers = Marker.Matches(text).Cast<Match>().Where(match => int.TryParse(match.Groups[1].Value, NumberStyles.None,
            CultureInfo.InvariantCulture, out var id) && ids.Contains(id)).ToArray();
        var result = new List<(int, int)>(positions.Length); var markerIndex = 0;
        for (var index = 0; index < positions.Length; index++)
        {
            var at = positions[index]; var end = index + 1 == positions.Length ? text.Length : positions[index + 1];
            while (markerIndex < markers.Length && markers[markerIndex].Index + markers[markerIndex].Length <= at) markerIndex++;
            var marker = markerIndex < markers.Length ? markers[markerIndex] : null;
            if (marker is not null && at >= marker.Index && at < marker.Index + marker.Length)
            { if (at == marker.Index) result.Add((at, marker.Length)); }
            else result.Add((at, end - at));
        }
        return result;
    }

    private int LineStart()
    { var position = Snapshot.CursorUtf16Offset; while (position > 0 && Snapshot.Text[position - 1] != '\n') position--; return position; }
    private int LineEnd()
    { var position = Snapshot.CursorUtf16Offset; while (position < Snapshot.Text.Length && Snapshot.Text[position] != '\n') position++; return position; }

    private void EnsureRevisionAvailable(int count = 1)
    {
        if (deliveringObserver) throw new TerminalTextEditorException(TerminalTextEditorFailure.InvalidOptions);
        if (editorRevision > long.MaxValue - count)
            throw new TerminalTextEditorException(TerminalTextEditorFailure.ResourceLimit);
    }
    private void AdvanceRevision()
    {
        if (cancelJumpOnAdmission) { characterJump = CharacterJump.None; cancelJumpOnAdmission = false; }
        editorRevision++;
    }

    private TerminalEditorInputDisposition DispatchCancelingJump(Func<TerminalEditorInputDisposition> operation)
    {
        if (characterJump == CharacterJump.None) return operation();
        EnsureRevisionAvailable(); cancelJumpOnAdmission = true;
        try
        {
            var result = operation(); AdmitJumpCancellation(); return result;
        }
        finally { cancelJumpOnAdmission = false; }
    }

    private void AdmitJumpCancellation()
    {
        if (!cancelJumpOnAdmission) return;
        EnsureRevisionAvailable(); AdvanceRevision();
    }

    private void CompleteCharacterJump(string target)
    {
        ValidateInput(target); EnsureRevisionAvailable();
        var snapshot = Snapshot; var text = snapshot.Text; var start = LineStart(); var end = LineEnd();
        var current = true; var match = -1;
        while (true)
        {
            if (characterJump == CharacterJump.Forward)
            {
                var search = current ? Math.Min(snapshot.CursorUtf16Offset + 1, end) : start;
                var found = text.AsSpan(search, end - search).IndexOf(target.AsSpan(), StringComparison.Ordinal);
                if (found >= 0) { match = search + found; break; }
                if (end == text.Length) break;
                start = end + 1; end = text.IndexOf('\n', start); if (end < 0) end = text.Length;
            }
            else
            {
                // JS lastIndexOf constrains the match's START, not its end, and clamps -1 to 0.
                var length = current ? Math.Min(end - start,
                    Math.Max(0, snapshot.CursorUtf16Offset - start - 1) + target.Length) : end - start;
                var found = text.AsSpan(start, length).LastIndexOf(target.AsSpan(), StringComparison.Ordinal);
                if (found >= 0) { match = start + found; break; }
                if (start == 0) break;
                end = start - 1; start = end;
                while (start > 0 && text[start - 1] != '\n') start--;
            }
            current = false;
        }
        // Source jump uses raw UTF16 substring offsets, including combining/marker interiors.
        // Existing literal controller admits scalar seams; strict grapheme model stays unchanged.
        if (match >= 0) { editor.SetCursor(match); ClearPreferences(); }
        characterJump = CharacterJump.None; lastAction = EditorAction.None; AdvanceRevision();
    }
    // Unicode 17 data and Node 24.19 scalar word-like observations; generated by the owned data tool.
    // Unicode data is distributed under word-data/Unicode-LICENSE.txt. No ICU dictionary emulation.
    private static class WordData
    {
        internal enum Kind { Other, CR, LF, Newline, Extend, ZWJ, Regional_Indicator, Format, Katakana, Hebrew_Letter, ALetter, Single_Quote, Double_Quote, MidNumLet, MidLetter, MidNum, Numeric, ExtendNumLet, WSegSpace }
        private static readonly int[] Properties =
        [
            0xa, 0xa, 0x2,
            0xb, 0xc, 0x3,
            0xd, 0xd, 0x1,
            0x20, 0x20, 0x12,
            0x22, 0x22, 0xc,
            0x27, 0x27, 0xb,
            0x2c, 0x2c, 0xf,
            0x2e, 0x2e, 0xd,
            0x30, 0x39, 0x10,
            0x3a, 0x3a, 0xe,
            0x3b, 0x3b, 0xf,
            0x41, 0x5a, 0xa,
            0x5f, 0x5f, 0x11,
            0x61, 0x7a, 0xa,
            0x85, 0x85, 0x3,
            0xaa, 0xaa, 0xa,
            0xad, 0xad, 0x7,
            0xb5, 0xb5, 0xa,
            0xb7, 0xb7, 0xe,
            0xb8, 0xb8, 0xa,
            0xba, 0xba, 0xa,
            0xc0, 0xd6, 0xa,
            0xd8, 0xf6, 0xa,
            0xf8, 0x1ba, 0xa,
            0x1bb, 0x1bb, 0xa,
            0x1bc, 0x1bf, 0xa,
            0x1c0, 0x1c3, 0xa,
            0x1c4, 0x293, 0xa,
            0x294, 0x295, 0xa,
            0x296, 0x2af, 0xa,
            0x2b0, 0x2c1, 0xa,
            0x2c2, 0x2c5, 0xa,
            0x2c6, 0x2d1, 0xa,
            0x2d2, 0x2d7, 0xa,
            0x2de, 0x2df, 0xa,
            0x2e0, 0x2e4, 0xa,
            0x2e5, 0x2eb, 0xa,
            0x2ec, 0x2ec, 0xa,
            0x2ed, 0x2ed, 0xa,
            0x2ee, 0x2ee, 0xa,
            0x2ef, 0x2ff, 0xa,
            0x300, 0x36f, 0x4,
            0x370, 0x373, 0xa,
            0x374, 0x374, 0xa,
            0x376, 0x377, 0xa,
            0x37a, 0x37a, 0xa,
            0x37b, 0x37d, 0xa,
            0x37e, 0x37e, 0xf,
            0x37f, 0x37f, 0xa,
            0x386, 0x386, 0xa,
            0x387, 0x387, 0xe,
            0x388, 0x38a, 0xa,
            0x38c, 0x38c, 0xa,
            0x38e, 0x3a1, 0xa,
            0x3a3, 0x3f5, 0xa,
            0x3f7, 0x481, 0xa,
            0x483, 0x487, 0x4,
            0x488, 0x489, 0x4,
            0x48a, 0x52f, 0xa,
            0x531, 0x556, 0xa,
            0x559, 0x559, 0xa,
            0x55a, 0x55c, 0xa,
            0x55e, 0x55e, 0xa,
            0x55f, 0x55f, 0xe,
            0x560, 0x588, 0xa,
            0x589, 0x589, 0xf,
            0x58a, 0x58a, 0xa,
            0x591, 0x5bd, 0x4,
            0x5bf, 0x5bf, 0x4,
            0x5c1, 0x5c2, 0x4,
            0x5c4, 0x5c5, 0x4,
            0x5c7, 0x5c7, 0x4,
            0x5d0, 0x5ea, 0x9,
            0x5ef, 0x5f2, 0x9,
            0x5f3, 0x5f3, 0xa,
            0x5f4, 0x5f4, 0xe,
            0x600, 0x605, 0x10,
            0x60c, 0x60d, 0xf,
            0x610, 0x61a, 0x4,
            0x61c, 0x61c, 0x7,
            0x620, 0x63f, 0xa,
            0x640, 0x640, 0xa,
            0x641, 0x64a, 0xa,
            0x64b, 0x65f, 0x4,
            0x660, 0x669, 0x10,
            0x66b, 0x66b, 0x10,
            0x66c, 0x66c, 0xf,
            0x66e, 0x66f, 0xa,
            0x670, 0x670, 0x4,
            0x671, 0x6d3, 0xa,
            0x6d5, 0x6d5, 0xa,
            0x6d6, 0x6dc, 0x4,
            0x6dd, 0x6dd, 0x10,
            0x6df, 0x6e4, 0x4,
            0x6e5, 0x6e6, 0xa,
            0x6e7, 0x6e8, 0x4,
            0x6ea, 0x6ed, 0x4,
            0x6ee, 0x6ef, 0xa,
            0x6f0, 0x6f9, 0x10,
            0x6fa, 0x6fc, 0xa,
            0x6ff, 0x6ff, 0xa,
            0x70f, 0x70f, 0xa,
            0x710, 0x710, 0xa,
            0x711, 0x711, 0x4,
            0x712, 0x72f, 0xa,
            0x730, 0x74a, 0x4,
            0x74d, 0x7a5, 0xa,
            0x7a6, 0x7b0, 0x4,
            0x7b1, 0x7b1, 0xa,
            0x7c0, 0x7c9, 0x10,
            0x7ca, 0x7ea, 0xa,
            0x7eb, 0x7f3, 0x4,
            0x7f4, 0x7f5, 0xa,
            0x7f8, 0x7f8, 0xf,
            0x7fa, 0x7fa, 0xa,
            0x7fd, 0x7fd, 0x4,
            0x800, 0x815, 0xa,
            0x816, 0x819, 0x4,
            0x81a, 0x81a, 0xa,
            0x81b, 0x823, 0x4,
            0x824, 0x824, 0xa,
            0x825, 0x827, 0x4,
            0x828, 0x828, 0xa,
            0x829, 0x82d, 0x4,
            0x840, 0x858, 0xa,
            0x859, 0x85b, 0x4,
            0x860, 0x86a, 0xa,
            0x870, 0x887, 0xa,
            0x889, 0x88f, 0xa,
            0x890, 0x891, 0x10,
            0x897, 0x89f, 0x4,
            0x8a0, 0x8c8, 0xa,
            0x8c9, 0x8c9, 0xa,
            0x8ca, 0x8e1, 0x4,
            0x8e2, 0x8e2, 0x10,
            0x8e3, 0x902, 0x4,
            0x903, 0x903, 0x4,
            0x904, 0x939, 0xa,
            0x93a, 0x93a, 0x4,
            0x93b, 0x93b, 0x4,
            0x93c, 0x93c, 0x4,
            0x93d, 0x93d, 0xa,
            0x93e, 0x940, 0x4,
            0x941, 0x948, 0x4,
            0x949, 0x94c, 0x4,
            0x94d, 0x94d, 0x4,
            0x94e, 0x94f, 0x4,
            0x950, 0x950, 0xa,
            0x951, 0x957, 0x4,
            0x958, 0x961, 0xa,
            0x962, 0x963, 0x4,
            0x966, 0x96f, 0x10,
            0x971, 0x971, 0xa,
            0x972, 0x980, 0xa,
            0x981, 0x981, 0x4,
            0x982, 0x983, 0x4,
            0x985, 0x98c, 0xa,
            0x98f, 0x990, 0xa,
            0x993, 0x9a8, 0xa,
            0x9aa, 0x9b0, 0xa,
            0x9b2, 0x9b2, 0xa,
            0x9b6, 0x9b9, 0xa,
            0x9bc, 0x9bc, 0x4,
            0x9bd, 0x9bd, 0xa,
            0x9be, 0x9c0, 0x4,
            0x9c1, 0x9c4, 0x4,
            0x9c7, 0x9c8, 0x4,
            0x9cb, 0x9cc, 0x4,
            0x9cd, 0x9cd, 0x4,
            0x9ce, 0x9ce, 0xa,
            0x9d7, 0x9d7, 0x4,
            0x9dc, 0x9dd, 0xa,
            0x9df, 0x9e1, 0xa,
            0x9e2, 0x9e3, 0x4,
            0x9e6, 0x9ef, 0x10,
            0x9f0, 0x9f1, 0xa,
            0x9fc, 0x9fc, 0xa,
            0x9fe, 0x9fe, 0x4,
            0xa01, 0xa02, 0x4,
            0xa03, 0xa03, 0x4,
            0xa05, 0xa0a, 0xa,
            0xa0f, 0xa10, 0xa,
            0xa13, 0xa28, 0xa,
            0xa2a, 0xa30, 0xa,
            0xa32, 0xa33, 0xa,
            0xa35, 0xa36, 0xa,
            0xa38, 0xa39, 0xa,
            0xa3c, 0xa3c, 0x4,
            0xa3e, 0xa40, 0x4,
            0xa41, 0xa42, 0x4,
            0xa47, 0xa48, 0x4,
            0xa4b, 0xa4d, 0x4,
            0xa51, 0xa51, 0x4,
            0xa59, 0xa5c, 0xa,
            0xa5e, 0xa5e, 0xa,
            0xa66, 0xa6f, 0x10,
            0xa70, 0xa71, 0x4,
            0xa72, 0xa74, 0xa,
            0xa75, 0xa75, 0x4,
            0xa81, 0xa82, 0x4,
            0xa83, 0xa83, 0x4,
            0xa85, 0xa8d, 0xa,
            0xa8f, 0xa91, 0xa,
            0xa93, 0xaa8, 0xa,
            0xaaa, 0xab0, 0xa,
            0xab2, 0xab3, 0xa,
            0xab5, 0xab9, 0xa,
            0xabc, 0xabc, 0x4,
            0xabd, 0xabd, 0xa,
            0xabe, 0xac0, 0x4,
            0xac1, 0xac5, 0x4,
            0xac7, 0xac8, 0x4,
            0xac9, 0xac9, 0x4,
            0xacb, 0xacc, 0x4,
            0xacd, 0xacd, 0x4,
            0xad0, 0xad0, 0xa,
            0xae0, 0xae1, 0xa,
            0xae2, 0xae3, 0x4,
            0xae6, 0xaef, 0x10,
            0xaf9, 0xaf9, 0xa,
            0xafa, 0xaff, 0x4,
            0xb01, 0xb01, 0x4,
            0xb02, 0xb03, 0x4,
            0xb05, 0xb0c, 0xa,
            0xb0f, 0xb10, 0xa,
            0xb13, 0xb28, 0xa,
            0xb2a, 0xb30, 0xa,
            0xb32, 0xb33, 0xa,
            0xb35, 0xb39, 0xa,
            0xb3c, 0xb3c, 0x4,
            0xb3d, 0xb3d, 0xa,
            0xb3e, 0xb3e, 0x4,
            0xb3f, 0xb3f, 0x4,
            0xb40, 0xb40, 0x4,
            0xb41, 0xb44, 0x4,
            0xb47, 0xb48, 0x4,
            0xb4b, 0xb4c, 0x4,
            0xb4d, 0xb4d, 0x4,
            0xb55, 0xb56, 0x4,
            0xb57, 0xb57, 0x4,
            0xb5c, 0xb5d, 0xa,
            0xb5f, 0xb61, 0xa,
            0xb62, 0xb63, 0x4,
            0xb66, 0xb6f, 0x10,
            0xb71, 0xb71, 0xa,
            0xb82, 0xb82, 0x4,
            0xb83, 0xb83, 0xa,
            0xb85, 0xb8a, 0xa,
            0xb8e, 0xb90, 0xa,
            0xb92, 0xb95, 0xa,
            0xb99, 0xb9a, 0xa,
            0xb9c, 0xb9c, 0xa,
            0xb9e, 0xb9f, 0xa,
            0xba3, 0xba4, 0xa,
            0xba8, 0xbaa, 0xa,
            0xbae, 0xbb9, 0xa,
            0xbbe, 0xbbf, 0x4,
            0xbc0, 0xbc0, 0x4,
            0xbc1, 0xbc2, 0x4,
            0xbc6, 0xbc8, 0x4,
            0xbca, 0xbcc, 0x4,
            0xbcd, 0xbcd, 0x4,
            0xbd0, 0xbd0, 0xa,
            0xbd7, 0xbd7, 0x4,
            0xbe6, 0xbef, 0x10,
            0xc00, 0xc00, 0x4,
            0xc01, 0xc03, 0x4,
            0xc04, 0xc04, 0x4,
            0xc05, 0xc0c, 0xa,
            0xc0e, 0xc10, 0xa,
            0xc12, 0xc28, 0xa,
            0xc2a, 0xc39, 0xa,
            0xc3c, 0xc3c, 0x4,
            0xc3d, 0xc3d, 0xa,
            0xc3e, 0xc40, 0x4,
            0xc41, 0xc44, 0x4,
            0xc46, 0xc48, 0x4,
            0xc4a, 0xc4d, 0x4,
            0xc55, 0xc56, 0x4,
            0xc58, 0xc5a, 0xa,
            0xc5c, 0xc5d, 0xa,
            0xc60, 0xc61, 0xa,
            0xc62, 0xc63, 0x4,
            0xc66, 0xc6f, 0x10,
            0xc80, 0xc80, 0xa,
            0xc81, 0xc81, 0x4,
            0xc82, 0xc83, 0x4,
            0xc85, 0xc8c, 0xa,
            0xc8e, 0xc90, 0xa,
            0xc92, 0xca8, 0xa,
            0xcaa, 0xcb3, 0xa,
            0xcb5, 0xcb9, 0xa,
            0xcbc, 0xcbc, 0x4,
            0xcbd, 0xcbd, 0xa,
            0xcbe, 0xcbe, 0x4,
            0xcbf, 0xcbf, 0x4,
            0xcc0, 0xcc4, 0x4,
            0xcc6, 0xcc6, 0x4,
            0xcc7, 0xcc8, 0x4,
            0xcca, 0xccb, 0x4,
            0xccc, 0xccd, 0x4,
            0xcd5, 0xcd6, 0x4,
            0xcdc, 0xcde, 0xa,
            0xce0, 0xce1, 0xa,
            0xce2, 0xce3, 0x4,
            0xce6, 0xcef, 0x10,
            0xcf1, 0xcf2, 0xa,
            0xcf3, 0xcf3, 0x4,
            0xd00, 0xd01, 0x4,
            0xd02, 0xd03, 0x4,
            0xd04, 0xd0c, 0xa,
            0xd0e, 0xd10, 0xa,
            0xd12, 0xd3a, 0xa,
            0xd3b, 0xd3c, 0x4,
            0xd3d, 0xd3d, 0xa,
            0xd3e, 0xd40, 0x4,
            0xd41, 0xd44, 0x4,
            0xd46, 0xd48, 0x4,
            0xd4a, 0xd4c, 0x4,
            0xd4d, 0xd4d, 0x4,
            0xd4e, 0xd4e, 0xa,
            0xd54, 0xd56, 0xa,
            0xd57, 0xd57, 0x4,
            0xd5f, 0xd61, 0xa,
            0xd62, 0xd63, 0x4,
            0xd66, 0xd6f, 0x10,
            0xd7a, 0xd7f, 0xa,
            0xd81, 0xd81, 0x4,
            0xd82, 0xd83, 0x4,
            0xd85, 0xd96, 0xa,
            0xd9a, 0xdb1, 0xa,
            0xdb3, 0xdbb, 0xa,
            0xdbd, 0xdbd, 0xa,
            0xdc0, 0xdc6, 0xa,
            0xdca, 0xdca, 0x4,
            0xdcf, 0xdd1, 0x4,
            0xdd2, 0xdd4, 0x4,
            0xdd6, 0xdd6, 0x4,
            0xdd8, 0xddf, 0x4,
            0xde6, 0xdef, 0x10,
            0xdf2, 0xdf3, 0x4,
            0xe31, 0xe31, 0x4,
            0xe34, 0xe3a, 0x4,
            0xe47, 0xe4e, 0x4,
            0xe50, 0xe59, 0x10,
            0xeb1, 0xeb1, 0x4,
            0xeb4, 0xebc, 0x4,
            0xec8, 0xece, 0x4,
            0xed0, 0xed9, 0x10,
            0xf00, 0xf00, 0xa,
            0xf18, 0xf19, 0x4,
            0xf20, 0xf29, 0x10,
            0xf35, 0xf35, 0x4,
            0xf37, 0xf37, 0x4,
            0xf39, 0xf39, 0x4,
            0xf3e, 0xf3f, 0x4,
            0xf40, 0xf47, 0xa,
            0xf49, 0xf6c, 0xa,
            0xf71, 0xf7e, 0x4,
            0xf7f, 0xf7f, 0x4,
            0xf80, 0xf84, 0x4,
            0xf86, 0xf87, 0x4,
            0xf88, 0xf8c, 0xa,
            0xf8d, 0xf97, 0x4,
            0xf99, 0xfbc, 0x4,
            0xfc6, 0xfc6, 0x4,
            0x102b, 0x102c, 0x4,
            0x102d, 0x1030, 0x4,
            0x1031, 0x1031, 0x4,
            0x1032, 0x1037, 0x4,
            0x1038, 0x1038, 0x4,
            0x1039, 0x103a, 0x4,
            0x103b, 0x103c, 0x4,
            0x103d, 0x103e, 0x4,
            0x1040, 0x1049, 0x10,
            0x1056, 0x1057, 0x4,
            0x1058, 0x1059, 0x4,
            0x105e, 0x1060, 0x4,
            0x1062, 0x1064, 0x4,
            0x1067, 0x106d, 0x4,
            0x1071, 0x1074, 0x4,
            0x1082, 0x1082, 0x4,
            0x1083, 0x1084, 0x4,
            0x1085, 0x1086, 0x4,
            0x1087, 0x108c, 0x4,
            0x108d, 0x108d, 0x4,
            0x108f, 0x108f, 0x4,
            0x1090, 0x1099, 0x10,
            0x109a, 0x109c, 0x4,
            0x109d, 0x109d, 0x4,
            0x10a0, 0x10c5, 0xa,
            0x10c7, 0x10c7, 0xa,
            0x10cd, 0x10cd, 0xa,
            0x10d0, 0x10fa, 0xa,
            0x10fc, 0x10fc, 0xa,
            0x10fd, 0x10ff, 0xa,
            0x1100, 0x1248, 0xa,
            0x124a, 0x124d, 0xa,
            0x1250, 0x1256, 0xa,
            0x1258, 0x1258, 0xa,
            0x125a, 0x125d, 0xa,
            0x1260, 0x1288, 0xa,
            0x128a, 0x128d, 0xa,
            0x1290, 0x12b0, 0xa,
            0x12b2, 0x12b5, 0xa,
            0x12b8, 0x12be, 0xa,
            0x12c0, 0x12c0, 0xa,
            0x12c2, 0x12c5, 0xa,
            0x12c8, 0x12d6, 0xa,
            0x12d8, 0x1310, 0xa,
            0x1312, 0x1315, 0xa,
            0x1318, 0x135a, 0xa,
            0x135d, 0x135f, 0x4,
            0x1380, 0x138f, 0xa,
            0x13a0, 0x13f5, 0xa,
            0x13f8, 0x13fd, 0xa,
            0x1401, 0x166c, 0xa,
            0x166f, 0x167f, 0xa,
            0x1680, 0x1680, 0x12,
            0x1681, 0x169a, 0xa,
            0x16a0, 0x16ea, 0xa,
            0x16ee, 0x16f0, 0xa,
            0x16f1, 0x16f8, 0xa,
            0x1700, 0x1711, 0xa,
            0x1712, 0x1714, 0x4,
            0x1715, 0x1715, 0x4,
            0x171f, 0x1731, 0xa,
            0x1732, 0x1733, 0x4,
            0x1734, 0x1734, 0x4,
            0x1740, 0x1751, 0xa,
            0x1752, 0x1753, 0x4,
            0x1760, 0x176c, 0xa,
            0x176e, 0x1770, 0xa,
            0x1772, 0x1773, 0x4,
            0x17b4, 0x17b5, 0x4,
            0x17b6, 0x17b6, 0x4,
            0x17b7, 0x17bd, 0x4,
            0x17be, 0x17c5, 0x4,
            0x17c6, 0x17c6, 0x4,
            0x17c7, 0x17c8, 0x4,
            0x17c9, 0x17d3, 0x4,
            0x17dd, 0x17dd, 0x4,
            0x17e0, 0x17e9, 0x10,
            0x180b, 0x180d, 0x4,
            0x180e, 0x180e, 0x7,
            0x180f, 0x180f, 0x4,
            0x1810, 0x1819, 0x10,
            0x1820, 0x1842, 0xa,
            0x1843, 0x1843, 0xa,
            0x1844, 0x1878, 0xa,
            0x1880, 0x1884, 0xa,
            0x1885, 0x1886, 0x4,
            0x1887, 0x18a8, 0xa,
            0x18a9, 0x18a9, 0x4,
            0x18aa, 0x18aa, 0xa,
            0x18b0, 0x18f5, 0xa,
            0x1900, 0x191e, 0xa,
            0x1920, 0x1922, 0x4,
            0x1923, 0x1926, 0x4,
            0x1927, 0x1928, 0x4,
            0x1929, 0x192b, 0x4,
            0x1930, 0x1931, 0x4,
            0x1932, 0x1932, 0x4,
            0x1933, 0x1938, 0x4,
            0x1939, 0x193b, 0x4,
            0x1946, 0x194f, 0x10,
            0x19d0, 0x19d9, 0x10,
            0x19da, 0x19da, 0x10,
            0x1a00, 0x1a16, 0xa,
            0x1a17, 0x1a18, 0x4,
            0x1a19, 0x1a1a, 0x4,
            0x1a1b, 0x1a1b, 0x4,
            0x1a55, 0x1a55, 0x4,
            0x1a56, 0x1a56, 0x4,
            0x1a57, 0x1a57, 0x4,
            0x1a58, 0x1a5e, 0x4,
            0x1a60, 0x1a60, 0x4,
            0x1a61, 0x1a61, 0x4,
            0x1a62, 0x1a62, 0x4,
            0x1a63, 0x1a64, 0x4,
            0x1a65, 0x1a6c, 0x4,
            0x1a6d, 0x1a72, 0x4,
            0x1a73, 0x1a7c, 0x4,
            0x1a7f, 0x1a7f, 0x4,
            0x1a80, 0x1a89, 0x10,
            0x1a90, 0x1a99, 0x10,
            0x1ab0, 0x1abd, 0x4,
            0x1abe, 0x1abe, 0x4,
            0x1abf, 0x1add, 0x4,
            0x1ae0, 0x1aeb, 0x4,
            0x1b00, 0x1b03, 0x4,
            0x1b04, 0x1b04, 0x4,
            0x1b05, 0x1b33, 0xa,
            0x1b34, 0x1b34, 0x4,
            0x1b35, 0x1b35, 0x4,
            0x1b36, 0x1b3a, 0x4,
            0x1b3b, 0x1b3b, 0x4,
            0x1b3c, 0x1b3c, 0x4,
            0x1b3d, 0x1b41, 0x4,
            0x1b42, 0x1b42, 0x4,
            0x1b43, 0x1b44, 0x4,
            0x1b45, 0x1b4c, 0xa,
            0x1b50, 0x1b59, 0x10,
            0x1b6b, 0x1b73, 0x4,
            0x1b80, 0x1b81, 0x4,
            0x1b82, 0x1b82, 0x4,
            0x1b83, 0x1ba0, 0xa,
            0x1ba1, 0x1ba1, 0x4,
            0x1ba2, 0x1ba5, 0x4,
            0x1ba6, 0x1ba7, 0x4,
            0x1ba8, 0x1ba9, 0x4,
            0x1baa, 0x1baa, 0x4,
            0x1bab, 0x1bad, 0x4,
            0x1bae, 0x1baf, 0xa,
            0x1bb0, 0x1bb9, 0x10,
            0x1bba, 0x1be5, 0xa,
            0x1be6, 0x1be6, 0x4,
            0x1be7, 0x1be7, 0x4,
            0x1be8, 0x1be9, 0x4,
            0x1bea, 0x1bec, 0x4,
            0x1bed, 0x1bed, 0x4,
            0x1bee, 0x1bee, 0x4,
            0x1bef, 0x1bf1, 0x4,
            0x1bf2, 0x1bf3, 0x4,
            0x1c00, 0x1c23, 0xa,
            0x1c24, 0x1c2b, 0x4,
            0x1c2c, 0x1c33, 0x4,
            0x1c34, 0x1c35, 0x4,
            0x1c36, 0x1c37, 0x4,
            0x1c40, 0x1c49, 0x10,
            0x1c4d, 0x1c4f, 0xa,
            0x1c50, 0x1c59, 0x10,
            0x1c5a, 0x1c77, 0xa,
            0x1c78, 0x1c7d, 0xa,
            0x1c80, 0x1c8a, 0xa,
            0x1c90, 0x1cba, 0xa,
            0x1cbd, 0x1cbf, 0xa,
            0x1cd0, 0x1cd2, 0x4,
            0x1cd4, 0x1ce0, 0x4,
            0x1ce1, 0x1ce1, 0x4,
            0x1ce2, 0x1ce8, 0x4,
            0x1ce9, 0x1cec, 0xa,
            0x1ced, 0x1ced, 0x4,
            0x1cee, 0x1cf3, 0xa,
            0x1cf4, 0x1cf4, 0x4,
            0x1cf5, 0x1cf6, 0xa,
            0x1cf7, 0x1cf7, 0x4,
            0x1cf8, 0x1cf9, 0x4,
            0x1cfa, 0x1cfa, 0xa,
            0x1d00, 0x1d2b, 0xa,
            0x1d2c, 0x1d6a, 0xa,
            0x1d6b, 0x1d77, 0xa,
            0x1d78, 0x1d78, 0xa,
            0x1d79, 0x1d9a, 0xa,
            0x1d9b, 0x1dbf, 0xa,
            0x1dc0, 0x1dff, 0x4,
            0x1e00, 0x1f15, 0xa,
            0x1f18, 0x1f1d, 0xa,
            0x1f20, 0x1f45, 0xa,
            0x1f48, 0x1f4d, 0xa,
            0x1f50, 0x1f57, 0xa,
            0x1f59, 0x1f59, 0xa,
            0x1f5b, 0x1f5b, 0xa,
            0x1f5d, 0x1f5d, 0xa,
            0x1f5f, 0x1f7d, 0xa,
            0x1f80, 0x1fb4, 0xa,
            0x1fb6, 0x1fbc, 0xa,
            0x1fbe, 0x1fbe, 0xa,
            0x1fc2, 0x1fc4, 0xa,
            0x1fc6, 0x1fcc, 0xa,
            0x1fd0, 0x1fd3, 0xa,
            0x1fd6, 0x1fdb, 0xa,
            0x1fe0, 0x1fec, 0xa,
            0x1ff2, 0x1ff4, 0xa,
            0x1ff6, 0x1ffc, 0xa,
            0x2000, 0x2006, 0x12,
            0x2008, 0x200a, 0x12,
            0x200c, 0x200c, 0x4,
            0x200d, 0x200d, 0x5,
            0x200e, 0x200f, 0x7,
            0x2018, 0x2018, 0xd,
            0x2019, 0x2019, 0xd,
            0x2024, 0x2024, 0xd,
            0x2027, 0x2027, 0xe,
            0x2028, 0x2028, 0x3,
            0x2029, 0x2029, 0x3,
            0x202a, 0x202e, 0x7,
            0x202f, 0x202f, 0x11,
            0x203f, 0x2040, 0x11,
            0x2044, 0x2044, 0xf,
            0x2054, 0x2054, 0x11,
            0x205f, 0x205f, 0x12,
            0x2060, 0x2064, 0x7,
            0x2066, 0x206f, 0x7,
            0x2071, 0x2071, 0xa,
            0x207f, 0x207f, 0xa,
            0x2090, 0x209c, 0xa,
            0x20d0, 0x20dc, 0x4,
            0x20dd, 0x20e0, 0x4,
            0x20e1, 0x20e1, 0x4,
            0x20e2, 0x20e4, 0x4,
            0x20e5, 0x20f0, 0x4,
            0x2102, 0x2102, 0xa,
            0x2107, 0x2107, 0xa,
            0x210a, 0x2113, 0xa,
            0x2115, 0x2115, 0xa,
            0x2119, 0x211d, 0xa,
            0x2124, 0x2124, 0xa,
            0x2126, 0x2126, 0xa,
            0x2128, 0x2128, 0xa,
            0x212a, 0x212d, 0xa,
            0x212f, 0x2134, 0xa,
            0x2135, 0x2138, 0xa,
            0x2139, 0x2139, 0xa,
            0x213c, 0x213f, 0xa,
            0x2145, 0x2149, 0xa,
            0x214e, 0x214e, 0xa,
            0x2160, 0x2182, 0xa,
            0x2183, 0x2184, 0xa,
            0x2185, 0x2188, 0xa,
            0x24b6, 0x24e9, 0xa,
            0x2c00, 0x2c7b, 0xa,
            0x2c7c, 0x2c7d, 0xa,
            0x2c7e, 0x2ce4, 0xa,
            0x2ceb, 0x2cee, 0xa,
            0x2cef, 0x2cf1, 0x4,
            0x2cf2, 0x2cf3, 0xa,
            0x2d00, 0x2d25, 0xa,
            0x2d27, 0x2d27, 0xa,
            0x2d2d, 0x2d2d, 0xa,
            0x2d30, 0x2d67, 0xa,
            0x2d6f, 0x2d6f, 0xa,
            0x2d7f, 0x2d7f, 0x4,
            0x2d80, 0x2d96, 0xa,
            0x2da0, 0x2da6, 0xa,
            0x2da8, 0x2dae, 0xa,
            0x2db0, 0x2db6, 0xa,
            0x2db8, 0x2dbe, 0xa,
            0x2dc0, 0x2dc6, 0xa,
            0x2dc8, 0x2dce, 0xa,
            0x2dd0, 0x2dd6, 0xa,
            0x2dd8, 0x2dde, 0xa,
            0x2de0, 0x2dff, 0x4,
            0x2e2f, 0x2e2f, 0xa,
            0x3000, 0x3000, 0x12,
            0x3005, 0x3005, 0xa,
            0x302a, 0x302d, 0x4,
            0x302e, 0x302f, 0x4,
            0x3031, 0x3035, 0x8,
            0x303b, 0x303b, 0xa,
            0x303c, 0x303c, 0xa,
            0x3099, 0x309a, 0x4,
            0x309b, 0x309c, 0x8,
            0x30a0, 0x30a0, 0x8,
            0x30a1, 0x30fa, 0x8,
            0x30fc, 0x30fe, 0x8,
            0x30ff, 0x30ff, 0x8,
            0x3105, 0x312f, 0xa,
            0x3131, 0x318e, 0xa,
            0x31a0, 0x31bf, 0xa,
            0x31f0, 0x31ff, 0x8,
            0x32d0, 0x32fe, 0x8,
            0x3300, 0x3357, 0x8,
            0xa000, 0xa014, 0xa,
            0xa015, 0xa015, 0xa,
            0xa016, 0xa48c, 0xa,
            0xa4d0, 0xa4f7, 0xa,
            0xa4f8, 0xa4fd, 0xa,
            0xa500, 0xa60b, 0xa,
            0xa60c, 0xa60c, 0xa,
            0xa610, 0xa61f, 0xa,
            0xa620, 0xa629, 0x10,
            0xa62a, 0xa62b, 0xa,
            0xa640, 0xa66d, 0xa,
            0xa66e, 0xa66e, 0xa,
            0xa66f, 0xa66f, 0x4,
            0xa670, 0xa672, 0x4,
            0xa674, 0xa67d, 0x4,
            0xa67f, 0xa67f, 0xa,
            0xa680, 0xa69b, 0xa,
            0xa69c, 0xa69d, 0xa,
            0xa69e, 0xa69f, 0x4,
            0xa6a0, 0xa6e5, 0xa,
            0xa6e6, 0xa6ef, 0xa,
            0xa6f0, 0xa6f1, 0x4,
            0xa708, 0xa716, 0xa,
            0xa717, 0xa71f, 0xa,
            0xa720, 0xa721, 0xa,
            0xa722, 0xa76f, 0xa,
            0xa770, 0xa770, 0xa,
            0xa771, 0xa787, 0xa,
            0xa788, 0xa788, 0xa,
            0xa789, 0xa78a, 0xa,
            0xa78b, 0xa78e, 0xa,
            0xa78f, 0xa78f, 0xa,
            0xa790, 0xa7dc, 0xa,
            0xa7f1, 0xa7f4, 0xa,
            0xa7f5, 0xa7f6, 0xa,
            0xa7f7, 0xa7f7, 0xa,
            0xa7f8, 0xa7f9, 0xa,
            0xa7fa, 0xa7fa, 0xa,
            0xa7fb, 0xa801, 0xa,
            0xa802, 0xa802, 0x4,
            0xa803, 0xa805, 0xa,
            0xa806, 0xa806, 0x4,
            0xa807, 0xa80a, 0xa,
            0xa80b, 0xa80b, 0x4,
            0xa80c, 0xa822, 0xa,
            0xa823, 0xa824, 0x4,
            0xa825, 0xa826, 0x4,
            0xa827, 0xa827, 0x4,
            0xa82c, 0xa82c, 0x4,
            0xa840, 0xa873, 0xa,
            0xa880, 0xa881, 0x4,
            0xa882, 0xa8b3, 0xa,
            0xa8b4, 0xa8c3, 0x4,
            0xa8c4, 0xa8c5, 0x4,
            0xa8d0, 0xa8d9, 0x10,
            0xa8e0, 0xa8f1, 0x4,
            0xa8f2, 0xa8f7, 0xa,
            0xa8fb, 0xa8fb, 0xa,
            0xa8fd, 0xa8fe, 0xa,
            0xa8ff, 0xa8ff, 0x4,
            0xa900, 0xa909, 0x10,
            0xa90a, 0xa925, 0xa,
            0xa926, 0xa92d, 0x4,
            0xa930, 0xa946, 0xa,
            0xa947, 0xa951, 0x4,
            0xa952, 0xa953, 0x4,
            0xa960, 0xa97c, 0xa,
            0xa980, 0xa982, 0x4,
            0xa983, 0xa983, 0x4,
            0xa984, 0xa9b2, 0xa,
            0xa9b3, 0xa9b3, 0x4,
            0xa9b4, 0xa9b5, 0x4,
            0xa9b6, 0xa9b9, 0x4,
            0xa9ba, 0xa9bb, 0x4,
            0xa9bc, 0xa9bd, 0x4,
            0xa9be, 0xa9c0, 0x4,
            0xa9cf, 0xa9cf, 0xa,
            0xa9d0, 0xa9d9, 0x10,
            0xa9e5, 0xa9e5, 0x4,
            0xa9f0, 0xa9f9, 0x10,
            0xaa00, 0xaa28, 0xa,
            0xaa29, 0xaa2e, 0x4,
            0xaa2f, 0xaa30, 0x4,
            0xaa31, 0xaa32, 0x4,
            0xaa33, 0xaa34, 0x4,
            0xaa35, 0xaa36, 0x4,
            0xaa40, 0xaa42, 0xa,
            0xaa43, 0xaa43, 0x4,
            0xaa44, 0xaa4b, 0xa,
            0xaa4c, 0xaa4c, 0x4,
            0xaa4d, 0xaa4d, 0x4,
            0xaa50, 0xaa59, 0x10,
            0xaa7b, 0xaa7b, 0x4,
            0xaa7c, 0xaa7c, 0x4,
            0xaa7d, 0xaa7d, 0x4,
            0xaab0, 0xaab0, 0x4,
            0xaab2, 0xaab4, 0x4,
            0xaab7, 0xaab8, 0x4,
            0xaabe, 0xaabf, 0x4,
            0xaac1, 0xaac1, 0x4,
            0xaae0, 0xaaea, 0xa,
            0xaaeb, 0xaaeb, 0x4,
            0xaaec, 0xaaed, 0x4,
            0xaaee, 0xaaef, 0x4,
            0xaaf2, 0xaaf2, 0xa,
            0xaaf3, 0xaaf4, 0xa,
            0xaaf5, 0xaaf5, 0x4,
            0xaaf6, 0xaaf6, 0x4,
            0xab01, 0xab06, 0xa,
            0xab09, 0xab0e, 0xa,
            0xab11, 0xab16, 0xa,
            0xab20, 0xab26, 0xa,
            0xab28, 0xab2e, 0xa,
            0xab30, 0xab5a, 0xa,
            0xab5b, 0xab5b, 0xa,
            0xab5c, 0xab5f, 0xa,
            0xab60, 0xab68, 0xa,
            0xab69, 0xab69, 0xa,
            0xab70, 0xabbf, 0xa,
            0xabc0, 0xabe2, 0xa,
            0xabe3, 0xabe4, 0x4,
            0xabe5, 0xabe5, 0x4,
            0xabe6, 0xabe7, 0x4,
            0xabe8, 0xabe8, 0x4,
            0xabe9, 0xabea, 0x4,
            0xabec, 0xabec, 0x4,
            0xabed, 0xabed, 0x4,
            0xabf0, 0xabf9, 0x10,
            0xac00, 0xd7a3, 0xa,
            0xd7b0, 0xd7c6, 0xa,
            0xd7cb, 0xd7fb, 0xa,
            0xfb00, 0xfb06, 0xa,
            0xfb13, 0xfb17, 0xa,
            0xfb1d, 0xfb1d, 0x9,
            0xfb1e, 0xfb1e, 0x4,
            0xfb1f, 0xfb28, 0x9,
            0xfb2a, 0xfb36, 0x9,
            0xfb38, 0xfb3c, 0x9,
            0xfb3e, 0xfb3e, 0x9,
            0xfb40, 0xfb41, 0x9,
            0xfb43, 0xfb44, 0x9,
            0xfb46, 0xfb4f, 0x9,
            0xfb50, 0xfbb1, 0xa,
            0xfbd3, 0xfd3d, 0xa,
            0xfd50, 0xfd8f, 0xa,
            0xfd92, 0xfdc7, 0xa,
            0xfdf0, 0xfdfb, 0xa,
            0xfe00, 0xfe0f, 0x4,
            0xfe13, 0xfe13, 0xe,
            0xfe20, 0xfe2f, 0x4,
            0xfe33, 0xfe34, 0x11,
            0xfe4d, 0xfe4f, 0x11,
            0xfe50, 0xfe50, 0xf,
            0xfe52, 0xfe52, 0xd,
            0xfe54, 0xfe54, 0xf,
            0xfe55, 0xfe55, 0xe,
            0xfe70, 0xfe74, 0xa,
            0xfe76, 0xfefc, 0xa,
            0xfeff, 0xfeff, 0x7,
            0xff07, 0xff07, 0xd,
            0xff0c, 0xff0c, 0xf,
            0xff0e, 0xff0e, 0xd,
            0xff10, 0xff19, 0x10,
            0xff1a, 0xff1a, 0xe,
            0xff1b, 0xff1b, 0xf,
            0xff21, 0xff3a, 0xa,
            0xff3f, 0xff3f, 0x11,
            0xff41, 0xff5a, 0xa,
            0xff66, 0xff6f, 0x8,
            0xff70, 0xff70, 0x8,
            0xff71, 0xff9d, 0x8,
            0xff9e, 0xff9f, 0x4,
            0xffa0, 0xffbe, 0xa,
            0xffc2, 0xffc7, 0xa,
            0xffca, 0xffcf, 0xa,
            0xffd2, 0xffd7, 0xa,
            0xffda, 0xffdc, 0xa,
            0xfff9, 0xfffb, 0x7,
            0x10000, 0x1000b, 0xa,
            0x1000d, 0x10026, 0xa,
            0x10028, 0x1003a, 0xa,
            0x1003c, 0x1003d, 0xa,
            0x1003f, 0x1004d, 0xa,
            0x10050, 0x1005d, 0xa,
            0x10080, 0x100fa, 0xa,
            0x10140, 0x10174, 0xa,
            0x101fd, 0x101fd, 0x4,
            0x10280, 0x1029c, 0xa,
            0x102a0, 0x102d0, 0xa,
            0x102e0, 0x102e0, 0x4,
            0x10300, 0x1031f, 0xa,
            0x1032d, 0x10340, 0xa,
            0x10341, 0x10341, 0xa,
            0x10342, 0x10349, 0xa,
            0x1034a, 0x1034a, 0xa,
            0x10350, 0x10375, 0xa,
            0x10376, 0x1037a, 0x4,
            0x10380, 0x1039d, 0xa,
            0x103a0, 0x103c3, 0xa,
            0x103c8, 0x103cf, 0xa,
            0x103d1, 0x103d5, 0xa,
            0x10400, 0x1044f, 0xa,
            0x10450, 0x1049d, 0xa,
            0x104a0, 0x104a9, 0x10,
            0x104b0, 0x104d3, 0xa,
            0x104d8, 0x104fb, 0xa,
            0x10500, 0x10527, 0xa,
            0x10530, 0x10563, 0xa,
            0x10570, 0x1057a, 0xa,
            0x1057c, 0x1058a, 0xa,
            0x1058c, 0x10592, 0xa,
            0x10594, 0x10595, 0xa,
            0x10597, 0x105a1, 0xa,
            0x105a3, 0x105b1, 0xa,
            0x105b3, 0x105b9, 0xa,
            0x105bb, 0x105bc, 0xa,
            0x105c0, 0x105f3, 0xa,
            0x10600, 0x10736, 0xa,
            0x10740, 0x10755, 0xa,
            0x10760, 0x10767, 0xa,
            0x10780, 0x10785, 0xa,
            0x10787, 0x107b0, 0xa,
            0x107b2, 0x107ba, 0xa,
            0x10800, 0x10805, 0xa,
            0x10808, 0x10808, 0xa,
            0x1080a, 0x10835, 0xa,
            0x10837, 0x10838, 0xa,
            0x1083c, 0x1083c, 0xa,
            0x1083f, 0x10855, 0xa,
            0x10860, 0x10876, 0xa,
            0x10880, 0x1089e, 0xa,
            0x108e0, 0x108f2, 0xa,
            0x108f4, 0x108f5, 0xa,
            0x10900, 0x10915, 0xa,
            0x10920, 0x10939, 0xa,
            0x10940, 0x10959, 0xa,
            0x10980, 0x109b7, 0xa,
            0x109be, 0x109bf, 0xa,
            0x10a00, 0x10a00, 0xa,
            0x10a01, 0x10a03, 0x4,
            0x10a05, 0x10a06, 0x4,
            0x10a0c, 0x10a0f, 0x4,
            0x10a10, 0x10a13, 0xa,
            0x10a15, 0x10a17, 0xa,
            0x10a19, 0x10a35, 0xa,
            0x10a38, 0x10a3a, 0x4,
            0x10a3f, 0x10a3f, 0x4,
            0x10a60, 0x10a7c, 0xa,
            0x10a80, 0x10a9c, 0xa,
            0x10ac0, 0x10ac7, 0xa,
            0x10ac9, 0x10ae4, 0xa,
            0x10ae5, 0x10ae6, 0x4,
            0x10b00, 0x10b35, 0xa,
            0x10b40, 0x10b55, 0xa,
            0x10b60, 0x10b72, 0xa,
            0x10b80, 0x10b91, 0xa,
            0x10c00, 0x10c48, 0xa,
            0x10c80, 0x10cb2, 0xa,
            0x10cc0, 0x10cf2, 0xa,
            0x10d00, 0x10d23, 0xa,
            0x10d24, 0x10d27, 0x4,
            0x10d30, 0x10d39, 0x10,
            0x10d40, 0x10d49, 0x10,
            0x10d4a, 0x10d4d, 0xa,
            0x10d4e, 0x10d4e, 0xa,
            0x10d4f, 0x10d4f, 0xa,
            0x10d50, 0x10d65, 0xa,
            0x10d69, 0x10d6d, 0x4,
            0x10d6f, 0x10d6f, 0xa,
            0x10d70, 0x10d85, 0xa,
            0x10e80, 0x10ea9, 0xa,
            0x10eab, 0x10eac, 0x4,
            0x10eb0, 0x10eb1, 0xa,
            0x10ec2, 0x10ec4, 0xa,
            0x10ec5, 0x10ec5, 0xa,
            0x10ec6, 0x10ec7, 0xa,
            0x10efa, 0x10eff, 0x4,
            0x10f00, 0x10f1c, 0xa,
            0x10f27, 0x10f27, 0xa,
            0x10f30, 0x10f45, 0xa,
            0x10f46, 0x10f50, 0x4,
            0x10f70, 0x10f81, 0xa,
            0x10f82, 0x10f85, 0x4,
            0x10fb0, 0x10fc4, 0xa,
            0x10fe0, 0x10ff6, 0xa,
            0x11000, 0x11000, 0x4,
            0x11001, 0x11001, 0x4,
            0x11002, 0x11002, 0x4,
            0x11003, 0x11037, 0xa,
            0x11038, 0x11046, 0x4,
            0x11066, 0x1106f, 0x10,
            0x11070, 0x11070, 0x4,
            0x11071, 0x11072, 0xa,
            0x11073, 0x11074, 0x4,
            0x11075, 0x11075, 0xa,
            0x1107f, 0x11081, 0x4,
            0x11082, 0x11082, 0x4,
            0x11083, 0x110af, 0xa,
            0x110b0, 0x110b2, 0x4,
            0x110b3, 0x110b6, 0x4,
            0x110b7, 0x110b8, 0x4,
            0x110b9, 0x110ba, 0x4,
            0x110bd, 0x110bd, 0x10,
            0x110c2, 0x110c2, 0x4,
            0x110cd, 0x110cd, 0x10,
            0x110d0, 0x110e8, 0xa,
            0x110f0, 0x110f9, 0x10,
            0x11100, 0x11102, 0x4,
            0x11103, 0x11126, 0xa,
            0x11127, 0x1112b, 0x4,
            0x1112c, 0x1112c, 0x4,
            0x1112d, 0x11134, 0x4,
            0x11136, 0x1113f, 0x10,
            0x11144, 0x11144, 0xa,
            0x11145, 0x11146, 0x4,
            0x11147, 0x11147, 0xa,
            0x11150, 0x11172, 0xa,
            0x11173, 0x11173, 0x4,
            0x11176, 0x11176, 0xa,
            0x11180, 0x11181, 0x4,
            0x11182, 0x11182, 0x4,
            0x11183, 0x111b2, 0xa,
            0x111b3, 0x111b5, 0x4,
            0x111b6, 0x111be, 0x4,
            0x111bf, 0x111c0, 0x4,
            0x111c1, 0x111c4, 0xa,
            0x111c9, 0x111cc, 0x4,
            0x111ce, 0x111ce, 0x4,
            0x111cf, 0x111cf, 0x4,
            0x111d0, 0x111d9, 0x10,
            0x111da, 0x111da, 0xa,
            0x111dc, 0x111dc, 0xa,
            0x11200, 0x11211, 0xa,
            0x11213, 0x1122b, 0xa,
            0x1122c, 0x1122e, 0x4,
            0x1122f, 0x11231, 0x4,
            0x11232, 0x11233, 0x4,
            0x11234, 0x11234, 0x4,
            0x11235, 0x11235, 0x4,
            0x11236, 0x11237, 0x4,
            0x1123e, 0x1123e, 0x4,
            0x1123f, 0x11240, 0xa,
            0x11241, 0x11241, 0x4,
            0x11280, 0x11286, 0xa,
            0x11288, 0x11288, 0xa,
            0x1128a, 0x1128d, 0xa,
            0x1128f, 0x1129d, 0xa,
            0x1129f, 0x112a8, 0xa,
            0x112b0, 0x112de, 0xa,
            0x112df, 0x112df, 0x4,
            0x112e0, 0x112e2, 0x4,
            0x112e3, 0x112ea, 0x4,
            0x112f0, 0x112f9, 0x10,
            0x11300, 0x11301, 0x4,
            0x11302, 0x11303, 0x4,
            0x11305, 0x1130c, 0xa,
            0x1130f, 0x11310, 0xa,
            0x11313, 0x11328, 0xa,
            0x1132a, 0x11330, 0xa,
            0x11332, 0x11333, 0xa,
            0x11335, 0x11339, 0xa,
            0x1133b, 0x1133c, 0x4,
            0x1133d, 0x1133d, 0xa,
            0x1133e, 0x1133f, 0x4,
            0x11340, 0x11340, 0x4,
            0x11341, 0x11344, 0x4,
            0x11347, 0x11348, 0x4,
            0x1134b, 0x1134d, 0x4,
            0x11350, 0x11350, 0xa,
            0x11357, 0x11357, 0x4,
            0x1135d, 0x11361, 0xa,
            0x11362, 0x11363, 0x4,
            0x11366, 0x1136c, 0x4,
            0x11370, 0x11374, 0x4,
            0x11380, 0x11389, 0xa,
            0x1138b, 0x1138b, 0xa,
            0x1138e, 0x1138e, 0xa,
            0x11390, 0x113b5, 0xa,
            0x113b7, 0x113b7, 0xa,
            0x113b8, 0x113ba, 0x4,
            0x113bb, 0x113c0, 0x4,
            0x113c2, 0x113c2, 0x4,
            0x113c5, 0x113c5, 0x4,
            0x113c7, 0x113ca, 0x4,
            0x113cc, 0x113cd, 0x4,
            0x113ce, 0x113ce, 0x4,
            0x113cf, 0x113cf, 0x4,
            0x113d0, 0x113d0, 0x4,
            0x113d1, 0x113d1, 0xa,
            0x113d2, 0x113d2, 0x4,
            0x113d3, 0x113d3, 0xa,
            0x113e1, 0x113e2, 0x4,
            0x11400, 0x11434, 0xa,
            0x11435, 0x11437, 0x4,
            0x11438, 0x1143f, 0x4,
            0x11440, 0x11441, 0x4,
            0x11442, 0x11444, 0x4,
            0x11445, 0x11445, 0x4,
            0x11446, 0x11446, 0x4,
            0x11447, 0x1144a, 0xa,
            0x11450, 0x11459, 0x10,
            0x1145e, 0x1145e, 0x4,
            0x1145f, 0x11461, 0xa,
            0x11480, 0x114af, 0xa,
            0x114b0, 0x114b2, 0x4,
            0x114b3, 0x114b8, 0x4,
            0x114b9, 0x114b9, 0x4,
            0x114ba, 0x114ba, 0x4,
            0x114bb, 0x114be, 0x4,
            0x114bf, 0x114c0, 0x4,
            0x114c1, 0x114c1, 0x4,
            0x114c2, 0x114c3, 0x4,
            0x114c4, 0x114c5, 0xa,
            0x114c7, 0x114c7, 0xa,
            0x114d0, 0x114d9, 0x10,
            0x11580, 0x115ae, 0xa,
            0x115af, 0x115b1, 0x4,
            0x115b2, 0x115b5, 0x4,
            0x115b8, 0x115bb, 0x4,
            0x115bc, 0x115bd, 0x4,
            0x115be, 0x115be, 0x4,
            0x115bf, 0x115c0, 0x4,
            0x115d8, 0x115db, 0xa,
            0x115dc, 0x115dd, 0x4,
            0x11600, 0x1162f, 0xa,
            0x11630, 0x11632, 0x4,
            0x11633, 0x1163a, 0x4,
            0x1163b, 0x1163c, 0x4,
            0x1163d, 0x1163d, 0x4,
            0x1163e, 0x1163e, 0x4,
            0x1163f, 0x11640, 0x4,
            0x11644, 0x11644, 0xa,
            0x11650, 0x11659, 0x10,
            0x11680, 0x116aa, 0xa,
            0x116ab, 0x116ab, 0x4,
            0x116ac, 0x116ac, 0x4,
            0x116ad, 0x116ad, 0x4,
            0x116ae, 0x116af, 0x4,
            0x116b0, 0x116b5, 0x4,
            0x116b6, 0x116b6, 0x4,
            0x116b7, 0x116b7, 0x4,
            0x116b8, 0x116b8, 0xa,
            0x116c0, 0x116c9, 0x10,
            0x116d0, 0x116e3, 0x10,
            0x1171d, 0x1171d, 0x4,
            0x1171e, 0x1171e, 0x4,
            0x1171f, 0x1171f, 0x4,
            0x11720, 0x11721, 0x4,
            0x11722, 0x11725, 0x4,
            0x11726, 0x11726, 0x4,
            0x11727, 0x1172b, 0x4,
            0x11730, 0x11739, 0x10,
            0x11800, 0x1182b, 0xa,
            0x1182c, 0x1182e, 0x4,
            0x1182f, 0x11837, 0x4,
            0x11838, 0x11838, 0x4,
            0x11839, 0x1183a, 0x4,
            0x118a0, 0x118df, 0xa,
            0x118e0, 0x118e9, 0x10,
            0x118ff, 0x11906, 0xa,
            0x11909, 0x11909, 0xa,
            0x1190c, 0x11913, 0xa,
            0x11915, 0x11916, 0xa,
            0x11918, 0x1192f, 0xa,
            0x11930, 0x11935, 0x4,
            0x11937, 0x11938, 0x4,
            0x1193b, 0x1193c, 0x4,
            0x1193d, 0x1193d, 0x4,
            0x1193e, 0x1193e, 0x4,
            0x1193f, 0x1193f, 0xa,
            0x11940, 0x11940, 0x4,
            0x11941, 0x11941, 0xa,
            0x11942, 0x11942, 0x4,
            0x11943, 0x11943, 0x4,
            0x11950, 0x11959, 0x10,
            0x119a0, 0x119a7, 0xa,
            0x119aa, 0x119d0, 0xa,
            0x119d1, 0x119d3, 0x4,
            0x119d4, 0x119d7, 0x4,
            0x119da, 0x119db, 0x4,
            0x119dc, 0x119df, 0x4,
            0x119e0, 0x119e0, 0x4,
            0x119e1, 0x119e1, 0xa,
            0x119e3, 0x119e3, 0xa,
            0x119e4, 0x119e4, 0x4,
            0x11a00, 0x11a00, 0xa,
            0x11a01, 0x11a0a, 0x4,
            0x11a0b, 0x11a32, 0xa,
            0x11a33, 0x11a38, 0x4,
            0x11a39, 0x11a39, 0x4,
            0x11a3a, 0x11a3a, 0xa,
            0x11a3b, 0x11a3e, 0x4,
            0x11a47, 0x11a47, 0x4,
            0x11a50, 0x11a50, 0xa,
            0x11a51, 0x11a56, 0x4,
            0x11a57, 0x11a58, 0x4,
            0x11a59, 0x11a5b, 0x4,
            0x11a5c, 0x11a89, 0xa,
            0x11a8a, 0x11a96, 0x4,
            0x11a97, 0x11a97, 0x4,
            0x11a98, 0x11a99, 0x4,
            0x11a9d, 0x11a9d, 0xa,
            0x11ab0, 0x11af8, 0xa,
            0x11b60, 0x11b60, 0x4,
            0x11b61, 0x11b61, 0x4,
            0x11b62, 0x11b64, 0x4,
            0x11b65, 0x11b65, 0x4,
            0x11b66, 0x11b66, 0x4,
            0x11b67, 0x11b67, 0x4,
            0x11bc0, 0x11be0, 0xa,
            0x11bf0, 0x11bf9, 0x10,
            0x11c00, 0x11c08, 0xa,
            0x11c0a, 0x11c2e, 0xa,
            0x11c2f, 0x11c2f, 0x4,
            0x11c30, 0x11c36, 0x4,
            0x11c38, 0x11c3d, 0x4,
            0x11c3e, 0x11c3e, 0x4,
            0x11c3f, 0x11c3f, 0x4,
            0x11c40, 0x11c40, 0xa,
            0x11c50, 0x11c59, 0x10,
            0x11c72, 0x11c8f, 0xa,
            0x11c92, 0x11ca7, 0x4,
            0x11ca9, 0x11ca9, 0x4,
            0x11caa, 0x11cb0, 0x4,
            0x11cb1, 0x11cb1, 0x4,
            0x11cb2, 0x11cb3, 0x4,
            0x11cb4, 0x11cb4, 0x4,
            0x11cb5, 0x11cb6, 0x4,
            0x11d00, 0x11d06, 0xa,
            0x11d08, 0x11d09, 0xa,
            0x11d0b, 0x11d30, 0xa,
            0x11d31, 0x11d36, 0x4,
            0x11d3a, 0x11d3a, 0x4,
            0x11d3c, 0x11d3d, 0x4,
            0x11d3f, 0x11d45, 0x4,
            0x11d46, 0x11d46, 0xa,
            0x11d47, 0x11d47, 0x4,
            0x11d50, 0x11d59, 0x10,
            0x11d60, 0x11d65, 0xa,
            0x11d67, 0x11d68, 0xa,
            0x11d6a, 0x11d89, 0xa,
            0x11d8a, 0x11d8e, 0x4,
            0x11d90, 0x11d91, 0x4,
            0x11d93, 0x11d94, 0x4,
            0x11d95, 0x11d95, 0x4,
            0x11d96, 0x11d96, 0x4,
            0x11d97, 0x11d97, 0x4,
            0x11d98, 0x11d98, 0xa,
            0x11da0, 0x11da9, 0x10,
            0x11db0, 0x11dd8, 0xa,
            0x11dd9, 0x11dd9, 0xa,
            0x11dda, 0x11ddb, 0xa,
            0x11de0, 0x11de9, 0x10,
            0x11ee0, 0x11ef2, 0xa,
            0x11ef3, 0x11ef4, 0x4,
            0x11ef5, 0x11ef6, 0x4,
            0x11f00, 0x11f01, 0x4,
            0x11f02, 0x11f02, 0xa,
            0x11f03, 0x11f03, 0x4,
            0x11f04, 0x11f10, 0xa,
            0x11f12, 0x11f33, 0xa,
            0x11f34, 0x11f35, 0x4,
            0x11f36, 0x11f3a, 0x4,
            0x11f3e, 0x11f3f, 0x4,
            0x11f40, 0x11f40, 0x4,
            0x11f41, 0x11f41, 0x4,
            0x11f42, 0x11f42, 0x4,
            0x11f50, 0x11f59, 0x10,
            0x11f5a, 0x11f5a, 0x4,
            0x11fb0, 0x11fb0, 0xa,
            0x12000, 0x12399, 0xa,
            0x12400, 0x1246e, 0xa,
            0x12480, 0x12543, 0xa,
            0x12f90, 0x12ff0, 0xa,
            0x13000, 0x1342f, 0xa,
            0x13430, 0x1343f, 0x7,
            0x13440, 0x13440, 0x4,
            0x13441, 0x13446, 0xa,
            0x13447, 0x13455, 0x4,
            0x13460, 0x143fa, 0xa,
            0x14400, 0x14646, 0xa,
            0x16100, 0x1611d, 0xa,
            0x1611e, 0x16129, 0x4,
            0x1612a, 0x1612c, 0x4,
            0x1612d, 0x1612f, 0x4,
            0x16130, 0x16139, 0x10,
            0x16800, 0x16a38, 0xa,
            0x16a40, 0x16a5e, 0xa,
            0x16a60, 0x16a69, 0x10,
            0x16a70, 0x16abe, 0xa,
            0x16ac0, 0x16ac9, 0x10,
            0x16ad0, 0x16aed, 0xa,
            0x16af0, 0x16af4, 0x4,
            0x16b00, 0x16b2f, 0xa,
            0x16b30, 0x16b36, 0x4,
            0x16b40, 0x16b43, 0xa,
            0x16b50, 0x16b59, 0x10,
            0x16b63, 0x16b77, 0xa,
            0x16b7d, 0x16b8f, 0xa,
            0x16d40, 0x16d42, 0xa,
            0x16d43, 0x16d6a, 0xa,
            0x16d6b, 0x16d6c, 0xa,
            0x16d70, 0x16d79, 0x10,
            0x16e40, 0x16e7f, 0xa,
            0x16ea0, 0x16eb8, 0xa,
            0x16ebb, 0x16ed3, 0xa,
            0x16f00, 0x16f4a, 0xa,
            0x16f4f, 0x16f4f, 0x4,
            0x16f50, 0x16f50, 0xa,
            0x16f51, 0x16f87, 0x4,
            0x16f8f, 0x16f92, 0x4,
            0x16f93, 0x16f9f, 0xa,
            0x16fe0, 0x16fe1, 0xa,
            0x16fe3, 0x16fe3, 0xa,
            0x16fe4, 0x16fe4, 0x4,
            0x16ff0, 0x16ff1, 0x4,
            0x1aff0, 0x1aff3, 0x8,
            0x1aff5, 0x1affb, 0x8,
            0x1affd, 0x1affe, 0x8,
            0x1b000, 0x1b000, 0x8,
            0x1b120, 0x1b122, 0x8,
            0x1b155, 0x1b155, 0x8,
            0x1b164, 0x1b167, 0x8,
            0x1bc00, 0x1bc6a, 0xa,
            0x1bc70, 0x1bc7c, 0xa,
            0x1bc80, 0x1bc88, 0xa,
            0x1bc90, 0x1bc99, 0xa,
            0x1bc9d, 0x1bc9e, 0x4,
            0x1bca0, 0x1bca3, 0x7,
            0x1ccf0, 0x1ccf9, 0x10,
            0x1cf00, 0x1cf2d, 0x4,
            0x1cf30, 0x1cf46, 0x4,
            0x1d165, 0x1d166, 0x4,
            0x1d167, 0x1d169, 0x4,
            0x1d16d, 0x1d172, 0x4,
            0x1d173, 0x1d17a, 0x7,
            0x1d17b, 0x1d182, 0x4,
            0x1d185, 0x1d18b, 0x4,
            0x1d1aa, 0x1d1ad, 0x4,
            0x1d242, 0x1d244, 0x4,
            0x1d400, 0x1d454, 0xa,
            0x1d456, 0x1d49c, 0xa,
            0x1d49e, 0x1d49f, 0xa,
            0x1d4a2, 0x1d4a2, 0xa,
            0x1d4a5, 0x1d4a6, 0xa,
            0x1d4a9, 0x1d4ac, 0xa,
            0x1d4ae, 0x1d4b9, 0xa,
            0x1d4bb, 0x1d4bb, 0xa,
            0x1d4bd, 0x1d4c3, 0xa,
            0x1d4c5, 0x1d505, 0xa,
            0x1d507, 0x1d50a, 0xa,
            0x1d50d, 0x1d514, 0xa,
            0x1d516, 0x1d51c, 0xa,
            0x1d51e, 0x1d539, 0xa,
            0x1d53b, 0x1d53e, 0xa,
            0x1d540, 0x1d544, 0xa,
            0x1d546, 0x1d546, 0xa,
            0x1d54a, 0x1d550, 0xa,
            0x1d552, 0x1d6a5, 0xa,
            0x1d6a8, 0x1d6c0, 0xa,
            0x1d6c2, 0x1d6da, 0xa,
            0x1d6dc, 0x1d6fa, 0xa,
            0x1d6fc, 0x1d714, 0xa,
            0x1d716, 0x1d734, 0xa,
            0x1d736, 0x1d74e, 0xa,
            0x1d750, 0x1d76e, 0xa,
            0x1d770, 0x1d788, 0xa,
            0x1d78a, 0x1d7a8, 0xa,
            0x1d7aa, 0x1d7c2, 0xa,
            0x1d7c4, 0x1d7cb, 0xa,
            0x1d7ce, 0x1d7ff, 0x10,
            0x1da00, 0x1da36, 0x4,
            0x1da3b, 0x1da6c, 0x4,
            0x1da75, 0x1da75, 0x4,
            0x1da84, 0x1da84, 0x4,
            0x1da9b, 0x1da9f, 0x4,
            0x1daa1, 0x1daaf, 0x4,
            0x1df00, 0x1df09, 0xa,
            0x1df0a, 0x1df0a, 0xa,
            0x1df0b, 0x1df1e, 0xa,
            0x1df25, 0x1df2a, 0xa,
            0x1e000, 0x1e006, 0x4,
            0x1e008, 0x1e018, 0x4,
            0x1e01b, 0x1e021, 0x4,
            0x1e023, 0x1e024, 0x4,
            0x1e026, 0x1e02a, 0x4,
            0x1e030, 0x1e06d, 0xa,
            0x1e08f, 0x1e08f, 0x4,
            0x1e100, 0x1e12c, 0xa,
            0x1e130, 0x1e136, 0x4,
            0x1e137, 0x1e13d, 0xa,
            0x1e140, 0x1e149, 0x10,
            0x1e14e, 0x1e14e, 0xa,
            0x1e290, 0x1e2ad, 0xa,
            0x1e2ae, 0x1e2ae, 0x4,
            0x1e2c0, 0x1e2eb, 0xa,
            0x1e2ec, 0x1e2ef, 0x4,
            0x1e2f0, 0x1e2f9, 0x10,
            0x1e4d0, 0x1e4ea, 0xa,
            0x1e4eb, 0x1e4eb, 0xa,
            0x1e4ec, 0x1e4ef, 0x4,
            0x1e4f0, 0x1e4f9, 0x10,
            0x1e5d0, 0x1e5ed, 0xa,
            0x1e5ee, 0x1e5ef, 0x4,
            0x1e5f0, 0x1e5f0, 0xa,
            0x1e5f1, 0x1e5fa, 0x10,
            0x1e6c0, 0x1e6de, 0xa,
            0x1e6e0, 0x1e6e2, 0xa,
            0x1e6e3, 0x1e6e3, 0x4,
            0x1e6e4, 0x1e6e5, 0xa,
            0x1e6e6, 0x1e6e6, 0x4,
            0x1e6e7, 0x1e6ed, 0xa,
            0x1e6ee, 0x1e6ef, 0x4,
            0x1e6f0, 0x1e6f4, 0xa,
            0x1e6f5, 0x1e6f5, 0x4,
            0x1e6fe, 0x1e6fe, 0xa,
            0x1e6ff, 0x1e6ff, 0xa,
            0x1e7e0, 0x1e7e6, 0xa,
            0x1e7e8, 0x1e7eb, 0xa,
            0x1e7ed, 0x1e7ee, 0xa,
            0x1e7f0, 0x1e7fe, 0xa,
            0x1e800, 0x1e8c4, 0xa,
            0x1e8d0, 0x1e8d6, 0x4,
            0x1e900, 0x1e943, 0xa,
            0x1e944, 0x1e94a, 0x4,
            0x1e94b, 0x1e94b, 0xa,
            0x1e950, 0x1e959, 0x10,
            0x1ee00, 0x1ee03, 0xa,
            0x1ee05, 0x1ee1f, 0xa,
            0x1ee21, 0x1ee22, 0xa,
            0x1ee24, 0x1ee24, 0xa,
            0x1ee27, 0x1ee27, 0xa,
            0x1ee29, 0x1ee32, 0xa,
            0x1ee34, 0x1ee37, 0xa,
            0x1ee39, 0x1ee39, 0xa,
            0x1ee3b, 0x1ee3b, 0xa,
            0x1ee42, 0x1ee42, 0xa,
            0x1ee47, 0x1ee47, 0xa,
            0x1ee49, 0x1ee49, 0xa,
            0x1ee4b, 0x1ee4b, 0xa,
            0x1ee4d, 0x1ee4f, 0xa,
            0x1ee51, 0x1ee52, 0xa,
            0x1ee54, 0x1ee54, 0xa,
            0x1ee57, 0x1ee57, 0xa,
            0x1ee59, 0x1ee59, 0xa,
            0x1ee5b, 0x1ee5b, 0xa,
            0x1ee5d, 0x1ee5d, 0xa,
            0x1ee5f, 0x1ee5f, 0xa,
            0x1ee61, 0x1ee62, 0xa,
            0x1ee64, 0x1ee64, 0xa,
            0x1ee67, 0x1ee6a, 0xa,
            0x1ee6c, 0x1ee72, 0xa,
            0x1ee74, 0x1ee77, 0xa,
            0x1ee79, 0x1ee7c, 0xa,
            0x1ee7e, 0x1ee7e, 0xa,
            0x1ee80, 0x1ee89, 0xa,
            0x1ee8b, 0x1ee9b, 0xa,
            0x1eea1, 0x1eea3, 0xa,
            0x1eea5, 0x1eea9, 0xa,
            0x1eeab, 0x1eebb, 0xa,
            0x1f130, 0x1f149, 0xa,
            0x1f150, 0x1f169, 0xa,
            0x1f170, 0x1f189, 0xa,
            0x1f1e6, 0x1f1ff, 0x6,
            0x1f3fb, 0x1f3ff, 0x4,
            0x1fbf0, 0x1fbf9, 0x10,
            0xe0001, 0xe0001, 0x7,
            0xe0020, 0xe007f, 0x4,
            0xe0100, 0xe01ef, 0x4,
        ];
        private static readonly int[] Pictographs =
        [
            0xa9, 0xa9,
            0xae, 0xae,
            0x203c, 0x203c,
            0x2049, 0x2049,
            0x2122, 0x2122,
            0x2139, 0x2139,
            0x2194, 0x2199,
            0x21a9, 0x21aa,
            0x231a, 0x231b,
            0x2328, 0x2328,
            0x23cf, 0x23cf,
            0x23e9, 0x23ec,
            0x23ed, 0x23ee,
            0x23ef, 0x23ef,
            0x23f0, 0x23f0,
            0x23f1, 0x23f2,
            0x23f3, 0x23f3,
            0x23f8, 0x23fa,
            0x24c2, 0x24c2,
            0x25aa, 0x25ab,
            0x25b6, 0x25b6,
            0x25c0, 0x25c0,
            0x25fb, 0x25fe,
            0x2600, 0x2601,
            0x2602, 0x2603,
            0x2604, 0x2604,
            0x260e, 0x260e,
            0x2611, 0x2611,
            0x2614, 0x2615,
            0x2618, 0x2618,
            0x261d, 0x261d,
            0x2620, 0x2620,
            0x2622, 0x2623,
            0x2626, 0x2626,
            0x262a, 0x262a,
            0x262e, 0x262e,
            0x262f, 0x262f,
            0x2638, 0x2639,
            0x263a, 0x263a,
            0x2640, 0x2640,
            0x2642, 0x2642,
            0x2648, 0x2653,
            0x265f, 0x265f,
            0x2660, 0x2660,
            0x2663, 0x2663,
            0x2665, 0x2666,
            0x2668, 0x2668,
            0x267b, 0x267b,
            0x267e, 0x267e,
            0x267f, 0x267f,
            0x2692, 0x2692,
            0x2693, 0x2693,
            0x2694, 0x2694,
            0x2695, 0x2695,
            0x2696, 0x2697,
            0x2699, 0x2699,
            0x269b, 0x269c,
            0x26a0, 0x26a1,
            0x26a7, 0x26a7,
            0x26aa, 0x26ab,
            0x26b0, 0x26b1,
            0x26bd, 0x26be,
            0x26c4, 0x26c5,
            0x26c8, 0x26c8,
            0x26ce, 0x26ce,
            0x26cf, 0x26cf,
            0x26d1, 0x26d1,
            0x26d3, 0x26d3,
            0x26d4, 0x26d4,
            0x26e9, 0x26e9,
            0x26ea, 0x26ea,
            0x26f0, 0x26f1,
            0x26f2, 0x26f3,
            0x26f4, 0x26f4,
            0x26f5, 0x26f5,
            0x26f7, 0x26f9,
            0x26fa, 0x26fa,
            0x26fd, 0x26fd,
            0x2702, 0x2702,
            0x2705, 0x2705,
            0x2708, 0x270c,
            0x270d, 0x270d,
            0x270f, 0x270f,
            0x2712, 0x2712,
            0x2714, 0x2714,
            0x2716, 0x2716,
            0x271d, 0x271d,
            0x2721, 0x2721,
            0x2728, 0x2728,
            0x2733, 0x2734,
            0x2744, 0x2744,
            0x2747, 0x2747,
            0x274c, 0x274c,
            0x274e, 0x274e,
            0x2753, 0x2755,
            0x2757, 0x2757,
            0x2763, 0x2763,
            0x2764, 0x2764,
            0x2795, 0x2797,
            0x27a1, 0x27a1,
            0x27b0, 0x27b0,
            0x27bf, 0x27bf,
            0x2934, 0x2935,
            0x2b05, 0x2b07,
            0x2b1b, 0x2b1c,
            0x2b50, 0x2b50,
            0x2b55, 0x2b55,
            0x3030, 0x3030,
            0x303d, 0x303d,
            0x3297, 0x3297,
            0x3299, 0x3299,
            0x1f004, 0x1f004,
            0x1f02c, 0x1f02f,
            0x1f094, 0x1f09f,
            0x1f0af, 0x1f0b0,
            0x1f0c0, 0x1f0c0,
            0x1f0cf, 0x1f0cf,
            0x1f0d0, 0x1f0d0,
            0x1f0f6, 0x1f0ff,
            0x1f170, 0x1f171,
            0x1f17e, 0x1f17f,
            0x1f18e, 0x1f18e,
            0x1f191, 0x1f19a,
            0x1f1ae, 0x1f1e5,
            0x1f201, 0x1f202,
            0x1f203, 0x1f20f,
            0x1f21a, 0x1f21a,
            0x1f22f, 0x1f22f,
            0x1f232, 0x1f23a,
            0x1f23c, 0x1f23f,
            0x1f249, 0x1f24f,
            0x1f250, 0x1f251,
            0x1f252, 0x1f25f,
            0x1f266, 0x1f2ff,
            0x1f300, 0x1f30c,
            0x1f30d, 0x1f30e,
            0x1f30f, 0x1f30f,
            0x1f310, 0x1f310,
            0x1f311, 0x1f311,
            0x1f312, 0x1f312,
            0x1f313, 0x1f315,
            0x1f316, 0x1f318,
            0x1f319, 0x1f319,
            0x1f31a, 0x1f31a,
            0x1f31b, 0x1f31b,
            0x1f31c, 0x1f31c,
            0x1f31d, 0x1f31e,
            0x1f31f, 0x1f320,
            0x1f321, 0x1f321,
            0x1f324, 0x1f32c,
            0x1f32d, 0x1f32f,
            0x1f330, 0x1f331,
            0x1f332, 0x1f333,
            0x1f334, 0x1f335,
            0x1f336, 0x1f336,
            0x1f337, 0x1f34a,
            0x1f34b, 0x1f34b,
            0x1f34c, 0x1f34f,
            0x1f350, 0x1f350,
            0x1f351, 0x1f37b,
            0x1f37c, 0x1f37c,
            0x1f37d, 0x1f37d,
            0x1f37e, 0x1f37f,
            0x1f380, 0x1f393,
            0x1f396, 0x1f397,
            0x1f399, 0x1f39b,
            0x1f39e, 0x1f39f,
            0x1f3a0, 0x1f3c4,
            0x1f3c5, 0x1f3c5,
            0x1f3c6, 0x1f3c6,
            0x1f3c7, 0x1f3c7,
            0x1f3c8, 0x1f3c8,
            0x1f3c9, 0x1f3c9,
            0x1f3ca, 0x1f3ca,
            0x1f3cb, 0x1f3ce,
            0x1f3cf, 0x1f3d3,
            0x1f3d4, 0x1f3df,
            0x1f3e0, 0x1f3e3,
            0x1f3e4, 0x1f3e4,
            0x1f3e5, 0x1f3f0,
            0x1f3f3, 0x1f3f3,
            0x1f3f4, 0x1f3f4,
            0x1f3f5, 0x1f3f5,
            0x1f3f7, 0x1f3f7,
            0x1f3f8, 0x1f3fa,
            0x1f400, 0x1f407,
            0x1f408, 0x1f408,
            0x1f409, 0x1f40b,
            0x1f40c, 0x1f40e,
            0x1f40f, 0x1f410,
            0x1f411, 0x1f412,
            0x1f413, 0x1f413,
            0x1f414, 0x1f414,
            0x1f415, 0x1f415,
            0x1f416, 0x1f416,
            0x1f417, 0x1f429,
            0x1f42a, 0x1f42a,
            0x1f42b, 0x1f43e,
            0x1f43f, 0x1f43f,
            0x1f440, 0x1f440,
            0x1f441, 0x1f441,
            0x1f442, 0x1f464,
            0x1f465, 0x1f465,
            0x1f466, 0x1f46b,
            0x1f46c, 0x1f46d,
            0x1f46e, 0x1f4ac,
            0x1f4ad, 0x1f4ad,
            0x1f4ae, 0x1f4b5,
            0x1f4b6, 0x1f4b7,
            0x1f4b8, 0x1f4eb,
            0x1f4ec, 0x1f4ed,
            0x1f4ee, 0x1f4ee,
            0x1f4ef, 0x1f4ef,
            0x1f4f0, 0x1f4f4,
            0x1f4f5, 0x1f4f5,
            0x1f4f6, 0x1f4f7,
            0x1f4f8, 0x1f4f8,
            0x1f4f9, 0x1f4fc,
            0x1f4fd, 0x1f4fd,
            0x1f4ff, 0x1f502,
            0x1f503, 0x1f503,
            0x1f504, 0x1f507,
            0x1f508, 0x1f508,
            0x1f509, 0x1f509,
            0x1f50a, 0x1f514,
            0x1f515, 0x1f515,
            0x1f516, 0x1f52b,
            0x1f52c, 0x1f52d,
            0x1f52e, 0x1f53d,
            0x1f549, 0x1f54a,
            0x1f54b, 0x1f54e,
            0x1f550, 0x1f55b,
            0x1f55c, 0x1f567,
            0x1f56f, 0x1f570,
            0x1f573, 0x1f579,
            0x1f57a, 0x1f57a,
            0x1f587, 0x1f587,
            0x1f58a, 0x1f58d,
            0x1f590, 0x1f590,
            0x1f595, 0x1f596,
            0x1f5a4, 0x1f5a4,
            0x1f5a5, 0x1f5a5,
            0x1f5a8, 0x1f5a8,
            0x1f5b1, 0x1f5b2,
            0x1f5bc, 0x1f5bc,
            0x1f5c2, 0x1f5c4,
            0x1f5d1, 0x1f5d3,
            0x1f5dc, 0x1f5de,
            0x1f5e1, 0x1f5e1,
            0x1f5e3, 0x1f5e3,
            0x1f5e8, 0x1f5e8,
            0x1f5ef, 0x1f5ef,
            0x1f5f3, 0x1f5f3,
            0x1f5fa, 0x1f5fa,
            0x1f5fb, 0x1f5ff,
            0x1f600, 0x1f600,
            0x1f601, 0x1f606,
            0x1f607, 0x1f608,
            0x1f609, 0x1f60d,
            0x1f60e, 0x1f60e,
            0x1f60f, 0x1f60f,
            0x1f610, 0x1f610,
            0x1f611, 0x1f611,
            0x1f612, 0x1f614,
            0x1f615, 0x1f615,
            0x1f616, 0x1f616,
            0x1f617, 0x1f617,
            0x1f618, 0x1f618,
            0x1f619, 0x1f619,
            0x1f61a, 0x1f61a,
            0x1f61b, 0x1f61b,
            0x1f61c, 0x1f61e,
            0x1f61f, 0x1f61f,
            0x1f620, 0x1f625,
            0x1f626, 0x1f627,
            0x1f628, 0x1f62b,
            0x1f62c, 0x1f62c,
            0x1f62d, 0x1f62d,
            0x1f62e, 0x1f62f,
            0x1f630, 0x1f633,
            0x1f634, 0x1f634,
            0x1f635, 0x1f635,
            0x1f636, 0x1f636,
            0x1f637, 0x1f640,
            0x1f641, 0x1f644,
            0x1f645, 0x1f64f,
            0x1f680, 0x1f680,
            0x1f681, 0x1f682,
            0x1f683, 0x1f685,
            0x1f686, 0x1f686,
            0x1f687, 0x1f687,
            0x1f688, 0x1f688,
            0x1f689, 0x1f689,
            0x1f68a, 0x1f68b,
            0x1f68c, 0x1f68c,
            0x1f68d, 0x1f68d,
            0x1f68e, 0x1f68e,
            0x1f68f, 0x1f68f,
            0x1f690, 0x1f690,
            0x1f691, 0x1f693,
            0x1f694, 0x1f694,
            0x1f695, 0x1f695,
            0x1f696, 0x1f696,
            0x1f697, 0x1f697,
            0x1f698, 0x1f698,
            0x1f699, 0x1f69a,
            0x1f69b, 0x1f6a1,
            0x1f6a2, 0x1f6a2,
            0x1f6a3, 0x1f6a3,
            0x1f6a4, 0x1f6a5,
            0x1f6a6, 0x1f6a6,
            0x1f6a7, 0x1f6ad,
            0x1f6ae, 0x1f6b1,
            0x1f6b2, 0x1f6b2,
            0x1f6b3, 0x1f6b5,
            0x1f6b6, 0x1f6b6,
            0x1f6b7, 0x1f6b8,
            0x1f6b9, 0x1f6be,
            0x1f6bf, 0x1f6bf,
            0x1f6c0, 0x1f6c0,
            0x1f6c1, 0x1f6c5,
            0x1f6cb, 0x1f6cb,
            0x1f6cc, 0x1f6cc,
            0x1f6cd, 0x1f6cf,
            0x1f6d0, 0x1f6d0,
            0x1f6d1, 0x1f6d2,
            0x1f6d5, 0x1f6d5,
            0x1f6d6, 0x1f6d7,
            0x1f6d8, 0x1f6d8,
            0x1f6d9, 0x1f6db,
            0x1f6dc, 0x1f6dc,
            0x1f6dd, 0x1f6df,
            0x1f6e0, 0x1f6e5,
            0x1f6e9, 0x1f6e9,
            0x1f6eb, 0x1f6ec,
            0x1f6ed, 0x1f6ef,
            0x1f6f0, 0x1f6f0,
            0x1f6f3, 0x1f6f3,
            0x1f6f4, 0x1f6f6,
            0x1f6f7, 0x1f6f8,
            0x1f6f9, 0x1f6f9,
            0x1f6fa, 0x1f6fa,
            0x1f6fb, 0x1f6fc,
            0x1f6fd, 0x1f6ff,
            0x1f7da, 0x1f7df,
            0x1f7e0, 0x1f7eb,
            0x1f7ec, 0x1f7ef,
            0x1f7f0, 0x1f7f0,
            0x1f7f1, 0x1f7ff,
            0x1f80c, 0x1f80f,
            0x1f848, 0x1f84f,
            0x1f85a, 0x1f85f,
            0x1f888, 0x1f88f,
            0x1f8ae, 0x1f8af,
            0x1f8bc, 0x1f8bf,
            0x1f8c2, 0x1f8cf,
            0x1f8d9, 0x1f8ff,
            0x1f90c, 0x1f90c,
            0x1f90d, 0x1f90f,
            0x1f910, 0x1f918,
            0x1f919, 0x1f91e,
            0x1f91f, 0x1f91f,
            0x1f920, 0x1f927,
            0x1f928, 0x1f92f,
            0x1f930, 0x1f930,
            0x1f931, 0x1f932,
            0x1f933, 0x1f93a,
            0x1f93c, 0x1f93e,
            0x1f93f, 0x1f93f,
            0x1f940, 0x1f945,
            0x1f947, 0x1f94b,
            0x1f94c, 0x1f94c,
            0x1f94d, 0x1f94f,
            0x1f950, 0x1f95e,
            0x1f95f, 0x1f96b,
            0x1f96c, 0x1f970,
            0x1f971, 0x1f971,
            0x1f972, 0x1f972,
            0x1f973, 0x1f976,
            0x1f977, 0x1f978,
            0x1f979, 0x1f979,
            0x1f97a, 0x1f97a,
            0x1f97b, 0x1f97b,
            0x1f97c, 0x1f97f,
            0x1f980, 0x1f984,
            0x1f985, 0x1f991,
            0x1f992, 0x1f997,
            0x1f998, 0x1f9a2,
            0x1f9a3, 0x1f9a4,
            0x1f9a5, 0x1f9aa,
            0x1f9ab, 0x1f9ad,
            0x1f9ae, 0x1f9af,
            0x1f9b0, 0x1f9b9,
            0x1f9ba, 0x1f9bf,
            0x1f9c0, 0x1f9c0,
            0x1f9c1, 0x1f9c2,
            0x1f9c3, 0x1f9ca,
            0x1f9cb, 0x1f9cb,
            0x1f9cc, 0x1f9cc,
            0x1f9cd, 0x1f9cf,
            0x1f9d0, 0x1f9e6,
            0x1f9e7, 0x1f9ff,
            0x1fa58, 0x1fa5f,
            0x1fa6e, 0x1fa6f,
            0x1fa70, 0x1fa73,
            0x1fa74, 0x1fa74,
            0x1fa75, 0x1fa77,
            0x1fa78, 0x1fa7a,
            0x1fa7b, 0x1fa7c,
            0x1fa7d, 0x1fa7f,
            0x1fa80, 0x1fa82,
            0x1fa83, 0x1fa86,
            0x1fa87, 0x1fa88,
            0x1fa89, 0x1fa89,
            0x1fa8a, 0x1fa8a,
            0x1fa8b, 0x1fa8d,
            0x1fa8e, 0x1fa8e,
            0x1fa8f, 0x1fa8f,
            0x1fa90, 0x1fa95,
            0x1fa96, 0x1faa8,
            0x1faa9, 0x1faac,
            0x1faad, 0x1faaf,
            0x1fab0, 0x1fab6,
            0x1fab7, 0x1faba,
            0x1fabb, 0x1fabd,
            0x1fabe, 0x1fabe,
            0x1fabf, 0x1fabf,
            0x1fac0, 0x1fac2,
            0x1fac3, 0x1fac5,
            0x1fac6, 0x1fac6,
            0x1fac7, 0x1fac7,
            0x1fac8, 0x1fac8,
            0x1fac9, 0x1facc,
            0x1facd, 0x1facd,
            0x1face, 0x1facf,
            0x1fad0, 0x1fad6,
            0x1fad7, 0x1fad9,
            0x1fada, 0x1fadb,
            0x1fadc, 0x1fadc,
            0x1fadd, 0x1fade,
            0x1fadf, 0x1fadf,
            0x1fae0, 0x1fae7,
            0x1fae8, 0x1fae8,
            0x1fae9, 0x1fae9,
            0x1faea, 0x1faea,
            0x1faeb, 0x1faee,
            0x1faef, 0x1faef,
            0x1faf0, 0x1faf6,
            0x1faf7, 0x1faf8,
            0x1faf9, 0x1faff,
            0x1fc00, 0x1fffd,
        ];
        private static readonly int[] Wordlike =
        [
            0x30, 0x39,
            0x41, 0x5a,
            0x61, 0x7a,
            0xaa, 0xaa,
            0xb5, 0xb5,
            0xba, 0xba,
            0xc0, 0xd6,
            0xd8, 0xf6,
            0xf8, 0x2c1,
            0x2c6, 0x2d1,
            0x2e0, 0x2e4,
            0x2ec, 0x2ec,
            0x2ee, 0x2ee,
            0x370, 0x374,
            0x376, 0x377,
            0x37a, 0x37d,
            0x37f, 0x37f,
            0x386, 0x386,
            0x388, 0x38a,
            0x38c, 0x38c,
            0x38e, 0x3a1,
            0x3a3, 0x3f5,
            0x3f7, 0x481,
            0x48a, 0x52f,
            0x531, 0x556,
            0x559, 0x559,
            0x560, 0x588,
            0x5d0, 0x5ea,
            0x5ef, 0x5f2,
            0x620, 0x64a,
            0x660, 0x669,
            0x66e, 0x66f,
            0x671, 0x6d3,
            0x6d5, 0x6d5,
            0x6e5, 0x6e6,
            0x6ee, 0x6fc,
            0x6ff, 0x6ff,
            0x710, 0x710,
            0x712, 0x72f,
            0x74d, 0x7a5,
            0x7b1, 0x7b1,
            0x7c0, 0x7ea,
            0x7f4, 0x7f5,
            0x7fa, 0x7fa,
            0x800, 0x815,
            0x81a, 0x81a,
            0x824, 0x824,
            0x828, 0x828,
            0x840, 0x858,
            0x860, 0x86a,
            0x870, 0x887,
            0x889, 0x88f,
            0x8a0, 0x8c9,
            0x904, 0x939,
            0x93d, 0x93d,
            0x950, 0x950,
            0x958, 0x961,
            0x966, 0x96f,
            0x971, 0x980,
            0x985, 0x98c,
            0x98f, 0x990,
            0x993, 0x9a8,
            0x9aa, 0x9b0,
            0x9b2, 0x9b2,
            0x9b6, 0x9b9,
            0x9bd, 0x9bd,
            0x9ce, 0x9ce,
            0x9dc, 0x9dd,
            0x9df, 0x9e1,
            0x9e6, 0x9f1,
            0x9fc, 0x9fc,
            0xa05, 0xa0a,
            0xa0f, 0xa10,
            0xa13, 0xa28,
            0xa2a, 0xa30,
            0xa32, 0xa33,
            0xa35, 0xa36,
            0xa38, 0xa39,
            0xa59, 0xa5c,
            0xa5e, 0xa5e,
            0xa66, 0xa6f,
            0xa72, 0xa74,
            0xa85, 0xa8d,
            0xa8f, 0xa91,
            0xa93, 0xaa8,
            0xaaa, 0xab0,
            0xab2, 0xab3,
            0xab5, 0xab9,
            0xabd, 0xabd,
            0xad0, 0xad0,
            0xae0, 0xae1,
            0xae6, 0xaef,
            0xaf9, 0xaf9,
            0xb05, 0xb0c,
            0xb0f, 0xb10,
            0xb13, 0xb28,
            0xb2a, 0xb30,
            0xb32, 0xb33,
            0xb35, 0xb39,
            0xb3d, 0xb3d,
            0xb5c, 0xb5d,
            0xb5f, 0xb61,
            0xb66, 0xb6f,
            0xb71, 0xb71,
            0xb83, 0xb83,
            0xb85, 0xb8a,
            0xb8e, 0xb90,
            0xb92, 0xb95,
            0xb99, 0xb9a,
            0xb9c, 0xb9c,
            0xb9e, 0xb9f,
            0xba3, 0xba4,
            0xba8, 0xbaa,
            0xbae, 0xbb9,
            0xbd0, 0xbd0,
            0xbe6, 0xbef,
            0xc05, 0xc0c,
            0xc0e, 0xc10,
            0xc12, 0xc28,
            0xc2a, 0xc39,
            0xc3d, 0xc3d,
            0xc58, 0xc5a,
            0xc5c, 0xc5d,
            0xc60, 0xc61,
            0xc66, 0xc6f,
            0xc80, 0xc80,
            0xc85, 0xc8c,
            0xc8e, 0xc90,
            0xc92, 0xca8,
            0xcaa, 0xcb3,
            0xcb5, 0xcb9,
            0xcbd, 0xcbd,
            0xcdc, 0xcde,
            0xce0, 0xce1,
            0xce6, 0xcef,
            0xcf1, 0xcf2,
            0xd04, 0xd0c,
            0xd0e, 0xd10,
            0xd12, 0xd3a,
            0xd3d, 0xd3d,
            0xd4e, 0xd4e,
            0xd54, 0xd56,
            0xd5f, 0xd61,
            0xd66, 0xd6f,
            0xd7a, 0xd7f,
            0xd85, 0xd96,
            0xd9a, 0xdb1,
            0xdb3, 0xdbb,
            0xdbd, 0xdbd,
            0xdc0, 0xdc6,
            0xde6, 0xdef,
            0xe01, 0xe30,
            0xe32, 0xe33,
            0xe40, 0xe46,
            0xe50, 0xe59,
            0xe81, 0xe82,
            0xe84, 0xe84,
            0xe86, 0xe8a,
            0xe8c, 0xea3,
            0xea5, 0xea5,
            0xea7, 0xeb0,
            0xeb2, 0xeb3,
            0xebd, 0xebd,
            0xec0, 0xec4,
            0xec6, 0xec6,
            0xed0, 0xed9,
            0xedc, 0xedf,
            0xf00, 0xf00,
            0xf20, 0xf29,
            0xf40, 0xf47,
            0xf49, 0xf6c,
            0xf88, 0xf8c,
            0x1000, 0x102a,
            0x103f, 0x1049,
            0x1050, 0x1055,
            0x105a, 0x105d,
            0x1061, 0x1061,
            0x1065, 0x1066,
            0x106e, 0x1070,
            0x1075, 0x1081,
            0x108e, 0x108e,
            0x1090, 0x1099,
            0x10a0, 0x10c5,
            0x10c7, 0x10c7,
            0x10cd, 0x10cd,
            0x10d0, 0x10fa,
            0x10fc, 0x1248,
            0x124a, 0x124d,
            0x1250, 0x1256,
            0x1258, 0x1258,
            0x125a, 0x125d,
            0x1260, 0x1288,
            0x128a, 0x128d,
            0x1290, 0x12b0,
            0x12b2, 0x12b5,
            0x12b8, 0x12be,
            0x12c0, 0x12c0,
            0x12c2, 0x12c5,
            0x12c8, 0x12d6,
            0x12d8, 0x1310,
            0x1312, 0x1315,
            0x1318, 0x135a,
            0x1380, 0x138f,
            0x13a0, 0x13f5,
            0x13f8, 0x13fd,
            0x1401, 0x166c,
            0x166f, 0x167f,
            0x1681, 0x169a,
            0x16a0, 0x16ea,
            0x16ee, 0x16f8,
            0x1700, 0x1711,
            0x171f, 0x1731,
            0x1740, 0x1751,
            0x1760, 0x176c,
            0x176e, 0x1770,
            0x1780, 0x17b3,
            0x17d7, 0x17d7,
            0x17dc, 0x17dc,
            0x17e0, 0x17e9,
            0x1810, 0x1819,
            0x1820, 0x1878,
            0x1880, 0x1884,
            0x1887, 0x18a8,
            0x18aa, 0x18aa,
            0x18b0, 0x18f5,
            0x1900, 0x191e,
            0x1946, 0x196d,
            0x1970, 0x1974,
            0x1980, 0x19ab,
            0x19b0, 0x19c9,
            0x19d0, 0x19da,
            0x1a00, 0x1a16,
            0x1a20, 0x1a54,
            0x1a80, 0x1a89,
            0x1a90, 0x1a99,
            0x1aa7, 0x1aa7,
            0x1b05, 0x1b33,
            0x1b45, 0x1b4c,
            0x1b50, 0x1b59,
            0x1b83, 0x1ba0,
            0x1bae, 0x1be5,
            0x1c00, 0x1c23,
            0x1c40, 0x1c49,
            0x1c4d, 0x1c7d,
            0x1c80, 0x1c8a,
            0x1c90, 0x1cba,
            0x1cbd, 0x1cbf,
            0x1ce9, 0x1cec,
            0x1cee, 0x1cf3,
            0x1cf5, 0x1cf6,
            0x1cfa, 0x1cfa,
            0x1d00, 0x1dbf,
            0x1e00, 0x1f15,
            0x1f18, 0x1f1d,
            0x1f20, 0x1f45,
            0x1f48, 0x1f4d,
            0x1f50, 0x1f57,
            0x1f59, 0x1f59,
            0x1f5b, 0x1f5b,
            0x1f5d, 0x1f5d,
            0x1f5f, 0x1f7d,
            0x1f80, 0x1fb4,
            0x1fb6, 0x1fbc,
            0x1fbe, 0x1fbe,
            0x1fc2, 0x1fc4,
            0x1fc6, 0x1fcc,
            0x1fd0, 0x1fd3,
            0x1fd6, 0x1fdb,
            0x1fe0, 0x1fec,
            0x1ff2, 0x1ff4,
            0x1ff6, 0x1ffc,
            0x2071, 0x2071,
            0x207f, 0x207f,
            0x2090, 0x209c,
            0x2102, 0x2102,
            0x2107, 0x2107,
            0x210a, 0x2113,
            0x2115, 0x2115,
            0x2119, 0x211d,
            0x2124, 0x2124,
            0x2126, 0x2126,
            0x2128, 0x2128,
            0x212a, 0x212d,
            0x212f, 0x2139,
            0x213c, 0x213f,
            0x2145, 0x2149,
            0x214e, 0x214e,
            0x2160, 0x2188,
            0x2c00, 0x2ce4,
            0x2ceb, 0x2cee,
            0x2cf2, 0x2cf3,
            0x2d00, 0x2d25,
            0x2d27, 0x2d27,
            0x2d2d, 0x2d2d,
            0x2d30, 0x2d67,
            0x2d6f, 0x2d6f,
            0x2d80, 0x2d96,
            0x2da0, 0x2da6,
            0x2da8, 0x2dae,
            0x2db0, 0x2db6,
            0x2db8, 0x2dbe,
            0x2dc0, 0x2dc6,
            0x2dc8, 0x2dce,
            0x2dd0, 0x2dd6,
            0x2dd8, 0x2dde,
            0x2e2f, 0x2e2f,
            0x3006, 0x3007,
            0x3021, 0x3029,
            0x3031, 0x3035,
            0x3038, 0x303a,
            0x303c, 0x303c,
            0x3041, 0x3096,
            0x309d, 0x309f,
            0x30a1, 0x30fa,
            0x30fc, 0x30ff,
            0x3105, 0x312f,
            0x3131, 0x318e,
            0x31a0, 0x31bf,
            0x31f0, 0x31ff,
            0x3400, 0x4dbf,
            0x4e00, 0xa48c,
            0xa4d0, 0xa4fd,
            0xa500, 0xa60c,
            0xa610, 0xa62b,
            0xa640, 0xa66e,
            0xa67f, 0xa69d,
            0xa6a0, 0xa6ef,
            0xa717, 0xa71f,
            0xa722, 0xa788,
            0xa78b, 0xa7dc,
            0xa7f1, 0xa801,
            0xa803, 0xa805,
            0xa807, 0xa80a,
            0xa80c, 0xa822,
            0xa840, 0xa873,
            0xa882, 0xa8b3,
            0xa8d0, 0xa8d9,
            0xa8f2, 0xa8f7,
            0xa8fb, 0xa8fb,
            0xa8fd, 0xa8fe,
            0xa900, 0xa925,
            0xa930, 0xa946,
            0xa960, 0xa97c,
            0xa984, 0xa9b2,
            0xa9cf, 0xa9d9,
            0xa9e0, 0xa9e4,
            0xa9e6, 0xa9fe,
            0xaa00, 0xaa28,
            0xaa40, 0xaa42,
            0xaa44, 0xaa4b,
            0xaa50, 0xaa59,
            0xaa60, 0xaa76,
            0xaa7a, 0xaa7a,
            0xaa7e, 0xaaaf,
            0xaab1, 0xaab1,
            0xaab5, 0xaab6,
            0xaab9, 0xaabd,
            0xaac0, 0xaac0,
            0xaac2, 0xaac2,
            0xaadb, 0xaadd,
            0xaae0, 0xaaea,
            0xaaf2, 0xaaf4,
            0xab01, 0xab06,
            0xab09, 0xab0e,
            0xab11, 0xab16,
            0xab20, 0xab26,
            0xab28, 0xab2e,
            0xab30, 0xab5a,
            0xab5c, 0xab69,
            0xab70, 0xabe2,
            0xabf0, 0xabf9,
            0xac00, 0xd7a3,
            0xd7b0, 0xd7c6,
            0xd7cb, 0xd7fb,
            0xf900, 0xfa6d,
            0xfa70, 0xfad9,
            0xfb00, 0xfb06,
            0xfb13, 0xfb17,
            0xfb1d, 0xfb1d,
            0xfb1f, 0xfb28,
            0xfb2a, 0xfb36,
            0xfb38, 0xfb3c,
            0xfb3e, 0xfb3e,
            0xfb40, 0xfb41,
            0xfb43, 0xfb44,
            0xfb46, 0xfbb1,
            0xfbd3, 0xfd3d,
            0xfd50, 0xfd8f,
            0xfd92, 0xfdc7,
            0xfdf0, 0xfdfb,
            0xfe70, 0xfe74,
            0xfe76, 0xfefc,
            0xff10, 0xff19,
            0xff21, 0xff3a,
            0xff41, 0xff5a,
            0xff66, 0xff9d,
            0xffa0, 0xffbe,
            0xffc2, 0xffc7,
            0xffca, 0xffcf,
            0xffd2, 0xffd7,
            0xffda, 0xffdc,
            0x10000, 0x1000b,
            0x1000d, 0x10026,
            0x10028, 0x1003a,
            0x1003c, 0x1003d,
            0x1003f, 0x1004d,
            0x10050, 0x1005d,
            0x10080, 0x100fa,
            0x10140, 0x10174,
            0x10280, 0x1029c,
            0x102a0, 0x102d0,
            0x10300, 0x1031f,
            0x1032d, 0x1034a,
            0x10350, 0x10375,
            0x10380, 0x1039d,
            0x103a0, 0x103c3,
            0x103c8, 0x103cf,
            0x103d1, 0x103d5,
            0x10400, 0x1049d,
            0x104a0, 0x104a9,
            0x104b0, 0x104d3,
            0x104d8, 0x104fb,
            0x10500, 0x10527,
            0x10530, 0x10563,
            0x10570, 0x1057a,
            0x1057c, 0x1058a,
            0x1058c, 0x10592,
            0x10594, 0x10595,
            0x10597, 0x105a1,
            0x105a3, 0x105b1,
            0x105b3, 0x105b9,
            0x105bb, 0x105bc,
            0x105c0, 0x105f3,
            0x10600, 0x10736,
            0x10740, 0x10755,
            0x10760, 0x10767,
            0x10780, 0x10785,
            0x10787, 0x107b0,
            0x107b2, 0x107ba,
            0x10800, 0x10805,
            0x10808, 0x10808,
            0x1080a, 0x10835,
            0x10837, 0x10838,
            0x1083c, 0x1083c,
            0x1083f, 0x10855,
            0x10860, 0x10876,
            0x10880, 0x1089e,
            0x108e0, 0x108f2,
            0x108f4, 0x108f5,
            0x10900, 0x10915,
            0x10920, 0x10939,
            0x10940, 0x10959,
            0x10980, 0x109b7,
            0x109be, 0x109bf,
            0x10a00, 0x10a00,
            0x10a10, 0x10a13,
            0x10a15, 0x10a17,
            0x10a19, 0x10a35,
            0x10a60, 0x10a7c,
            0x10a80, 0x10a9c,
            0x10ac0, 0x10ac7,
            0x10ac9, 0x10ae4,
            0x10b00, 0x10b35,
            0x10b40, 0x10b55,
            0x10b60, 0x10b72,
            0x10b80, 0x10b91,
            0x10c00, 0x10c48,
            0x10c80, 0x10cb2,
            0x10cc0, 0x10cf2,
            0x10d00, 0x10d23,
            0x10d30, 0x10d39,
            0x10d40, 0x10d65,
            0x10d6f, 0x10d85,
            0x10e80, 0x10ea9,
            0x10eb0, 0x10eb1,
            0x10ec2, 0x10ec7,
            0x10f00, 0x10f1c,
            0x10f27, 0x10f27,
            0x10f30, 0x10f45,
            0x10f70, 0x10f81,
            0x10fb0, 0x10fc4,
            0x10fe0, 0x10ff6,
            0x11003, 0x11037,
            0x11066, 0x1106f,
            0x11071, 0x11072,
            0x11075, 0x11075,
            0x11083, 0x110af,
            0x110d0, 0x110e8,
            0x110f0, 0x110f9,
            0x11103, 0x11126,
            0x11136, 0x1113f,
            0x11144, 0x11144,
            0x11147, 0x11147,
            0x11150, 0x11172,
            0x11176, 0x11176,
            0x11183, 0x111b2,
            0x111c1, 0x111c4,
            0x111d0, 0x111da,
            0x111dc, 0x111dc,
            0x11200, 0x11211,
            0x11213, 0x1122b,
            0x1123f, 0x11240,
            0x11280, 0x11286,
            0x11288, 0x11288,
            0x1128a, 0x1128d,
            0x1128f, 0x1129d,
            0x1129f, 0x112a8,
            0x112b0, 0x112de,
            0x112f0, 0x112f9,
            0x11305, 0x1130c,
            0x1130f, 0x11310,
            0x11313, 0x11328,
            0x1132a, 0x11330,
            0x11332, 0x11333,
            0x11335, 0x11339,
            0x1133d, 0x1133d,
            0x11350, 0x11350,
            0x1135d, 0x11361,
            0x11380, 0x11389,
            0x1138b, 0x1138b,
            0x1138e, 0x1138e,
            0x11390, 0x113b5,
            0x113b7, 0x113b7,
            0x113d1, 0x113d1,
            0x113d3, 0x113d3,
            0x11400, 0x11434,
            0x11447, 0x1144a,
            0x11450, 0x11459,
            0x1145f, 0x11461,
            0x11480, 0x114af,
            0x114c4, 0x114c5,
            0x114c7, 0x114c7,
            0x114d0, 0x114d9,
            0x11580, 0x115ae,
            0x115d8, 0x115db,
            0x11600, 0x1162f,
            0x11644, 0x11644,
            0x11650, 0x11659,
            0x11680, 0x116aa,
            0x116b8, 0x116b8,
            0x116c0, 0x116c9,
            0x116d0, 0x116e3,
            0x11700, 0x1171a,
            0x11730, 0x1173b,
            0x11740, 0x11746,
            0x11800, 0x1182b,
            0x118a0, 0x118e9,
            0x118ff, 0x11906,
            0x11909, 0x11909,
            0x1190c, 0x11913,
            0x11915, 0x11916,
            0x11918, 0x1192f,
            0x1193f, 0x1193f,
            0x11941, 0x11941,
            0x11950, 0x11959,
            0x119a0, 0x119a7,
            0x119aa, 0x119d0,
            0x119e1, 0x119e1,
            0x119e3, 0x119e3,
            0x11a00, 0x11a00,
            0x11a0b, 0x11a32,
            0x11a3a, 0x11a3a,
            0x11a50, 0x11a50,
            0x11a5c, 0x11a89,
            0x11a9d, 0x11a9d,
            0x11ab0, 0x11af8,
            0x11bc0, 0x11be0,
            0x11bf0, 0x11bf9,
            0x11c00, 0x11c08,
            0x11c0a, 0x11c2e,
            0x11c40, 0x11c40,
            0x11c50, 0x11c59,
            0x11c72, 0x11c8f,
            0x11d00, 0x11d06,
            0x11d08, 0x11d09,
            0x11d0b, 0x11d30,
            0x11d46, 0x11d46,
            0x11d50, 0x11d59,
            0x11d60, 0x11d65,
            0x11d67, 0x11d68,
            0x11d6a, 0x11d89,
            0x11d98, 0x11d98,
            0x11da0, 0x11da9,
            0x11db0, 0x11ddb,
            0x11de0, 0x11de9,
            0x11ee0, 0x11ef2,
            0x11f02, 0x11f02,
            0x11f04, 0x11f10,
            0x11f12, 0x11f33,
            0x11f50, 0x11f59,
            0x11fb0, 0x11fb0,
            0x12000, 0x12399,
            0x12400, 0x1246e,
            0x12480, 0x12543,
            0x12f90, 0x12ff0,
            0x13000, 0x1342f,
            0x13441, 0x13446,
            0x13460, 0x143fa,
            0x14400, 0x14646,
            0x16100, 0x1611d,
            0x16130, 0x16139,
            0x16800, 0x16a38,
            0x16a40, 0x16a5e,
            0x16a60, 0x16a69,
            0x16a70, 0x16abe,
            0x16ac0, 0x16ac9,
            0x16ad0, 0x16aed,
            0x16b00, 0x16b2f,
            0x16b40, 0x16b43,
            0x16b50, 0x16b59,
            0x16b63, 0x16b77,
            0x16b7d, 0x16b8f,
            0x16d40, 0x16d6c,
            0x16d70, 0x16d79,
            0x16e40, 0x16e7f,
            0x16ea0, 0x16eb8,
            0x16ebb, 0x16ed3,
            0x16f00, 0x16f4a,
            0x16f50, 0x16f50,
            0x16f93, 0x16f9f,
            0x16fe0, 0x16fe1,
            0x16ff2, 0x16ff6,
            0x17000, 0x18cd5,
            0x18cff, 0x18d1e,
            0x18d80, 0x18df2,
            0x1aff0, 0x1aff3,
            0x1aff5, 0x1affb,
            0x1affd, 0x1affe,
            0x1b000, 0x1b122,
            0x1b132, 0x1b132,
            0x1b150, 0x1b152,
            0x1b155, 0x1b155,
            0x1b164, 0x1b167,
            0x1b170, 0x1b2fb,
            0x1bc00, 0x1bc6a,
            0x1bc70, 0x1bc7c,
            0x1bc80, 0x1bc88,
            0x1bc90, 0x1bc99,
            0x1ccf0, 0x1ccf9,
            0x1d400, 0x1d454,
            0x1d456, 0x1d49c,
            0x1d49e, 0x1d49f,
            0x1d4a2, 0x1d4a2,
            0x1d4a5, 0x1d4a6,
            0x1d4a9, 0x1d4ac,
            0x1d4ae, 0x1d4b9,
            0x1d4bb, 0x1d4bb,
            0x1d4bd, 0x1d4c3,
            0x1d4c5, 0x1d505,
            0x1d507, 0x1d50a,
            0x1d50d, 0x1d514,
            0x1d516, 0x1d51c,
            0x1d51e, 0x1d539,
            0x1d53b, 0x1d53e,
            0x1d540, 0x1d544,
            0x1d546, 0x1d546,
            0x1d54a, 0x1d550,
            0x1d552, 0x1d6a5,
            0x1d6a8, 0x1d6c0,
            0x1d6c2, 0x1d6da,
            0x1d6dc, 0x1d6fa,
            0x1d6fc, 0x1d714,
            0x1d716, 0x1d734,
            0x1d736, 0x1d74e,
            0x1d750, 0x1d76e,
            0x1d770, 0x1d788,
            0x1d78a, 0x1d7a8,
            0x1d7aa, 0x1d7c2,
            0x1d7c4, 0x1d7cb,
            0x1d7ce, 0x1d7ff,
            0x1df00, 0x1df1e,
            0x1df25, 0x1df2a,
            0x1e030, 0x1e06d,
            0x1e100, 0x1e12c,
            0x1e137, 0x1e13d,
            0x1e140, 0x1e149,
            0x1e14e, 0x1e14e,
            0x1e290, 0x1e2ad,
            0x1e2c0, 0x1e2eb,
            0x1e2f0, 0x1e2f9,
            0x1e4d0, 0x1e4eb,
            0x1e4f0, 0x1e4f9,
            0x1e5d0, 0x1e5ed,
            0x1e5f0, 0x1e5fa,
            0x1e6c0, 0x1e6de,
            0x1e6e0, 0x1e6e2,
            0x1e6e4, 0x1e6e5,
            0x1e6e7, 0x1e6ed,
            0x1e6f0, 0x1e6f4,
            0x1e6fe, 0x1e6ff,
            0x1e7e0, 0x1e7e6,
            0x1e7e8, 0x1e7eb,
            0x1e7ed, 0x1e7ee,
            0x1e7f0, 0x1e7fe,
            0x1e800, 0x1e8c4,
            0x1e900, 0x1e943,
            0x1e94b, 0x1e94b,
            0x1e950, 0x1e959,
            0x1ee00, 0x1ee03,
            0x1ee05, 0x1ee1f,
            0x1ee21, 0x1ee22,
            0x1ee24, 0x1ee24,
            0x1ee27, 0x1ee27,
            0x1ee29, 0x1ee32,
            0x1ee34, 0x1ee37,
            0x1ee39, 0x1ee39,
            0x1ee3b, 0x1ee3b,
            0x1ee42, 0x1ee42,
            0x1ee47, 0x1ee47,
            0x1ee49, 0x1ee49,
            0x1ee4b, 0x1ee4b,
            0x1ee4d, 0x1ee4f,
            0x1ee51, 0x1ee52,
            0x1ee54, 0x1ee54,
            0x1ee57, 0x1ee57,
            0x1ee59, 0x1ee59,
            0x1ee5b, 0x1ee5b,
            0x1ee5d, 0x1ee5d,
            0x1ee5f, 0x1ee5f,
            0x1ee61, 0x1ee62,
            0x1ee64, 0x1ee64,
            0x1ee67, 0x1ee6a,
            0x1ee6c, 0x1ee72,
            0x1ee74, 0x1ee77,
            0x1ee79, 0x1ee7c,
            0x1ee7e, 0x1ee7e,
            0x1ee80, 0x1ee89,
            0x1ee8b, 0x1ee9b,
            0x1eea1, 0x1eea3,
            0x1eea5, 0x1eea9,
            0x1eeab, 0x1eebb,
            0x1fbf0, 0x1fbf9,
            0x20000, 0x2a6df,
            0x2a700, 0x2b81d,
            0x2b820, 0x2cead,
            0x2ceb0, 0x2ebe0,
            0x2ebf0, 0x2ee5d,
            0x2f800, 0x2fa1d,
            0x30000, 0x3134a,
            0x31350, 0x33479,
        ];

        private static int Find(int[] ranges, int scalar, int stride)
        {
            var lo = 0; var hi = ranges.Length / stride - 1;
            while (lo <= hi) { var mid = (lo + hi) / 2; var at = mid * stride;
                if (scalar < ranges[at]) hi = mid - 1; else if (scalar > ranges[at + 1]) lo = mid + 1; else return at; }
            return -1;
        }
        internal static Kind Property(int scalar) { var at = Find(Properties, scalar, 3); return at < 0 ? Kind.Other : (Kind)Properties[at + 2]; }
        internal static bool IsPictograph(int scalar) => Find(Pictographs, scalar, 2) >= 0;
        internal static bool IsWordlike(int scalar) => Find(Wordlike, scalar, 2) >= 0;
    }

    private void ExitHistoryBrowsing() { historyIndex = -1; historyDraft = null; }
    private void ClearPreferences() { preferredUtf16Offset = null; snappedUtf16Offset = null; }
    private void ExitHistoryWithRevision(bool resetAction = false)
    {
        if (historyIndex < 0 && historyDraft is null &&
            (!resetAction || lastAction == EditorAction.None)) return;
        EnsureRevisionAvailable(); ExitHistoryBrowsing();
        if (resetAction) lastAction = EditorAction.None;
        AdvanceRevision();
    }

    private void ValidateMap(TerminalEditorVisualMap map, TerminalEditorGeometryIdentity expectedGeometryIdentity)
    {
        if (map is null || !map.HasValidConstruction)
            throw new TerminalEditorLayoutException(TerminalEditorLayoutFailure.InvalidMap);
        if (map.EditorIdentity != LayoutIdentity || map.SourceText != Snapshot.Text || map.SourceCursorUtf16Offset != Snapshot.CursorUtf16Offset ||
            !map.AtomicRanges.SequenceEqual(CaptureLayoutInput().AtomicRanges))
            throw new TerminalEditorLayoutException(TerminalEditorLayoutFailure.StaleEditor);
        if (map.GeometryIdentity != expectedGeometryIdentity || expectedGeometryIdentity.ViewLifetimeId == Guid.Empty ||
            expectedGeometryIdentity.GeometryRevision < 0 || expectedGeometryIdentity.PolicyId is not
                (TerminalEditorVisualMapBuilder.PolicyId or TerminalEditorVisualMapBuilder.SourcePolicyId))
            throw new TerminalEditorLayoutException(TerminalEditorLayoutFailure.StaleGeometry);
    }

    private int FindVisualRow(ImmutableArray<TerminalEditorVisualRow> rows, int cursor)
    {
        var logicalStart = cursor;
        while (logicalStart > 0 && Snapshot.Text[logicalStart - 1] != '\n') logicalStart--;
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            if (row.LogicalLineStartUtf16 != logicalStart || cursor < row.SourceStartUtf16) continue;
            var last = index == rows.Length - 1 || rows[index + 1].LogicalLineStartUtf16 != logicalStart;
            if (cursor < row.SourceStartUtf16 + row.LengthUtf16 || last && cursor == row.SourceStartUtf16 + row.LengthUtf16)
                return index;
        }
        throw new TerminalEditorLayoutException(TerminalEditorLayoutFailure.InvalidMap);
    }

    private void NavigateHistory(int direction)
    {
        var index = historyIndex - direction;
        if (history.IsEmpty || index < -1 || index >= history.Length)
        {
            if (lastAction != EditorAction.None) { EnsureRevisionAvailable(); lastAction = EditorAction.None; AdvanceRevision(); }
            return;
        }
        var first = historyIndex == -1 && index >= 0;
        var saved = first ? Snapshot : historyDraft;
        var target = index == -1 ? saved ?? new TerminalTextEditorSnapshot("", 0) :
            new TerminalTextEditorSnapshot(history[index], direction == -1 ? 0 : history[index].Length);
        // Recall deliberately uses the raw history text and the CURRENT paste registry.
        Commit(target.Text, target.CursorUtf16Offset, pastes, pasteCounter, first, false, resetScroll: true);
        historyIndex = index; historyDraft = index == -1 ? null : saved; ClearPreferences();
        Notify();
    }

    private void MoveToVisualRow(TerminalEditorVisualMap map, int current, int target)
    {
        EnsureRevisionAvailable();
        var rows = map.Rows; var currentRow = rows[current];
        var resolved = snappedUtf16Offset is { } snap ? rows[FindVisualRow(rows, snap)] : currentRow;
        var currentColumn = (snappedUtf16Offset ?? Snapshot.CursorUtf16Offset) - resolved.SourceStartUtf16;
        var sourceMax = current == rows.Length - 1 || rows[current + 1].LogicalLineStartUtf16 != currentRow.LogicalLineStartUtf16
            ? currentRow.LengthUtf16 : Math.Max(0, currentRow.LengthUtf16 - 1);
        var preferred = preferredUtf16Offset;
        int cursor; int? snapped = null;
        while (true)
        {
            var row = rows[target];
            var targetMax = target == rows.Length - 1 || rows[target + 1].LogicalLineStartUtf16 != row.LogicalLineStartUtf16
                ? row.LengthUtf16 : Math.Max(0, row.LengthUtf16 - 1);
            int column;
            if (preferred is null || currentColumn < sourceMax)
            {
                if (targetMax < currentColumn) { preferred = currentColumn; column = targetMax; }
                else { preferred = null; column = currentColumn; }
            }
            else if (targetMax < currentColumn || targetMax < preferred.Value) column = targetMax;
            else { column = preferred.Value; preferred = null; }
            cursor = row.SourceStartUtf16 + column;
            var end = map.DisplayText.IndexOf('\n', row.LogicalLineStartUtf16);
            if (end < 0) end = map.DisplayText.Length;
            var retry = false;
            foreach (var segment in Segment(map.DisplayText[row.LogicalLineStartUtf16..end]))
            {
                var start = row.LogicalLineStartUtf16 + segment.Start;
                if (start > cursor) break;
                if (segment.Length <= 1 || cursor >= start + segment.Length) continue;
                if (start < row.SourceStartUtf16 && target > current)
                {
                    var next = target + 1;
                    while (next < rows.Length && rows[next].LogicalLineStartUtf16 == row.LogicalLineStartUtf16 &&
                        rows[next].SourceStartUtf16 < start + segment.Length) next++;
                    if (next < rows.Length) { target = next; retry = true; break; }
                }
                snapped = cursor; cursor = start; break;
            }
            if (!retry) break;
        }
        // Cursor movement has no text observer and never pushes undo or exits history browsing.
        editor.SetCursor(cursor);
        preferredUtf16Offset = preferred; snappedUtf16Offset = snapped; lastAction = EditorAction.None;
        AdvanceRevision();
    }

    private void Notify()
    {
        AdmitJumpCancellation();
        if (TextChanged is null) return;
        deliveringObserver = true;
        try { TextChanged.Invoke(Snapshot.Text); }
        finally { deliveringObserver = false; }
    }
    // Managed ICU78.3 dictionary engines. Full source/data/licensing provenance:
    // tests/PiSharp.Tui.Editor.Tests/word-data/icu78. No OS ICU or Node runtime calls.
    private static class WordDictionary
    {
        private static readonly Lazy<CompactDictionary> Cjk = new(() => new(Load(PinnedWordDictionaryData.CjkResource, PinnedWordDictionaryData.CjkBytes, PinnedWordDictionaryData.CjkHash), true));
        private static readonly Lazy<CompactDictionary> Thai = new(() => new(Load(PinnedWordDictionaryData.ThaiResource, PinnedWordDictionaryData.ThaiBytes, PinnedWordDictionaryData.ThaiHash), false));
        private static readonly Lazy<CompactDictionary> Lao = new(() => new(Load(PinnedWordSeaData.LaoResource, PinnedWordSeaData.LaoBytes, PinnedWordSeaData.LaoHash), false));
        private static readonly Lazy<CompactDictionary> Khmer = new(() => new(Load(PinnedWordSeaData.KhmerResource, PinnedWordSeaData.KhmerBytes, PinnedWordSeaData.KhmerHash), false));
        private static readonly Lazy<CompactDictionary> Burmese = new(() => new(Load(PinnedWordSeaData.BurmeseResource, PinnedWordSeaData.BurmeseBytes, PinnedWordSeaData.BurmeseHash), false));
        private static readonly Lazy<NormalizationData> Normalization = new(() => new(Load(PinnedWordDictionaryData.NormalizationResource, PinnedWordDictionaryData.NormalizationBytes, PinnedWordDictionaryData.NormalizationHash)));
        private static byte[] Load(string resourceName, int size, string sha256)
        {
            if (size is < 1 or > 2_007_296) throw new TerminalTextEditorException(TerminalTextEditorFailure.ResourceLimit);
            using var compressed = typeof(TerminalTextEditorPasteController).Assembly.GetManifestResourceStream(resourceName)
                ?? throw new TerminalTextEditorException(TerminalTextEditorFailure.InvalidOptions);
            if (compressed.CanSeek && compressed.Length > 2_106_316) throw new TerminalTextEditorException(TerminalTextEditorFailure.ResourceLimit);
            using var gzip = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionMode.Decompress);
            var data = new byte[size]; gzip.ReadExactly(data);
            if (gzip.ReadByte() != -1 || Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data)) != sha256)
                throw new TerminalTextEditorException(TerminalTextEditorFailure.InvalidOptions);
            return data;
        }
        internal static int Flags(int scalar)
        {
            var flags = Normalization.Value.Flags(scalar); var ranges = PinnedWordSeaData.ScriptRanges; var lo = 0; var hi = ranges.Length / 3 - 1;
            while (lo <= hi) { var at = (lo + hi) / 2 * 3; if (scalar < ranges[at]) hi = at / 3 - 1; else if (scalar > ranges[at + 1]) lo = at / 3 + 1; else return flags | ranges[at + 2]; }
            return flags;
        }
        internal static int? ExactSeaValue(string value, int language)
        {
            var cursor = new TrieCursor(SeaDictionary(language)); var result = 0;
            foreach (var rune in value.EnumerateRunes()) { result = cursor.Next(rune.Value); if (result == 0) return null; }
            return result >= 2 ? cursor.Value() : null;
        }
        private static CompactDictionary SeaDictionary(int language) => language switch
        { 16 => Thai.Value, 128 => Lao.Value, 256 => Khmer.Value, 512 => Burmese.Value, _ => throw new TerminalTextEditorException(TerminalTextEditorFailure.InvalidOptions) };
        internal static string Normalize(string value) => Normalization.Value.Normalize(value);
        internal static int? ExactValue(string value, bool thai)
        {
            var cursor = new TrieCursor(thai ? Thai.Value : Cjk.Value); var result = 0;
            foreach (var unit in value) { result = cursor.Next(unit); if (result == 0) return null; }
            return result >= 2 ? cursor.Value() : null;
        }
        private readonly record struct Candidate(int Length, int CodePoints, int Value);
        private sealed class CompactDictionary
        {
            private readonly byte[] data; internal readonly bool UChars; internal readonly int Transform;
            private readonly int start, units;
            internal CompactDictionary(byte[] value, bool uchars)
            {
                data = value; UChars = uchars;
                var header = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data);
                if (data[2] != 0xda || data[3] != 0x27 || data[8] != 0 || data[10] != 2 ||
                    !data.AsSpan(12, 4).SequenceEqual("Dict"u8) || data[16] != 1 || header > data.Length - 32)
                    throw new TerminalTextEditorException(TerminalTextEditorFailure.InvalidOptions);
                int Index(int at) => System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(header + at * 4, 4));
                if ((Index(4) & 7) != (uchars ? 1 : 0) || Index(0) < 32 || Index(3) > data.Length - header || Index(3) <= Index(0))
                    throw new TerminalTextEditorException(TerminalTextEditorFailure.InvalidOptions);
                start = header + Index(0); units = (Index(3) - Index(0)) / (uchars ? 2 : 1); Transform = Index(5);
            }
            internal int Unit(int position)
            {
                if ((uint)position >= (uint)units) throw new TerminalTextEditorException(TerminalTextEditorFailure.InvalidOptions);
                return UChars ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(start + position * 2, 2)) : data[start + position];
            }
            internal int MatchesInto(string text, int at, int end, int maximum, Span<Candidate> matches, out int prefix)
            {
                var cursor = new TrieCursor(this); var count = 0; var cp = 0; var startAt = at;
                while (at < end)
                {
                    var rune = Rune.GetRuneAt(text, at); at += rune.Utf16SequenceLength; cp++;
                    var result = cursor.Next(rune.Value);
                    if (result >= 2) { if (count < matches.Length) matches[count++] = new(at - startAt, cp, cursor.Value()); if (result == 2) break; }
                    else if (result == 0) break;
                    if (at - startAt >= maximum) break;
                }
                prefix = cp; return count;
            }
        }
        private struct TrieCursor
        {
            private readonly CompactDictionary dictionary; private int position, remaining;
            internal TrieCursor(CompactDictionary value) { dictionary = value; position = 0; remaining = -1; }
            private int MinimumValue => dictionary.UChars ? 0x40 : 0x20;
            private bool Final(int node) => dictionary.UChars ? (node & 0x8000) != 0 : (node & 1) != 0;
            private int Result() { var node = dictionary.Unit(position); return remaining < 0 && node >= MinimumValue ? Final(node) ? 2 : 3 : 1; }
            internal int Value()
            {
                var at = position; var node = dictionary.Unit(at++);
                if (!dictionary.UChars) return ReadValue(ref at, node >> 1);
                if (Final(node)) return ReadValue(ref at, node & 0x7fff);
                return node < 0x4040 ? (node >> 6) - 1 : node < 0x7fc0 ? (((node & 0x7fc0) - 0x4040) << 10) | dictionary.Unit(at) : (dictionary.Unit(at) << 16) | dictionary.Unit(at + 1);
            }
            private int ReadValue(ref int at, int lead)
            {
                if (dictionary.UChars) return lead < 0x4000 ? lead : lead < 0x7fff ? ((lead - 0x4000) << 16) | dictionary.Unit(at++) : (dictionary.Unit(at++) << 16) | dictionary.Unit(at++);
                if (lead < 0x51) return lead - 0x10;
                if (lead < 0x6c) return ((lead - 0x51) << 8) | dictionary.Unit(at++);
                if (lead < 0x7e) return ((lead - 0x6c) << 16) | (dictionary.Unit(at++) << 8) | dictionary.Unit(at++);
                if (lead == 0x7e) return (dictionary.Unit(at++) << 16) | (dictionary.Unit(at++) << 8) | dictionary.Unit(at++);
                return (dictionary.Unit(at++) << 24) | (dictionary.Unit(at++) << 16) | (dictionary.Unit(at++) << 8) | dictionary.Unit(at++);
            }
            private int Delta(ref int at)
            {
                var lead = dictionary.Unit(at++);
                if (dictionary.UChars) return lead < 0xfc00 ? lead : lead < 0xffff ? ((lead - 0xfc00) << 16) | dictionary.Unit(at++) : (dictionary.Unit(at++) << 16) | dictionary.Unit(at++);
                if (lead < 0xc0) return lead;
                if (lead < 0xf0) return ((lead - 0xc0) << 8) | dictionary.Unit(at++);
                if (lead < 0xfe) return ((lead - 0xf0) << 16) | (dictionary.Unit(at++) << 8) | dictionary.Unit(at++);
                if (lead == 0xfe) return (dictionary.Unit(at++) << 16) | (dictionary.Unit(at++) << 8) | dictionary.Unit(at++);
                return (dictionary.Unit(at++) << 24) | (dictionary.Unit(at++) << 16) | (dictionary.Unit(at++) << 8) | dictionary.Unit(at++);
            }
            internal int Next(int scalar)
            {
                if (position < 0) return 0;
                if (!dictionary.UChars && (dictionary.Transform & 0x7f000000) == 0x1000000)
                { if (scalar == 0x200d) scalar = 255; else if (scalar == 0x200c) scalar = 254; else { scalar -= dictionary.Transform & 0x1fffff; if (scalar is < 0 or > 253) scalar = 255; } }
                var at = position;
                if (remaining >= 0)
                {
                    if (dictionary.Unit(at++) != scalar) { position = -1; return 0; }
                    remaining--; position = at; return Result();
                }
                var node = dictionary.Unit(at++); var minimumLinear = dictionary.UChars ? 0x30 : 0x10;
                while (true)
                {
                    if (node < minimumLinear) return Branch(at, node, scalar);
                    if (node < MinimumValue)
                    {
                        if (dictionary.Unit(at++) != scalar) break;
                        remaining = node - minimumLinear - 1; position = at; return Result();
                    }
                    if (Final(node)) break;
                    if (dictionary.UChars) { if (node >= 0x4040) at += node < 0x7fc0 ? 1 : 2; node &= 0x3f; }
                    else { _ = ReadValue(ref at, node >> 1); node = dictionary.Unit(at++); }
                }
                position = -1; return 0;
            }
            private int Branch(int at, int length, int scalar)
            {
                if (length == 0) length = dictionary.Unit(at++); length++;
                while (length > 5)
                {
                    var pivot = dictionary.Unit(at++); var jump = Delta(ref at);
                    if (scalar < pivot) { length >>= 1; at += jump; } else length -= length >> 1;
                }
                while (length > 1)
                {
                    var key = dictionary.Unit(at++); var valueAt = at; var node = dictionary.Unit(at++);
                    var value = ReadValue(ref at, dictionary.UChars ? node & 0x7fff : node >> 1);
                    if (scalar == key) { position = Final(node) ? valueAt : at + value; remaining = -1; return Result(); }
                    length--;
                }
                if (dictionary.Unit(at++) == scalar) { position = at; remaining = -1; return Result(); }
                position = -1; return 0;
            }
        }
        // Scratch belongs to one operation and is never retained by the editor or a global cache.
        private sealed class NormalizationWork
        {
            internal readonly List<int> Decomposed = [], Output = [];
            internal int[] Scratch = []; internal int[]? Counts;
        }
        private sealed class NormalizationData
        {
            private readonly Dictionary<int, string> mappings = [];
            private readonly Dictionary<int, int> classes = [];
            private readonly Dictionary<long, int> compositions = [];
            private readonly HashSet<int> backwardStarters = [];
            private readonly int[][] flags;
            internal NormalizationData(byte[] data)
            {
                using var json = System.Text.Json.JsonDocument.Parse(data); var root = json.RootElement;
                foreach (var entry in root.GetProperty("mapping").EnumerateArray()) mappings.Add(entry[0].GetInt32(), entry[1].GetString()!);
                foreach (var entry in root.GetProperty("ccc").EnumerateArray()) classes.Add(entry[0].GetInt32(), entry[1].GetInt32());
                foreach (var entry in root.GetProperty("compose").EnumerateArray()) { var second = entry[1].GetInt32(); compositions.Add(((long)entry[0].GetInt32() << 21) | (uint)second, entry[2].GetInt32()); if (Class(second) == 0) backwardStarters.Add(second); }
                flags = root.GetProperty("flags").EnumerateArray().Select(row => row.EnumerateArray().Select(value => value.GetInt32()).ToArray()).ToArray();
                if (mappings.Count != 5914 || classes.Count > 2000 || compositions.Count > 2000 || flags.Length > 5000) throw new TerminalTextEditorException(TerminalTextEditorFailure.InvalidOptions);
            }
            internal int Flags(int scalar)
            {
                var lo = 0; var hi = flags.Length - 1;
                while (lo <= hi) { var at = (lo + hi) / 2; if (scalar < flags[at][0]) hi = at - 1; else if (scalar > flags[at][1]) lo = at + 1; else return flags[at][2]; }
                return 0;
            }
            private int Class(int scalar) => classes.GetValueOrDefault(scalar);
            private void DecomposeInto(int scalar, List<int> output)
            {
                if (scalar is >= 0xac00 and <= 0xd7a3)
                {
                    var index = scalar - 0xac00; Add(0x1100 + index / 588); Add(0x1161 + index % 588 / 28);
                    if (index % 28 != 0) Add(0x11a7 + index % 28);
                }
                else if (mappings.TryGetValue(scalar, out var value)) { foreach (var rune in value.EnumerateRunes()) Add(rune.Value); }
                else Add(scalar);
                void Add(int value)
                {
                    if (output.Count == 65_536 * 18) throw new TerminalTextEditorException(TerminalTextEditorFailure.ResourceLimit);
                    output.Add(value);
                }
            }
            internal bool BoundaryBefore(int scalar)
            {
                var first = scalar is >= 0xac00 and <= 0xd7a3 ? 0x1100 + (scalar - 0xac00) / 588 :
                    mappings.TryGetValue(scalar, out var value) ? Rune.GetRuneAt(value, 0).Value : scalar;
                return Class(first) == 0 && !backwardStarters.Contains(first) && first is not (>= 0x1161 and <= 0x1175 or >= 0x11a8 and <= 0x11c2);
            }
            private int Compose(int first, int second)
            {
                if (first is >= 0x1100 and <= 0x1112 && second is >= 0x1161 and <= 0x1175) return 0xac00 + ((first - 0x1100) * 21 + second - 0x1161) * 28;
                if (first is >= 0xac00 and <= 0xd7a3 && (first - 0xac00) % 28 == 0 && second is >= 0x11a8 and <= 0x11c2) return first + second - 0x11a7;
                return compositions.GetValueOrDefault(((long)first << 21) | (uint)second, -1);
            }
            internal string Normalize(string text)
            {
                var result = new StringBuilder(text.Length);
                AppendNormalized(text.AsSpan(), result, new NormalizationWork());
                return result.ToString();
            }
            internal void AppendNormalized(ReadOnlySpan<char> text, StringBuilder result, NormalizationWork work)
            {
                var decomposed = work.Decomposed; var output = work.Output; decomposed.Clear(); output.Clear();
                foreach (var rune in text.EnumerateRunes()) DecomposeInto(rune.Value, decomposed);
                for (var begin = 0; begin < decomposed.Count;)
                {
                    if (Class(decomposed[begin]) == 0) { begin++; continue; }
                    var end = begin + 1; while (end < decomposed.Count && Class(decomposed[end]) != 0) end++;
                    if (end - begin > 1)
                    {
                        if (work.Scratch.Length < decomposed.Count) work.Scratch = new int[decomposed.Count];
                        var counts = work.Counts ??= new int[256]; Array.Clear(counts);
                        for (var i = begin; i < end; i++) counts[Class(decomposed[i])]++;
                        var total = begin; for (var c = 1; c < counts.Length; c++) { var size = counts[c]; counts[c] = total; total += size; }
                        for (var i = begin; i < end; i++) work.Scratch[counts[Class(decomposed[i])]++] = decomposed[i];
                        for (var i = begin; i < end; i++) decomposed[i] = work.Scratch[i];
                    }
                    begin = end;
                }
                var starter = -1; var previousClass = 0;
                foreach (var scalar in decomposed)
                {
                    var currentClass = Class(scalar); var composed = starter >= 0 && (previousClass == 0 || previousClass < currentClass) ? Compose(output[starter], scalar) : -1;
                    if (composed >= 0) output[starter] = composed;
                    else { if (currentClass == 0) starter = output.Count; output.Add(scalar); previousClass = currentClass; }
                }
                Span<char> unit = stackalloc char[2];
                foreach (var scalar in output) { var length = new Rune(scalar).EncodeToUtf16(unit); result.Append(unit[..length]); }
            }
        }
        internal static List<int> Apply(string text, List<int> boundaries)
        {
            var result = new SortedSet<int>(boundaries);
            for (var at = 0; at < text.Length;)
            {
                var scalar = Rune.GetRuneAt(text, at); var flags = Flags(scalar.Value); var cjk = (flags & 3) == 3; var language = flags & (16 | 128 | 256 | 512);
                if (!cjk && language == 0 || language != 0 && WordData.Property(scalar.Value) is WordData.Kind.Extend or WordData.Kind.Format or WordData.Kind.ZWJ)
                { at += scalar.Utf16SequenceLength; continue; }
                var start = at; at += scalar.Utf16SequenceLength;
                while (at < text.Length && (Flags(Rune.GetRuneAt(text, at).Value) & (cjk ? 1 : language)) != 0) at += Rune.GetRuneAt(text, at).Utf16SequenceLength;
                var end = at;
                // A single scalar cannot contribute an interior boundary, even when NFKC expands it.
                if (end - start == scalar.Utf16SequenceLength) continue;
                result.GetViewBetween(start + 1, end - 1).Clear();
                foreach (var point in cjk ? CjkBreaks(text, start, end) : SeaBreaks(text, start, end, language)) if (point > start && point < end) result.Add(point);
            }
            return result.ToList();
        }
        private static List<int> CjkBreaks(string text, int start, int end)
        {
            var norm = Normalization.Value; var work = new NormalizationWork(); var normalized = new StringBuilder(end - start);
            norm.AppendNormalized(text.AsSpan(start, end - start), normalized, work); var value = normalized.ToString();
            var runes = value.EnumerateRunes().ToArray(); var count = runes.Length; var offsets = new int[count + 1]; offsets[count] = end;
            if (text.AsSpan(start, end - start).SequenceEqual(value))
            {
                var at = start; for (var i = 0; i < count; i++) { offsets[i] = at; at += runes[i].Utf16SequenceLength; }
            }
            else
            {
                var chunk = new StringBuilder(); var normalizedAt = 0; var cpIndex = 0;
                for (var at = start; at < end;)
                {
                    var chunkStart = at; at += Rune.GetRuneAt(text, at).Utf16SequenceLength;
                    while (at < end && !norm.BoundaryBefore(Rune.GetRuneAt(text, at).Value)) at += Rune.GetRuneAt(text, at).Utf16SequenceLength;
                    chunk.Clear(); norm.AppendNormalized(text.AsSpan(chunkStart, at - chunkStart), chunk, work);
                    for (var i = 0; i < chunk.Length; i++)
                    {
                        if (normalizedAt >= value.Length || value[normalizedAt++] != chunk[i]) throw new TerminalTextEditorException(TerminalTextEditorFailure.InvalidOptions);
                        if (!char.IsLowSurrogate(chunk[i])) offsets[cpIndex++] = chunkStart;
                    }
                }
                if (normalizedAt != value.Length || cpIndex != count) throw new TerminalTextEditorException(TerminalTextEditorFailure.InvalidOptions);
            }
            var cost = new uint[count + 1]; Array.Fill(cost, uint.MaxValue); cost[0] = 0;
            var previous = new int[count + 1]; Array.Fill(previous, -1); var unit = 0; var previousKatakana = false;
            Span<Candidate> matches = stackalloc Candidate[21];
            ReadOnlySpan<uint> katakanaCosts = [8192, 984, 408, 240, 204, 252, 300, 372, 480];
            for (var i = 0; i < count; i++)
            {
                if (cost[i] == uint.MaxValue) { unit += runes[i].Utf16SequenceLength; continue; }
                var matchCount = Cjk.Value.MatchesInto(value, unit, value.Length, 20, matches[..20], out _);
                if ((matchCount == 0 || matches[0].CodePoints != 1) && (Flags(runes[i].Value) & 4) == 0) matches[matchCount++] = new(runes[i].Utf16SequenceLength, 1, 255);
                for (var m = 0; m < matchCount; m++)
                {
                    var match = matches[m]; var target = i + match.CodePoints; var nextCost = cost[i] + (uint)match.Value;
                    if (nextCost < cost[target]) { cost[target] = nextCost; previous[target] = i; }
                }
                var katakana = IsKatakana(runes[i].Value);
                if (katakana && !previousKatakana)
                {
                    var j = i + 1; while (j < count && j - i < 20 && IsKatakana(runes[j].Value)) j++;
                    var length = j - i;
                    if (length < 20)
                    {
                        var extra = length <= 8 ? katakanaCosts[length] : 8192; var nextCost = cost[i] + extra;
                        if (nextCost < cost[j]) { cost[j] = nextCost; previous[j] = i; }
                    }
                }
                previousKatakana = katakana; unit += runes[i].Utf16SequenceLength;
            }
            var result = new List<int>();
            for (var at = count; at > 0; at = previous[at]) { var position = offsets[at]; if (position > start && position < end) result.Add(position); if (previous[at] < 0) break; }
            result.Sort(); return result.Distinct().ToList();
        }
        private static bool IsKatakana(int scalar) => scalar is >= 0x30a1 and <= 0x30fe && scalar != 0x30fb || scalar is >= 0xff66 and <= 0xff9f;
        private sealed class PossibleWord
        {
            private readonly CompactDictionary dictionary; private readonly Candidate[] values = new Candidate[20];
            private int offset = -1, current, mark, count; internal int Prefix;
            internal PossibleWord(CompactDictionary value) => dictionary = value;
            internal int Candidates(string text, ref int at, int end)
            {
                if (offset != at) { offset = at; count = dictionary.MatchesInto(text, at, end, end - at, values, out Prefix); }
                current = mark = count - 1; if (current >= 0) at = offset + values[current].Length; return count;
            }
            internal void Mark() => mark = current;
            internal bool Back(ref int at) { if (current <= 0) return false; at = offset + values[--current].Length; return true; }
            internal Candidate Accept(ref int at) { var value = values[mark]; at = offset + value.Length; return value; }
        }
        private static List<int> SeaBreaks(string text, int start, int end, int language)
        {
            var result = new List<int>(); if (language == 16 ? end - start <= 4 : end - start < 4) return result;
            var dictionary = SeaDictionary(language); var words = new PossibleWord[] { new(dictionary), new(dictionary), new(dictionary) }; var found = 0; var at = start;
            bool EndWord(int scalar) => (Flags(scalar) & language) != 0 && (language switch
            { 16 => scalar != 0x0e31 && scalar is not (>= 0x0e40 and <= 0x0e44), 128 => scalar is not (>= 0x0ec0 and <= 0x0ec4), 256 => scalar != 0x17d2, 512 => true, _ => false });
            bool BeginWord(int scalar) => language switch
            { 16 => scalar is >= 0x0e01 and <= 0x0e2e or >= 0x0e40 and <= 0x0e44, 128 => scalar is >= 0x0e81 and <= 0x0eae or >= 0x0edc and <= 0x0edd or >= 0x0ec0 and <= 0x0ec4, 256 => scalar is >= 0x1780 and <= 0x17b3, 512 => scalar is >= 0x1000 and <= 0x102a, _ => false };
            static bool Suffix(int scalar) => scalar is 0x0e2f or 0x0e46;
            while (at < end)
            {
                var current = at; var length = 0; var cpLength = 0; var first = words[found % 3]; var candidates = first.Candidates(text, ref at, end);
                if (candidates == 1) { var word = first.Accept(ref at); length = word.Length; cpLength = word.CodePoints; found++; }
                else if (candidates > 1)
                {
                    if (at < end)
                    {
                        var stop = false;
                        do
                        {
                            var second = words[(found + 1) % 3];
                            if (second.Candidates(text, ref at, end) > 0)
                            {
                                first.Mark(); if (at >= end) { stop = true; break; }
                                do { if (words[(found + 2) % 3].Candidates(text, ref at, end) > 0) { first.Mark(); stop = true; break; } } while (second.Back(ref at));
                            }
                            if (stop) break;
                        } while (first.Back(ref at));
                    }
                    var word = first.Accept(ref at); length = word.Length; cpLength = word.CodePoints; found++;
                }
                if (at < end && cpLength < 3)
                {
                    var next = words[found % 3];
                    if (next.Candidates(text, ref at, end) <= 0 && (length == 0 || next.Prefix < 3))
                    {
                        var chars = 0;
                        while (true)
                        {
                            var previousScalar = Rune.GetRuneAt(text, at); at += previousScalar.Utf16SequenceLength; chars += previousScalar.Utf16SequenceLength;
                            if (at >= end) break;
                            if (EndWord(previousScalar.Value) && BeginWord(Rune.GetRuneAt(text, at).Value))
                            { var resume = at; var foundNext = words[(found + 1) % 3].Candidates(text, ref at, end); at = resume; if (foundNext > 0) break; }
                        }
                        if (length == 0) found++; length += chars;
                    }
                    else at = current + length;
                }
                while (at < end && ((Flags(Rune.GetRuneAt(text, at).Value) & (language | 32)) == (language | 32) || text[at] == ' ')) { var size = Rune.GetRuneAt(text, at).Utf16SequenceLength; at += size; length += size; }
                if (language == 16 && at < end && length > 0)
                {
                    if (words[found % 3].Candidates(text, ref at, end) <= 0 && Suffix(Rune.GetRuneAt(text, at).Value))
                    {
                        if (text[at] == '\u0e2f' && !Suffix(text[at - 1])) { at++; length++; }
                        if (at < end && text[at] == '\u0e46' && text[at - 1] != '\u0e46') { at++; length++; }
                    }
                    else at = current + length;
                }
                if (length > 0) result.Add(current + length);
                else throw new TerminalTextEditorException(TerminalTextEditorFailure.InvalidOptions);
            }
            if (result.Count != 0 && result[^1] >= end) result.RemoveAt(result.Count - 1); return result;
        }
    }

    // Immutable official ICU78.3 data; provenance, full licensing and generator reside in word-data/icu78.
    // Payloads are stored as gzip-compressed embedded resources under Input/Assets/WordData/*.gz
    // and verified against these pinned byte counts and SHA-256 digests on first load.
    [System.CodeDom.Compiler.GeneratedCode("PiSharp.WordData", "ICU78.3")]
    private static class PinnedWordDictionaryData
    {
        internal const int CjkBytes = 2007296;
        internal const string CjkHash = "3850478c713b2004cbb96ef4b9f049caf16d6faf32a883d2e63c09659d0c59c7";
        internal const string CjkResource = "PiSharp.Tui.Input.Assets.WordData.cjk.gz";

        internal const int ThaiBytes = 126144;
        internal const string ThaiHash = "ea47ae521cea65077bf047a75d1e9d28c495ec2f8cf4d2734fcb65a888229be9";
        internal const string ThaiResource = "PiSharp.Tui.Input.Assets.WordData.thai.gz";

        internal const int NormalizationBytes = 116723;
        internal const string NormalizationHash = "c66eb5009782666af89b49022babeb4896be4b1112715d276095431d0c4e8309";
        internal const string NormalizationResource = "PiSharp.Tui.Input.Assets.WordData.normalization.gz";
    }

    // Exact official ICU78.3 data; full notices and input proof in word-data/icu78-sea.
    [System.CodeDom.Compiler.GeneratedCode("PiSharp.WordData", "ICU78.3")]
    private static class PinnedWordSeaData
    {
        internal const int LaoBytes = 162624;
        internal const string LaoHash = "b044d4232c2fea926b88f6047c4efcd683e7cf0573cf9f60990aa7500c60ef0c";
        internal const string LaoResource = "PiSharp.Tui.Input.Assets.WordData.lao.gz";

        internal const int KhmerBytes = 445552;
        internal const string KhmerHash = "080582d61636c7f9039d65bc37452d100faa07f3ba7f5e21aa32aa1672441413";
        internal const string KhmerResource = "PiSharp.Tui.Input.Assets.WordData.khmer.gz";

        internal const int BurmeseBytes = 254448;
        internal const string BurmeseHash = "99f1b2afe6672bc495a872e85dd5ed2be5ffc4d9b8377deff2dbc18a2b4538e5";
        internal const string BurmeseResource = "PiSharp.Tui.Input.Assets.WordData.burmese.gz";

        internal static ReadOnlySpan<int> ScriptRanges => [
            3713, 3714, 128,
            3716, 3716, 128,
            3718, 3722, 128,
            3724, 3747, 128,
            3749, 3749, 128,
            3751, 3773, 128,
            3776, 3780, 128,
            3782, 3782, 128,
            3784, 3790, 128,
            3804, 3807, 128,
            4096, 4159, 512,
            4176, 4239, 512,
            4250, 4255, 512,
            6016, 6099, 256,
            6103, 6103, 256,
            6108, 6109, 256,
            43488, 43503, 512,
            43514, 43518, 512,
            43616, 43647, 512,
        ];
    }

}
