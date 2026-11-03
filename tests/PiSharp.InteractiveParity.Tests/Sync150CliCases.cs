// Pi 1.5.0 sync, cli rows: interactive-mode.ts and the interactive components, as changed between v1.2.0 and v1.5.0
// (f9a26365568127461c47a07a37dcb42c140a3ac5). Expectations are authored from reading the v1.5.0 sources and tests; nothing is captured
// from an upstream run.
using System.Text.Json.Nodes;
using PiSharp.Cli.Interactive.Mode;
using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Tui.Pi;
using static Expect;

internal static class Sync150CliCases
{
    private static (TuiMainScreen Tui, UiLoop Loop) NewTui()
    {
        var loop = UiLoop.CreateManual();
        return (new TuiMainScreen(new NullTerminal(), loop, environment: _ => null), loop);
    }

    private sealed class NullTerminal : ITerminal
    {
        public void Start(Action<string> onInput, Action onResize) { }
        public void Stop() { }
        public Task DrainInputAsync(int maxMs = 1000, int idleMs = 50) => Task.CompletedTask;
        public void Write(string data) { }
        public int Columns => 80;
        public int Rows => 24;
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

    private static KeybindingsManager AppManager() => new(AppKeybindings.Definitions(_ => null), null);

    /// <summary>A TUI that only counts render requests.</summary>
    private sealed class CountingTui : ITui
    {
        public int RenderRequests { get; private set; }
        public UiLoop Loop { get; } = UiLoop.CreateManual();
        public void RequestRender(bool force = false) => RenderRequests++;
        public TuiMode Mode => TuiMode.Regular;
        public ITerminal Terminal => throw new NotSupportedException();
        public int FullRedraws => 0;
        public List<IComponent> Children { get; } = [];
        public void AddChild(IComponent component) => Children.Add(component);
        public void RemoveChild(IComponent component) => Children.Remove(component);
        public void Clear() => Children.Clear();
        public bool ShowHardwareCursor { get; set; }
        public bool ClearOnShrink { get; set; }
        public IComponent? FocusedComponent { get; private set; }
        public void SetFocus(IComponent? component) => FocusedComponent = component;
        public IOverlayHandle ShowOverlay(IComponent component, OverlayOptions? options = null) => throw new NotSupportedException();
        public void HideOverlay() { }
        public bool HasOverlay() => false;
        public void Start() { }
        public void Stop(bool preserveScreen = false) { }
        public void RenderNow(bool force = false) { }
        public Action AddInputListener(Func<string, TuiInputListenerResult?> listener) => () => { };
        public Action OnTerminalColorSchemeChange(Action<TerminalColorScheme> listener) => () => { };
        public void SetTerminalColorSchemeNotifications(bool enabled) { }
        public Task<TerminalColors> QueryTerminalColors(int timeoutMs, Action<TerminalColors>? onLateReply = null) => Task.FromResult(new TerminalColors(null, null, null));
        public Action? OnDebug { get; set; }
        public void Invalidate() { }
    }

    public static IEnumerable<(string Id, Func<Task> Run)> All()
    {
        // interactive-mode.ts showLoadedResources (f14813e96): skill diagnostics are mostly validation warnings for skills that still load,
        // so the startup heading is [Skill warnings], not [Skill conflicts].
        yield return ("sync150.startup-skill-diagnostics-are-skill-warnings", async () =>
        {
            await using var pi = new InteractiveHarness("sync150-skill-warnings");
            var skill = pi.Write(Path.Combine(pi.Cwd, ".pi", "skills", "bad-name", "SKILL.md"), "---\nname: Bad_Name\ndescription: Loads despite the name.\n---\n\n# Body\n");
            // The interactive host does not feed skill validation diagnostics to the mode yet; the case supplies the warning the source
            // reports for this skill.
            pi.Configure = context => context with
            {
                GetResourceDiagnostics = () => new ResourceDiagnostics([new("warning", "name contains invalid characters", skill)], [], [], [])
            };
            pi.Start("--provider", "anthropic", "--model", "claude-sonnet-4-5", "--tui-mode", "regular");
            var screen = await pi.WaitFor("[Skill warnings]");
            Check(!screen.Contains("[Skill conflicts]", StringComparison.Ordinal), "old heading gone: " + screen);
            Equal(0, await pi.Quit(), "exit code");
        });

        // external-editor.ts (6454d07ec, #11627): a failed editor run says why the prompt is unchanged; the extension editor shows the
        // message under its editor (and clears it after a later success).
        yield return ("sync150.external-editor-failures-say-why-the-prompt-is-unchanged", async () =>
        {
            async Task<ExternalEditorResult> Run(ExternalEditorProcessRunner runner) =>
                await ExternalEditor.EditInExternalEditorAsync(new ExternalEditorOptions("fake-editor --wait", "original"), runner, TextWriter.Null);
            var notStarted = await Run((_, _, _) => Task.FromResult<int?>(null));
            Equal("failed|Failed to launch external editor: fake-editor --wait", notStarted.Status + "|" + notStarted.Message, "not launched");
            var threw = await Run((_, _, _) => throw new InvalidOperationException("boom"));
            Equal("failed|Failed to launch external editor: fake-editor --wait", threw.Status + "|" + threw.Message, "runner exception");
            var exited = await Run((_, args, _) => { File.WriteAllText(args[^1], "changed"); return Task.FromResult<int?>(2); });
            Equal("failed|External editor exited with code 2; prompt unchanged|", exited.Status + "|" + exited.Message + "|" + exited.Content, "nonzero exit");
            var complete = await Run((_, args, _) => { File.WriteAllText(args[^1], "done\n"); return Task.FromResult<int?>(0); });
            Equal("complete|done|", complete.Status + "|" + complete.Content + "|" + complete.Message, "success");

            var (tui, loop) = NewTui();
            int? code = 2;
            var editor = new ExtensionEditorComponent(tui, AppManager(), "Edit", "keep", _ => { }, () => { }, null, "fake-editor", _ => null,
                (_, args, _) => { if (code == 0) File.WriteAllText(args[^1], "edited"); return Task.FromResult(code); }, TextWriter.Null);
            var before = Strip(editor.Render(60)).Select(line => line.TrimEnd()).ToList();
            async Task OpenAndWait(Func<bool> done)
            {
                loop.Post(() => editor.HandleInput("\u0007"));
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!done() && DateTime.UtcNow < deadline) { loop.RunPending(); await Task.Delay(5); }
            }
            string? Status() => Strip(editor.Render(60)).Select(line => line.TrimEnd()).FirstOrDefault(line => line.Contains("External editor", StringComparison.Ordinal));
            await OpenAndWait(() => Status() is not null);
            Equal(" External editor exited with code 2; prompt unchanged", Status(), "status row under the editor");
            Equal("keep", editor.GetEditor().GetText(), "prompt unchanged");
            var after = Strip(editor.Render(60)).Select(line => line.TrimEnd()).ToList();
            Equal(before.Count + 1, after.Count, "one row added");
            var promptRow = after.IndexOf("keep");
            Contains(after[promptRow + 2], "External editor exited with code 2", "status follows the editor box: " + string.Join(" / ", after));
            Check(editor.Render(60).Any(line => line.Contains(Themes.Current.Fg("warning", "External editor exited with code 2; prompt unchanged"), StringComparison.Ordinal)), "warning color");
            code = 0;
            await OpenAndWait(() => editor.GetEditor().GetText() == "edited");
            Equal(null, Status(), "a later success clears the status");
            Equal(before.Count, Strip(editor.Render(60)).Count, "status row renders nothing while empty");
        });

        // interactive-mode.ts openExternalEditor: the main editor reports the failure in the status line and keeps the prompt.
        yield return ("sync150.main-editor-reports-external-editor-failure", async () =>
        {
            await using var pi = new InteractiveHarness("sync150-external-editor");
            pi.Write(Path.Combine(pi.AgentDir, "settings.json"), "{\"externalEditor\":\"pisharp-no-such-editor-7f3a\"}");
            pi.Start("--provider", "anthropic", "--model", "claude-sonnet-4-5", "--tui-mode", "regular");
            await pi.WaitFor("escape interrupt");
            pi.Type("draft prompt");
            await pi.WaitFor("draft prompt");
            pi.Type("\u0007");
            // Windows runs the editor through cmd.exe (shell: true), which starts and exits 9009; elsewhere the launch itself fails.
            var screen = await pi.WaitUntil(text => text.Contains("Failed to launch external editor: pisharp-no-such-editor-7f3a", StringComparison.Ordinal)
                || text.Contains("External editor exited with code", StringComparison.Ordinal) && text.Contains("prompt unchanged", StringComparison.Ordinal), "failure status");
            Contains(screen, "draft prompt", "prompt kept");
            pi.Type("\u0003"); // Ctrl+C clears the editor, so Ctrl+D quits.
            await pi.WaitUntil(text => !text.Contains("draft prompt", StringComparison.Ordinal), "editor cleared");
            Equal(0, await pi.Quit(), "exit code");
        });

        // tool-execution.ts updateArgs (f715a15c1, #9265): streamed argument deltas are stored and a render requested; the next render
        // applies them, so the call renderer runs once per frame however many deltas arrived in between.
        yield return ("sync150.tool-args-apply-once-per-frame", Sync(() =>
        {
            var tui = new CountingTui();
            var calls = new List<string?>();
            var renderers = new ToolRenderers(RenderCall: (args, _, _) => { calls.Add(args?["path"]?.GetValue<string>()); return new Text("call " + args?["path"], 0, 0); });
            var component = new ToolExecutionComponent("custom", "call-1", new JsonObject(), null, renderers, tui, "/");
            var initial = calls.Count;
            var requests = tui.RenderRequests;
            foreach (var path in new[] { "a", "ab", "abc", "abcd" }) component.UpdateArgs(new JsonObject { ["path"] = path });
            Equal(initial, calls.Count, "no renderer call per delta");
            Equal(requests + 4, tui.RenderRequests, "each delta requests a render");
            var frame = string.Join("\n", Strip(component.Render(60)));
            Equal(initial + 1, calls.Count, "one renderer call at the next render");
            Equal("abcd", calls[^1], "the last arguments");
            Contains(frame, "call abcd", "rendered row");
            component.Render(60);
            Equal(initial + 1, calls.Count, "a render without new arguments keeps the display");
            component.SetArgsComplete();
            Equal(initial + 2, calls.Count, "other updates still apply at once");
        }));

        // assistant-message.test.ts "renders nothing for hidden thinking with an empty label" and "keeps label row and spacing for hidden
        // thinking with a label" (a6a7bac86, #11470): spacers are added lazily before the next visible block.
        yield return ("sync150.hidden-thinking-with-an-empty-label-renders-nothing", Sync(() =>
        {
            var thinkingOnly = new AssistantMessageComponent(Assistant([Block("thinking", "thinking", "reasoning"),
                new JsonObject { ["type"] = "toolCall", ["id"] = "tool-1", ["name"] = "read", ["arguments"] = new JsonObject { ["path"] = "file.txt" } }], "toolUse"),
                true, null, "");
            Equal(0, thinkingOnly.Render(80).Count, "thinking plus a tool call renders no rows");
            var withText = new AssistantMessageComponent(Assistant([Block("thinking", "thinking", "reasoning"), Block("text", "text", "answer")]), true, null, "");
            Equal("|answer", Trimmed(withText), "a blank row, then the text");
            var labelled = new AssistantMessageComponent(Assistant([Block("thinking", "thinking", "reasoning"), Block("text", "text", "answer")]), true);
            Equal("|Thinking...||answer", Trimmed(labelled), "label row and spacing kept");
            var whitespaceLabel = new AssistantMessageComponent(Assistant([Block("thinking", "thinking", "reasoning"), Block("text", "text", "answer")]), true, null, "   ");
            Equal("|answer", Trimmed(whitespaceLabel), "a label that trims to empty counts as empty");
            // Default rendering is unchanged: expanded thinking keeps its blank row before the text.
            var expanded = new AssistantMessageComponent(Assistant([Block("thinking", "thinking", "reasoning"), Block("text", "text", "answer")]));
            Equal("|reasoning||answer", Trimmed(expanded), "expanded thinking");
            // A skipped run still takes its run index: after the label returns, the second run is still the one the click override
            // names.
            var twoRuns = new AssistantMessageComponent(Assistant([Block("thinking", "thinking", "first"), Block("text", "text", "t1"),
                Block("thinking", "thinking", "second"), Block("text", "text", "t2")]), true, null, "");
            Equal("|t1|t2", Trimmed(twoRuns), "both hidden runs skipped");
            twoRuns.SetHiddenThinkingLabel("Thinking...");
            Equal("|Thinking...||t1|Thinking...||t2", Trimmed(twoRuns), "labels back");
            var secondLabelRow = Strip(twoRuns.Render(80)).FindLastIndex(line => line.Trim() == "Thinking...");
            twoRuns.HandleMouse(new TuiMouseEvent(TuiMouseEventType.Click, TuiMouseButton.Left, 2, secondLabelRow, 2, secondLabelRow, 80, twoRuns.Render(80).Count, ClickCount: 1));
            Equal("|Thinking...||t1|second||t2", Trimmed(twoRuns), "the second run expands");
        }));

        // assistant-message.test.ts "reuses its Markdown across streaming and its end, and renders like a new component after a theme
        // change" (0f21a3319): the code block is highlighted once, the transformer sees the stream end, and a theme change re-renders.
        yield return ("sync150.assistant-message-reuses-markdown-across-streaming-and-its-end", Sync(() =>
        {
            var streaming = new List<bool>();
            const string code = "```ts\nconst a = 1;\n```";
            JsonArray Content(string text) => [Block("thinking", "thinking", "Thinking with **bold** text"), Block("text", "text", text)];
            var component = new AssistantMessageComponent(null, false, null, "Thinking...", 1,
                [(markdown, context) => { streaming.Add(context.IsStreaming); return markdown; }]);
            var noGc = GC.TryStartNoGCRegion(64L * 1024 * 1024); // The Markdown keeps its rendered blocks weakly.
            long calls;
            try
            {
                var before = SyntaxHighlight.HighlightCalls;
                component.UpdateContent(Assistant(Content(code)), true);
                component.Render(80);
                component.UpdateContent(Assistant(Content(code + "\n\nThe end.")), false);
                component.Render(80);
                calls = SyntaxHighlight.HighlightCalls - before;
            }
            finally { if (noGc && System.Runtime.GCSettings.LatencyMode == System.Runtime.GCLatencyMode.NoGCRegion) GC.EndNoGCRegion(); }
            Equal(1L, calls, "the code block is highlighted once");
            Equal(false, streaming[^1], "a reused block sees the stream end");
            var message = Assistant(Content(code + "\n\nThe end."));
            Themes.InitTheme("light");
            component.Invalidate();
            Equal(string.Join("\n", new AssistantMessageComponent(message).Render(80)), string.Join("\n", component.Render(80)), "renders like a new component");
            // outputPad rebuilds the Markdown, which takes its padding at construction.
            component.SetOutputPad(3);
            Equal(string.Join("\n", new AssistantMessageComponent(message, outputPad: 3).Render(80)), string.Join("\n", component.Render(80)), "new padding");
        }));

        // code-highlighter.test.ts "reuses its last result until the code, the theme or the grammars change".
        yield return ("sync150.code-highlighter-reuses-until-code-theme-or-grammars-change", Sync(() =>
        {
            const string code = "const page = html`<div class=\"x\">ok</div>`;";
            var highlighter = new CodeHighlighter();
            var first = highlighter.Highlight(code, "javascript");
            Check(ReferenceEquals(first, highlighter.Highlight(code, "javascript")), "same code reuses the result");
            Check(!ReferenceEquals(first, highlighter.Highlight("const a = 1;", "javascript")), "other code highlights again");
            Check(!ReferenceEquals(highlighter.Highlight(code, "javascript"), highlighter.Highlight(code, "typescript")), "other language highlights again");
            Themes.InitTheme("light");
            Equal(string.Join("\n", Themes.HighlightCode(code, "javascript")), string.Join("\n", highlighter.Highlight(code, "javascript")), "a theme change highlights again");
            var light = highlighter.Highlight(code, "javascript");
            var grammars = SyntaxHighlight.GetHighlightLanguageCount();
            SyntaxHighlight.LoadAllLanguages();
            // Another case may have loaded every grammar already; the count only grows once per process.
            if (SyntaxHighlight.GetHighlightLanguageCount() > grammars)
                Check(string.Join("\n", highlighter.Highlight(code, "javascript")) != string.Join("\n", light), "new grammars (xml inside html``) highlight again");
            else Check(ReferenceEquals(light, highlighter.Highlight(code, "javascript")), "unchanged grammars reuse the result");
            // highlightCode's failure fallback colors lines like an unknown language does.
            Equal(Themes.Current.Fg("mdCodeBlock", "plain"), Themes.HighlightCode("plain", "no-such-language")[0], "unknown language");
        }));

        // code-highlighter.test.ts "codemode highlights the script once across progress, timer ticks, expanding and completion" (without
        // the timer ticks: the row's timer runs on the real clock here).
        yield return ("sync150.codemode-row-highlights-its-script-once", Sync(() =>
        {
            var tui = new CountingTui();
            var before = SyntaxHighlight.HighlightCalls;
            var row = new ToolExecutionComponent("codemode", "c", new JsonObject { ["code"] = "await tools.read({});" }, null, CodemodeRenderers.Renderers, tui, "/");
            row.SetArgsComplete();
            row.MarkExecutionStarted();
            row.UpdateResult(new JsonObject { ["content"] = new JsonArray(), ["details"] = new JsonObject { ["calls"] = new JsonArray() }, ["isError"] = false }, true);
            row.SetExpanded(true);
            row.UpdateResult(new JsonObject { ["content"] = new JsonArray(Block("text", "text", "done")), ["details"] = new JsonObject { ["calls"] = new JsonArray() }, ["isError"] = false });
            row.Render(80);
            Equal(1L, SyntaxHighlight.HighlightCalls - before, "one highlight");
        }));

        // code-highlighter.test.ts "write highlights unchanged first lines and a complete file only once".
        yield return ("sync150.write-row-highlights-unchanged-lines-and-a-complete-file-once", Sync(() =>
        {
            var tui = new CountingTui();
            var content = string.Join("\n", Enumerable.Range(0, 60).Select(i => $"const v{i} = {i};"));
            var row = new ToolExecutionComponent("write", "w", new JsonObject { ["path"] = "a.ts", ["content"] = content }, null, WriteRenderers.Renderers, tui, "/");
            row.UpdateArgs(new JsonObject { ["path"] = "a.ts", ["content"] = content + " //" });
            row.Render(80);
            var before = SyntaxHighlight.HighlightCalls;
            // Only the changed last line is highlighted again.
            row.UpdateArgs(new JsonObject { ["path"] = "a.ts", ["content"] = content + " // more" });
            row.Render(80);
            Equal(1L, SyntaxHighlight.HighlightCalls - before, "streamed delta");
            row.SetArgsComplete();
            row.SetExpanded(true);
            row.SetExpanded(false);
            row.Render(80);
            Equal(2L, SyntaxHighlight.HighlightCalls - before, "complete file highlighted once");
        }));

        // interactive-mode-model-scope.test.ts (7391dbb0f, #11508): fullscreen shows "Model scope: ..." in the header and logs nothing
        // to the shell; regular mode still logs it before the TUI starts; hidden startup details show it nowhere.
        yield return ("sync150.fullscreen-model-scope-notice-is-in-the-header", async () =>
        {
            const string Notice = "Model scope: claude-sonnet-4-5, claude-haiku-4-5 (Ctrl+P to cycle)";
            foreach (var (mode, quiet) in new[] { ("fullscreen", false), ("regular", false), ("fullscreen", true) })
            {
                await using var pi = new InteractiveHarness($"sync150-model-scope-{mode}{(quiet ? "-quiet" : "")}");
                pi.Write(Path.Combine(pi.AgentDir, "settings.json"), $"{{\"quietStartup\":{(quiet ? "true" : "false")}}}");
                var logged = new List<string>();
                pi.Configure = context => context with { ConsoleLog = line => { lock (logged) logged.Add(Strip(line)); } };
                pi.Start("--models", "claude-sonnet-4-5,claude-haiku-4-5", "--tui-mode", mode);
                var screen = await pi.WaitFor("claude-sonnet-4-5");
                if (mode == "fullscreen" && !quiet) screen = await pi.WaitFor(Notice);
                List<string> lines; lock (logged) lines = [.. logged];
                var inHeader = screen.Contains("Model scope:", StringComparison.Ordinal);
                var inLog = lines.Any(line => line.Contains("Model scope:", StringComparison.Ordinal));
                Equal(mode == "fullscreen" && !quiet, inHeader, $"{mode} quiet={quiet}: in the header\n{screen}");
                Equal(mode == "regular", inLog, $"{mode} quiet={quiet}: logged to the shell ({string.Join(" | ", lines)})");
                if (inLog) Check(lines.Contains(Notice), "logged text: " + string.Join(" | ", lines));
                Equal(0, await pi.Quit(), "exit code");
            }
        });

        // paths.test.ts "shortenHomePath replaces only a leading home directory" and "... ignores casing and keeps separators on
        // Windows" (this platform's half only: the source simulates the other platform), and the five display sites that now use it.
        yield return ("sync150.home-tilde-only-at-a-segment-boundary", Sync(() =>
        {
            static string Short(string path, string? home) => PiSharp.Cli.Pi.PiPaths.ShortenHomePath(path, home);
            var sep = Path.DirectorySeparatorChar;
            if (OperatingSystem.IsWindows())
            {
                Equal(@"~\project", Short(@"c:\users\me\project", @"C:\Users\Me"), "casing ignored");
                Equal(@"~\folder", Short(@"\\server\share\folder", @"\\server\share"), "UNC home");
                Equal(@"C:\Users\me2\project", Short(@"C:\Users\me2\project", @"C:\Users\me"), "a longer name is not inside home");
                Equal("~", Short(@"C:\Users\me\", @"C:\Users\me"), "home itself");
                Equal(@"~\Windows\x", Short(@"C:\Windows\x", @"C:\"), "a root home");
            }
            else
            {
                Equal("~", Short("/home/me", "/home/me"), "home itself");
                Equal("~/project", Short("/home/me/project", "/home/me"), "inside home");
                Equal("/home/me2/project", Short("/home/me2/project", "/home/me"), "a longer name is not inside home");
                Equal("/home/Me/project", Short("/home/Me/project", "/home/me"), "case-sensitive");
                Equal("~/etc/passwd", Short("/etc/passwd", "/"), "a root home");
            }
            Equal("home/me/project", Short("home/me/project", "/home/me"), "relative unchanged");
            Equal("/home/me/project", Short("/home/me/project", ""), "no home");
            Equal("", Short("", "/home/me"), "empty");

            var root = Path.Combine(Path.GetTempPath(), "pisharp-sync150-home");
            var home = Path.Combine(root, "al");
            var sibling = Path.Combine(root, "alice", "notes.md");
            var inside = Path.Combine(home, "notes.md");
            var previous = RenderUtils.HomeDirectory;
            RenderUtils.HomeDirectory = () => home;
            try
            {
                Equal(sibling, RenderUtils.ShortenPath(sibling), "tool rows keep a sibling home");
                Equal($"~{sep}notes.md", RenderUtils.ShortenPath(inside), "tool rows shorten inside home");
            }
            finally { RenderUtils.HomeDirectory = previous; }
            Equal($"{sibling.Replace('\\', '/')}/", ConfigSelectorComponent.FormatBaseDirForTests(sibling, home), "config selector keeps a sibling home");
            Equal("~/notes.md/", ConfigSelectorComponent.FormatBaseDirForTests(inside, home), "config selector shortens inside home");
            Equal("~/", ConfigSelectorComponent.FormatBaseDirForTests(home, home), "config selector home itself");
        }));

        // clipboard-command.test.ts "runs commands from the filesystem root" (#11592) and "runs commands outside pi's process group"
        // (#11179, skipped on Windows like the source).
        yield return ("sync150.clipboard-helpers-start-from-the-root-in-their-own-group", async () =>
        {
            static async Task<string?> Run(string command, params string[] args) =>
                await PiSharp.Cli.Interactive.Mode.Utilities.ClipboardCommand.RunClipboardCommand(command, args) is { } bytes
                    ? System.Text.Encoding.UTF8.GetString(bytes).Trim() : null;
            var root = Path.GetPathRoot(Path.GetFullPath("/"));
            Equal(root, PiSharp.Cli.Interactive.Mode.Utilities.ClipboardCommand.HelperWorkingDirectory(), "helper cwd");
            Equal(root, OperatingSystem.IsWindows() ? await Run("cmd.exe", "/d", "/c", "cd") : await Run("/bin/sh", "-c", "pwd"), "child cwd");
            Equal(null, await Run("pisharp-no-such-clipboard-tool"), "a command that cannot start is a failure");
            if (OperatingSystem.IsWindows()) return;
            var childPgid = await Run("sh", "-c", "ps -o pgid= -p $$");
            var ownPgid = await Run("ps", "-o", "pgid=", "-p", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Check(childPgid is { Length: > 0 } && childPgid.All(char.IsAsciiDigit), $"child pgid {childPgid}");
            Check(ownPgid is { Length: > 0 } && ownPgid.All(char.IsAsciiDigit), $"own pgid {ownPgid}");
            Check(childPgid != ownPgid, "the helper leads its own group");
            // A writer gets no output pipe; it still succeeds with an empty result.
            var written = await PiSharp.Cli.Interactive.Mode.Utilities.ClipboardCommand.RunClipboardCommand("sh", ["-c", "cat > /dev/null"], new("copied"));
            Equal(0, written?.Length ?? -1, "writer result");
        });
    }

    private static JsonObject Block(string type, string key, string value) => new() { ["type"] = type, [key] = value };

    private static JsonObject Assistant(JsonArray content, string stopReason = "stop") => new()
    {
        ["role"] = "assistant", ["content"] = content, ["api"] = "anthropic-messages", ["provider"] = "anthropic", ["model"] = "claude-sonnet-4-5",
        ["stopReason"] = stopReason, ["timestamp"] = 0,
    };

    private static string Trimmed(IComponent component) => string.Join("|", Strip(component.Render(80)).Select(line => line.Trim()));
}
