using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.Tui;
using PiSharp.Tui.Input;

internal static class TerminalHistoryNavigationV2Tests
{
    internal static readonly List<object> RejectionObservations = [];
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("history-v2.ecmascript-trim-dedup-and-raw-recall", Trim);
        yield return ("history-v2.exact-100-entry-oldest-eviction", EntryLimit);
        yield return ("history-v2.exact-payload-budget-and-atomic-invalid-entry", PayloadLimit);
        yield return ("history-v2.saved-caret-current-paste-registry-and-single-browse-undo", Recall);
        yield return ("history-v2.edit-undo-and-reset-release-browse-retain-recall-ring", Lifetime);
        yield return ("history-v2.callback-fault-commits-once-and-reentry-rejects", Observers);
        yield return ("layout-v2.identical-marker-reset-replacement-is-stale", RegistryFreshness);
        yield return ("layout-v2.editor-view-policy-and-away-back-identities", Identities);
        yield return ("layout-v2.malformed-default-arrays-and-provider-fault-are-atomic", Malformed);
        yield return ("layout-v2.public-record-hash-collections-equality-and-copy-admission", RecordOperations);
        yield return ("layout-v2.revision-overflow-rejects-before-effects", Overflow);
        yield return ("layout-v2.half-open-wrap-affinity-and-decoration-exclusion", Affinity);
        yield return ("layout-v2.empty-logical-lines-and-tiny-widths", EmptyLines);
        yield return ("layout-v2.every-raw-history-scalar-seam-has-row-membership", RawSeams);
        yield return ("layout-v2.long-short-long-sticky-utf16", Sticky);
        yield return ("layout-v2.scalar-grapheme-zwj-snap-and-resize", Scalars);
        yield return ("layout-v2.registered-marker-downward-continuation-skip", Markers);
        yield return ("layout-v2.actual-height-page-extent-independent-of-three-row-clip", Pages);
        yield return ("layout-v2.pure-mapping-while-private-write-held-and-joined", HeldWrite);
        yield return ("layout-v2.maximum-draft-row-text-bounds-and-no-map-retention", Retention);
    }
    private static readonly TerminalEditorVisualMapBuilder Builder = new();
    private static TerminalEditorGeometrySnapshot Geometry(int columns = 80, int rows = 24, Guid? lifetime = null, long revision = 0) =>
        new(new(lifetime ?? Guid.NewGuid(), revision, TerminalEditorVisualMapBuilder.PolicyId), columns, rows);
    private static TerminalEditorVisualMap Map(TerminalTextEditorPasteController editor, TerminalEditorGeometrySnapshot? geometry = null) =>
        Builder.Build(editor.CaptureLayoutInput(), geometry ?? Geometry());
    private static void Key(TerminalTextEditorPasteController editor, string key, TerminalEditorGeometrySnapshot? geometry = null)
    { var map = Map(editor, geometry); editor.HandleInput(new TerminalKey(key), map, map.GeometryIdentity); }
    private static void Undo(TerminalTextEditorPasteController e) => e.HandleInput(new TerminalKey("-", TerminalModifiers.Control));
    private static void Trim()
    {
        var e = new TerminalTextEditorPasteController(); var original = e.LayoutIdentity;
        Check(!e.AddToHistory("\ufeff\t\r\n ") && e.LayoutIdentity == original, "Empty trimmed entry mutated identity.");
        Check(e.AddToHistory("\ufeffalpha\u3000") && !e.AddToHistory(" alpha "), "Consecutive dedup differs.");
        e.AddToHistory("\u0085"); e.AddToHistory(" x\r\ny\tZ ");
        Check(e.History.SequenceEqual(new[] { "x\r\ny\tZ", "\u0085", "alpha" }), "ECMAScript trim/raw controls differ.");
        Key(e, "Up"); Check(e.Snapshot == new TerminalTextEditorSnapshot("x\r\ny\tZ", 0), "Recall normalized raw history.");
        e.SetCursor(e.Snapshot.Text.Length); Key(e, "Down"); Check(e.Snapshot.Text == "", "Saved empty draft not restored.");
    }
    private static void EntryLimit()
    {
        var e = new TerminalTextEditorPasteController();
        for (var i = 0; i < 110; i++) e.AddToHistory("entry-" + i);
        Check(e.HistoryCount == 100 && e.History[0] == "entry-109" && e.History[^1] == "entry-10", "Entry eviction differs.");
        e.AddToHistory("entry-10"); Check(e.History[0] == "entry-10" && e.HistoryCount == 100, "Nonconsecutive duplicate was removed.");
    }
    private static void PayloadLimit()
    {
        var e = new TerminalTextEditorPasteController();
        for (var i = 0; i < 17; i++) e.AddToHistory(new string((char)('A' + i), 65536));
        Check(e.HistoryCount == 16 && e.RetainedHistoryCharacters == 1048576 && e.History[^1][0] == 'B', "Payload eviction differs.");
        RejectAtomic(e, () => e.AddToHistory(new string('x', 65537)), typeof(TerminalTextEditorException));
        RejectAtomic(e, () => e.AddToHistory("\ud800"), typeof(TerminalTextEditorException));
        var small = new TerminalTextEditorPasteController(4);
        RejectAtomic(small, () => small.AddToHistory(" abc "), typeof(TerminalTextEditorException));
    }
    private static void Recall()
    {
        var e = new TerminalTextEditorPasteController(); e.HandleInput(new TerminalPaste(new string('p', 1001)));
        var marker = e.Snapshot.Text; e.InsertText("tail"); e.SetCursor(0); var saved = e.Snapshot; var depth = e.UndoDepth;
        e.AddToHistory("old"); e.AddToHistory(marker); Key(e, "Up");
        Check(e.GetExpandedText() == new string('p', 1001) && e.HistoryIndex == 0 && e.SavedHistoryDraft == saved && e.UndoDepth == depth + 1, "First browse/current registry differs.");
        Key(e, "Up"); Check(e.Snapshot.Text == "old" && e.UndoDepth == depth + 1, "Browse pushed extra undo.");
        Key(e, "Down"); Check(e.Snapshot == new TerminalTextEditorSnapshot(marker, marker.Length), "Newer recall did not place cursor at end.");
        Key(e, "Down"); Check(e.Snapshot == saved && e.SavedHistoryDraft is null && e.RegisteredPasteCount == 1, "Saved caret/registry not restored.");
        Key(e, "Up"); Undo(e); Check(e.Snapshot == saved && e.HistoryIndex == -1, "Undo did not exit recall with one saved snapshot.");
        e.SetText(""); Key(e, "Up"); var beforeEmptyPaste = State(e); e.HandleInput(new TerminalPaste(""));
        Check(State(e) == beforeEmptyPaste, "Empty bracketed paste exited source browsing or changed identity.");
    }
    private static void Lifetime()
    {
        var e = new TerminalTextEditorPasteController(); e.SetText("kill"); e.HandleInput(new TerminalKey("u", TerminalModifiers.Control));
        e.AddToHistory("accepted"); e.SetText("draft"); e.SetCursor(0); Key(e, "Up"); e.HandleInput(new TerminalText("x"));
        Check(e.HistoryIndex == -1 && e.SavedHistoryDraft is null, "Typing retained old browse draft.");
        e.SetCursor(0); Key(e, "Up"); var prior = e.LayoutIdentity; e.Reset();
        Check(e.History.SequenceEqual(new[] { "accepted" }) && e.KillRingCount == 1 && e.HistoryIndex == -1 && e.SavedHistoryDraftCharacters == 0 &&
            e.UndoDepth == 0 && e.RegisteredPasteCount == 0 && e.PreferredUtf16Offset is null && e.SnappedUtf16Offset is null && e.LayoutIdentity != prior, "Reset lifetime retention differs.");
        Key(e, "Up"); Check(e.Snapshot.Text == "accepted", "Same-run recall lost on reset.");
        Check(new TerminalTextEditorPasteController().HistoryCount == 0, "New run restored global history.");
    }
    private static void Observers()
    {
        var e = new TerminalTextEditorPasteController(); e.AddToHistory("accepted"); var before = e.LayoutIdentity; var calls = 0;
        Action<string> observer = _ => { calls++; Check(e.LayoutIdentity != before && e.HistoryIndex == 0 && e.UndoDepth == 1, "Observer saw incomplete recall commit.");
            RejectAtomic(e, () => e.SetText("reentrant"), typeof(TerminalTextEditorException)); throw new InvalidOperationException("observer"); };
        e.TextChanged += observer; Throws<InvalidOperationException>(() => Key(e, "Up")); e.TextChanged -= observer;
        Check(calls == 1 && e.Snapshot.Text == "accepted" && e.UndoDepth == 1, "Observer fault changed exactly-once effects.");
        Undo(e); Check(e.Snapshot.Text == "", "Faulted recall could not undo.");
        var y = new TerminalTextEditorPasteController(); y.SetText("a"); y.HandleInput(new TerminalKey("u", TerminalModifiers.Control));
        TerminalEditorVisualMap? during = null; y.TextChanged += _ => during = Map(y);
        y.HandleInput(new TerminalKey("y", TerminalModifiers.Control));
        RejectAtomic(y, () => y.HandleInput(new TerminalKey("Down"), during!, during!.GeometryIdentity), typeof(TerminalEditorLayoutException));
    }
    private static void RegistryFreshness()
    {
        var e = new TerminalTextEditorPasteController(); e.HandleInput(new TerminalPaste(new string('p', 1001))); var old = Map(e);
        e.Reset(); e.HandleInput(new TerminalPaste(new string('q', 1001)));
        Check(e.Snapshot.Text == old.SourceText && e.Snapshot.CursorUtf16Offset == old.SourceCursorUtf16Offset, "Fixture is not identical display.");
        RejectAtomic(e, () => e.HandleInput(new TerminalKey("Up"), old, old.GeometryIdentity), typeof(TerminalEditorLayoutException));
        var current = Map(e); e.Reset(); e.SetText(current.SourceText);
        RejectAtomic(e, () => e.HandleInput(new TerminalKey("Up"), current, current.GeometryIdentity), typeof(TerminalEditorLayoutException));
    }
    private static void Identities()
    {
        var e = new TerminalTextEditorPasteController(); e.SetText("same"); var geometry = Geometry(); var old = Map(e, geometry);
        var other = new TerminalTextEditorPasteController(); other.SetText("same");
        RejectAtomic(other, () => other.HandleInput(new TerminalKey("Up"), old, geometry.Identity), typeof(TerminalEditorLayoutException));
        foreach (var identity in new[] { geometry.Identity with { GeometryRevision = 2 }, geometry.Identity with { ViewLifetimeId = Guid.NewGuid() }, geometry.Identity with { PolicyId = "changed" }, default })
            RejectAtomic(e, () => e.HandleInput(new TerminalKey("Up"), old, identity), typeof(TerminalEditorLayoutException));
        // Width-away/back and height-only resize both require the caller's new revision.
        var resized = geometry with { Identity = geometry.Identity with { GeometryRevision = 2 } };
        var fresh = Map(e, resized); e.HandleInput(new TerminalKey("Up"), fresh, resized.Identity);
        var captured = Map(e); e.AddToHistory("new");
        RejectAtomic(e, () => e.HandleInput(new TerminalKey("Up"), captured, captured.GeometryIdentity), typeof(TerminalEditorLayoutException));
    }
    private static void Malformed()
    {
        var e = new TerminalTextEditorPasteController(); e.SetText("x"); var map = Map(e);
        foreach (var invalid in new[] { map with { Rows = default }, map with { RenderedRows = [] }, map with { SourceCursorUtf16Offset = -1 },
            new TerminalEditorVisualMap("x", 1, 80, 24, e.LayoutIdentity, map.GeometryIdentity, [], [], [], 0, 0) })
            RejectAtomic(e, () => e.HandleInput(new TerminalKey("Up"), invalid, map.GeometryIdentity), typeof(TerminalEditorLayoutException));
        var input = e.CaptureLayoutInput();
        foreach (var invalid in new[] { input with { AtomicRanges = default }, input with { AtomicRanges = [new(-1, 1)] }, input with { AtomicRanges = [new(0, int.MaxValue)] },
            input with { AtomicRanges = [new(0, 1), new(0, 1)] }, input with { Identity = default } })
            RejectAtomic(e, () => Builder.Build(invalid, Geometry()), typeof(TerminalEditorLayoutException));
        foreach (var geometry in new[] { Geometry(0), Geometry(257), Geometry(80, 0), Geometry(80, 1025), Geometry() with { Identity = default } })
            RejectAtomic(e, () => Builder.Build(input, geometry), typeof(TerminalEditorLayoutException));
        RejectAtomic(e, () => new ThrowingMapSource().Build(input, Geometry()), typeof(InvalidOperationException));
    }
    private static void Overflow()
    {
        var field = typeof(TerminalTextEditorPasteController).GetField("editorRevision", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var e = new TerminalTextEditorPasteController(); e.SetText("x"); e.AddToHistory("h"); field.SetValue(e, long.MaxValue);
        foreach (Action action in new Action[] { () => e.SetText("y"), () => e.SetCursor(0), () => e.Reset(), () => e.AddToHistory("z"), () => e.InsertText("a"), () => Key(e, "Down") })
            RejectAtomic(e, action, typeof(TerminalTextEditorException));
        var y = new TerminalTextEditorPasteController(); y.SetText("a"); y.HandleInput(new TerminalKey("u", TerminalModifiers.Control)); field.SetValue(y, long.MaxValue - 1);
        RejectAtomic(y, () => y.HandleInput(new TerminalKey("y", TerminalModifiers.Control)), typeof(TerminalTextEditorException));
    }
    private sealed record MapConsumer(TerminalEditorVisualMap Map);
    private static void RecordOperations()
    {
        var e = new TerminalTextEditorPasteController(); e.SetText("abc\ndef"); var g = Geometry(8); var map = Map(e, g);
        var hash = map.GetHashCode(); Check(hash == map.GetHashCode(), "Valid map hash was unstable.");
        var set = new HashSet<TerminalEditorVisualMap> { map };
        Check(set.Contains(map) && !set.Add(map), "Map failed ordinary hash collection use.");
        var consumer = new MapConsumer(map); Check(consumer.GetHashCode() == consumer.GetHashCode(), "Consumer record hash recursed.");
        var rebuilt = Map(e, g); Check(!ReferenceEquals(map, rebuilt), "Pure builder unexpectedly cached map.");
        _ = map.Equals(rebuilt); _ = map.ToString();
        var clone = map with { }; Check(map.Equals(clone) && hash == clone.GetHashCode(), "Unchanged record copy changed record equality/hash semantics.");
        RejectAtomic(e, () => e.HandleInput(new TerminalKey("Up"), clone, g.Identity), typeof(TerminalEditorLayoutException));
        RejectAtomic(e, () => e.HandleInput(new TerminalKey("Up"), map with { ContentColumns = 1 }, g.Identity), typeof(TerminalEditorLayoutException));
        e.HandleInput(new TerminalKey("Up"), map, g.Identity);
    }
    private static void Affinity()
    {
        var e = new TerminalTextEditorPasteController(); e.SetText("ab"); var map = Map(e, Geometry(4));
        Check(map.Rows.Length == 1 && map.RenderedRows.SequenceEqual(new[] { "> ab", "" }) && map.CursorRenderedRow == 1 && map.Rows[0].RenderedRowIndex == 0, "Exact-end caret row became editable.");
        e.AddToHistory("old"); Key(e, "Up", Geometry(4)); Check(e.Snapshot.CursorUtf16Offset == 0 && e.HistoryIndex == -1, "Exact end incorrectly entered history.");
        Key(e, "Up", Geometry(4)); Check(e.HistoryIndex == 0, "First editable row start did not enter history.");
        var tiny = new TerminalTextEditorPasteController(); tiny.SetText("abc"); var g = Geometry(1); map = Map(tiny, g);
        Check(map.Rows.Length == 3 && map.Rows[0].RenderedRowIndex == 2 && map.RenderedRows.Length == 6, "Prefix/caret decorations entered navigation rows.");
        tiny.SetCursor(1); Key(tiny, "Up", g); Check(tiny.Snapshot.CursorUtf16Offset == 0, "Internal boundary did not belong to following source row.");
    }
    private static void EmptyLines()
    {
        foreach (var text in new[] { "", "\n", "\n\n", "a\n\nb\n" })
            foreach (var columns in new[] { 1, 2, 3 })
            {
                var e = new TerminalTextEditorPasteController(); e.SetText(text); var g = Geometry(columns); var map = Map(e, g);
                Check(map.Rows.Count(r => r.LengthUtf16 == 0) == text.Split('\n').Count(v => v.Length == 0), "Empty logical line lacks exactly one editable row.");
                foreach (var row in map.Rows) Check(row.RenderedRowIndex >= 0 && row.RenderedRowIndex < map.RenderedRows.Length, "Editable row points outside drawing.");
                for (var i = 0; i < map.Rows.Length + 1; i++) Key(e, "Up", g);
                for (var i = 0; i < map.Rows.Length + 1; i++) Key(e, "Down", g);
                Check(e.Snapshot.CursorUtf16Offset == text.Length, "Empty-line navigation did not reach final end.");
            }
    }
    private static void Sticky()
    {
        var e = new TerminalTextEditorPasteController(); e.SetText("abcdefgh\nxy\nABCDEFGH"); e.SetCursor(6); var g = Geometry(80);
        Key(e, "Down", g); Check(e.Snapshot.CursorUtf16Offset == 11 && e.PreferredUtf16Offset == 6, "Short row did not retain UTF16 preference.");
        Key(e, "Down", g); Check(e.Snapshot.CursorUtf16Offset == 18 && e.PreferredUtf16Offset is null, "Long row did not restore preferred column.");
        Key(e, "Up", g); Key(e, "Up", g); Check(e.Snapshot.CursorUtf16Offset == 6, "Reverse sticky offset differs.");
        foreach (var noop in new[] { new TerminalKey("k", TerminalModifiers.Control), new TerminalKey("Delete") })
        {
            e.SetText("abcdefgh\nxy"); e.SetCursor(6); Key(e, "Down", g); e.HandleInput(noop); Key(e, "Up", g);
            Check(e.Snapshot.CursorUtf16Offset == 6, "No-op deletion discarded source sticky offset.");
        }
    }
    private static void RawSeams()
    {
        foreach (var text in new[] { "a\rb", "a\r\nb", "a\tZ\n\nB", "a\r\rb\r", "e\u0301\U0001f642\nZ" })
            foreach (var columns in new[] { 1, 2, 4, 16 })
                for (var cursor = 0; cursor <= text.Length; cursor++)
                {
                    if (cursor > 0 && cursor < text.Length && char.IsHighSurrogate(text[cursor - 1]) && char.IsLowSurrogate(text[cursor])) continue;
                    var e = new TerminalTextEditorPasteController(); e.AddToHistory(text); Key(e, "Up");
                    // Source trim removes a trailing CR; use the actual recalled length for legal seams.
                    if (cursor > e.Snapshot.Text.Length) continue;
                    e.SetCursor(cursor); Key(e, "PageDown", Geometry(columns));
                    Check(e.Snapshot.CursorUtf16Offset <= e.Snapshot.Text.Length, "Raw scalar seam had no source row.");
                }
    }
    private static void Scalars()
    {
        foreach (var cluster in new[] { "e\u0301", "\U0001f642", "\U0001f469\u200d\U0001f467", "\U0001f1fa\U0001f1f8" })
        {
            var e = new TerminalTextEditorPasteController(); e.SetText("a\n" + cluster + "z\nabc"); e.SetCursor(1);
            var g = Geometry(80); Key(e, "Down", g);
            Check(e.Snapshot.CursorUtf16Offset == 2 && e.SnappedUtf16Offset == 3, "Vertical movement did not retain pre-snap UTF16.");
            g = g with { ContentColumns = 64, Identity = g.Identity with { GeometryRevision = 1 } };
            Key(e, "Down", g); Check(e.Snapshot.CursorUtf16Offset == 2 + cluster.Length + 3, "Resize lost pre-snap column.");
        }
        var cjk = new TerminalTextEditorPasteController(); cjk.SetText("a\n\u754c\nabc"); cjk.SetCursor(1); Key(cjk, "Down");
        Check(cjk.Snapshot.CursorUtf16Offset == 3 && cjk.SnappedUtf16Offset is null, "Single UTF16 CJK used cells instead of source offset.");
    }
    private static void Markers()
    {
        var e = new TerminalTextEditorPasteController(); e.HandleInput(new TerminalPaste(new string('p', 1001))); e.InsertText("\nend"); e.SetCursor(0);
        var g = Geometry(8); var map = Map(e, g); Check(map.AtomicRanges.Length == 1 && map.Rows.Length >= 4, "Marker fixture did not wrap.");
        Key(e, "Down", g); Check(e.Snapshot.CursorUtf16Offset > map.AtomicRanges[0].LengthUtf16, "Downward marker continuations were revisited.");
        Key(e, "Up", g); Check(e.Snapshot.CursorUtf16Offset == 0 && e.SnappedUtf16Offset is not null, "Upward continuation did not snap to marker start.");
        var revised = g with { ContentColumns = 10, Identity = g.Identity with { GeometryRevision = 1 } }; Key(e, "Down", revised);
        Check(e.Snapshot.CursorUtf16Offset > map.AtomicRanges[0].LengthUtf16, "Resized marker continuation lost skip behavior.");
    }
    private static void Pages()
    {
        var text = string.Join('\n', Enumerable.Repeat("x", 45));
        foreach (var (height, expected) in new[] { (1, 5), (24, 7), (100, 30) })
        {
            var e = new TerminalTextEditorPasteController(); e.SetText(text); e.SetCursor(0); var g = Geometry(80, height); var map = Map(e, g);
            Check(map.Project(3).Rows.Length == 3 && map.Rows.Length == 45, "Three-row clip replaced full row extent.");
            Key(e, "PageDown", g); Check(e.Snapshot.CursorUtf16Offset == expected * 2, "Page extent did not use actual terminal height.");
            var projection = Map(e, g).Project(1); var physicalOrigin = 9;
            Check(physicalOrigin + projection.CursorRow == 9 && projection.FirstVisibleRow >= 0, "Frame origin altered source coordinates.");
        }
    }
    private static void HeldWrite()
    {
        using var held = new SemaphoreSlim(1, 1); held.Wait(); using var cancellation = new CancellationTokenSource();
        var waiter = held.WaitAsync(cancellation.Token); var e = new TerminalTextEditorPasteController(); e.SetText("a\nb");
        var mapping = Task.Run(() => Map(e)); Check(mapping.Wait(TimeSpan.FromSeconds(2)), "Pure mapping waited on unrelated write ownership.");
        Key(e, "Up"); cancellation.Cancel(); Throws<OperationCanceledException>(() => waiter.GetAwaiter().GetResult()); held.Release();
        Check(mapping.IsCompletedSuccessfully && waiter.IsCompleted, "Private mapping/write tasks did not join.");
    }
    private static void Retention()
    {
        var e = new TerminalTextEditorPasteController(); e.SetText(new string('x', 65536)); var g = Geometry(1); var map = Map(e, g);
        Check(map.Rows.Length <= 65537 && map.RenderedRows.Length <= 65539 && map.RenderedRows.Sum(v => (long)v.Length) <= 655362, "Maximum row/text caps exceeded.");
        Check(map.Rows.Length == 65536 && map.RenderedRows.Length == 65539 && map.SourceText == e.Snapshot.Text, "Maximum source/drawing distinction differs.");
        var weak = MakeWeakMap(e); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Check(!weak.IsAlive, "Controller or builder retained a former full map.");
        e.Reset(); Check(e.SavedHistoryDraftCharacters == 0 && e.RetainedPasteCharacters == 0 && e.UndoDepth == 0, "Reset retained draft-owned payload.");
    }
    [MethodImpl(MethodImplOptions.NoInlining)] private static WeakReference MakeWeakMap(TerminalTextEditorPasteController e) => new(Map(e));
    private static string State(TerminalTextEditorPasteController e) => JsonSerializer.Serialize(new { e.Snapshot, expanded = e.GetExpandedText(), e.RegisteredPasteCount, e.RetainedPasteCharacters,
        e.UndoDepth, e.RetainedUndoCharacters, e.KillRingCount, e.RetainedKillCharacters, e.LayoutIdentity, e.History, e.HistoryCount, e.RetainedHistoryCharacters,
        e.HistoryIndex, e.SavedHistoryDraft, e.SavedHistoryDraftCharacters, e.PreferredUtf16Offset, e.SnappedUtf16Offset, input = e.CaptureLayoutInput(),
        action = Hidden(e, "lastAction"), registry = Hidden(e, "pastes"), counter = Hidden(e, "pasteCounter"), undoFrames = Hidden(e, "undo"), ring = Hidden(e, "killRing") });
    private static object? Hidden(TerminalTextEditorPasteController e, string name) => typeof(TerminalTextEditorPasteController)
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(e);
    private static void RejectAtomic(TerminalTextEditorPasteController e, Action action, Type error)
    { var before = State(e); var calls = 0; Action<string> observer = _ => calls++; e.TextChanged += observer;
        try { try { action(); } catch (Exception ex) when (error.IsInstanceOfType(ex)) { var after = State(e);
            RejectionObservations.Add(new { id = RejectionObservations.Count, error = ex.GetType().Name, message = ex.Message,
                expected = JsonSerializer.Deserialize<JsonElement>(before), actual = JsonSerializer.Deserialize<JsonElement>(after), callbacks = calls, matches = before == after && calls == 0 });
            Check(before == after && calls == 0, "Rejected operation changed complete native state or callbacks."); return; }
            throw new InvalidOperationException("Expected " + error.Name); } finally { e.TextChanged -= observer; } }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class ThrowingMapSource : ITerminalEditorVisualMapSource
    { public TerminalEditorVisualMap Build(TerminalEditorLayoutInput input, TerminalEditorGeometrySnapshot geometry) => throw new InvalidOperationException("provider"); }
}
