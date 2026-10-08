using System.Reflection;
using System.Text.Json;
using PiSharp.Tui;
using PiSharp.Tui.Input;

internal static class TerminalCharacterJumpTests
{
    internal static readonly List<object> RejectionObservations = [];
    internal static readonly List<object> DecoderObservations = [];
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("character-jump.logical-lines-ordinal-compound-and-zero-position", Search);
        yield return ("character-jump.all-raw-kitty-actions-every-two-chunk-split", DecoderSplits);
        yield return ("character-jump.target-release-hotkey-release-and-protocol-cancellation", Actions);
        yield return ("character-jump.trigger-action-preservation-and-found-only-preference-reset", Preferences);
        yield return ("character-jump.history-marker-interior-and-undo-retention", Retention);
        yield return ("character-jump.programmatic-replacement-retains-mode-host-reset-releases", Lifecycle);
        yield return ("character-jump.invalid-target-and-normal-fallback-rejections-are-atomic", InvalidInputs);
        yield return ("character-jump.stale-maps-and-revision-exhaustion-are-atomic", Admission);
        yield return ("character-jump.observer-fault-and-reentry-see-admitted-cancellation", Observers);
        yield return ("character-jump.no-op-cancellation-and-yank-two-phase-observers", NoOps);
        yield return ("character-jump.maximum-draft-scalar-seams-and-independent-lifetimes", MaximumDraft);
    }
    private static void Jump(TerminalTextEditorPasteController e, bool backward = false,
        TerminalKeyAction action = TerminalKeyAction.Press) => e.HandleInput(new TerminalKey(
            "]", backward ? TerminalModifiers.Control | TerminalModifiers.Alt : TerminalModifiers.Control, action));
    private static void Target(TerminalTextEditorPasteController e, string value) => e.HandleInput(new TerminalText(value));
    private static string Mode(TerminalTextEditorPasteController e) => Hidden(e, "characterJump")!.ToString()!;
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void At(TerminalTextEditorPasteController e, string text, int cursor) =>
        Check(e.Snapshot == new TerminalTextEditorSnapshot(text, cursor), "Complete draft/caret differs.");
    private static object? Hidden(TerminalTextEditorPasteController e, string name) => typeof(TerminalTextEditorPasteController)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(e);
    private static void SetHidden(TerminalTextEditorPasteController e, string name, object value) => typeof(TerminalTextEditorPasteController)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(e, value);
    private static TerminalEditorVisualMap Map(TerminalTextEditorPasteController e) => new TerminalEditorVisualMapBuilder().Build(
        e.CaptureLayoutInput(), new(new(Guid.NewGuid(), 0, TerminalEditorVisualMapBuilder.PolicyId), 80, 24));
    private static void Vertical(TerminalTextEditorPasteController e, string key)
    { var map = Map(e); e.HandleInput(new TerminalKey(key), map, map.GeometryIdentity); }

    private static void Search()
    {
        var e = new TerminalTextEditorPasteController(); var calls = 0; e.SetText("ababa\n\nzaba"); e.SetCursor(0); e.TextChanged += _ => calls++;
        var undo = e.UndoDepth;
        Jump(e); Target(e, "aba"); At(e, "ababa\n\nzaba", 2);
        Jump(e); Target(e, "aba"); At(e, "ababa\n\nzaba", 8);
        Jump(e, true); Target(e, "aba"); At(e, "ababa\n\nzaba", 2);
        e.SetCursor(1); Jump(e, true); Target(e, "aba"); At(e, "ababa\n\nzaba", 0);
        Jump(e, true); Target(e, "aba"); At(e, "ababa\n\nzaba", 0); // JS lastIndexOf clamps -1 to zero.
        Jump(e); Target(e, "a\nz"); At(e, "ababa\n\nzaba", 0); // Search cannot cross LF.
        Check(calls == 0 && e.UndoDepth == undo && Mode(e) == "None", "Search edited text, notified or pushed undo.");
    }
    private static void DecoderSplits()
    {
        foreach (var trigger in new[] { "\u001d", "\u001b\u001d", "\u001b[93;5u", "\u001b[93;7u",
            "\u001b[93;5:2u", "\u001b[93;7:2u", "\u001b[93;5:3u", "\u001b[93;7:3u" })
        foreach (var target in new[] { "\U0001f642", "\u001b[128578;1u", "\u001b[128578;1:2u", "\u001b[128578;1:3u" })
        {
            var wire = trigger + target; var backward = trigger.Contains(";7", StringComparison.Ordinal) || trigger == "\u001b\u001d";
            for (var split = 0; split <= wire.Length; split++)
            {
                var decoder = new TerminalInputDecoder(timeProvider: new FixedClock());
                var e = new TerminalTextEditorPasteController(); e.SetText("A\U0001f642B\U0001f642C"); e.SetCursor(backward ? 7 : 0);
                var calls = 0; e.TextChanged += _ => calls++;
                var events = decoder.Feed(wire.AsSpan(0, split)).Concat(decoder.Feed(wire.AsSpan(split))).Concat(decoder.Complete()).ToArray();
                foreach (var input in events) e.HandleInput(input);
                At(e, "A\U0001f642B\U0001f642C", backward ? 4 : 1);
                Check(calls == 0 && Mode(e) == "None", "Decoded jump inserted text or retained pending mode.");
                DecoderObservations.Add(new { wire, split, events = events.Select(input => new { type = input.GetType().Name,
                    value = JsonSerializer.SerializeToElement(input, input.GetType()) }).ToArray(), expectedCursor = backward ? 4 : 1, actual = e.Snapshot, callbacks = calls });
            }
        }
    }
    private static void Actions()
    {
        var e = new TerminalTextEditorPasteController(); e.SetText("axbxx"); e.SetCursor(0);
        Jump(e); e.HandleInput(new TerminalKey("x", Action: TerminalKeyAction.Release)); At(e, "axbxx", 1);
        Target(e, "!"); At(e, "a!xbxx", 2);
        Jump(e); Jump(e, true, TerminalKeyAction.Release); Check(Mode(e) == "None", "Opposite released hotkey replaced pending mode.");
        Target(e, "x"); At(e, "a!xxbxx", 3);
        foreach (var input in new TerminalInputEvent[] { new TerminalProtocol("\u001b[I"), new TerminalUnknownSequence("\u001b[?999z"),
            new TerminalKey("x", TerminalModifiers.Alt), new TerminalKey("Left", Action: TerminalKeyAction.Release) })
        { Jump(e); var snapshot = e.Snapshot; e.HandleInput(input); Check(Mode(e) == "None" && e.Snapshot == snapshot, "Nonprintable cancellation changed draft."); }
        var before = State(e); e.HandleInput(new TerminalKey("x", Action: TerminalKeyAction.Release)); Check(State(e) == before, "Ordinary release stopped being ignored.");
    }
    private static void Preferences()
    {
        var e = new TerminalTextEditorPasteController(); e.SetText("abcdef\nx\nabcdef"); e.SetCursor(5); Vertical(e, "Down");
        var preferred = e.PreferredUtf16Offset; Check(preferred == 5, "Fixture has no sticky offset.");
        var action = Hidden(e, "lastAction"); Jump(e); Check(e.PreferredUtf16Offset == preferred && Equals(action, Hidden(e, "lastAction")), "Trigger reset preferences/action.");
        Target(e, "z"); Check(e.PreferredUtf16Offset == preferred && Mode(e) == "None", "Miss reset sticky preference.");
        Jump(e); Target(e, "f"); Check(e.PreferredUtf16Offset is null && e.SnappedUtf16Offset is null, "Found target retained preferences.");
        var typed = new TerminalTextEditorPasteController(); Target(typed, "a"); var undo = typed.UndoDepth;
        Jump(typed); Jump(typed); Target(typed, "b"); Check(typed.UndoDepth == undo, "Trigger/cancel broke source typing group.");
        Jump(typed); Target(typed, "z"); Target(typed, "c"); Check(typed.UndoDepth == undo + 1, "Failed search did not reset typing action.");
    }
    private static void Retention()
    {
        var e = new TerminalTextEditorPasteController(); e.HandleInput(new TerminalPaste(new string('p', 1001))); var marker = e.Snapshot.Text;
        e.AddToHistory(marker + " x"); e.SetCursor(0); Vertical(e, "Up"); e.SetCursor(0); var depth = e.UndoDepth; var saved = e.SavedHistoryDraft;
        Jump(e); Target(e, "#"); Check(e.Snapshot.CursorUtf16Offset == 7 && e.RegisteredPasteCount == 1 && e.HistoryIndex == 0 && e.SavedHistoryDraft == saved && e.UndoDepth == depth,
            "Marker search snapped or changed registry/history/undo.");
        e.HandleInput(new TerminalKey("Backspace")); e.HandleInput(new TerminalKey("-", TerminalModifiers.Control));
        At(e, marker + " x", 7); Check(e.RegisteredPasteCount == 1 && e.GetExpandedText() == new string('p', 1001) + " x", "Undo lost marker interior/expansion.");
    }
    private static void Lifecycle()
    {
        var e = new TerminalTextEditorPasteController(); Jump(e); e.SetText("axa"); e.SetCursor(0); e.InsertText("b"); Check(Mode(e) == "Forward", "Programmatic edit canceled pending jump.");
        Target(e, "x"); At(e, "baxa", 2);
        e.SetText("kill"); e.HandleInput(new TerminalKey("u", TerminalModifiers.Control)); e.AddToHistory("accepted"); Jump(e);
        e.HandleInput(new TerminalPaste(new string('p', 1001))); Jump(e); e.Reset();
        Check(Mode(e) == "None" && !(bool)Hidden(e, "cancelJumpOnAdmission")! && e.Snapshot.Text == "" && e.UndoDepth == 0 && e.RegisteredPasteCount == 0 && e.HistoryCount == 1 && e.KillRingCount == 1,
            "Host reset did not release pending/draft state while retaining same-run history/ring.");
        Target(e, "x"); At(e, "x", 1); Check(Mode(new()) == "None", "New controller inherited pending jump.");
    }
    private static void InvalidInputs()
    {
        var e = new TerminalTextEditorPasteController(8); e.SetText("axa"); e.SetCursor(0); Jump(e);
        Reject(e, "invalid-target-surrogate", () => Target(e, "\ud800"), typeof(TerminalTextEditorException));
        Reject(e, "oversized-target", () => Target(e, new string('x', 9)), typeof(TerminalTextEditorException));
        Reject(e, "null-input", () => e.HandleInput(null!), typeof(ArgumentNullException));
        Reject(e, "null-fallback-text", () => Target(e, null!), typeof(ArgumentNullException));
        Reject(e, "null-paste", () => e.HandleInput(new TerminalPaste(null!)), typeof(ArgumentNullException));
        Reject(e, "invalid-fallback-paste", () => e.HandleInput(new TerminalPaste("\udc00")), typeof(TerminalTextEditorException));
        Reject(e, "oversized-fallback-paste", () => e.HandleInput(new TerminalPaste(new string('x', 9))), typeof(TerminalTextEditorException));
        var full = new TerminalTextEditorPasteController(4); full.SetText("abcd"); Jump(full);
        Reject(full, "fallback-newline-bound", () => full.HandleInput(new TerminalKey("j", TerminalModifiers.Control)), typeof(TerminalTextEditorException));
        var registry = new TerminalTextEditorPasteController(maximumPasteEntries: 1); registry.HandleInput(new TerminalPaste(new string('p', 1001))); Jump(registry);
        Reject(registry, "fallback-registry-bound", () => registry.HandleInput(new TerminalPaste(new string('q', 1001))), typeof(TerminalTextEditorException));
        var expansion = new TerminalTextEditorPasteController(2010); expansion.HandleInput(new TerminalPaste(new string('p', 1001))); Jump(expansion);
        Reject(expansion, "fallback-expanded-alias-bound", () => expansion.HandleInput(new TerminalPaste("[paste #1]012345678")), typeof(TerminalTextEditorException));
        Check(Mode(e) == "Forward" && Mode(full) == "Forward", "Rejection lost pending mode.");
        Target(e, "x"); At(e, "axa", 1);
    }
    private static void Admission()
    {
        var e = new TerminalTextEditorPasteController(); e.SetText("a\nb"); var stale = Map(e); Jump(e); var map = Map(e);
        Reject(e, "stale-map-pending-up", () => e.HandleInput(new TerminalKey("Up"), stale, stale.GeometryIdentity), typeof(TerminalEditorLayoutException));
        Reject(e, "stale-map-pending-target", () => e.HandleInput(new TerminalText("b"), stale, stale.GeometryIdentity), typeof(TerminalEditorLayoutException));
        Reject(e, "foreign-geometry-pending-up", () => e.HandleInput(new TerminalKey("Up"), map, map.GeometryIdentity with { GeometryRevision = 1 }), typeof(TerminalEditorLayoutException));
        SetHidden(e, "editorRevision", long.MaxValue); map = Map(e);
        Reject(e, "exhausted-target", () => Target(e, "b"), typeof(TerminalTextEditorException));
        Reject(e, "exhausted-hotkey-cancel", () => Jump(e), typeof(TerminalTextEditorException));
        Reject(e, "exhausted-protocol-cancel", () => e.HandleInput(new TerminalProtocol("focus")), typeof(TerminalTextEditorException));
        Reject(e, "exhausted-noop-delete", () => e.HandleInput(new TerminalKey("Delete")), typeof(TerminalTextEditorException));
        Reject(e, "exhausted-vertical", () => e.HandleInput(new TerminalKey("Up"), map, map.GeometryIdentity), typeof(TerminalTextEditorException));
        var fresh = new TerminalTextEditorPasteController(); SetHidden(fresh, "editorRevision", long.MaxValue);
        Reject(fresh, "exhausted-trigger", () => Jump(fresh), typeof(TerminalTextEditorException));
    }
    private static void Observers()
    {
        var e = new TerminalTextEditorPasteController(); e.SetText("abcd"); Jump(e); var original = new IOException("character jump observer"); var calls = 0;
        var revision = e.LayoutIdentity.EditorRevision;
        Action<string> observer = text =>
        {
            calls++; Check(text == "abc" && Mode(e) == "None" && !(bool)Hidden(e, "cancelJumpOnAdmission")! && e.LayoutIdentity.EditorRevision == revision + 1,
                "Observer saw uncommitted cancellation/control.");
            Reject(e, "observer-reentrant-trigger", () => Jump(e), typeof(TerminalTextEditorException));
            Reject(e, "observer-reentrant-target", () => Target(e, "x"), typeof(TerminalTextEditorException)); throw original;
        };
        e.TextChanged += observer;
        try { e.HandleInput(new TerminalKey("Backspace")); throw new InvalidOperationException("Expected observer fault."); }
        catch (IOException error) { Check(ReferenceEquals(error, original), "Observer failure identity changed."); }
        e.TextChanged -= observer; Check(calls == 1 && Mode(e) == "None", "Fault retried or retained mode.");
        e.HandleInput(new TerminalKey("-", TerminalModifiers.Control)); At(e, "abcd", 4);
    }
    private static void NoOps()
    {
        var e = new TerminalTextEditorPasteController(); var calls = 0; e.TextChanged += _ =>
        { calls++; Check(Mode(e) == "None" && !(bool)Hidden(e, "cancelJumpOnAdmission")!, "No-op observer saw pending cancellation."); };
        Jump(e); var revision = e.LayoutIdentity.EditorRevision; e.HandleInput(new TerminalKey("Backspace"));
        Check(calls == 1 && e.LayoutIdentity.EditorRevision == revision + 1, "No-op cancellation was missing or doubled.");
        Jump(e); revision = e.LayoutIdentity.EditorRevision; e.HandleInput(new TerminalPaste(""));
        Check(calls == 1 && Mode(e) == "None" && e.LayoutIdentity.EditorRevision == revision + 1, "Empty paste cancellation notified or failed admission.");
        var y = new TerminalTextEditorPasteController(); y.SetText("one"); y.HandleInput(new TerminalKey("u", TerminalModifiers.Control));
        y.HandleInput(new TerminalText("two")); y.HandleInput(new TerminalKey("u", TerminalModifiers.Control));
        y.HandleInput(new TerminalKey("y", TerminalModifiers.Control)); Jump(y); var phases = new List<string>();
        y.TextChanged += text => { Check(Mode(y) == "None" && !(bool)Hidden(y, "cancelJumpOnAdmission")!, "Yank phase retained pending jump."); phases.Add(text); };
        y.HandleInput(new TerminalKey("y", TerminalModifiers.Alt)); Check(phases.SequenceEqual(new[] { "", "one" }), "Jump trigger broke two-phase yank-pop action.");
    }
    private static void MaximumDraft()
    {
        var e = new TerminalTextEditorPasteController(); var text = new string('a', 65530) + "\n\ne\u0301xZ"; e.SetText(text); e.SetCursor(0); var calls = 0; e.TextChanged += _ => calls++;
        Jump(e); Target(e, "\u0301"); At(e, text, 65533); // Raw source search admits a scalar seam inside the grapheme.
        var untouched = new TerminalTextEditorPasteController(); untouched.SetText("z");
        Jump(e, true); Target(e, "a"); At(e, text, 65529); Check(calls == 0 && untouched.Snapshot == new TerminalTextEditorSnapshot("z", 1) && Mode(untouched) == "None", "Maximum search notified or crossed editor lifetime.");
    }
    private static string State(TerminalTextEditorPasteController e) => JsonSerializer.Serialize(new { e.Snapshot, expanded = e.GetExpandedText(),
        e.RegisteredPasteCount, e.RetainedPasteCharacters, e.UndoDepth, e.RetainedUndoCharacters, e.KillRingCount, e.RetainedKillCharacters,
        e.LayoutIdentity, e.History, e.HistoryCount, e.RetainedHistoryCharacters, e.HistoryIndex, e.SavedHistoryDraft, e.SavedHistoryDraftCharacters,
        e.PreferredUtf16Offset, e.SnappedUtf16Offset, input = e.CaptureLayoutInput(), action = Hidden(e, "lastAction"), registry = Hidden(e, "pastes"),
        counter = Hidden(e, "pasteCounter"), undoFrames = Hidden(e, "undo"), ring = Hidden(e, "killRing"), jump = Mode(e),
        cancellationAdmission = Hidden(e, "cancelJumpOnAdmission"), deliveringObserver = Hidden(e, "deliveringObserver") });
    private static void Reject(TerminalTextEditorPasteController e, string id, Action action, Type errorType)
    {
        var before = State(e); var calls = 0; Action<string> observer = _ => calls++; e.TextChanged += observer;
        try
        {
            try { action(); }
            catch (Exception error) when (errorType.IsInstanceOfType(error))
            {
                var after = State(e); RejectionObservations.Add(new { id, error = error.GetType().Name, message = error.Message,
                    expected = JsonSerializer.Deserialize<JsonElement>(before), actual = JsonSerializer.Deserialize<JsonElement>(after), callbacks = calls, matches = before == after && calls == 0 });
                Check(before == after && calls == 0, "Rejected jump changed complete controller state/callbacks: " + id); return;
            }
            throw new InvalidOperationException("Expected " + errorType.Name + ": " + id);
        }
        finally { e.TextChanged -= observer; }
    }
    private sealed class FixedClock : TimeProvider
    { public override long GetTimestamp() => 0; public override long TimestampFrequency => 1000; }
}
