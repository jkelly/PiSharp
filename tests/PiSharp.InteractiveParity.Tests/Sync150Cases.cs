// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/tui/test/tui-alt-screen.test.ts, tui-render.test.ts,
// exit-restore.test.ts, fixtures/exit-without-stop.ts, select-list.test.ts, settings-list.test.ts, editor.test.ts, text.test.ts,
// keys.test.ts, fuzzy.test.ts, terminal.test.ts, terminal-image.test.ts, wrap-ansi.test.ts and autocomplete.test.ts.
// Ported expectations of the Pi 1.3.0-1.5.0 additions to those tests, read from the source; nothing is captured from an upstream
// run. Cases marked "authored" cover PiSharp API seams the upstream tests reach differently.
using System.Reflection;
using PiSharp.Tui.Pi;
using static Expect;

/// <summary>The Pi 1.5.0 TUI changes (sync rows tui.*): exit restore, control characters, select page keys, viewport input
/// capture, Kitty function keys and image crops, Rex and Orca, native Shift+Enter, styled wrap boundaries and fuzzy ranking.</summary>
internal static class Sync150Cases
{
    private sealed class FakeTerminal(int columns = 20, int rows = 5) : ITerminal
    {
        private Action<string>? input;
        public List<string> Writes { get; } = [];
        public string Output => string.Concat(Writes);
        public void Send(string data) => input?.Invoke(data);
        public void Start(Action<string> onInput, Action onResize) => input = onInput;
        public void Stop() => input = null;
        public Task DrainInputAsync(int maxMs = 1000, int idleMs = 50) => Task.CompletedTask;
        public void Write(string data) => Writes.Add(data);
        public int Columns => columns;
        public int Rows => rows;
        public bool KittyProtocolActive => false;
        public void MoveBy(int lines) { }
        public void HideCursor() { }
        public void ShowCursor() { }
        public void ClearLine() { }
        public void ClearFromCursor() { }
        public void ClearScreen() { }
        public void SetTitle(string title) { }
        public void SetProgress(bool active) { }
        public void SetProgramStatus(ProgramStatus status) { }
    }

    /// <summary>Raw terminal I/O for <see cref="ProcessTerminal"/>: records writes and raw mode, reports Shift as configured.</summary>
    private sealed class RecordingIo : IRawTerminalIo
    {
        private Action<string>? onText;
        public List<string> Writes { get; } = [];
        public string Output => string.Concat(Writes);
        public bool RawMode { get; private set; }
        public bool ShiftPressed { get; set; }
        public void Send(string data) => onText?.Invoke(data);
        public void EnterRawMode() => RawMode = true;
        public void ExitRawMode() => RawMode = false;
        public void StartReading(Action<string> handler) => onText = handler;
        public void StopReading() => onText = null;
        public IDisposable ObserveActivity(Action onActivity) => new Nothing();
        public void Write(string data) => Writes.Add(data);
        public (int Columns, int Rows) Size() => (80, 24);
        public void WatchResize(Action handler) { }
        public void StopWatchingResize() { }
        public bool IsShiftPressed() => ShiftPressed;
        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    private sealed class FixedLines(params string[] lines) : IComponent
    {
        public List<string> Render(int width) => [.. lines];
        public void Invalidate() { }
    }

    private static readonly SelectListTheme IdentityTheme = new(text => text, text => text, text => text, text => text, text => text);
    private static readonly EditorTheme IdentityEditorTheme = new(text => text, IdentityTheme);

    private static void Seq(IEnumerable<string> expected, IEnumerable<string> actual, string what) =>
        Check(expected.SequenceEqual(actual, StringComparer.Ordinal),
            $"{what}: expected [{string.Join(", ", expected.Select(Show))}], actual [{string.Join(", ", actual.Select(Show))}].");

    private static string Show(string text) => System.Text.Json.JsonSerializer.Serialize(text);

    private static int Count(string haystack, string needle)
    {
        var count = 0;
        for (var index = haystack.IndexOf(needle, StringComparison.Ordinal); index >= 0; index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static void WithImageEnvironment(Dictionary<string, string> env, Action run)
    {
        var previous = TerminalImage.Environment;
        var capabilities = TerminalImage.GetCapabilities();
        TerminalImage.Environment = name => env.TryGetValue(name, out var value) ? value : null;
        TerminalImage.ResetCapabilitiesCache();
        try { run(); }
        finally { TerminalImage.Environment = previous; TerminalImage.SetCapabilities(capabilities); }
    }

    private static void WithKittyImages(Action run)
    {
        var capabilities = TerminalImage.GetCapabilities();
        TerminalImage.SetCapabilities(capabilities with { Images = ImageProtocol.Kitty });
        try { run(); }
        finally { TerminalImage.SetCapabilities(capabilities); }
    }

    /// <summary>What AppDomain.ProcessExit runs for a started TUI (the "exit" event handler in tui.ts).</summary>
    private static void RaiseProcessExit(TuiBase tui) =>
        typeof(TuiBase).GetMethod("RestoreTerminalOnExit", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tui, [null, EventArgs.Empty]);

    private sealed class CommandProvider : IAutocompleteProvider
    {
        public Task<AutocompleteSuggestions?> GetSuggestionsAsync(IReadOnlyList<string> lines, int cursorLine, int cursorCol, bool force, CancellationToken cancellationToken) =>
            Task.FromResult<AutocompleteSuggestions?>(new([.. Enumerable.Range(0, 12).Select(index => new AutocompleteItem($"/command-{index}", $"command-{index}"))], "/"));
        public CompletionResult ApplyCompletion(IReadOnlyList<string> lines, int cursorLine, int cursorCol, AutocompleteItem item, string prefix) =>
            new([.. lines.Select((line, index) => index == cursorLine ? item.Value : line)], cursorLine, item.Value.Length);
    }

    private static string? FindFd()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "fd.exe", "fd" } : ["fd"];
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }

    public static IEnumerable<(string Id, Func<Task> Run)> All()
    {
        // ---------------------------------------------------------------- tui.exit-restore-terminal-modes
        yield return ("sync150.exit-restore.restores-modes-without-stop", Sync(() =>
        {
            // exit-restore.test.ts (#11170) with fixtures/exit-without-stop.ts: TuiAltScreen on a ProcessTerminal with mouse, then exit.
            var io = new RecordingIo(); var loop = UiLoop.CreateManual();
            var tui = new TuiAltScreen(new ProcessTerminal(loop, io, _ => null), loop, false, null,
                new TuiAltScreenOptions { Mouse = true, Environment = _ => null });
            tui.Start();
            RaiseProcessExit(tui);
            var output = io.Output;
            foreach (var sequence in new[] { "\u001b[?1006l", "\u001b[?1004l", "\u001b[?1003l", "\u001b[?1000l", "\u001b[<u", "\u001b[?2004l" })
                Equal(1, Count(output, sequence), Show(sequence));
            Check(output.IndexOf("\u001b[<u", StringComparison.Ordinal) < output.IndexOf("\u001b[?1049l", StringComparison.Ordinal), "Kitty pop must precede leaving alt screen");
            Check(output.EndsWith("\u001b[?1049l\u001b[?25h", StringComparison.Ordinal), "ends by leaving the alternate screen and showing the cursor: " + Show(output[^Math.Min(40, output.Length)..]));
            // Authored: Node resets the tty at exit by itself; .NET does not, so the restore also leaves raw mode.
            Equal(false, io.RawMode, "raw mode left");
            tui.Stop();
        }));
        yield return ("sync150.exit-restore.no-second-restore-after-stop", Sync(() =>
        {
            // exit-restore.test.ts: does not restore twice after a normal stop().
            var io = new RecordingIo(); var loop = UiLoop.CreateManual();
            var tui = new TuiAltScreen(new ProcessTerminal(loop, io, _ => null), loop, false, null,
                new TuiAltScreenOptions { Mouse = true, Environment = _ => null });
            tui.Start();
            tui.Stop(preserveScreen: true);
            RaiseProcessExit(tui);
            var output = io.Output;
            foreach (var sequence in new[] { "\u001b[?1006l", "\u001b[?1004l", "\u001b[<u", "\u001b[?2004l", "\u001b[?1049l" })
                Equal(1, Count(output, sequence), Show(sequence));
        }));
        yield return ("sync150.exit-restore.main-screen-restores-terminal-modes", Sync(() =>
        {
            // Authored: the regular-mode renderer restores the terminal's own modes and the cursor, without alternate-screen codes.
            var io = new RecordingIo(); var loop = UiLoop.CreateManual();
            var tui = new TuiMainScreen(new ProcessTerminal(loop, io, _ => null), loop, environment: _ => null);
            tui.Start();
            var before = io.Writes.Count;
            RaiseProcessExit(tui);
            var restore = string.Concat(io.Writes.Skip(before));
            Equal("\u001b[?25h\u001b[?2004l\u001b[<u", restore, "restore sequence");
            tui.Stop();
        }));

        // ---------------------------------------------------------------- tui.replace-control-characters / tui.set-title-control-chars
        yield return ("sync150.replace-control-characters.runs-become-one-space", Sync(() =>
        {
            // Authored for utils.ts replaceControlCharacters: each run of C0, DEL or C1 characters becomes one space.
            Equal("a b c d", TextUtils.ReplaceControlCharacters("a\u0000\u001fb\u007fc\u0080\u009fd"), "control runs");
            Equal(" x ", TextUtils.ReplaceControlCharacters("\r\nx\t"), "line breaks and tabs");
            Equal("plain é ✓", TextUtils.ReplaceControlCharacters("plain é ✓"), "printable text unchanged");
        }));
        yield return ("sync150.set-title.replaces-control-characters", Sync(() =>
        {
            // terminal.test.ts: replaces control characters so the title cannot end the OSC sequence.
            var io = new RecordingIo();
            var terminal = new ProcessTerminal(UiLoop.CreateManual(), io, _ => null);
            terminal.SetTitle("pi - work\u0007\u001b]52;c;Zm9v\u0007\u009b2J - repo");
            Seq(["\u001b]0;pi - work ]52;c;Zm9v 2J - repo\u0007"], io.Writes, "writes");
        }));
        yield return ("sync150.replace-control-characters.program-status-message", Sync(() =>
        {
            // Authored: the OSC 7501 program status message uses the same replacement, trimmed.
            var status = new ProgramStatus(ProgramState.Working, Message: "\u0007 build\u001b]8 done \n");
            Equal("\u001b]7501;state=working:msg=" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("build ]8 done")) + "\u001b\\", status.Format(), "formatted status");
        }));

        // ---------------------------------------------------------------- tui.native-shift-enter-esc-return
        yield return ("sync150.native-shift-enter.esc-return", Sync(() =>
        {
            // terminal.test.ts (#11559): ESC+CR becomes CSI-u Shift+Enter when Shift is pressed. Detection runs on Windows and in
            // macOS Apple Terminal, as upstream; the fake raw I/O stands in for the platform's Shift state.
            var native = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
            foreach (var (data, shift, expected) in new[]
                     {
                         ("\u001b\r", true, native ? "\u001b[13;2u" : "\u001b\r"),
                         ("\u001b\r", false, "\u001b\r"),
                         ("\r", true, native ? "\u001b[13;2u" : "\r"),
                         ("\r", false, "\r"),
                     })
            {
                var io = new RecordingIo { ShiftPressed = shift };
                var loop = UiLoop.CreateManual();
                var terminal = new ProcessTerminal(loop, io, name => name == "TERM_PROGRAM" ? "Apple_Terminal" : null);
                var received = new List<string>();
                terminal.Start(received.Add, () => { });
                io.Send(data);
                loop.RunPending();
                terminal.Stop();
                Seq([expected], received, $"{Show(data)} shift={shift}");
            }
        }));

        // ---------------------------------------------------------------- tui.regular-mode-strip-osc133
        yield return ("sync150.regular-mode.strips-osc133-markers", Sync(() =>
        {
            // tui-render.test.ts: does not write OSC 133 prompt markers to the terminal.
            var terminal = new FakeTerminal(40, 10);
            var tui = new TuiMainScreen(terminal, UiLoop.CreateManual(), environment: _ => null);
            var component = new MutableLines("\u001b]133;A\u0007prompt", "\u001b]133;B\u0007\u001b]133;C\u0007output");
            tui.AddChild(component);
            tui.RenderNow();
            component.Lines = [.. component.Lines, "\u001b]133;A\u001b\\reply", "\u001b]133;B\u001b\\\u001b]133;C\u001b\\done"];
            tui.RenderNow();
            var output = terminal.Output;
            Check(!output.Contains("\u001b]133;", StringComparison.Ordinal), "regular mode must not emit OSC 133 markers");
            foreach (var text in new[] { "prompt", "output", "reply", "done" }) Contains(output, text, "rendered text");
        }));

        // ---------------------------------------------------------------- tui.double-click-word-chars
        yield return ("sync150.double-click.path-word-joiners", async () =>
        {
            // tui-alt-screen.test.ts: coalesces slash, hyphen, dot, tilde, backslash and plus separated segments on double-click.
            foreach (var (line, needle) in new[]
                     {
                         ("extensions/starline/fixed-editor/compositor.ts", "starline"),
                         ("earendil-works/pi-tui", "works"),
                         ("~/.pi/agent/settings.json", "agent"),
                         ("~\\Documents\\pi", "Documents"),
                         ("foo+bar", "bar"),
                     })
            {
                var copied = new List<string>();
                var terminal = new FakeTerminal(80, 1);
                var tui = new TuiAltScreen(terminal, UiLoop.CreateManual(), options: new TuiAltScreenOptions
                {
                    Environment = _ => null,
                    CopySelection = text => { copied.Add(text); return Task.FromResult((true, (string?)null)); },
                });
                tui.AddChild(new Text(line, 0, 0));
                tui.Start();
                tui.RenderNow();
                var column = line.IndexOf(needle, StringComparison.Ordinal) + 1;
                terminal.Send($"\u001b[<0;{column};1M");
                terminal.Send($"\u001b[<0;{column};1m");
                terminal.Send($"\u001b[<0;{column};1M");
                terminal.Send($"\u001b[<0;{column};1m");
                await Task.Yield();
                tui.RenderNow();
                Seq([line], copied, line);
                tui.Stop();
            }
        });

        // ---------------------------------------------------------------- tui.kitty-image-crop-boxed
        yield return ("sync150.kitty-image-crop.padded-colored-box", Sync(() => WithKittyImages(() =>
        {
            // tui-alt-screen.test.ts: crops a Kitty image in a padded, colored box whose first line is above the viewport.
            var terminal = new FakeTerminal(20, 3);
            var tui = new TuiAltScreen(terminal, UiLoop.CreateManual(), options: new TuiAltScreenOptions { Environment = _ => null });
            const int imageId = 11271;
            var imageLine = TerminalImage.EncodeKitty("AAAA", columns: 2, rows: 3, imageId: imageId, moveCursor: false);
            TerminalImage.RegisterKittyImageMetadata(new(imageId, 2, 3, 100, 100));
            var box = new Box(1, 0, text => $"\u001b[44m{text}\u001b[49m");
            box.AddChild(new FixedLines(imageLine, "", ""));
            tui.AddChild(new FixedLines("before"));
            tui.AddChild(box);
            tui.AddChild(new FixedLines("after", "end"));
            tui.Start();
            tui.RenderNow();
            Equal(3, tui.ViewportTop, "viewportTop");
            Check(terminal.Writes.Any(write => write.Contains("i=11271", StringComparison.Ordinal) && write.Contains("y=66,h=34,r=1", StringComparison.Ordinal)),
                "cropped placement written: " + Show(terminal.Output));
            tui.Stop();
        })));
        yield return ("sync150.kitty-image-crop.placeholder-rows-with-text", Sync(() => WithKittyImages(() =>
        {
            // tui-alt-screen.test.ts (#11271): side-by-side text on placeholder rows no longer stops the search for the image line.
            var terminal = new FakeTerminal(20, 3);
            var tui = new TuiAltScreen(terminal, UiLoop.CreateManual(), options: new TuiAltScreenOptions { Environment = _ => null });
            const int imageId = 11272;
            var imageLine = TerminalImage.EncodeKitty("AAAA", columns: 2, rows: 3, imageId: imageId, moveCursor: false);
            TerminalImage.RegisterKittyImageMetadata(new(imageId, 2, 3, 100, 100));
            tui.AddChild(new FixedLines("before", imageLine, "   | side", "   | side"));
            tui.AddChild(new FixedLines("after", "end"));
            tui.Start();
            tui.RenderNow();
            Equal(3, tui.ViewportTop, "viewportTop");
            Check(terminal.Writes.Any(write => write.Contains("i=11272", StringComparison.Ordinal) && write.Contains("y=66,h=34,r=1", StringComparison.Ordinal)),
                "cropped placement written: " + Show(terminal.Output));
            tui.Stop();
        })));

        // ---------------------------------------------------------------- tui.select-page-keys / tui.select-page-index
        yield return ("sync150.select-page.select-list", Sync(() =>
        {
            // select-list.test.ts (#7629): moves by one visible page with selection page keys.
            var list = new SelectList(Enumerable.Range(0, 12).Select(index => new SelectItem($"item-{index}", $"Item {index}")), 5, IdentityTheme);
            var selections = new List<string>();
            list.OnSelectionChange = item => selections.Add(item.Value);
            foreach (var key in new[] { "\u001b[6~", "\u001b[6~", "\u001b[6~", "\u001b[6~", "\u001b[5~" }) list.HandleInput(key);
            Seq(["item-5", "item-10", "item-11", "item-6"], selections, "selections");
            list.SetFilter("no match");
            list.HandleInput("\u001b[6~");
            Seq(["item-5", "item-10", "item-11", "item-6"], selections, "no change without matches");
        }));
        yield return ("sync150.select-page.settings-list", Sync(() =>
        {
            // settings-list.test.ts (#7629): moves by one visible page with selection page keys.
            var list = new SettingsList(
                Enumerable.Range(0, 12).Select(index => new SettingItem { Id = $"setting-{index}", Label = $"Setting {index}", CurrentValue = index.ToString(System.Globalization.CultureInfo.InvariantCulture) }),
                5, new SettingsListTheme((text, _) => text, (text, _) => text, text => text, "> ", text => text), (_, _) => { }, () => { });
            list.HandleInput("\u001b[6~");
            Contains(string.Join("\n", list.Render(80)), "> Setting 5", "after one page down");
            list.HandleInput("\u001b[6~");
            list.HandleInput("\u001b[6~");
            Contains(string.Join("\n", list.Render(80)), "> Setting 11", "clamped at the last setting");
            list.HandleInput("\u001b[5~");
            Contains(string.Join("\n", list.Render(80)), "> Setting 6", "after one page up");
        }));
        yield return ("sync150.select-page.index-helper", Sync(() =>
        {
            // Authored for keybindings.ts getSelectPageIndex: page keys move by the page size, clamped; other keys return null.
            var keybindings = KeybindingsManager.Global;
            Equal<int?>(5, KeybindingsManager.GetSelectPageIndex(keybindings, "\u001b[6~", 0, 12, 5), "page down");
            Equal<int?>(11, KeybindingsManager.GetSelectPageIndex(keybindings, "\u001b[6~", 10, 12, 5), "page down clamped");
            Equal<int?>(0, KeybindingsManager.GetSelectPageIndex(keybindings, "\u001b[5~", 3, 12, 5), "page up clamped");
            Equal<int?>(null, KeybindingsManager.GetSelectPageIndex(keybindings, "\u001b[B", 3, 12, 5), "down arrow is not a page key");
        }));

        // ---------------------------------------------------------------- tui.captures-viewport-input
        yield return ("sync150.captures-viewport-input.editor-autocomplete-pages", Sync(() =>
        {
            // editor.test.ts (#7629): moves the autocomplete selection with selection page keys.
            var editor = new Editor(new TuiMainScreen(new FakeTerminal(40, 10), UiLoop.CreateManual(), environment: _ => null), IdentityEditorTheme);
            var submitted = "";
            editor.OnSubmit = text => submitted = text;
            editor.SetAutocompleteProvider(new CommandProvider());
            Equal(false, editor.CapturesViewportInput, "no capture without a menu");
            editor.HandleInput("/");
            Equal(true, editor.CapturesViewportInput, "captures while the menu shows");
            editor.HandleInput("\u001b[6~");
            editor.HandleInput("\u001b[6~");
            editor.HandleInput("\u001b[5~");
            editor.HandleInput("\r");
            Equal("/command-5", submitted, "submitted");
        }));
        yield return ("sync150.captures-viewport-input.focused-list-keeps-viewport", Sync(() =>
        {
            // tui-alt-screen.test.ts (#7629): defers viewport keys to a focused list instead of scrolling the transcript.
            var terminal = new FakeTerminal(20, 6);
            var tui = new TuiAltScreen(terminal, UiLoop.CreateManual(), options: new TuiAltScreenOptions { Environment = _ => null });
            var list = new SelectList(Enumerable.Range(0, 12).Select(index => new SelectItem($"item-{index}", $"item-{index}")), 3, IdentityTheme);
            tui.AddChild(new Text(string.Join("\n", Enumerable.Range(1, 12).Select(index => $"line {index}")), 0, 0));
            tui.AddChild(list);
            tui.SetFocus(list);
            tui.Start();
            tui.RenderNow();
            var topBefore = tui.ViewportTop;
            terminal.Send("\u001b[6~");
            terminal.Send("\u001b[1;5H");
            tui.RenderNow();
            Equal("item-3", list.GetSelectedItem()?.Value, "selected item");
            Equal(topBefore, tui.ViewportTop, "viewport unchanged");
            tui.Stop();
        }));
        yield return ("sync150.captures-viewport-input.editor-menu-releases-viewport", Sync(() =>
        {
            // tui-alt-screen.test.ts (#7629): gives viewport keys back to the transcript when editor autocomplete closes.
            var terminal = new FakeTerminal(40, 8);
            var tui = new TuiAltScreen(terminal, UiLoop.CreateManual(), options: new TuiAltScreenOptions { Environment = _ => null });
            var editor = new Editor(tui, IdentityEditorTheme);
            var submitted = "";
            editor.OnSubmit = text => submitted = text;
            editor.SetAutocompleteProvider(new CommandProvider());
            tui.AddChild(new Text(string.Join("\n", Enumerable.Range(1, 30).Select(index => $"line {index}")), 0, 0));
            tui.AddChild(editor);
            tui.SetFocus(editor);
            tui.Start();
            tui.RenderNow();
            terminal.Send("/");
            tui.RenderNow();
            Equal(true, editor.IsShowingAutocomplete(), "menu showing");
            var topWithMenu = tui.ViewportTop;
            terminal.Send("\u001b[6~");
            tui.RenderNow();
            Equal(topWithMenu, tui.ViewportTop, "page down pages the menu");
            terminal.Send("\r");
            tui.RenderNow();
            Equal("/command-5", submitted, "submitted");
            Equal(false, editor.IsShowingAutocomplete(), "menu closed");
            var topWithoutMenu = tui.ViewportTop;
            terminal.Send("\u001b[5~");
            tui.RenderNow();
            Check(tui.ViewportTop < topWithoutMenu, $"page up scrolls the transcript again: {tui.ViewportTop} < {topWithoutMenu}");
            tui.Stop();
        }));
        yield return ("sync150.captures-viewport-input.component-defaults", Sync(() =>
        {
            // Authored for tui.ts Component.capturesViewportInput: absent (false) by default; lists always capture.
            Equal(false, ((IComponent)new Text("x")).CapturesViewportInput, "Text");
            Equal(true, ((IComponent)new SelectList([], 3, IdentityTheme)).CapturesViewportInput, "SelectList");
            Equal(true, ((IComponent)new SettingsList([], 3, new SettingsListTheme((text, _) => text, (text, _) => text, text => text, "> ", text => text), (_, _) => { }, () => { })).CapturesViewportInput, "SettingsList");
        }));

        // ---------------------------------------------------------------- tui.text-settext-unchanged
        yield return ("sync150.text.same-text-keeps-wrapped-lines", Sync(() =>
        {
            // text.test.ts: keeps its wrapped lines when the same text is set again.
            var text = new Text("hello world", 0, 0);
            var lines = text.Render(5);
            text.SetText("hello world");
            Check(ReferenceEquals(lines, text.Render(5)), "same rendered list");
        }));

        // ---------------------------------------------------------------- tui.fuzzy-contiguous-match
        yield return ("sync150.fuzzy.contiguous-occurrence-beats-scattered-alignment", Sync(() =>
        {
            // fuzzy.test.ts (#11220): greedy alignment took o, p, u from "openrouter", so the real "opus" scored worst.
            var opus = Fuzzy.Match("opus", "openrouter openrouter/anthropic/claude-opus-4.1");
            var sonnet = Fuzzy.Match("opus", "openrouter openrouter/anthropic/claude-sonnet-4.5");
            var gptOss = Fuzzy.Match("opus", "openrouter openrouter/openai/gpt-oss-120b");
            Check(opus.Score < sonnet.Score, $"opus {opus.Score} < sonnet {sonnet.Score}");
            Check(opus.Score < gptOss.Score, $"opus {opus.Score} < gpt-oss {gptOss.Score}");
        }));
        yield return ("sync150.fuzzy.best-contiguous-occurrence", Sync(() =>
        {
            // fuzzy.test.ts: uses the best contiguous occurrence, not the first.
            Equal(Fuzzy.Match("opus", "xxxxxx opus").Score, Fuzzy.Match("opus", "xxopus opus").Score, "scores");
        }));
        yield return ("sync150.fuzzy.filter-ranks-contained-query-first", Sync(() =>
        {
            // fuzzy.test.ts (#11220): ranks a model containing the query above models matching a shared provider prefix.
            string[] items =
            [
                "radius radius/gpt-5.4 radius gpt-5.4 GPT 5.4",
                "radius radius/agentic radius agentic Agentic",
                "radius radius/gpt-6-sol radius gpt-6-sol GPT 6 Sol",
                "radius radius/gpt-6-astra radius gpt-6-astra GPT 6 Astra",
            ];
            Equal(items[3], Fuzzy.Filter(items, "astra", item => item)[0], "first result");
        }));

        // ---------------------------------------------------------------- tui.kitty-function-keys
        yield return ("sync150.keys.kitty-function-keys", Sync(() =>
        {
            // keys.test.ts: should match Kitty function keys F1-F12 with and without modifiers.
            Equal(true, Keys.Matches("\u001b[P", "f1"), "CSI P f1");
            Equal(true, Keys.Matches("\u001b[Q", "f2"), "CSI Q f2");
            Equal(true, Keys.Matches("\u001b[13~", "f3"), "CSI 13~ f3");
            Equal(true, Keys.Matches("\u001b[S", "f4"), "CSI S f4");
            Equal(true, Keys.Matches("\u001b[1;5P", "ctrl+f1"), "ctrl+f1");
            Equal(false, Keys.Matches("\u001b[1;5P", "f1"), "ctrl+f1 is not f1");
            Equal(true, Keys.Matches("\u001b[1;2S", "shift+f4"), "shift+f4");
            Equal(true, Keys.Matches("\u001b[15;5~", "ctrl+f5"), "ctrl+f5");
            Equal(true, Keys.Matches("\u001b[24;3~", "alt+f12"), "alt+f12");
            Equal(true, Keys.Matches("\u001b[15;1:2~", "f5"), "f5 repeat");
            Equal("f1", Keys.Parse("\u001b[P"), "parse f1");
            Equal("ctrl+f1", Keys.Parse("\u001b[1;5P"), "parse ctrl+f1");
            Equal("f3", Keys.Parse("\u001b[13~"), "parse f3");
            Equal("shift+f10", Keys.Parse("\u001b[21;2~"), "parse shift+f10");
            Equal(null, Keys.Parse("\u001b[1;5R"), "CSI R is not a function key");
            Equal(true, Keys.IsKeyRelease("\u001b[1;1:3P"), "release");
            Equal(true, Keys.IsKeyRepeat("\u001b[1;1:2S"), "repeat");
        }));
        yield return ("sync150.keys.unmapped-kitty-functional-keys-not-printable", Sync(() =>
        {
            // keys.test.ts: ignores unmapped Kitty functional keys such as F13-F35.
            Equal(null, Keys.DecodeKittyPrintable("\u001b[57376u"), "F13");
            Equal(null, Keys.DecodeKittyPrintable("\u001b[57398;2u"), "shift+F35");
            Equal(null, Keys.DecodePrintableKey("\u001b[57382u"), "F19");
            Equal("0", Keys.DecodeKittyPrintable("\u001b[57399u"), "keypad 0 still decodes");
        }));

        // ---------------------------------------------------------------- tui.rex-terminal / tui.orca-kitty-images
        yield return ("sync150.terminal-image.rex", Sync(() =>
        {
            // terminal-image.test.ts (#11131): Rex gets Kitty images, truecolor and hyperlinks; PI_HYPERLINKS=0 still wins.
            WithImageEnvironment(new() { ["TERM_PROGRAM"] = "rex", ["TERM"] = "xterm-256color", ["KITTY_WINDOW_ID"] = "1" }, () =>
            {
                var caps = TerminalImage.DetectCapabilities();
                Equal(true, caps.Hyperlinks, "hyperlinks");
                Equal(ImageProtocol.Kitty, caps.Images, "images");
                Equal(true, caps.TrueColor, "truecolor");
            });
            WithImageEnvironment(new() { ["TERM_PROGRAM"] = "rex", ["PI_HYPERLINKS"] = "0" }, () =>
                Equal(false, TerminalImage.DetectCapabilities().Hyperlinks, "PI_HYPERLINKS=0"));
        }));
        yield return ("sync150.terminal-image.orca", Sync(() =>
        {
            // terminal-image.test.ts: Orca keeps images off without ORCA_IMAGE_PROTOCOL=kitty and enables them with it.
            WithImageEnvironment(new() { ["TERM_PROGRAM"] = "Orca" }, () =>
                Equal(ImageProtocol.None, TerminalImage.DetectCapabilities().Images, "no image signal"));
            WithImageEnvironment(new() { ["TERM_PROGRAM"] = "Orca", ["ORCA_IMAGE_PROTOCOL"] = "kitty" }, () =>
            {
                var caps = TerminalImage.DetectCapabilities();
                Equal(ImageProtocol.Kitty, caps.Images, "images");
                Equal(true, caps.TrueColor, "truecolor");
                Equal(true, caps.Hyperlinks, "hyperlinks");
            });
        }));

        // ---------------------------------------------------------------- tui.wrap-styled-word-boundary
        yield return ("sync150.wrap.closing-code-stays-on-wrapped-line", Sync(() =>
        {
            // wrap-ansi.test.ts (#10925): keeps the closing code on the wrapped line and drops the following space.
            Seq(["aaaa \u001b[1mbold\u001b[22m", "next word"], TextUtils.WrapTextWithAnsi("aaaa \u001b[1mbold\u001b[22m next word", 9), "lines");
        }));
        yield return ("sync150.wrap.closed-style-not-reopened", Sync(() =>
        {
            // wrap-ansi.test.ts: does not re-open a style closed by the dropped space.
            Seq(["aaaa \u001b[31mred\u001b[39m", "next"], TextUtils.WrapTextWithAnsi("aaaa \u001b[31mred\u001b[39m  next", 8), "lines");
        }));
        yield return ("sync150.wrap.styled-whitespace-within-width", Sync(() =>
        {
            // wrap-ansi.test.ts: does not exceed width for styled whitespace wider than the line.
            foreach (var text in new[]
                     {
                         $"\u001b[41m{new string(' ', 20)}\u001b[0m",
                         $"ab\u001b[41m{new string(' ', 20)}\u001b[49mcd",
                         $"\u001b[33m{new string(' ', 20)}x\u001b[39m",
                     })
                foreach (var line in TextUtils.WrapTextWithAnsi(text, 10))
                    Check(TextUtils.VisibleWidth(line) <= 10, Show(line));
        }));
        yield return ("sync150.wrap.trims-spaces-before-long-word", Sync(() =>
        {
            // wrap-ansi.test.ts: trims trailing spaces before the line-end reset when a long word follows.
            Seq(["aaa\u001b[9m\u001b[29m", "\u001b[9mabcdefghi\u001b[29m", "\u001b[9mj\u001b[29m"],
                TextUtils.WrapTextWithAnsi("aaa\u001b[9m    abcdefghij\u001b[29m", 9), "lines");
        }));
        yield return ("sync150.wrap.strikethrough-reset-and-restored", Sync(() =>
        {
            // wrap-ansi.test.ts: resets strikethrough at line end and restores it on the next line.
            Seq(["aaaa \u001b[9mstrike\u001b[29m", "\u001b[9mme\u001b[29m next"], TextUtils.WrapTextWithAnsi("aaaa \u001b[9mstrike me\u001b[29m next", 12), "lines");
        }));
        yield return ("sync150.wrap.strikethrough-reset-keeps-background", Sync(() =>
        {
            // wrap-ansi.test.ts: preserves background when resetting strikethrough at line end.
            Seq(["\u001b[41;9mstrike\u001b[29m", "\u001b[9;41mme\u001b[0m"], TextUtils.WrapTextWithAnsi("\u001b[41;9mstrike me\u001b[0m", 6), "lines");
        }));

        // ---------------------------------------------------------------- tui.autocomplete-regex-metachar
        yield return ("sync150.autocomplete.fd-query-escapes-metacharacters", Sync(() =>
        {
            // Authored for autocomplete.ts buildFdPathQuery: every segment is a literal regex, single segments included.
            var build = typeof(CombinedAutocompleteProvider).GetMethod("BuildFdPathQuery", BindingFlags.Static | BindingFlags.NonPublic)!;
            string Query(string query) => (string)build.Invoke(null, [query])!;
            Equal("pages[\\\\/]\\[", Query("pages/["), "pages/[");
            Equal("pages[\\\\/]\\(", Query("pages/("), "pages/(");
            Equal("\\[id", Query("[id"), "[id");
            Equal("a\\.b\\*c", Query("a.b*c"), "single segment");
            Equal("src[\\\\/]", Query("src/"), "trailing separator");
        }));
        yield return ("sync150.autocomplete.metacharacters-match-literally", async () =>
        {
            // autocomplete.test.ts (#10972): matches regex metacharacters in queries literally. Needs fd, as upstream's case does.
            if (FindFd() is not { } fd) throw new SkipCaseException("fd is not on PATH.");
            using var temp = new TempDir();
            Directory.CreateDirectory(Path.Combine(temp.Path, "pages"));
            foreach (var file in new[] { "pages/[id].tsx", "pages/index.tsx", "pages/(group).ts" }) File.WriteAllText(Path.Combine(temp.Path, file), "x");
            var provider = new CombinedAutocompleteProvider([], temp.Path, fd);
            foreach (var (line, expected) in new[] { ("@pages/[", "@pages/[id].tsx"), ("@pages/(", "@pages/(group).ts"), ("@[id", "@pages/[id].tsx") })
            {
                var result = await provider.GetSuggestionsAsync([line], 0, line.Length, false, CancellationToken.None);
                Seq([expected], result?.Items.Select(item => item.Value) ?? [], line);
            }
        });
    }

    private sealed class MutableLines(params string[] lines) : IComponent
    {
        public List<string> Lines { get; set; } = [.. lines];
        public List<string> Render(int width) => [.. Lines];
        public void Invalidate() { }
    }
}
