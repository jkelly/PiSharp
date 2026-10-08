using PiSharp.Tui;
using PiSharp.Tui.Input;

/// <summary>Existing production boundaries needed by a future map; no map/history API is simulated.</summary>
internal static class TerminalHistoryLayoutInvariantTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("history-layout.baseline-identical-display-distinct-paste-context", PasteIdentity),
        ("history-layout.baseline-rejection-preserves-complete-public-state", Rejections),
        ("history-layout.baseline-observer-error-keeps-one-committed-undo", ObserverFailure),
        ("history-layout.baseline-projection-valid-scalar-carets-tiny-views", ProjectionCarets),
        ("history-layout.baseline-full-draft-narrow-wide-narrow", MaximumProjection)
    ];

    private static Task PasteIdentity()
    {
        var editor = new TerminalTextEditorPasteController();
        editor.HandleInput(new TerminalPaste(new string('p', 1001)));
        var registered = editor.Snapshot;
        Check(editor.RegisteredPasteCount == 1 && editor.GetExpandedText() == new string('p', 1001), "Registered paste fixture differs.");
        editor.Reset(); editor.HandleInput(new TerminalPaste(new string('q', 1001)));
        Check(editor.Snapshot == registered && editor.RegisteredPasteCount == 1 && editor.GetExpandedText() == new string('q', 1001),
            "Identical display/ID/length did not admit a distinct registered payload.");
        editor.Reset(); editor.SetText(registered.Text);
        Check(editor.Snapshot == registered && editor.RegisteredPasteCount == 0 && editor.GetExpandedText() == registered.Text,
            "Literal marker fixture differs from registered paste.");
        // A future freshness test must use the finalized admission identity; text equality alone is insufficient.
        return Task.CompletedTask;
    }

    private static Task Rejections()
    {
        var editor = new TerminalTextEditorPasteController(2048); var changes = new List<string>();
        editor.TextChanged += changes.Add;
        editor.HandleInput(new TerminalText("seed")); editor.HandleInput(new TerminalKey("u", TerminalModifiers.Control)); editor.Reset();
        editor.HandleInput(new TerminalPaste(new string('p', 1001)));
        Reject(() => editor.InsertText(new string('q', 2048)), TerminalTextEditorFailure.ResourceLimit);
        Reject(() => editor.HandleInput(new TerminalPaste(new string('q', 2049))), TerminalTextEditorFailure.ResourceLimit);
        Reject(() => editor.SetText("\ud800"), TerminalTextEditorFailure.InvalidUnicode);
        Reject(() => editor.SetCursor(99_999), TerminalTextEditorFailure.InvalidCursor);
        editor.SetText("A\U0001f642B");
        Reject(() => editor.SetCursor(2), TerminalTextEditorFailure.InvalidCursor);
        return Task.CompletedTask;

        void Reject(Action effect, TerminalTextEditorFailure expected)
        {
            var before = Observe(editor); var callbacks = changes.ToArray(); var rejected = false;
            try { effect(); } catch (TerminalTextEditorException error) { Check(error.Failure == expected, "Rejection kind differs."); rejected = true; }
            Check(rejected && Observe(editor) == before && changes.SequenceEqual(callbacks), "Rejected edit changed observable draft/paste/undo/ring/callback state.");
        }
    }

    private static Task ObserverFailure()
    {
        var editor = new TerminalTextEditorPasteController(4096);
        editor.HandleInput(new TerminalText("seed")); editor.HandleInput(new TerminalKey("u", TerminalModifiers.Control)); editor.Reset();
        var before = Observe(editor); var failure = new IOException("owned observer failure"); var notifications = 0;
        void Observer(string _) { notifications++; throw failure; }
        editor.TextChanged += Observer;
        try { editor.HandleInput(new TerminalPaste(new string('p', 1001))); throw new InvalidOperationException("Observer failure omitted."); }
        catch (IOException error) { Check(ReferenceEquals(error, failure), "Observer exception identity changed."); }
        Check(notifications == 1 && editor.UndoDepth == 1 && editor.RegisteredPasteCount == 1 && editor.GetExpandedText() == new string('p', 1001) &&
            editor.KillRingCount == 1 && editor.RetainedKillCharacters == 4, "Committed observer failure replayed/rolled back edit or metadata.");
        editor.TextChanged -= Observer;
        editor.HandleInput(new TerminalKey("-", TerminalModifiers.Control));
        Check(Observe(editor) == before && notifications == 1, "A single undo did not restore the exact pre-observer edit state.");
        return Task.CompletedTask;
    }

    private static Task ProjectionCarets()
    {
        string[] samples = ["", "\n", "\n\n", "abcd", "A\u754cB", "e\u0301", "\U0001f469\u200d\U0001f4bb", "[paste #1 1001 chars]", "a\tb", "a\r\nb"];
        foreach (var text in samples)
            for (var cursor = 0; cursor <= text.Length; cursor++)
            {
                if (cursor > 0 && cursor < text.Length && char.IsHighSurrogate(text[cursor - 1]) && char.IsLowSurrogate(text[cursor])) continue;
                var snapshot = new TerminalTextEditorSnapshot(text, cursor);
                foreach (var columns in new[] { 1, 2, 5, 16 })
                    foreach (var rows in new[] { 1, 3 })
                    {
                        var projected = TerminalTextEditorProjection.Create(snapshot, columns, rows);
                        Check(projected.SourceCursorUtf16Offset == cursor && projected.Rows.Length >= 1 && projected.Rows.Length <= rows &&
                            projected.CursorRow >= 0 && projected.CursorRow < projected.Rows.Length && projected.CursorColumn >= 0 && projected.CursorColumn < columns &&
                            projected.FirstVisibleRow >= 0 && projected.FirstVisibleRow + projected.CursorRow < projected.TotalRows &&
                            projected.Rows.All(row => row.Length <= columns && row.All(value => value is >= ' ' and <= '~')),
                            "Current escaped projection lost a legal scalar caret or exceeded its viewport.");
                        Check(snapshot.Text == text && snapshot.CursorUtf16Offset == cursor, "Projection mutated input snapshot.");
                    }
            }
        var longDraft = new TerminalTextEditorSnapshot(string.Join('\n', Enumerable.Repeat("x", 24)), 47);
        var clipped = TerminalTextEditorProjection.Create(longDraft, 16, 1);
        Check(clipped.Rows.Length == 1 && clipped.TotalRows == 24 && clipped.FirstVisibleRow == 23,
            "Visible rows were confused with the complete projected draft extent.");
        return Task.CompletedTask;
    }

    private static Task MaximumProjection()
    {
        var text = new string('x', TerminalTextEditor.MaximumSupportedCharacters); var snapshot = new TerminalTextEditorSnapshot(text, text.Length);
        var first = TerminalTextEditorProjection.Create(snapshot, 1, 3);
        foreach (var columns in new[] { 2, 256, 1 })
        {
            var value = TerminalTextEditorProjection.Create(snapshot, columns, 3);
            Check(value.Rows.Length <= 3 && value.SourceCursorUtf16Offset == text.Length, "Maximum projection exceeded visible retention or changed source offset.");
            if (columns == 1) Check(value.Rows.SequenceEqual(first.Rows) && value.CursorRow == first.CursorRow && value.CursorColumn == first.CursorColumn &&
                value.FirstVisibleRow == first.FirstVisibleRow && value.TotalRows == first.TotalRows, "Narrow-wide-narrow projection retained stale geometry.");
        }
        Check(first.TotalRows > text.Length, "Prompt/trailing-caret drawing rows were incorrectly omitted from complete display extent.");
        return Task.CompletedTask;
    }

    private sealed record PublicEditorState(TerminalTextEditorSnapshot Snapshot, string Expanded, int PasteEntries, int PasteCharacters,
        int UndoEntries, int UndoCharacters, int KillEntries, int KillCharacters);
    private static PublicEditorState Observe(TerminalTextEditorPasteController editor) => new(editor.Snapshot, editor.GetExpandedText(),
        editor.RegisteredPasteCount, editor.RetainedPasteCharacters, editor.UndoDepth, editor.RetainedUndoCharacters, editor.KillRingCount, editor.RetainedKillCharacters);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
