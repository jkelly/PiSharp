using System.Text.Json;
using PiSharp.Tui;
using PiSharp.Tui.Input;

internal static class EditorBoundaryCases
{
    // A finite authored seam for these schedules only. This is not Source matchesKey or a native protocol implementation.
    private static string Symbol(string raw) => raw switch
    {
        "\u001bj" => "alt+j", "\u001bk" => "alt+k", "\u001b[D" => "left", "\u0002" => "ctrl+b", "\u0010" => "ctrl+p",
        _ => throw new InvalidDataException("Unspecified authored matcher payload")
    };

    internal static void Run(JsonElement fixture, ConsumerEvidence evidence, Action<TerminalInputEvent>? beforeHandle = null)
    {
        foreach (var scenario in fixture.GetProperty("editorCases").EnumerateArray())
        {
            var id = scenario.GetProperty("id").GetString()!;
            evidence.Begin("editor", id, scenario.Clone());
            var registry = new TerminalKeybindings((raw, key) =>
            {
                evidence.Observe("registry-schedule-matcher", new { raw, key }); return Symbol(raw) == key;
            },
                RegistryCases.Config(scenario.GetProperty("userBindings")));
            var expectedSelection = id switch
            {
                "remap-left-alt-j" or "default-left-control-b-guard" or "default-left-arrow-guard" => "tui.editor.cursorLeft",
                "remap-right-alt-k" => "tui.editor.cursorRight",
                "remap-word-left-alt-j" => "tui.editor.cursorWordLeft",
                "remap-history-previous-ctrl-p" => "tui.editor.historyPrevious",
                "remap-newline-alt-j" => "tui.input.newLine",
                "disable-left" => null,
                _ => throw new InvalidDataException("Unknown authored schedule")
            };
            var consideredActions = new[] { "tui.editor.cursorLeft", "tui.editor.cursorRight", "tui.editor.cursorWordLeft",
                "tui.editor.historyPrevious", "tui.input.newLine" };
            var selections = scenario.GetProperty("raw").EnumerateArray().Select(raw =>
                consideredActions.Where(action => registry.Matches(raw.GetString()!, action)).ToArray()).ToArray();
            var expected = expectedSelection is null ? Array.Empty<string>() : new[] { expectedSelection };
            var registrySelectionMatchesAuthoredExpectation = selections.All(selection => selection.SequenceEqual(expected));
            evidence.Observe("registry-selections", new { selections, expectedSelection, registrySelectionMatchesAuthoredExpectation });

            // Preserve the actual released controller boundary. Do not apply a synthetic remap to make the desired state pass.
            var editor = new TerminalTextEditorPasteController();
            editor.SetText(scenario.GetProperty("initialText").GetString()!);
            editor.SetCursor(scenario.GetProperty("initialCursor").GetInt32());
            if (scenario.TryGetProperty("history", out var history))
                foreach (var value in history.EnumerateArray()) editor.AddToHistory(value.GetString()!);
            evidence.Observe("initial-editor-state", new { text = editor.Snapshot.Text, expandedText = editor.GetExpandedText(),
                cursorUtf16Offset = editor.Snapshot.CursorUtf16Offset, history = editor.History.ToArray() });
            var changes = new List<string>(); editor.TextChanged += changes.Add;
            editor.TextChanged += value => evidence.Observe("text-changed", new { text = value });
            var decoder = new TerminalInputDecoder(); var events = new List<object>();
            void Dispatch(TerminalInputEvent input)
            {
                evidence.Observe("decoded-input-before-dispatch", new { type = input.GetType().Name, value = input.ToString() });
                beforeHandle?.Invoke(input); // Isolated fault control only; production Program supplies no injection.
                var disposition = editor.HandleInput(input);
                var observed = new { type = input.GetType().Name, value = input.ToString(), disposition };
                events.Add(observed); evidence.Observe("dispatched-input", observed);
            }
            foreach (var raw in scenario.GetProperty("raw").EnumerateArray())
                foreach (var input in decoder.Feed(raw.GetString()!)) Dispatch(input);
            foreach (var input in decoder.Complete()) Dispatch(input);
            var actual = new { text = editor.Snapshot.Text, expandedText = editor.GetExpandedText(),
                cursorUtf16Offset = editor.Snapshot.CursorUtf16Offset, changes = changes.ToArray(), history = editor.History.ToArray() };
            bool EqualsState(JsonElement expectedState) => actual.text == expectedState.GetProperty("text").GetString() &&
                actual.expandedText == expectedState.GetProperty("expandedText").GetString() &&
                actual.cursorUtf16Offset == expectedState.GetProperty("cursorUtf16Offset").GetInt32() &&
                actual.changes.SequenceEqual(expectedState.GetProperty("changes").EnumerateArray().Select(value => value.GetString())) &&
                actual.history.SequenceEqual(expectedState.GetProperty("history").EnumerateArray().Select(value => value.GetString()));
            var matchesAuthoredBaselinePrediction = EqualsState(scenario.GetProperty("currentNativePrediction"));
            var matchesDesiredConfiguredBehavior = EqualsState(scenario.GetProperty("desired"));
            var guard = scenario.TryGetProperty("guard", out var guardValue) && guardValue.GetBoolean();
            evidence.Complete(new { id, completeAuthoredScenario = scenario.Clone(), actual, events, selections, expectedSelection,
                registrySelectionMatchesAuthoredExpectation, matchesAuthoredBaselinePrediction, matchesDesiredConfiguredBehavior,
                passed = registrySelectionMatchesAuthoredExpectation && matchesAuthoredBaselinePrediction && (guard == matchesDesiredConfiguredBehavior),
                registryConfigurationApplied = true, controllerConfigurationApplied = false, sourceGolden = false,
                editorIntegrationAcceptance = false, matcherKind = "finite authored legacy schedule seam" });
        }
    }

    // The immutable R36 scenarios remain authored. This additional route uses the actual configured controller.
    internal static void RunConfigured(JsonElement fixture, ConsumerEvidence evidence)
    {
        foreach (var scenario in fixture.GetProperty("editorCases").EnumerateArray())
        {
            var id = scenario.GetProperty("id").GetString()!;
            evidence.Begin("configured-editor", id, scenario.Clone());
            var bindings = new TerminalKeybindings((raw, key) => TerminalInputDecoder.MatchesRawKey(raw, key),
                RegistryCases.Config(scenario.GetProperty("userBindings")));
            var editor = new TerminalTextEditorPasteController(keybindings: bindings);
            editor.SetText(scenario.GetProperty("initialText").GetString()!);
            editor.SetCursor(scenario.GetProperty("initialCursor").GetInt32());
            if (scenario.TryGetProperty("history", out var history))
                foreach (var value in history.EnumerateArray()) editor.AddToHistory(value.GetString()!);
            var changes = new List<string>(); editor.TextChanged += changes.Add;
            editor.TextChanged += value => evidence.Observe("configured-text-changed", new { text = value });
            var decoder = new TerminalInputDecoder(); var events = new List<object>();
            void Dispatch(TerminalInputEvent input)
            {
                var action = editor.ResolveConfiguredAction(input);
                TerminalInputDecoder.TryGetOriginalInput(input, out var raw, out var kittyActive);
                evidence.Observe("original-event-before-configured-dispatch", new { raw, kittyActive, action,
                    type = input.GetType().Name, value = input.ToString() });
                var disposition = editor.HandleInput(input);
                var observation = new { raw, kittyActive, action, disposition, text = editor.Snapshot.Text,
                    cursorUtf16Offset = editor.Snapshot.CursorUtf16Offset, editor.IsCharacterJumpPending };
                events.Add(observation); evidence.Observe("configured-dispatch-result", observation);
            }
            foreach (var raw in scenario.GetProperty("raw").EnumerateArray())
                foreach (var input in decoder.Feed(raw.GetString()!)) Dispatch(input);
            foreach (var input in decoder.Complete()) Dispatch(input);
            var actual = new { text = editor.Snapshot.Text, expandedText = editor.GetExpandedText(),
                cursorUtf16Offset = editor.Snapshot.CursorUtf16Offset, changes = changes.ToArray(), history = editor.History.ToArray() };
            var desired = scenario.GetProperty("desired");
            var passed = actual.text == desired.GetProperty("text").GetString() &&
                actual.expandedText == desired.GetProperty("expandedText").GetString() &&
                actual.cursorUtf16Offset == desired.GetProperty("cursorUtf16Offset").GetInt32() &&
                actual.changes.SequenceEqual(desired.GetProperty("changes").EnumerateArray().Select(value => value.GetString())) &&
                actual.history.SequenceEqual(desired.GetProperty("history").EnumerateArray().Select(value => value.GetString()));
            evidence.Complete(new { id, completeAuthoredScenario = scenario.Clone(), actual, events, passed,
                controllerConfigurationApplied = true, sourceGolden = false, editorIntegrationAcceptance = false,
                matcherKind = "production original-event matcher; authored expected state" });
        }
    }

    internal static object ConfiguredControls(ConsumerEvidence evidence)
    {
        var failures = new List<string>(); var observations = new List<object>();
        void Check(string id, bool passed, object observation)
        {
            var row = new { id, passed, observation }; observations.Add(row);
            evidence.Observe("configured-control", row); if (!passed) failures.Add(id);
        }
        TerminalKeybindings Bind(params (string Id, TerminalKeybindingValue? Value)[] values) =>
            new((raw, key) => TerminalInputDecoder.MatchesRawKey(raw, key), values.Select(value =>
                new KeyValuePair<string, TerminalKeybindingValue?>(value.Id, value.Value)));
        TerminalInputEvent Decode(string raw, bool kitty = false) => new TerminalInputDecoder(kittyProtocolActive: kitty).Feed(raw).Single();
        void Feed(TerminalTextEditorPasteController editor, params string[] raws)
        {
            var decoder = new TerminalInputDecoder();
            foreach (var raw in raws) foreach (var input in decoder.Feed(raw)) editor.HandleInput(input);
            foreach (var input in decoder.Complete()) editor.HandleInput(input);
        }

        var original = (TerminalKey)Decode("\u001b[49::97;5u"); var clone = original with { };
        Check("dual-original-binding-and-clone-identity",
            original == clone && original.GetHashCode() == clone.GetHashCode() && original.ToString() == clone.ToString() &&
            TerminalInputDecoder.MatchesKey(original, "ctrl+a") && TerminalInputDecoder.MatchesKey(original, "ctrl+1") &&
            !TerminalInputDecoder.MatchesKey(clone, "ctrl+a") && TerminalInputDecoder.MatchesKey(clone, "ctrl+1"),
            new { original = original.ToString(), clone = clone.ToString() });
        var shifted = Decode("\u001b[65;6u");
        Check("shifted-latin-identity", TerminalInputDecoder.MatchesKey(shifted, "shift+ctrl+a"), shifted.ToString());
        var locked = Decode("\u001b[1094::119;69u");
        Check("kitty-lock-mask", TerminalInputDecoder.MatchesKey(locked, "ctrl+w"), locked.ToString());
        var sourceAlias = Decode("\u001b[1094::119;5u");
        var projected = TerminalInputDecoder.ForEditorInput(sourceAlias, false);
        Check("internal-projection-retains-origin", TerminalInputDecoder.MatchesKey(projected, "ctrl+w") &&
            TerminalInputDecoder.TryGetOriginalInput(projected, out var retained, out _) && retained == "\u001b[1094::119;5u",
            new { projected = projected.ToString() });
        var lfLegacy = Decode("\n"); var lfKitty = Decode("\n", true);
        Check("per-event-protocol-mode", TerminalInputDecoder.MatchesKey(lfLegacy, "enter") &&
            !TerminalInputDecoder.MatchesKey(lfLegacy, "shift+enter") && !TerminalInputDecoder.MatchesKey(lfKitty, "enter") &&
            TerminalInputDecoder.MatchesKey(lfKitty, "shift+enter"), new { legacy = lfLegacy.ToString(), kitty = lfKitty.ToString() });
        Check("windows-backspace-context", TerminalInputDecoder.MatchesRawKey("\b", "ctrl+backspace", windowsTerminal: true) &&
            !TerminalInputDecoder.MatchesRawKey("\b", "backspace", windowsTerminal: true) &&
            TerminalInputDecoder.MatchesRawKey("\b", "backspace") && TerminalInputDecoder.MatchesRawKey("\b", "ctrl+h"), new { raw = "\b" });
        var mokLocked = Decode("\u001b[27;69;119~");
        Check("modify-other-keys-lock-is-not-kitty-lock", !TerminalInputDecoder.MatchesKey(mokLocked, "ctrl+w"), mokLocked.ToString());
        Check("protocol-and-paste-never-bind", !TerminalInputDecoder.MatchesKey(new TerminalProtocol("\u001b]0;x\a"), "escape") &&
            !TerminalInputDecoder.MatchesKey(new TerminalPaste("\u0003"), "ctrl+c") &&
            !TerminalInputDecoder.MatchesKey(new TerminalUnknownSequence("\u001b[27;69;119~"), "ctrl+w"), new { admitted = false });

        var bindings = Bind(("tui.editor.cursorLeft", "alt+j")); var snapshot = bindings.CreateSnapshot();
        bindings.SetUserBindings([]);
        Check("owner-snapshot-replacement-isolation", snapshot.GetKeys("tui.editor.cursorLeft").SequenceEqual(new[] { "alt+j" }) &&
            bindings.GetKeys("tui.editor.cursorLeft").SequenceEqual(new[] { "left", "ctrl+b" }),
            new { original = bindings.GetKeys("tui.editor.cursorLeft"), snapshot = snapshot.GetKeys("tui.editor.cursorLeft") });
        var overlap = new TerminalTextEditorPasteController(keybindings: Bind(("tui.editor.cursorLeft", "alt+j"), ("tui.editor.cursorRight", "alt+j")));
        overlap.SetText("abc"); overlap.SetCursor(1); Feed(overlap, "\u001bj");
        Check("right-before-left-conflict-context", overlap.Snapshot.CursorUtf16Offset == 2, overlap.Snapshot);
        var newline = new TerminalTextEditorPasteController(keybindings: Bind(("tui.input.submit", "alt+j"), ("tui.input.newLine", "alt+j")));
        newline.SetText("abc"); var newlineInput = Decode("\u001bj"); var submitted = newline.TryPrepareConfiguredSubmit(newlineInput);
        newline.HandleInput(newlineInput);
        Check("newline-before-submit-conflict-context", !submitted && newline.Snapshot.Text == "abc\n", newline.Snapshot);
        var disabledDelete = new TerminalTextEditorPasteController(keybindings: Bind(("tui.editor.deleteCharBackward", new TerminalKeybindingValue(Array.Empty<string>()))));
        disabledDelete.SetText("abc"); Feed(disabledDelete, "\u007f");
        Check("empty-override-suppresses-delete", disabledDelete.Snapshot.Text == "abc", disabledDelete.Snapshot);
        Feed(disabledDelete, "\u001b[127;2u");
        Check("direct-shift-backspace-survives-empty-override", disabledDelete.Snapshot.Text == "ab", disabledDelete.Snapshot);

        var releases = new TerminalTextEditorPasteController(keybindings: Bind()); releases.SetText("one two three");
        Feed(releases, "\u001b[1094::119;5:3u");
        Check("configured-r41-nonlatin-release", releases.Snapshot.Text == "one two three", releases.Snapshot);
        releases.HandleInput(new TerminalKey("w", TerminalModifiers.Control, TerminalKeyAction.Release));
        Check("configured-direct-word-release-guard", releases.Snapshot.Text == "one two ", releases.Snapshot);
        var pending = new TerminalTextEditorPasteController(keybindings: Bind()); pending.SetText("bA b"); pending.SetCursor(0);
        Feed(pending, "\u001d", "\u001b[32:13;2u", "Z");
        Check("configured-r41-pending-space-and-next-text", pending.Snapshot.Text == " ZbA b" &&
            pending.Snapshot.CursorUtf16Offset == 2 && !pending.IsCharacterJumpPending, pending.Snapshot);
        pending.SetText("bA b"); pending.SetCursor(0); Feed(pending, "\u001d", "\u001b[65;1:3u");
        Check("pending-printable-release-target-guard", pending.Snapshot.Text == "bA b" &&
            pending.Snapshot.CursorUtf16Offset == 1 && !pending.IsCharacterJumpPending, pending.Snapshot);

        var submit = new TerminalTextEditorPasteController(keybindings: Bind()); submit.SetText("abc\\");
        var enter = Decode("\r"); var shouldSubmit = submit.TryPrepareConfiguredSubmit(enter); submit.HandleInput(enter);
        Check("backslash-submit-key-adds-one-newline", !shouldSubmit && submit.Snapshot.Text == "abc\n", submit.Snapshot);
        var inverted = new TerminalTextEditorPasteController(keybindings: Bind(("tui.input.submit", "shift+enter"), ("tui.input.newLine", "enter")));
        inverted.SetText("abc\\");
        Check("backslash-newline-key-submits-with-inverted-binding", inverted.TryPrepareConfiguredSubmit(enter) &&
            inverted.Snapshot.Text == "abc", inverted.Snapshot);
        inverted.SetText("abc\\"); inverted.DisableSubmit = true; var disabled = inverted.TryPrepareConfiguredSubmit(enter); inverted.HandleInput(enter);
        Check("disabled-submit-retains-newline-branch", !disabled && inverted.Snapshot.Text == "abc\\\n", inverted.Snapshot);

        var vertical = new TerminalTextEditorPasteController(keybindings: Bind(("tui.editor.cursorUp", "ctrl+j"),
            ("tui.input.newLine", new TerminalKeybindingValue(Array.Empty<string>()))));
        vertical.SetText("ab\ncd"); Feed(vertical, "\u001d");
        var geometry = new TerminalEditorGeometrySnapshot(new(Guid.NewGuid(), 0, TerminalEditorVisualMapBuilder.SourcePolicyId), 20, 10);
        var map = new TerminalEditorVisualMapBuilder().BuildForTerminal(vertical.CaptureLayoutInput(), geometry);
        // Explicitly disable the configured newline claim. CSI-u avoids the separate raw LF compatibility branch.
        var up = Decode("\u001b[106;5u"); var requiresMap = vertical.RequiresConfiguredLayout(up);
        vertical.HandleInput(up, map, geometry.Identity);
        Check("pending-control-remap-uses-current-layout", requiresMap && !vertical.IsCharacterJumpPending &&
            vertical.Snapshot.Text == "ab\ncd" && vertical.Snapshot.CursorUtf16Offset == 2, vertical.Snapshot);
        var beforeStale = vertical.Snapshot;
        try { vertical.HandleInput(up, map, geometry.Identity); failures.Add("stale-configured-map-was-admitted"); }
        catch (TerminalEditorLayoutException error)
        { Check("stale-configured-map-rejects-before-change", error.Failure == TerminalEditorLayoutFailure.StaleEditor &&
            vertical.Snapshot == beforeStale, new { error.Failure, snapshot = vertical.Snapshot }); }

        // Retain the original R42 collision as its own guard: Source newline precedence is unchanged.
        var conflict = new TerminalTextEditorPasteController(keybindings: Bind(("tui.editor.cursorUp", "ctrl+j")));
        conflict.SetText("ab\ncd"); Feed(conflict, "\u001d"); var conflictNeedsMap = conflict.RequiresConfiguredLayout(up);
        var conflictMap = new TerminalEditorVisualMapBuilder().BuildForTerminal(conflict.CaptureLayoutInput(), geometry);
        conflict.HandleInput(up, conflictMap, geometry.Identity);
        Check("pending-layout-newline-conflict-retains-source-precedence", !conflictNeedsMap && !conflict.IsCharacterJumpPending &&
            conflict.Snapshot.Text == "ab\ncd\n" && conflict.Snapshot.CursorUtf16Offset == 6,
            new { originalR42Configuration = "cursorUp=ctrl+j; default newline retained", conflict.Snapshot });
        var rawLf = new TerminalTextEditorPasteController(keybindings: Bind(("tui.editor.cursorUp", "ctrl+j"),
            ("tui.input.newLine", new TerminalKeybindingValue(Array.Empty<string>()))));
        rawLf.SetText("ab\ncd"); var lf = Decode("\n"); var lfNeedsMap = rawLf.RequiresConfiguredLayout(lf); rawLf.HandleInput(lf);
        Check("raw-lf-compatibility-retains-newline-before-remapped-navigation", !lfNeedsMap && rawLf.Snapshot.Text == "ab\ncd\n",
            rawLf.Snapshot);

        // R43 regressions are authored; the R42 review and original fixtures remain immutable.
        var printableCases = new[]
        {
            (Id: "nonprintable-shift-press", Raw: "\u001b[97:13;2u", Text: (string?)null),
            (Id: "nonprintable-shift-repeat", Raw: "\u001b[97:13;2:2u", Text: (string?)null),
            (Id: "nonprintable-shift-caps-lock", Raw: "\u001b[97:13;66u", Text: (string?)null),
            (Id: "nonprintable-shift-both-locks", Raw: "\u001b[97:13;194u", Text: (string?)null),
            (Id: "nonprintable-shift-functional-left", Raw: "\u001b[97:57417;2u", Text: (string?)null),
            (Id: "printable-shift-value", Raw: "\u001b[97:65;2u", Text: (string?)"A")
        };
        foreach (var vector in printableCases)
            foreach (var awaitingTarget in new[] { false, true })
            {
                var configured = new TerminalTextEditorPasteController(keybindings: Bind());
                var historical = new TerminalTextEditorPasteController();
                configured.SetText("x"); historical.SetText("x");
                var configuredChanges = new List<string>(); var historicalChanges = new List<string>();
                configured.TextChanged += configuredChanges.Add; historical.TextChanged += historicalChanges.Add;
                if (awaitingTarget) { Feed(configured, "\u001d"); Feed(historical, "\u001d"); }
                var decoded = Decode(vector.Raw); configured.HandleInput(decoded); Feed(historical, vector.Raw);
                var expected = awaitingTarget ? "x" : "x" + vector.Text;
                var notifications = !awaitingTarget && vector.Text is not null ? new[] { expected } : Array.Empty<string>();
                Check("printable-authority-" + vector.Id + (awaitingTarget ? "-pending" : "-ordinary"),
                    TerminalInputDecoder.GetPrintableText(decoded) == vector.Text && configured.Snapshot.Text == expected &&
                    historical.Snapshot.Text == expected && configured.Snapshot.CursorUtf16Offset == expected.Length &&
                    historical.Snapshot.CursorUtf16Offset == expected.Length && configuredChanges.SequenceEqual(notifications) &&
                    historicalChanges.SequenceEqual(notifications) && !configured.IsCharacterJumpPending && !historical.IsCharacterJumpPending,
                    new { vector.Id, vector.Raw, expectedPrintable = vector.Text, awaitingTarget, expected, notifications,
                        configured = configured.Snapshot, historical = historical.Snapshot, configuredChanges, historicalChanges });
            }
        var rejectedOriginal = (TerminalKey)Decode("\u001b[97:13;2u"); var rejectedClone = rejectedOriginal with { };
        var clonedEditor = new TerminalTextEditorPasteController(keybindings: Bind()); clonedEditor.SetText("x"); clonedEditor.HandleInput(rejectedClone);
        Check("caller-clone-retains-named-printable-fallback", rejectedOriginal == rejectedClone &&
            TerminalInputDecoder.GetPrintableText(rejectedOriginal) is null && TerminalInputDecoder.GetPrintableText(rejectedClone) == "a" &&
            clonedEditor.Snapshot.Text == "xa", clonedEditor.Snapshot);
        var constructedEditor = new TerminalTextEditorPasteController(keybindings: Bind()); constructedEditor.SetText("x");
        constructedEditor.HandleInput(new TerminalKey("a", TerminalModifiers.Shift));
        Check("constructed-key-retains-named-printable-fallback", constructedEditor.Snapshot.Text == "xa", constructedEditor.Snapshot);
        var boundNonprintable = new TerminalTextEditorPasteController(keybindings: Bind(("tui.editor.cursorLeft", "shift+a")));
        boundNonprintable.SetText("xy"); boundNonprintable.HandleInput(rejectedOriginal);
        Check("nonprintable-result-does-not-disable-a-configured-binding", boundNonprintable.Snapshot.Text == "xy" &&
            boundNonprintable.Snapshot.CursorUtf16Offset == 1, boundNonprintable.Snapshot);
        var disabledPrimary = new TerminalTextEditorPasteController(keybindings: Bind(("tui.editor.cursorLineStart", new TerminalKeybindingValue(Array.Empty<string>()))));
        disabledPrimary.SetText("xy"); Feed(disabledPrimary, "\u001b[1094::97;5u");
        Check("printable-fallback-does-not-restore-disabled-default-projection", disabledPrimary.Snapshot.Text == "xy" &&
            disabledPrimary.Snapshot.CursorUtf16Offset == 2, disabledPrimary.Snapshot);
        return new { passed = failures.Count == 0, failures, observations, sourceGolden = false, executedAtAuthoring = false,
            productionCliRoutesCovered = false, kind = "authored configured controller and event-origin controls" };
    }
}
