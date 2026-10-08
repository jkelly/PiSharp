using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.Tui;
using PiSharp.Tui.Input;

internal static class AlternateKeyControls
{
    internal sealed record Result(int Failed, int Cases, int Splits, int Prefixes, object[] Observations, object[] Controls);
    private sealed record Expected(string Raw, string Primary, TerminalModifiers Modifiers, string Ordinary, string? Printable,
        TerminalKeyAction Action = TerminalKeyAction.Press, string? Pending = null);

    // Printable and action outcomes originate in the frozen public Source observations.
    // Primary public record values and bounds are the explicit native identity contract, not Source goldens.
    private static readonly Expected[] cases =
    [
        new("\u001b[97:65;2u", "a", TerminalModifiers.Shift, "A", "A"),
        new("\u001b[98:66;2u", "b", TerminalModifiers.Shift, "B", "B"),
        new("\u001b[1092::97;1u", "\u0444", TerminalModifiers.None, "\u0444", "\u0444"),
        new("\u001b[1092::97;5u", "\u0444", TerminalModifiers.Control, "a", null),
        new("\u001b[1094::119;5u", "\u0446", TerminalModifiers.Control, "w", null),
        new("\u001b[119::97;5u", "w", TerminalModifiers.Control, "w", null),
        new("\u001b[120:88;193u", "x", TerminalModifiers.None, "x", "x"),
        new("\u001b[120:88;194u", "x", TerminalModifiers.Shift, "X", "X"),
        new("\u001b[120:;193u", "x", TerminalModifiers.None, "x", "x"),
        new("\u001b[120::97;193u", "x", TerminalModifiers.None, "x", "x"),
        new("\u001b[1092::97;5:2u", "\u0444", TerminalModifiers.Control, "a", null, TerminalKeyAction.Repeat),
        new("\u001b[1092::97;5:3u", "\u0444", TerminalModifiers.Control, "a", null, TerminalKeyAction.Release),
        new("\u001b[49::97;5u", "1", TerminalModifiers.Control, "a", null),
        new("\u001b[57417::127;1u", "Left", TerminalModifiers.None, "Backspace", null),
        new("\u001b[57417::113;1u", "Left", TerminalModifiers.None, "Left", null),
        new("\u001b[57417:65;2u", "Left", TerminalModifiers.Shift, "A", "A"),
        new("\u001b[120:88:97:2u", "x", TerminalModifiers.None, "x", "x", TerminalKeyAction.Repeat),
        new("\u001b[120::97:3u", "x", TerminalModifiers.None, "x", "x", TerminalKeyAction.Release),
    ];

    internal static Result Run()
    {
        var controls = new List<object>(); var observations = new List<object>();
        var failed = 0; var splits = 0; var prefixes = 0;
        void Check(string id, bool passed) { if (!passed) failed++; controls.Add(new { id, passed }); }
        var shiftSpaceCases = from shifted in new[] { 65, 13 }
            from encoded in new[] { 2, 66, 130, 194 }
            from action in new[] { 1, 2, 3 }
            select new Expected($"\u001b[32:{shifted};{encoded}{(action == 1 ? "" : ":" + action)}u", " ", TerminalModifiers.Shift,
                " ", shifted == 65 ? "A" : null, (TerminalKeyAction)(action - 1), shifted == 65 ? "A" : "Space");
        var allCases = cases.Concat(shiftSpaceCases).ToArray();
        foreach (var test in allCases)
        {
            var decoder = new TerminalInputDecoder();
            var decoded = decoder.Feed(test.Raw).Concat(decoder.Complete()).Single() as TerminalKey;
            var canonical = new TerminalKey(test.Primary, test.Modifiers, test.Action);
            var primary = decoded is not null && decoded == canonical && decoded.GetHashCode() == canonical.GetHashCode() &&
                decoded.ToString() == canonical.ToString() && JsonSerializer.Serialize(decoded) == JsonSerializer.Serialize(canonical);
            Check("canonical-record-" + test.Raw, primary);
            if (decoded is null) continue;
            decoded.Deconstruct(out var name, out var modifiers, out var action);
            Check("deconstruct-" + test.Raw, name == test.Primary && modifiers == test.Modifiers && action == test.Action);
            var clone = decoded with { }; var alteredClone = decoded with { Key = "Tab" };
            Check("external-clone-origin-" + test.Raw, ReferenceEquals(clone, TerminalInputDecoder.ForEditorInput(clone, false)) &&
                ReferenceEquals(clone, TerminalInputDecoder.ForPendingCharacterJump(clone)) &&
                ReferenceEquals(alteredClone, TerminalInputDecoder.ForEditorInput(alteredClone, false)) &&
                ReferenceEquals(canonical, TerminalInputDecoder.ForPendingCharacterJump(canonical)));
            var ordinary = TerminalInputDecoder.ForEditorInput(decoded, false) as TerminalKey;
            var printable = TerminalInputDecoder.ForPendingCharacterJump(decoded) as TerminalKey;
            Check("ordinary-" + test.Raw, ordinary is not null && ordinary.Key == test.Ordinary && ordinary.Modifiers == test.Modifiers && ordinary.Action == test.Action);
            Check("pending-printable-" + test.Raw, test.Printable is null ? ReferenceEquals(printable, decoded) :
                printable is not null && printable.Key == test.Printable && printable.Modifiers == test.Modifiers && printable.Action == test.Action);
            Check("ordinary-idempotent-" + test.Raw, ordinary is not null && ReferenceEquals(ordinary, TerminalInputDecoder.ForEditorInput(ordinary, false)));
            var pending = TerminalInputDecoder.ForEditorInput(decoded, true) as TerminalKey;
            Check("pending-editor-" + test.Raw, pending is not null && pending.Key == (test.Pending ?? test.Printable ?? test.Ordinary) && pending.Action == test.Action && pending.Modifiers == test.Modifiers);
            if (test.Pending == "Space" && pending is not null)
            {
                Check("cancel-marker-idempotent-" + test.Raw, ReferenceEquals(pending, TerminalInputDecoder.ForEditorInput(pending, true)));
                Check("cancel-marker-ordinary-space-" + test.Raw, TerminalInputDecoder.ForEditorInput(pending, false) is TerminalKey ordinarySpace &&
                    ordinarySpace.Key == " " && ordinarySpace.Modifiers == test.Modifiers && ordinarySpace.Action == test.Action);
                var markerClone = pending with { };
                Check("external-marker-clone-no-origin-" + test.Raw, ReferenceEquals(markerClone, TerminalInputDecoder.ForEditorInput(markerClone, false)));
            }
            observations.Add(new { test.Raw, canonical, ordinary, printable, pending, historicalSubsetControl = test.Raw == "\u001b[120:88;193u" });
            for (var split = 0; split <= test.Raw.Length; split++)
            {
                var d = new TerminalInputDecoder();
                var events = d.Feed(test.Raw.AsSpan(0, split)).Concat(d.Feed(test.Raw.AsSpan(split))).Concat(d.Complete()).ToArray();
                splits++; Check("split-" + split + "-" + test.Raw, events.Length == 1 && events[0] == canonical &&
                    TerminalInputDecoder.ForEditorInput(events[0], false) is TerminalKey key && key.Key == test.Ordinary && key.Action == test.Action);
            }
            for (var length = 0; length < test.Raw.Length; length++)
            {
                var prefix = test.Raw[..length]; var d = new TerminalInputDecoder();
                var events = d.Feed(prefix).Concat(d.Complete()).ToArray(); prefixes++;
                Check("eof-" + length + "-" + test.Raw, length == 0 ? events.Length == 0 : length == 1 ?
                    events.Length == 1 && events[0] is TerminalKey { Key: "Escape" } :
                    events.Length == 1 && events[0] is TerminalUnknownSequence eofUnknown && eofUnknown.Sequence == prefix);
                if (length == 0) continue;
                var clock = new ManualClock(); var timed = new TerminalInputDecoder(timeProvider: clock);
                var first = timed.Feed(prefix); clock.Advance(length == 1 ? 34 : 249); var early = timed.FlushTimeouts();
                clock.Advance(1); var expired = timed.FlushTimeouts();
                var expected = length == 1 ? expired.Length == 1 && expired[0] is TerminalKey { Key: "Escape" } :
                    expired.Length == 1 && expired[0] is TerminalUnknownSequence expiredUnknown && expiredUnknown.Sequence == prefix;
                Check("timeout-" + length + "-" + test.Raw, first.Length == 0 && early.Length == 0 && expected &&
                    timed.Feed("z").Concat(timed.Complete()).Single() is TerminalText { Text: "z" });
            }
            var exact = new TerminalInputDecoder(new(MaximumSequenceCharacters: test.Raw.Length, MaximumChunkCharacters: test.Raw.Length));
            Check("exact-bound-" + test.Raw, exact.Feed(test.Raw).Single() == canonical);
            foreach (var sequence in new[] { true, false })
            {
                var bounded = new TerminalInputDecoder(sequence ? new(MaximumSequenceCharacters: test.Raw.Length - 1) :
                    new(MaximumChunkCharacters: test.Raw.Length - 1));
                var resource = false; var closed = false;
                try { bounded.Feed(test.Raw); } catch (TerminalInputException e) { resource = e.Failure == TerminalInputFailure.ResourceLimit; }
                try { bounded.Feed("x"); } catch (TerminalInputException e) { closed = e.Failure == TerminalInputFailure.Closed; }
                Check("over-bound-closed-" + sequence + "-" + test.Raw, resource && closed);
            }
        }
        foreach (var raw in new[] { "\u001b[120::;193u", "\u001b[120:88:97:99;193u", "\u001b[120;193;120u",
            "\u001b[120;193:0u", "\u001b[120;193:4u", "\u001b[120;193:1:1u", "\u001b[120;;193u",
            "\u001b[120:1114112;1u", "\u001b[120:55296;2u", "\u001b[120::1114112;1u", "\u001b[120:2147483648;2u",
            "\u001b[55296:88;2u", "\u001b[1114112::97;5u", "\u001b[120:88;257u" })
        {
            var d = new TerminalInputDecoder(); var events = d.Feed(raw).Concat(d.Complete()).ToArray();
            Check("declared-native-negative-" + raw, events.Length == 1 && events[0] is TerminalUnknownSequence u && u.Sequence == raw);
        }
        var properties = typeof(TerminalKey).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).Order().ToArray();
        Check("public-record-shape", properties.SequenceEqual(new[] { "Action", "Key", "Modifiers" }));
        var weak = WeakOrigin(); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Check("weak-origin-collected", !weak.TryGetTarget(out _));
        var weakMarker = WeakMarker(); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Check("weak-cancel-marker-collected", !weakMarker.TryGetTarget(out _));
        return new(failed, allCases.Length, splits, prefixes, observations.ToArray(), controls.ToArray());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<TerminalKey> WeakOrigin()
    {
        var d = new TerminalInputDecoder(); var key = (TerminalKey)d.Feed("\u001b[1092::97;5u").Single(); d.Complete();
        return new(key);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<TerminalKey> WeakMarker()
    {
        var d = new TerminalInputDecoder(); var key = d.Feed("\u001b[32:13;2u").Single(); d.Complete();
        return new((TerminalKey)TerminalInputDecoder.ForEditorInput(key, true));
    }

    private sealed class ManualClock : TimeProvider
    {
        private long now;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => now;
        internal void Advance(long milliseconds) => now += milliseconds;
    }
}
