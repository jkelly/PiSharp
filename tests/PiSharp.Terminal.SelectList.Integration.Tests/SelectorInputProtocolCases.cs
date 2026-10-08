using PiSharp.Tui;
using PiSharp.Tui.Input;

internal static class SelectorInputProtocolCases
{
    internal static void Run(ConsumerEvidence evidence)
    {
        var schedules = 0;
        foreach (var (final, key) in new[] { ('A', "Up"), ('B', "Down"), ('C', "Right"),
                     ('D', "Left"), ('H', "Home"), ('F', "End") })
        foreach (var kind in new[] { 1, 2, 3 })
        foreach (var (encoded, modifiers, prefix) in new[] {
                     (1, TerminalModifiers.None, ""), (2, TerminalModifiers.Shift, "shift+"),
                     (3, TerminalModifiers.Alt, "alt+"), (5, TerminalModifiers.Control, "ctrl+"),
                     (9, TerminalModifiers.Super, "super+"),
                     (16, TerminalModifiers.Shift | TerminalModifiers.Alt | TerminalModifiers.Control | TerminalModifiers.Super,
                         "shift+alt+ctrl+super+"),
                     (193, TerminalModifiers.None, "") })
        {
            var raw = $"\u001b[1;{encoded}:{kind}{final}";
            EverySchedule(raw, input =>
            {
                Require(input == new TerminalKey(key, modifiers, (TerminalKeyAction)(kind - 1)), raw);
                Require(TerminalInputDecoder.TryGetOriginalInput(input, out var original, out var kitty) &&
                    original == raw && kitty, "original navigation provenance");
                // Kitty matching ignores lock state while retaining the original semantic modifier mask.
                Require(TerminalInputDecoder.MatchesKey(input, prefix + key.ToLowerInvariant()),
                    "original navigation binding");
                InertForApprovalOrCancel(input);
            }, ref schedules);
        }

        foreach (var body in new[] { "1;:3B", "1;1:B", "1;0:3B", "1;1:0B", "1;1:4B",
                     "1;1:3:2B", "1;2147483648:3B", "1;1:2147483648B", "1;17:3B", "2;1:3B",
                     "1;1:3P", "1;1:3Z", "1;1:3~", "1;1:3;1B" })
        {
            var raw = "\u001b[" + body;
            EverySchedule(raw, input =>
            {
                Require(input == new TerminalUnknownSequence(raw), "invalid navigation form: " + raw);
                Require(!TerminalInputDecoder.MatchesKey(input, "down"), "invalid form cannot navigate");
                InertForApprovalOrCancel(input);
            }, ref schedules);
        }
        EverySchedule("\u001bO1;1:3B", input =>
        {
            Require(input == new TerminalUnknownSequence("\u001bO1;1:3B"), "CSI-only event suffix");
            InertForApprovalOrCancel(input);
        }, ref schedules);
        EverySchedule("\u001b[57353;1:3u", input =>
        {
            Require(input == new TerminalKey("Down", Action: TerminalKeyAction.Release), "decoded alias retained");
            Require(!TerminalInputDecoder.MatchesKey(input, "down"), "source-unmatched alias retained");
            InertForApprovalOrCancel(input);
        }, ref schedules);
        const string literal = "\u001b[1;1:3B\u0003\u001b";
        EverySchedule("\u001b[200~" + literal + "\u001b[201~", input =>
        {
            Require(input == new TerminalPaste(literal), "paste preserves literal controls");
            InertForApprovalOrCancel(input);
        }, ref schedules);
        var incomplete = new TerminalInputDecoder(kittyProtocolActive: true);
        Require(incomplete.Feed("\u001b[1;1:").Length == 0, "incomplete input remains buffered");
        var completed = incomplete.Complete().Single();
        Require(completed == new TerminalUnknownSequence("\u001b[1;1:"), "EOF cannot promote incomplete input");
        InertForApprovalOrCancel(completed);
        evidence.Observe("navigation-event-suffix-controls", new { schedules, actions = "press/repeat/release",
            keys = "Up/Down/Right/Left/Home/End", rawProvenance = true, invalidForms = 15,
            unmatchedAlias = 57353, literalPaste = true, incompleteEof = true });
    }

    private static void EverySchedule(string raw, Action<TerminalInputEvent> check, ref int schedules)
    {
        for (var split = 0; split <= raw.Length; split++)
        {
            var decoder = new TerminalInputDecoder(kittyProtocolActive: true);
            var events = decoder.Feed(raw.AsSpan(0, split)).Concat(decoder.Feed(raw.AsSpan(split))).Concat(decoder.Complete()).ToArray();
            Require(events.Length == 1, "single event at split " + split + ": " + raw);
            check(events[0]); schedules++;
        }
        var fragmented = new TerminalInputDecoder(kittyProtocolActive: true);
        var chunks = new List<TerminalInputEvent>();
        foreach (var character in raw) chunks.AddRange(fragmented.Feed(character.ToString()));
        chunks.AddRange(fragmented.Complete()); Require(chunks.Count == 1, "single event with one-character chunks");
        check(chunks[0]); schedules++;
    }

    private static void InertForApprovalOrCancel(TerminalInputEvent input)
    {
        foreach (var binding in new[] { "enter", "escape", "ctrl+c" })
            Require(!TerminalInputDecoder.MatchesKey(input, binding), "navigation/data cannot match " + binding);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
