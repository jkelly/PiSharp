using System.Text.Json;
using PiSharp.Tui;
using PiSharp.Tui.Input;

internal static class RegistryCases
{
    internal static TerminalKeybindingValue? Value(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => new TerminalKeybindingValue(value.GetString()!),
        JsonValueKind.Array => new TerminalKeybindingValue(value.EnumerateArray().Select(key => key.GetString()!)),
        JsonValueKind.Null => null,
        _ => throw new InvalidDataException("Authored configuration must be a string, string array or null")
    };

    internal static KeyValuePair<string, TerminalKeybindingValue?>[] Config(JsonElement value) =>
        value.EnumerateObject().Select(entry => new KeyValuePair<string, TerminalKeybindingValue?>(entry.Name, Value(entry.Value))).ToArray();

    internal static void Run(JsonElement fixture, ConsumerEvidence evidence, Func<string, string, bool>? matcher = null)
    {
        evidence.Stage("registry-definitions");
        var definitions = fixture.GetProperty("registryDefinitions").EnumerateObject().Select(entry =>
            new KeyValuePair<string, TerminalKeybindingDefinition>(entry.Name,
                new(Value(entry.Value.GetProperty("defaultKeys"))!))).ToArray();
        foreach (var scenario in fixture.GetProperty("registryCases").EnumerateArray())
        {
            evidence.Begin("registry", scenario.GetProperty("id").GetString()!, scenario.Clone());
            var failures = new List<string>(); var calls = new List<object>();
            void Fail(string detail) { failures.Add(detail); evidence.Observe("assertion-failure", new { detail }); }
            var registry = new TerminalKeybindings(definitions, (input, key) =>
            {
                var call = new { input, key }; calls.Add(call); evidence.Observe("matcher-call", call);
                return matcher is null ? StringComparer.Ordinal.Equals(input, key) : matcher(input, key);
            }, scenario.TryGetProperty("user", out var user) ? Config(user) : []);
            if (scenario.TryGetProperty("updates", out var updates))
                foreach (var update in updates.EnumerateArray()) registry.SetUserBindings(Config(update));
            if (scenario.TryGetProperty("mutateReturnedKeys", out var mutation))
            {
                var copy = registry.GetKeys(mutation.GetProperty("action").GetString()!);
                if (copy.Length > 0) copy[0] = mutation.GetProperty("append").GetString()!;
                Array.Resize(ref copy, copy.Length + 1); copy[^1] = mutation.GetProperty("append").GetString()!;
            }
            if (scenario.TryGetProperty("mutateReturnedConflict", out mutation))
            {
                var copy = registry.GetConflicts();
                if (copy.Length > 0)
                {
                    var actions = copy[0].Keybindings;
                    actions[0] = mutation.GetProperty("appendAction").GetString()!;
                    Array.Resize(ref actions, actions.Length + 1);
                    copy[0] = new(copy[0].Key, actions);
                }
            }
            if (scenario.TryGetProperty("expectedKeys", out var expectedKeys))
                foreach (var entry in expectedKeys.EnumerateObject())
                    if (!registry.GetKeys(entry.Name).SequenceEqual(entry.Value.EnumerateArray().Select(key => key.GetString())))
                        Fail("keys:" + entry.Name);
            if (scenario.TryGetProperty("conflicts", out var expectedConflicts))
            {
                var actual = registry.GetConflicts(); var expected = expectedConflicts.EnumerateArray().ToArray();
                if (actual.Length != expected.Length) Fail("conflict count");
                else for (var i = 0; i < actual.Length; i++)
                    if (actual[i].Key != expected[i].GetProperty("key").GetString() ||
                        !actual[i].Keybindings.SequenceEqual(expected[i].GetProperty("keybindings").EnumerateArray().Select(id => id.GetString())))
                        Fail("conflict:" + i);
            }
            if (scenario.TryGetProperty("resolved", out var expectedResolved))
            {
                var actual = registry.GetResolvedBindings();
                if (!actual.Keys.SequenceEqual(expectedResolved.EnumerateObject().Select(entry => entry.Name))) Fail("resolved order");
                foreach (var entry in expectedResolved.EnumerateObject())
                {
                    var expected = Value(entry.Value)!;
                    if (!actual.TryGetValue(entry.Name, out var value) || value.IsScalar != expected.IsScalar ||
                        !value.Keys.SequenceEqual(expected.Keys)) Fail("resolved shape:" + entry.Name);
                }
            }
            if (scenario.TryGetProperty("symbolicMatcherCalls", out var matcherCalls))
                foreach (var call in matcherCalls.EnumerateArray())
                    if (registry.Matches(call.GetProperty("input").GetString()!, call.GetProperty("action").GetString()!) !=
                        call.GetProperty("expected").GetBoolean()) Fail("symbolic matcher result");
            evidence.Complete(new { id = scenario.GetProperty("id").GetString(), completeAuthoredScenario = scenario.Clone(),
                passed = failures.Count == 0, failures, resolved = Shape(registry.GetResolvedBindings()),
                conflicts = registry.GetConflicts(), matcherCalls = calls, matcherKind = "authored ordinal symbolic seam; no wire protocol claim" });
        }
    }

    internal static Dictionary<string, object?> Shape(IEnumerable<KeyValuePair<string, TerminalKeybindingValue>> values) =>
        values.ToDictionary(pair => pair.Key, pair => pair.Value.IsScalar ? (object?)pair.Value.Scalar : pair.Value.Keys.ToArray(), StringComparer.Ordinal);

    internal static object Defaults(JsonElement inventory, ConsumerEvidence evidence)
    {
        var expected = inventory.GetProperty("definitions").EnumerateArray().ToArray();
        var actual = TerminalKeybindingDefinitions.Tui; var failures = new List<string>();
        void Fail(string detail) { failures.Add(detail); evidence.Observe("assertion-failure", new { detail }); }
        if (actual.Count != expected.Length) Fail("definition count");
        for (var i = 0; i < Math.Min(actual.Count, expected.Length); i++)
        {
            evidence.Observe("default-definition", new { index = i, actualId = actual[i].Key, actual[i].Value.Description,
                isScalar = actual[i].Value.DefaultKeys.IsScalar, keys = actual[i].Value.DefaultKeys.Keys.ToArray(), expected = expected[i].Clone() });
            var value = Value(expected[i].GetProperty("defaultKeys"))!;
            if (actual[i].Key != expected[i].GetProperty("id").GetString() ||
                actual[i].Value.Description != expected[i].GetProperty("description").GetString() ||
                actual[i].Value.DefaultKeys.IsScalar != value.IsScalar || !actual[i].Value.DefaultKeys.Keys.SequenceEqual(value.Keys))
                Fail("definition:" + i);
        }
        return new { passed = failures.Count == 0, failures, definitions = actual.Count,
            sourceKind = "static Source inventory; no Source behavior capture" };
    }

    internal static object Supplemental(ConsumerEvidence evidence)
    {
        var failures = new List<string>(); var calls = new List<string>();
        void Fail(string detail) { failures.Add(detail); evidence.Observe("assertion-failure", new { detail }); }
        var definitions = new Dictionary<string, TerminalKeybindingDefinition>
        {
            ["left"] = new(new TerminalKeybindingValue(new[] { "left", "ctrl+b" }), "Left"),
            ["right"] = new(new TerminalKeybindingValue("right"))
        };
        var inputKeys = new[] { "ALT+J", "alt+j", "ALT+J" };
        var config = new Dictionary<string, TerminalKeybindingValue?>
        {
            ["right"] = new(new[] { "alt+k", "alt+j" }), ["missing"] = "alt+k", ["left"] = new(inputKeys)
        };
        var registry = new TerminalKeybindings(definitions, (data, key) =>
        { calls.Add(key); evidence.Observe("supplemental-matcher-call", new { data, key }); return data == key; }, config);
        evidence.Observe("supplemental-initial-resolved", Shape(registry.GetResolvedBindings()));
        inputKeys[0] = "mutated"; config.Clear(); definitions.Clear();
        if (!registry.GetKeys("left").SequenceEqual(new[] { "ALT+J", "alt+j" })) Fail("immutable input / exact key identity");
        var conflict = registry.GetConflicts();
        if (conflict.Length != 1 || conflict[0].Key != "alt+j" || !conflict[0].Keybindings.SequenceEqual(new[] { "right", "left" }))
            Fail("user claimant order differs from definition order");
        if (registry.GetDefinition("missing") is not null || registry.GetDefinition("left")?.Description != "Left") Fail("definition lookup");
        if (!registry.Matches("ALT+J", "left") || !calls.SequenceEqual(new[] { "ALT+J" })) Fail("matcher short circuit");
        calls.Clear();
        if (registry.Matches("alt+j", "missing") || calls.Count != 0) Fail("unknown action calls matcher");
        var userCopy = registry.GetUserBindings();
        if (!userCopy.ContainsKey("missing") || userCopy["left"]?.Keys.Count != 3) Fail("user configuration retains unknown / duplicate keys");
        userCopy.Clear();
        if (registry.GetUserBindings().Count != 3) Fail("user map copy");
        var resolved = registry.GetResolvedBindings(); resolved.Clear();
        if (registry.GetResolvedBindings().Count != 2) Fail("resolved map copy");
        registry.SetUserBindings(new Dictionary<string, TerminalKeybindingValue?>
        {
            ["right"] = new(new[] { "alt+k", "alt+j" }), ["left"] = new(new[] { "alt+j", "alt+k" })
        });
        conflict = registry.GetConflicts();
        if (!conflict.Select(value => value.Key).SequenceEqual(new[] { "alt+k", "alt+j" }) ||
            conflict.Any(value => !value.Keybindings.SequenceEqual(new[] { "right", "left" }))) Fail("conflict key order");
        registry.SetUserBindings(new Dictionary<string, TerminalKeybindingValue?> { ["left"] = null, ["missing"] = null });
        if (!registry.GetKeys("left").SequenceEqual(new[] { "left", "ctrl+b" }) || registry.GetConflicts().Length != 0 ||
            registry.GetUserBindings()["left"] is not null) Fail("undefined restores default / retains config");
        try
        {
            registry.SetUserBindings(new[] { new KeyValuePair<string, TerminalKeybindingValue?>("left", "alt+j"),
                new KeyValuePair<string, TerminalKeybindingValue?>("left", "alt+k") });
            Fail("duplicate IDs accepted");
        }
        catch (ArgumentException) { }
        if (!registry.GetKeys("left").SequenceEqual(new[] { "left", "ctrl+b" })) Fail("failed replacement changed registry");
        // Authored regressions for the two actual R41 schedule defects. Existing Source fixtures remain unchanged.
        var releaseEditor = new TerminalTextEditorPasteController(); releaseEditor.SetText("one two three");
        var releaseDecoder = new TerminalInputDecoder();
        foreach (var input in releaseDecoder.Feed("\u001b[1094::119;5:3u"))
            releaseEditor.HandleInput(TerminalInputDecoder.ForEditorInput(input, false));
        if (releaseEditor.Snapshot.Text != "one two three" || releaseEditor.Snapshot.CursorUtf16Offset != 13)
            Fail("R41 non-Latin base-layout release must not delete");
        // Historical direct named word-release behavior is deliberately retained.
        releaseEditor.HandleInput(new TerminalKey("w", TerminalModifiers.Control, TerminalKeyAction.Release));
        if (releaseEditor.Snapshot.Text != "one two ") Fail("direct word-release compatibility guard");
        var jumpEditor = new TerminalTextEditorPasteController(); jumpEditor.SetText("bA b"); jumpEditor.SetCursor(0);
        var jumpDecoder = new TerminalInputDecoder();
        foreach (var raw in new[] { "\u001d", "\u001b[32:13;2u", "Z" })
            foreach (var input in jumpDecoder.Feed(raw))
                jumpEditor.HandleInput(TerminalInputDecoder.ForEditorInput(input, jumpEditor.IsCharacterJumpPending));
        if (jumpEditor.Snapshot.Text != " ZbA b" || jumpEditor.Snapshot.CursorUtf16Offset != 2 || jumpEditor.IsCharacterJumpPending)
            Fail("R41 pending nonprintable Shift+Space must insert before following Z");
        return new { passed = failures.Count == 0, failures, kind = "authored supplemental native contract controls" };
    }
}
