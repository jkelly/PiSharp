// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/tui/src/tui.ts (renderFakeCursor, resolveFakeCursors),
// tui-main-screen.ts, tui-alt-screen.ts, components/editor.ts, components/input.ts, utils.ts (extractSegments), stdin-buffer.ts,
// terminal-image.ts and keys.ts; coding-agent/src/modes/interactive/components/status-indicator.ts and settings-selector.ts.
// Ported expectations of the 1.2.0 additions to tui-render.test.ts, tui-alt-screen.test.ts, editor.test.ts, stdin-buffer.test.ts,
// terminal-colors.test.ts, terminal-image.test.ts and keys.test.ts, plus authored cases for the PiSharp ports.
using PiSharp.Cli.Interactive.Mode;
using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Tui.Pi;
using static Expect;

/// <summary>The Pi 1.2.0 TUI changes: the fake cursor, overlay cursor markers, unhandled wheel input under a focused overlay,
/// fragmented terminal replies, Herdr images, shifted-symbol key bindings and the embedded compaction status colour.</summary>
internal static class TuiSyncCases
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

    private sealed class FixedLines(params string[] lines) : IComponent
    {
        public List<string> Lines { get; set; } = [.. lines];
        public List<string> Render(int width) => [.. Lines];
        public void Invalidate() { }
    }

    private sealed class InputOverlay : IComponent, IFocusable, IInputHandler
    {
        public bool Focused { get; set; }
        public List<string> Inputs { get; } = [];
        public void HandleInput(string data) => Inputs.Add(data);
        public List<string> Render(int width) => ["overlay"];
        public void Invalidate() { }
    }

    private sealed class InputRecorder : IComponent, IInputHandler
    {
        public List<string> Inputs { get; } = [];
        public void HandleInput(string data) => Inputs.Add(data);
        public List<string> Render(int width) => [""];
        public void Invalidate() { }
    }

    /// <summary>node:test mock timers for setTimeout: timers run only when <see cref="Tick"/> advances past their due time.</summary>
    private sealed class FakeTimers
    {
        private sealed class Handle(long due, long order, Action action) : IDisposable
        {
            public long Due { get; } = due;
            public long Order { get; } = order;
            public Action Action { get; } = action;
            public bool Cancelled { get; private set; }
            public void Dispose() => Cancelled = true;
        }
        private readonly List<Handle> timers = [];
        private long now, order;

        public IDisposable SetTimeout(Action action, double milliseconds)
        {
            var handle = new Handle(now + (long)Math.Max(0, milliseconds), order++, action);
            timers.Add(handle);
            return handle;
        }

        public void Tick(long milliseconds)
        {
            var target = now + milliseconds;
            while (timers.Where(timer => !timer.Cancelled && timer.Due <= target).OrderBy(timer => timer.Due).ThenBy(timer => timer.Order).FirstOrDefault() is { } next)
            {
                timers.Remove(next);
                now = next.Due;
                next.Dispose();
                next.Action();
            }
            now = target;
        }
    }

    private static (StdinBuffer Buffer, List<string> Sequences, FakeTimers Timers) NewBuffer(double timeoutMs = 50)
    {
        var timers = new FakeTimers();
        var buffer = new StdinBuffer(timers.SetTimeout, timeoutMs);
        var sequences = new List<string>();
        buffer.Data += sequences.Add;
        return (buffer, sequences, timers);
    }

    private static string Show(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(Escape)) + "]";
    private static string Escape(string value) => System.Text.Json.JsonSerializer.Serialize(value);
    private static void Seq(IEnumerable<string> expected, IEnumerable<string> actual, string what) =>
        Check(expected.SequenceEqual(actual, StringComparer.Ordinal), $"{what}: expected {Show(expected)}, actual {Show(actual)}.");

    private static void WithKitty(bool active, Action run)
    {
        var previous = Keys.IsKittyProtocolActive;
        Keys.SetKittyProtocolActive(active);
        try { run(); }
        finally { Keys.SetKittyProtocolActive(previous); }
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

    public static IEnumerable<(string Id, Func<Task> Run)> All()
    {
        // ---------------------------------------------------------------- fake cursor
        yield return ("tui.fake-cursor.render-fake-cursor-markers", Sync(() =>
        {
            Equal("\u001b_pi:fc\u0007", TuiBase.FakeCursorStart, "start marker");
            Equal("\u001b_pi:/fc\u0007", TuiBase.FakeCursorEnd, "end marker");
            Equal("\u001b_pi:fc\u0007x\u001b_pi:/fc\u0007", TuiBase.RenderFakeCursor("x"), "renderFakeCursor wraps the text");
            Equal(0, TextUtils.VisibleWidth(TuiBase.RenderFakeCursor("")), "markers are zero-width");
            Equal("x", Strip(TuiBase.RenderFakeCursor("x")), "stripTerminalSequences removes the markers");
        }));
        yield return ("tui.fake-cursor.input-wraps-cursor-cell", Sync(() =>
        {
            var input = new Input();
            input.SetValue("abc");
            Equal("> " + TuiBase.RenderFakeCursor("a") + "bc" + new string(' ', 15), input.Render(20)[0], "unfocused cursor on a");
            input.Focused = true;
            Equal("> " + TuiBase.CursorMarker + TuiBase.RenderFakeCursor("a") + "bc" + new string(' ', 15), input.Render(20)[0], "focused cursor on a");
            input.HandleInput("\u0005"); // ctrl+e: cursor to the end
            Equal("> abc" + TuiBase.CursorMarker + TuiBase.RenderFakeCursor(" ") + new string(' ', 14), input.Render(20)[0], "cursor at the end");
            Check(!input.Render(20)[0].Contains("\u001b[7m", StringComparison.Ordinal), "no reverse video emitted by the component");
            Equal("> abc", Strip(input.Render(20)[0]).TrimEnd(), "stripped text");
        }));
        yield return ("tui.fake-cursor.editor-wraps-cursor-cell", Sync(() =>
        {
            // editor.test.ts: the cursor cell is a fake cursor; at the end of a line it is a highlighted space; stripped
            // content stays the text, and the line keeps its width.
            var editor = new Editor(Ui(), Themes.GetEditorTheme());
            editor.SetText("hello");
            var lines = editor.Render(20);
            Check(lines[1].EndsWith(TuiBase.RenderFakeCursor(" ") + new string(' ', 14), StringComparison.Ordinal) || lines[1].Contains(TuiBase.RenderFakeCursor(" "), StringComparison.Ordinal),
                "cursor at the end is a fake-cursor space: " + Escape(lines[1]));
            Check(lines[1].Contains(TuiBase.FakeCursorStart, StringComparison.Ordinal), "fake cursor present");
            Check(!lines[1].Contains("\u001b[7m", StringComparison.Ordinal), "no reverse video emitted by the component");
            Equal(20, TextUtils.VisibleWidth(lines[1]), "line width");
            Equal("hello", Strip(lines[1]).Trim(), "stripped content");
            editor.HandleInput("\u0001"); // ctrl+a
            Contains(editor.Render(20)[1], TuiBase.RenderFakeCursor("h") + "ello", "cursor on the first grapheme");
        }));
        yield return ("tui.fake-cursor.main-screen-reverse-video-or-hardware-cursor", Sync(() =>
        {
            // tui-render.test.ts: draws the focused fake cursor in reverse video and omits it when the hardware cursor is shown.
            var terminal = new FakeTerminal(20, 5);
            var tui = new TuiMainScreen(terminal, UiLoop.CreateManual(), environment: _ => null);
            var input = new Input();
            input.SetValue("abc");
            tui.AddChild(input);
            tui.SetFocus(input);
            tui.Start();
            tui.RenderNow();
            Contains(terminal.Output, "> \u001b[7ma\u001b[27mbc", "reverse-video cursor");
            terminal.Writes.Clear();
            tui.ShowHardwareCursor = true;
            tui.RenderNow();
            Check(terminal.Output.Length > 0, "re-rendered");
            Check(!terminal.Output.Contains("\u001b[7m", StringComparison.Ordinal), "no reverse video with the hardware cursor: " + Escape(terminal.Output));
            Check(!terminal.Output.Contains("\u001b_pi:", StringComparison.Ordinal), "no markers reach the terminal");
            tui.Stop();
        }));
        yield return ("tui.fake-cursor.unfocused-input-keeps-fake-cursor", Sync(() =>
        {
            // tui-render.test.ts: keeps the fake cursor of an unfocused input when the hardware cursor is shown.
            var terminal = new FakeTerminal(20, 5);
            var tui = new TuiMainScreen(terminal, UiLoop.CreateManual(), showHardwareCursor: true, environment: _ => null);
            var input = new Input();
            input.SetValue("abc");
            tui.AddChild(input);
            tui.Start();
            tui.RenderNow();
            Contains(terminal.Output, "> \u001b[7ma\u001b[27mbc", "unfocused fake cursor stays reverse video");
            tui.Stop();
        }));
        yield return ("tui.fake-cursor.truncated-end-marker-omitted-with-hardware-cursor", Sync(() =>
        {
            // tui-render.test.ts: omits the focused fake cursor when truncation dropped its end marker.
            var terminal = new FakeTerminal(20, 5);
            var tui = new TuiMainScreen(terminal, UiLoop.CreateManual(), showHardwareCursor: true, environment: _ => null);
            tui.AddChild(new FixedLines(TextUtils.SliceByColumn("ab" + TuiBase.CursorMarker + TuiBase.RenderFakeCursor("c") + "def", 0, 3, true)));
            tui.Start();
            tui.RenderNow();
            Check(!terminal.Output.Contains("\u001b[7m", StringComparison.Ordinal), "no reverse video: " + Escape(terminal.Output));
            Check(terminal.Output.EndsWith("\u001b[3G", StringComparison.Ordinal), "hardware cursor at column 3: " + Escape(terminal.Output));
            tui.Stop();
        }));
        yield return ("tui.fake-cursor.overlay-ending-left-of-cursor-keeps-marker", Sync(() =>
        {
            // tui-render.test.ts: keeps the cursor when an overlay ends directly left of it.
            foreach (var showHardwareCursor in new[] { false, true })
            {
                var terminal = new FakeTerminal(20, 5);
                var tui = new TuiMainScreen(terminal, UiLoop.CreateManual(), showHardwareCursor: showHardwareCursor, environment: _ => null);
                tui.AddChild(new FixedLines("abc" + TuiBase.CursorMarker + TuiBase.RenderFakeCursor("d") + "ef"));
                tui.ShowOverlay(new FixedLines("XXX"), new OverlayOptions { Row = 0, Col = 0, Width = 3 });
                tui.Start();
                tui.RenderNow();
                var writes = terminal.Output;
                Contains(writes, "XXX", $"overlay drawn ({showHardwareCursor})");
                Equal(!showHardwareCursor, writes.Contains("\u001b[7md", StringComparison.Ordinal), $"fake cursor drawn ({showHardwareCursor})");
                Check(writes.EndsWith("\u001b[4G", StringComparison.Ordinal), $"hardware cursor at column 4 ({showHardwareCursor}): " + Escape(writes));
                tui.Stop();
            }
        }));
        yield return ("tui.fake-cursor.overlay-fake-cursor-resolved", Sync(() =>
        {
            // tui.ts compositeOverlays: overlay lines resolve their fake cursors before compositing.
            var terminal = new FakeTerminal(20, 5);
            var tui = new TuiMainScreen(terminal, UiLoop.CreateManual(), environment: _ => null);
            tui.AddChild(new FixedLines("base"));
            tui.ShowOverlay(new FixedLines("o" + TuiBase.RenderFakeCursor("k")), new OverlayOptions { Row = 1, Col = 0, Width = 5 });
            tui.Start();
            tui.RenderNow();
            Contains(terminal.Output, "o\u001b[7mk\u001b[27m", "overlay fake cursor in reverse video");
            Check(!terminal.Output.Contains("\u001b_pi:fc", StringComparison.Ordinal), "no fake-cursor markers reach the terminal");
            tui.Stop();
        }));
        yield return ("tui.fake-cursor.extract-segments-keeps-apc-at-after-boundary", Sync(() =>
        {
            // utils.ts extractSegments: a zero-width APC marker at the start of "after" is kept; SGR codes still wait for "after".
            var line = "abc" + TuiBase.CursorMarker + "\u001b[1md\u001b[22mef";
            var segments = TextUtils.ExtractSegments(line, 0, 3, 17, true);
            Check(segments.After.StartsWith(TuiBase.CursorMarker, StringComparison.Ordinal), "marker kept: " + Escape(segments.After));
            Equal("def", Strip(segments.After), "after text");
            Equal(3, segments.AfterWidth, "after width");
            var composited = TuiBase.CompositeLine(line, "XXX", 0, 3, 20);
            Contains(composited, "XXX" + TuiBase.SegmentReset + TuiBase.CursorMarker, "cursor marker directly after the overlay");
        }));
        yield return ("tui.fake-cursor.alt-screen-selection-after-fake-cursor", Sync(() =>
        {
            // tui-alt-screen.test.ts: keeps the selection highlight after a fake cursor cell.
            var terminal = new FakeTerminal(20, 4);
            var tui = new TuiAltScreen(terminal, UiLoop.CreateManual());
            tui.AddChild(new Text("> " + TuiBase.RenderFakeCursor("a") + "bcd", 0, 0));
            tui.Start();
            tui.RenderNow();
            Check(terminal.Output.Contains("> \u001b[7ma\u001b[27mbcd", StringComparison.Ordinal), "fake cursor resolved in the alt screen: " + Escape(terminal.Output));
            terminal.Send("\u001b[<0;1;1M");
            terminal.Send("\u001b[<32;6;1M");
            terminal.Send("\u001b[<3;6;1m");
            tui.RenderNow();
            Check(terminal.Output.Contains("\u001b[27m\u001b[7mbcd", StringComparison.Ordinal),
                "selection inverse must be reapplied after the fake cursor ends: " + Escape(terminal.Output));
            tui.Stop();
        }));
        yield return ("tui.fake-cursor.settings-hardware-cursor-description", Sync(() =>
        {
            // settings-selector.ts: the setting now uses only the terminal cursor.
            var list = new SettingsSelectorComponent(new SettingsConfig { ShowHardwareCursor = false }, new SettingsCallbacks()).GetSettingsList();
            list.SelectItem("show-hardware-cursor");
            var lines = Strip(list.Render(100));
            Check(lines.Any(line => line.StartsWith("→ Show hardware cursor", StringComparison.Ordinal)), "row selected");
            Check(lines.Any(line => line == "  Use the terminal cursor instead of Pi's drawn cursor"), "description: " + string.Join("\n", lines));
        }));

        // ---------------------------------------------------------------- wheel input under a focused overlay
        yield return ("tui.overlay-wheel.unhandled-wheel-scrolls-transcript", Sync(() =>
        {
            // tui-alt-screen.test.ts (#10769): scrolls the transcript on wheel input a focused overlay does not handle.
            var terminal = new FakeTerminal(20, 6);
            var tui = new TuiAltScreen(terminal, UiLoop.CreateManual());
            tui.AddChild(new Text(string.Join("\n", Enumerable.Range(1, 12).Select(index => $"line {index}")), 0, 0));
            var overlay = new InputOverlay();
            tui.Start();
            tui.RenderNow();
            var topBefore = tui.ViewportTop;
            tui.ShowOverlay(overlay, new OverlayOptions { Anchor = OverlayAnchor.TopLeft, Width = 10 });
            tui.RenderNow();
            Equal(true, overlay.Focused, "overlay focused");
            terminal.Send("\u001b[<64;3;1M");
            tui.RenderNow();
            Check(tui.ViewportTop < topBefore, $"transcript scrolled up: {tui.ViewportTop} < {topBefore}");
            Seq([], overlay.Inputs, "overlay inputs");
            tui.Stop();
        }));
        yield return ("tui.overlay-wheel.viewport-keys-still-go-to-focused-overlay", Sync(() =>
        {
            // tui-alt-screen.test.ts: gives viewport keys (not the wheel) to a focused overlay.
            var terminal = new FakeTerminal(20, 6);
            var tui = new TuiAltScreen(terminal, UiLoop.CreateManual());
            tui.AddChild(new Text(string.Join("\n", Enumerable.Range(1, 12).Select(index => $"line {index}")), 0, 0));
            var overlay = new InputOverlay();
            tui.Start();
            tui.RenderNow();
            var topBefore = tui.ViewportTop;
            var handle = tui.ShowOverlay(overlay);
            tui.RenderNow();
            Equal(true, overlay.Focused, "overlay focused");
            string[] keys = ["\u001b[5~", "\u001b[6~", "\u001bOH", "\u001bOF"];
            foreach (var key in keys) terminal.Send(key);
            tui.RenderNow();
            Seq(keys, overlay.Inputs, "overlay inputs");
            Equal(topBefore, tui.ViewportTop, "viewport unchanged");
            handle.Hide();
            tui.RenderNow();
            terminal.Send("\u001b[5~");
            tui.RenderNow();
            Check(tui.ViewportTop < topBefore, "page up scrolls once the overlay is hidden");
            tui.Stop();
        }));

        // ---------------------------------------------------------------- fragmented terminal replies
        (string Header, string Reply)[] replies =
        [
            ("\u001b]4;1;", "\u001b]4;1;rgb:eaea/6c6c/6d6d\u0007"), // Ghostty's Ayu Light palette color 1.
            ("\u001b]10;", "\u001b]10;rgb:ffff/ffff/ffff\u001b\\"),
            ("\u001b]11;", "\u001b]11;rgb:0000/0000/0000\u0007"),
            ("\u001b]4;1;", "\u001b]4;1;rgb:6464/6464/4d4d\u0007"), // Another reported color-reply tail.
            ("\u001bP>|", "\u001bP>|Ghostty 1.3.1\u001b\\"),
            ("\u001b_Gi=1;", "\u001b_Gi=1;OK\u001b\\"),
            ("\u001b]7501;?", "\u001b]7501;?\u001b\\"),
        ];
        foreach (var (header, reply) in replies)
            yield return ($"tui.stdin-buffer.delayed-reply-intact-at-every-split {Escape(reply)}", Sync(() =>
            {
                for (var split = header.Length; split < reply.Length; split++)
                {
                    var (buffer, sequences, timers) = NewBuffer();
                    try
                    {
                        buffer.Process(reply[..split]);
                        timers.Tick(5000);
                        buffer.Process(reply[split..] + "x");
                        Check(!sequences.Any(sequence => Keys.Matches(sequence, "ctrl+g")), $"no ctrl+g at split {split}");
                        Seq([reply, "x"], sequences, $"split {split}");
                    }
                    finally { buffer.Destroy(); }
                }
            }));
        yield return ("tui.stdin-buffer.reply-buffered-across-expired-timeouts", Sync(() =>
        {
            var (buffer, sequences, timers) = NewBuffer(10);
            buffer.Process("\u001b]4;1;rgb:eaea/6c");
            timers.Tick(100);
            Seq([], sequences, "nothing after the first timeout");
            buffer.Process("6c/6d6d");
            timers.Tick(100);
            Seq([], sequences, "nothing after the second timeout");
            buffer.Process("\u0007");
            Seq([replies[0].Reply], sequences, "the whole reply");
        }));
        yield return ("tui.stdin-buffer.bare-string-introducers-time-out-as-alt-keys", Sync(() =>
        {
            var (buffer, sequences, timers) = NewBuffer(10);
            foreach (var prefix in new[] { "\u001b]", "\u001bP", "\u001b_" })
            {
                buffer.Process(prefix);
                timers.Tick(10);
            }
            Seq(["\u001b]", "\u001bP", "\u001b_"], sequences, "alt keys");
        }));
        foreach (var ambiguous in new[] { "\u001b]a", "\u001b]1", "\u001b]10", "\u001b]0;title", "\u001b]4;", "\u001b]4;a;", "\u001bPa", "\u001bP>", "\u001b_a", "\u001b_G", "\u001b_Gi=1" })
            yield return ($"tui.stdin-buffer.recovers-batched-legacy-alt-input {Escape(ambiguous)}", Sync(() =>
            {
                var (buffer, sequences, timers) = NewBuffer();
                try
                {
                    buffer.Process(ambiguous);
                    timers.Tick(20);
                    buffer.Process("x\r\u0003\u0004");
                    timers.Tick(49);
                    Seq([], sequences, "nothing before the sequence timeout");
                    timers.Tick(1);
                    Seq([ambiguous[..2], .. ambiguous[2..].Select(c => c.ToString()), "x", "\r", "\u0003", "\u0004"], sequences, "alt key and reparsed tail");
                    Equal("", buffer.Buffered, "nothing left buffered");
                    buffer.Process("y\r\u0003\u0004");
                    Seq(["y", "\r", "\u0003", "\u0004"], sequences.TakeLast(4), "later input flows");
                }
                finally { buffer.Destroy(); }
            }));
        yield return ("tui.stdin-buffer.reply-header-assembled-within-timeout", Sync(() =>
        {
            var (buffer, sequences, timers) = NewBuffer(10);
            buffer.Process("\u001b]1");
            timers.Tick(5);
            buffer.Process("0;");
            timers.Tick(5000);
            Seq([], sequences, "a recognized header waits for its terminator");
            buffer.Process("rgb:ffff/ffff/ffff\u001b\\");
            Seq([replies[1].Reply], sequences, "the whole reply");
        }));
        yield return ("tui.stdin-buffer.new-escape-aborts-unterminated-reply", Sync(() =>
        {
            var (buffer, sequences, timers) = NewBuffer(10);
            buffer.Process("\u001b]4;1;rgb:eaea/6c");
            timers.Tick(100);
            buffer.Process("\u001b");
            timers.Tick(100);
            buffer.Process("[Ahello");
            Seq(["\u001b[A", "h", "e", "l", "l", "o"], sequences, "the reply is dropped, later input survives");
        }));
        yield return ("tui.stdin-buffer.cancelled-reply-discarded", Sync(() =>
        {
            var (buffer, sequences, _) = NewBuffer(10);
            foreach (var cancel in new[] { "\u0018", "\u001a" }) buffer.Process($"\u001b]11;rgb:0000{cancel}x");
            Seq(["x", "x"], sequences, "CAN and SUB cancel only the reply");
        }));
        yield return ("tui.stdin-buffer.delayed-color-fragments-do-not-reach-editor", async () =>
        {
            // terminal-colors.test.ts: consumes delayed Ghostty color fragments without typing their tail or opening the editor.
            var terminal = new FakeTerminal(40, 10);
            var loop = UiLoop.CreateManual();
            var tui = new TuiMainScreen(terminal, loop, environment: _ => null);
            var component = new InputRecorder();
            tui.AddChild(component);
            tui.SetFocus(component);
            tui.Start();
            var (buffer, _, timers) = NewBuffer();
            buffer.Data += terminal.Send;
            try
            {
                var query = tui.QueryTerminalColors(1000);
                buffer.Process("\u001b]4;1;rgb:eaea/6c");
                timers.Tick(100);
                buffer.Process("6c/6d6d\u0007\u001b[?62;22c");
                var colors = await query.WaitAsync(TimeSpan.FromSeconds(10));
                Equal(null, colors.Foreground, "no foreground");
                Equal(null, colors.Background, "no background");
                Seq([], component.Inputs, "nothing reached the focused component");
                buffer.Process("\u0007");
                Seq(["\u0007"], component.Inputs, "a real Ctrl+G still reaches the editor");
            }
            finally { buffer.Destroy(); tui.Stop(); }
        });

        // ---------------------------------------------------------------- Herdr images
        yield return ("tui.terminal-image.herdr-kitty-images", Sync(() =>
        {
            // terminal-image.test.ts: enables hyperlinks and Kitty images for Herdr, before the leaked outer-terminal variables.
            WithImageEnvironment(new() { ["TERM_PROGRAM"] = "herdr", ["TERM"] = "xterm-256color", ["COLORTERM"] = "truecolor", ["KITTY_WINDOW_ID"] = "1" }, () =>
            {
                var caps = TerminalImage.DetectCapabilities();
                Equal(true, caps.Hyperlinks, "hyperlinks");
                Equal(ImageProtocol.Kitty, caps.Images, "Kitty images");
                Equal(true, caps.TrueColor, "truecolor");
            });
            WithImageEnvironment(new() { ["TERM_PROGRAM"] = "herdr", ["TERM"] = "xterm-256color" }, () =>
            {
                var caps = TerminalImage.DetectCapabilities();
                Equal(ImageProtocol.Kitty, caps.Images, "Kitty images without outer-terminal variables");
                Equal(false, caps.TrueColor, "no truecolor hint");
            });
        }));

        // ---------------------------------------------------------------- shifted-symbol keys
        yield return ("tui.keys.kitty-shifted-symbols-match-produced-character", Sync(() => WithKitty(true, () =>
        {
            // keys.test.ts: should match shifted symbols by their produced character.
            Equal(true, Keys.Matches("\u001b[61:43;2u", "+"), "+");
            Equal(true, Keys.Matches("\u001b[61:43;2u", "shift+="), "physical shift+=");
            Equal("+", Keys.Parse("\u001b[61:43;2u"), "parseKey +");
            Equal(true, Keys.Matches("\u001b[61:43;6u", "ctrl++"), "ctrl++");
            Equal("ctrl++", Keys.Parse("\u001b[61:43;6u"), "parseKey ctrl++");
            Equal(false, Keys.Matches("\u001b[61:43;6u", "+"), "Ctrl is not consumed");
            Equal(false, Keys.Matches("\u001b[61:43;2u", "="), "the unshifted key does not match without Shift");
            Equal(true, Keys.Matches("\u001b[61:43;4u", "alt++"), "alt++ (Shift+Alt reported)");
            Equal("alt++", Keys.Parse("\u001b[61:43;4u"), "parseKey alt++");
            Equal(true, Keys.Matches("\u001b[49:33;2u", "!"), "! from Shift+1");
            Equal("!", Keys.Parse("\u001b[49:33;2u"), "parseKey !");
            Equal("shift+a", Keys.Parse("\u001b[97:65;2u"), "shifted letters keep their identity");
        })));
        yield return ("tui.keys.modify-other-keys-shifted-symbols", Sync(() => WithKitty(false, () =>
        {
            // keys.test.ts: xterm modifyOtherKeys reports ctrl++ as Ctrl+Shift with the produced symbol.
            Equal(true, Keys.Matches("\u001b[27;5;47~", "ctrl+/"), "ctrl+/");
            Equal("ctrl+/", Keys.Parse("\u001b[27;5;47~"), "parseKey ctrl+/");
            Equal(true, Keys.Matches("\u001b[27;6;43~", "ctrl++"), "ctrl++");
            Equal("ctrl++", Keys.Parse("\u001b[27;6;43~"), "parseKey ctrl++");
            Equal(true, Keys.Matches("\u001b[27;2;43~", "+"), "+ from Shift with the produced symbol");
            Equal("+", Keys.Parse("\u001b[27;2;43~"), "parseKey +");
            Equal(true, Keys.Matches("\u001b[27;2;43~", "shift++"), "physical spelling still matches");
            Equal(false, Keys.Matches("\u001b[27;6;61~", "ctrl++"), "an unshifted = is not +");
        })));
        yield return ("tui.keys.plain-symbols-and-key-ids", Sync(() => WithKitty(false, () =>
        {
            // keys.test.ts: parseKey("+") is "+" and matchesKey("+", "+").
            Equal("+", Keys.Parse("+"), "parseKey +");
            Equal(true, Keys.Matches("+", "+"), "matchesKey +");
            Equal(true, Keys.Matches("\u001f", "ctrl+-"), "ctrl+- still parses");
            Equal(true, Keys.Matches("\u0003", "CTRL+C"), "case-insensitive modifiers");
            Equal(true, Keys.Matches("\u001b[1;6A", "shift+ctrl+up"), "modifier order is free");
            Equal(false, Keys.Matches("\u0003", "ctrl+ctrl+c"), "duplicate modifiers are invalid");
            Equal(false, Keys.Matches("a", "a+b"), "a + inside the key is invalid");
            Equal(false, Keys.Matches("+", "ctrl+"), "a missing key is invalid");
            Equal(false, Keys.Matches("\u0003", "c+ctrl"), "modifiers come first");
        })));

        yield return ("tui.keys.terminal-input-decoder-shifted-symbols", Sync(() =>
        {
            // PiSharp.Tui.Input's translation of keys.ts matchesKey (the native terminal frontend) follows the same 1.2.0 rules.
            PiSharp.Tui.TerminalInputEvent Decode(string raw, bool kitty = false) => new PiSharp.Tui.Input.TerminalInputDecoder(kittyProtocolActive: kitty).Feed(raw).Single();
            Equal(true, PiSharp.Tui.Input.TerminalInputDecoder.MatchesRawKey("\u001b[61:43;2u", "+", kittyActive: true), "kitty +");
            Equal(true, PiSharp.Tui.Input.TerminalInputDecoder.MatchesRawKey("\u001b[61:43;2u", "shift+=", kittyActive: true), "kitty physical shift+=");
            Equal(true, PiSharp.Tui.Input.TerminalInputDecoder.MatchesRawKey("\u001b[61:43;6u", "ctrl++", kittyActive: true), "kitty ctrl++");
            Equal(true, PiSharp.Tui.Input.TerminalInputDecoder.MatchesKey(Decode("\u001b[61:43;6u", true), "ctrl++"), "decoded kitty ctrl++");
            Equal(false, PiSharp.Tui.Input.TerminalInputDecoder.MatchesRawKey("\u001b[61:43;6u", "+", kittyActive: true), "Ctrl is not consumed");
            Equal(false, PiSharp.Tui.Input.TerminalInputDecoder.MatchesRawKey("\u001b[61:43;2u", "=", kittyActive: true), "= needs no Shift");
            Equal(true, PiSharp.Tui.Input.TerminalInputDecoder.MatchesRawKey("\u001b[27;6;43~", "ctrl++"), "modifyOtherKeys ctrl++");
            Equal(true, PiSharp.Tui.Input.TerminalInputDecoder.MatchesRawKey("\u001b[27;2;43~", "+"), "modifyOtherKeys +");
            Equal(true, PiSharp.Tui.Input.TerminalInputDecoder.MatchesRawKey("\u001b[27;5;47~", "ctrl+/"), "modifyOtherKeys ctrl+/");
            Equal(false, PiSharp.Tui.Input.TerminalInputDecoder.MatchesRawKey("\u001b[27;6;61~", "ctrl++"), "an unshifted = is not +");
            Equal(true, PiSharp.Tui.Input.TerminalInputDecoder.MatchesRawKey("+", "+"), "plain +");
            Equal(true, PiSharp.Tui.Input.TerminalInputDecoder.MatchesRawKey("\u001f", "ctrl+-"), "ctrl+-");
            Equal(true, PiSharp.Tui.Input.TerminalInputDecoder.MatchesRawKey("\u0003", "CTRL+C"), "case-insensitive");
            Equal(false, PiSharp.Tui.Input.TerminalInputDecoder.MatchesRawKey("\u0003", "ctrl+ctrl+c"), "duplicate modifier");
            Equal(false, PiSharp.Tui.Input.TerminalInputDecoder.MatchesRawKey("a", "a+b"), "+ inside the key");
            Equal(false, PiSharp.Tui.Input.TerminalInputDecoder.MatchesRawKey("\u0003", "c+ctrl"), "modifiers come first");
            Equal(false, PiSharp.Tui.Input.TerminalInputDecoder.MatchesKey(Decode("\u0003"), "ctrl+ctrl+c"), "decoded duplicate modifier");
            Equal(true, PiSharp.Tui.Input.TerminalInputDecoder.MatchesKey(Decode("\u0003"), "ctrl+c"), "decoded ctrl+c");
        }));

        // ---------------------------------------------------------------- compaction status colour
        yield return ("tui.compaction-indicator.color-fn", Sync(() =>
        {
            // status-indicator.ts: CompactionStatusIndicator takes an optional colorFn for both the spinner and the message.
            var color = T.GetThinkingBorderColor("high");
            var embedded = new CompactionStatusIndicator(Ui(), CompactionStatusReason.Manual, text => color(text));
            var standalone = new CompactionStatusIndicator(Ui(), CompactionStatusReason.Manual);
            try
            {
                var inBorder = embedded.RenderInBorder(80);
                Equal("⠋ Compacting context... (escape to cancel)", Strip(inBorder), "embedded label");
                Equal(3, inBorder.Split(T.GetFgAnsi("thinkingHigh")).Length, "spinner and message use the border colour");
                Check(!inBorder.Contains(T.GetFgAnsi("muted"), StringComparison.Ordinal), "no muted message");
                var line = standalone.Render(80)[1];
                Contains(line, T.GetFgAnsi("accent"), "standalone accent spinner");
                Contains(line, T.GetFgAnsi("muted"), "standalone muted message");
            }
            finally { embedded.Dispose(); standalone.Dispose(); }
        }));
        foreach (var embeddedCase in EmbeddedCompactionCases()) yield return embeddedCase;
    }

    /// <summary>interactive-mode.ts (#10809): the compaction status embedded in the default custom editor's border uses the editor
    /// border or thinking-level colour, like the working indicator, instead of the accent/muted standalone colours.</summary>
    internal static IEnumerable<(string Id, Func<Task> Run)> EmbeddedCompactionCases()
    {
        yield return ("tui.compaction-indicator.embedded-in-editor-uses-border-color", async () =>
        {
            await using var pi = new InteractiveHarness("compaction-color");
            File.WriteAllText(Path.Combine(pi.AgentDir, "settings.json"), """{"compaction":{"keepRecentTokens":1}}""");
            pi.Start(["--provider", "anthropic", "--model", "claude-sonnet-4-5", "--tui-mode", "regular"]);
            await pi.WaitFor("escape interrupt");
            await pi.Submit("first turn");
            await pi.WaitFor("Hello from the fake model.");
            await pi.Submit("second turn");
            await pi.WaitUntil(text => text.Split("Hello from the fake model.").Length > 2, "second answer");
            using var gate = new ManualResetEventSlim();
            pi.Respond = (_, _) => { gate.Wait(TimeSpan.FromSeconds(60)); return InteractiveHarness.AnthropicText("## Goal\nsummary"); };
            try
            {
                await pi.Submit("/compact");
                await pi.WaitFor("Compacting context...");
                var raw = pi.Terminal.RawOutput;
                var at = raw.LastIndexOf("Compacting context...", StringComparison.Ordinal);
                Check(at > 0, "compaction label drawn");
                var before = raw[..at];
                string[] thinkingTokens = ["thinkingOff", "thinkingMinimal", "thinkingLow", "thinkingMedium", "thinkingHigh", "thinkingXhigh", "thinkingMax"];
                Check(!before.EndsWith(T.GetFgAnsi("muted"), StringComparison.Ordinal), "not the standalone muted colour");
                Check(thinkingTokens.Any(token => before.EndsWith(T.GetFgAnsi(token), StringComparison.Ordinal)),
                    "the label uses a thinking-level border colour: " + Escape(before[Math.Max(0, before.Length - 40)..]));
            }
            finally { gate.Set(); }
        });
    }

    private static Theme T => Themes.Current;

    private static ITui Ui() => new TuiMainScreen(new FakeTerminal(), UiLoop.CreateManual(), environment: _ => null);
}
