using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using System.Text.Json;
using PiSharp.Tui;
using PiSharp.Tui.Input;

internal static class TerminalWordControlsTests
{
    internal static readonly List<object> BoundaryObservations = [], DecoderObservations = [], RejectionObservations = [];
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("word-controls.unicode17-official-default-word-boundaries", UnicodeBoundaries);
        yield return ("word-controls.all-original-aliases-actions-every-two-chunk-split", DecoderSchedules);
        yield return ("word-controls.logical-line-kill-registry-ring-and-undo", RegistryAndUndo);
        yield return ("word-controls.rejected-input-map-ring-and-revision-are-atomic", Admission);
        yield return ("word-controls.observer-fault-and-reentry-see-complete-commit", Observers);
        yield return ("word-controls.maximum-draft-and-independent-lifetimes", Maximum);
    }
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static object? Hidden(TerminalTextEditorPasteController e, string name) => e.GetType().GetField(name, Private)!.GetValue(e);
    private static void SetHidden(TerminalTextEditorPasteController e, string name, object value) => e.GetType().GetField(name, Private)!.SetValue(e, value);
    private static string State(TerminalTextEditorPasteController e) => (string)typeof(TerminalCharacterJumpTests).GetMethod("State", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [e])!;
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static void Key(TerminalTextEditorPasteController e, string key, TerminalModifiers mods = TerminalModifiers.Control, TerminalKeyAction action = TerminalKeyAction.Press) => e.HandleInput(new TerminalKey(key, mods, action));
    private static void UnicodeBoundaries()
    {
        var method = typeof(TerminalTextEditorPasteController).GetMethod("WordBoundaryOffsets", BindingFlags.Static | BindingFlags.NonPublic)!;
        var ordinal = 0; var differences = 0;
        foreach (var line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "word-data/WordBreakTest-17.0.0.txt")))
        {
            var content = line.Split('#')[0].Trim(); if (content.Length == 0) continue;
            var text = new StringBuilder(); var expected = new List<int>();
            foreach (var token in content.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (token == "\u00f7") expected.Add(text.Length); else if (token != "\u00d7") text.Append(new Rune(Convert.ToInt32(token, 16)).ToString());
            var actual = (List<int>)method.Invoke(null, [text.ToString()])!;
            var matches = expected.SequenceEqual(actual); if (!matches) differences++;
            BoundaryObservations.Add(new { ordinal = ordinal++, text = text.ToString(), expected, actual, matches });
        }
        Check(ordinal > 1_000 && differences == 0, $"Official Unicode word-break vectors: {differences} differences of {ordinal}.");
    }
    private static void DecoderSchedules()
    {
        var bindings = new (string Wire, int Direction, bool Kill)[] {
            ("\u001bb", -1, false), ("\u001b[1;3D", -1, false), ("\u001b[1;5D", -1, false),
            ("\u001bf", 1, false), ("\u001b[1;3C", 1, false), ("\u001b[1;5C", 1, false),
            ("\u0017", -1, true), ("\u001b\u007f", -1, true), ("\u001bd", 1, true), ("\u001b[3;3~", 1, true),
            ("\u001b[98;3u", -1, false), ("\u001b[102;3u", 1, false), ("\u001b[119;5u", -1, true), ("\u001b[127;3u", -1, true), ("\u001b[100;3u", 1, true),
            ("\u001b[98;3:2u", -1, false), ("\u001b[102;3:2u", 1, false), ("\u001b[119;5:2u", -1, true), ("\u001b[127;3:2u", -1, true), ("\u001b[100;3:2u", 1, true),
            ("\u001b[98;3:3u", -1, false), ("\u001b[102;3:3u", 1, false), ("\u001b[119;5:3u", -1, true), ("\u001b[127;3:3u", -1, true), ("\u001b[100;3:3u", 1, true) };
        foreach (var (wire, direction, kill) in bindings)
            for (var split = 0; split <= wire.Length; split++)
            {
                var e = new TerminalTextEditorPasteController(); e.SetText("one two three"); if (direction > 0) e.SetCursor(0);
                var callbacks = 0; e.TextChanged += _ => callbacks++;
                var decoder = new TerminalInputDecoder(timeProvider: new FixedClock());
                var events = decoder.Feed(wire.AsSpan(0, split)).Concat(decoder.Feed(wire.AsSpan(split))).Concat(decoder.Complete()).ToArray();
                foreach (var input in events) e.HandleInput(input);
                var expectedText = kill ? direction < 0 ? "one two " : " two three" : "one two three";
                var expectedCursor = kill ? direction < 0 ? 8 : 0 : direction < 0 ? 8 : 3;
                var matches = e.Snapshot == new TerminalTextEditorSnapshot(expectedText, expectedCursor) && callbacks == (kill ? 1 : 0);
                DecoderObservations.Add(new { wire, split, direction, kill, events = events.Select(input => new { type = input.GetType().Name, value = JsonSerializer.SerializeToElement(input, input.GetType()) }), expectedText, expectedCursor, actual = e.Snapshot, callbacks, matches });
                Check(matches, "Word decoder/dispatch differs at split " + split);
            }
    }
    private static void RegistryAndUndo()
    {
        var e = new TerminalTextEditorPasteController(); e.HandleInput(new TerminalPaste(new string('p', 1001))); var marker = e.Snapshot.Text;
        Key(e, "w"); Check(e.Snapshot.Text == "" && e.RegisteredPasteCount == 1 && e.KillRingCount == 1, "Word kill pruned registry or split marker.");
        Key(e, "y"); Check(e.Snapshot.Text == marker && e.GetExpandedText() == new string('p', 1001), "Word yank lost expansion.");
        Key(e, "-"); Check(e.Snapshot.Text == "" && e.KillRingCount == 1, "Undo changed independent ring.");
        Key(e, "-"); Check(e.Snapshot.Text == marker && e.RegisteredPasteCount == 1, "Word undo lost registry.");
        e.SetText("one\n\ntwo"); Key(e, "w"); Key(e, "w"); Key(e, "w"); Key(e, "w"); Key(e, "y");
        Check(e.Snapshot.Text == "one\n\ntwo", "Logical LF kills did not accumulate in source order.");
    }
    private static void Admission()
    {
        foreach (var action in new Action<TerminalTextEditorPasteController>[] { e => Key(e, "b", TerminalModifiers.Alt), e => Key(e, "f", TerminalModifiers.Alt), e => Key(e, "w"), e => Key(e, "d", TerminalModifiers.Alt), e => Key(e, "w", action: TerminalKeyAction.Release) })
        {
            var e = new TerminalTextEditorPasteController(); e.SetText("one two"); Key(e, "]"); SetHidden(e, "editorRevision", long.MaxValue);
            Reject(e, "revision-exhausted-" + RejectionObservations.Count, () => action(e));
        }
        var ring = new TerminalTextEditorPasteController(8); ring.SetText("one two");
        SetHidden(ring, "killRing", ImmutableArray.Create("12345678")); SetHidden(ring, "retainedKillCharacters", 8);
        var enumType = Hidden(ring, "lastAction")!.GetType(); SetHidden(ring, "lastAction", Enum.Parse(enumType, "Kill")); Key(ring, "]");
        Reject(ring, "backward-ring-accumulation-overflow", () => Key(ring, "w"));
        var forward = new TerminalTextEditorPasteController(8); forward.SetText("one two"); forward.SetCursor(0);
        SetHidden(forward, "killRing", ImmutableArray.Create("12345678")); SetHidden(forward, "retainedKillCharacters", 8); SetHidden(forward, "lastAction", Enum.Parse(enumType, "Kill")); Key(forward, "]");
        Reject(forward, "forward-ring-accumulation-overflow", () => Key(forward, "d", TerminalModifiers.Alt));
        var stale = new TerminalTextEditorPasteController(); stale.SetText("one two");
        var geometry = new TerminalEditorGeometrySnapshot(new(Guid.NewGuid(), 0, TerminalEditorVisualMapBuilder.PolicyId), 80, 24);
        var map = new TerminalEditorVisualMapBuilder().Build(stale.CaptureLayoutInput(), geometry); Key(stale, "]");
        Reject(stale, "stale-map-word-movement", () => stale.HandleInput(new TerminalKey("b", TerminalModifiers.Alt), map, geometry.Identity), typeof(TerminalEditorLayoutException));
        Reject(stale, "stale-map-word-kill", () => stale.HandleInput(new TerminalKey("w", TerminalModifiers.Control), map, geometry.Identity), typeof(TerminalEditorLayoutException));
    }
    private static void Observers()
    {
        var e = new TerminalTextEditorPasteController(); e.SetText("one two"); Key(e, "]"); var revision = e.LayoutIdentity.EditorRevision; var calls = 0;
        Action<string> observer = text =>
        {
            calls++; Check(text == "one " && Hidden(e, "characterJump")!.ToString() == "None" && !(bool)Hidden(e, "cancelJumpOnAdmission")! && e.LayoutIdentity.EditorRevision == revision + 1 && e.KillRingCount == 1 && e.UndoDepth == 2, "Observer saw incomplete word kill.");
            Reject(e, "reentrant-word-movement", () => Key(e, "b", TerminalModifiers.Alt));
            Reject(e, "reentrant-word-kill", () => Key(e, "w"));
            throw new IOException("word observer");
        };
        e.TextChanged += observer;
        try { Key(e, "w", action: TerminalKeyAction.Release); throw new InvalidOperationException("Missing observer fault."); } catch (IOException) { }
        e.TextChanged -= observer; Check(calls == 1 && e.Snapshot.Text == "one ", "Fault retried word kill."); Key(e, "-"); Check(e.Snapshot.Text == "one two", "Fault lost undo.");
    }
    private static void Maximum()
    {
        var e = new TerminalTextEditorPasteController(); var text = new string('a', 65529) + " beta z"; e.SetText(text); Key(e, "b", TerminalModifiers.Alt); Key(e, "b", TerminalModifiers.Alt); Key(e, "b", TerminalModifiers.Alt);
        Check(e.Snapshot.CursorUtf16Offset == 0, "Maximum draft did not find first word."); Key(e, "f", TerminalModifiers.Alt); Check(e.Snapshot.CursorUtf16Offset == 65529, "Maximum word seam differs.");
        var other = new TerminalTextEditorPasteController(); Check(other.Snapshot.Text == "" && other.KillRingCount == 0, "Word state leaked between lifetimes.");
    }
    private static void Reject(TerminalTextEditorPasteController e, string id, Action action, Type? errorType = null)
    {
        errorType ??= typeof(TerminalTextEditorException); var before = State(e); var calls = 0; Action<string> observer = _ => calls++; e.TextChanged += observer;
        try
        {
            try { action(); } catch (Exception error) when (errorType.IsInstanceOfType(error))
            {
                var after = State(e); var matches = before == after && calls == 0;
                RejectionObservations.Add(new { id, error = error.GetType().Name, expected = JsonSerializer.Deserialize<JsonElement>(before), actual = JsonSerializer.Deserialize<JsonElement>(after), callbacks = calls, matches }); Check(matches, "Rejected word operation changed complete state."); return;
            }
            throw new InvalidOperationException("Expected rejection: " + id);
        }
        finally { e.TextChanged -= observer; }
    }
    private sealed class FixedClock : TimeProvider { public override long GetTimestamp() => 0; public override long TimestampFrequency => 1000; }
}
