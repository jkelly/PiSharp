// Pi 1.5.0 sync, cli rows: interactive-mode.ts and the interactive components, as changed between v1.2.0 and v1.5.0
// (f9a26365568127461c47a07a37dcb42c140a3ac5). Expectations are authored from reading the v1.5.0 sources and tests; nothing is captured
// from an upstream run.
using System.Text.Json.Nodes;
using PiSharp.AI.Authentication.OAuth;
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

        // 3bae85274 (#7629): the coding-agent selectors handle tui.select.pageUp/pageDown through getSelectPageIndex, and capture
        // fullscreen viewport keys while focused. model-selector.test.ts "moves by one page with remapped selection page keys",
        // settings-selector.test.ts "pages a searchable submenu with selection page keys" and custom-editor-keybindings.test.ts
        // "pages an open autocomplete menu before pasting an image with the same key"; the auth, scoped models and user message
        // lists are authored the same way.
        yield return ("sync150.selectors-page-with-selection-page-keys", Sync(() =>
        {
            const string CtrlV = "\u0016", AltV = "\u001bv", Enter = "\r";
            var remapped = new KeybindingsManager(AppKeybindings.Definitions(_ => null), new Dictionary<string, IReadOnlyList<string>?>
            {
                ["tui.select.pageUp"] = ["alt+v"], ["tui.select.pageDown"] = ["ctrl+v"], ["app.clipboard.pasteImage"] = ["ctrl+v"]
            });
            var previous = KeybindingsManager.Global;
            KeybindingsManager.SetGlobal(remapped);
            try
            {
                var (tui, loop) = NewTui();
                static JsonObject Model(string id) => new()
                {
                    ["id"] = id, ["name"] = id, ["provider"] = "faux", ["api"] = "faux", ["reasoning"] = false, ["input"] = new JsonArray("text"),
                    ["contextWindow"] = 128000, ["maxTokens"] = 16384
                };
                var models = Enumerable.Range(0, 12).Select(index => Model($"model-{index:00}")).ToList();

                JsonObject? chosen = null;
                var modelSelector = new ModelSelectorComponent(tui, null, new FixedModels(models), [], model => chosen = model, () => { });
                Equal(true, ((IComponent)modelSelector).CapturesViewportInput, "model selector captures viewport keys");
                modelSelector.HandleInput(CtrlV);
                modelSelector.HandleInput(CtrlV);
                modelSelector.HandleInput(AltV);
                modelSelector.HandleInput(Enter);
                Equal("model-01", chosen?["id"]?.GetValue<string>(), "model selector: two pages down (clamped), one page up");
                modelSelector.Dispose();
                loop.RunPending();

                var providers = Enumerable.Range(0, 10).Select(index => new AuthSelectorProvider($"p{index}", $"Provider {index}", "api_key",
                    new AuthMethodInfo($"Provider {index} API key"))).ToList();
                string? provider = null;
                var auth = new OAuthSelectorComponent("login", providers, (id, _) => provider = id, () => { });
                Equal(true, ((IComponent)auth).CapturesViewportInput, "auth selector captures viewport keys");
                auth.HandleInput(CtrlV);
                auth.HandleInput(Enter);
                Equal("p8", provider, "auth selector pages by its eight visible rows");

                IReadOnlyList<string>? enabled = null;
                var scoped = new ScopedModelsSelectorComponent(new ModelsConfig(models, ["faux/model-00"]), new ModelsCallbacks(ids => enabled = ids, _ => { }, () => { }));
                Equal(true, ((IComponent)scoped).CapturesViewportInput, "scoped models selector captures viewport keys");
                scoped.HandleInput(CtrlV);
                scoped.HandleInput(Enter);
                Check(enabled?.Contains("faux/model-08") == true, "scoped models selector pages by eight: " + string.Join(", ", enabled ?? []));

                var messages = new UserMessageList(Enumerable.Range(0, 15).Select(index => new UserMessageItem($"m{index}", $"message {index}")).ToList());
                string? message = null;
                messages.OnSelect = id => message = id;
                Equal(true, ((IComponent)messages).CapturesViewportInput, "user message list captures viewport keys");
                messages.HandleInput(AltV);
                messages.HandleInput(Enter);
                Equal("m4", message, "user message list pages up by ten from the newest");

                // The editor pages its open autocomplete menu before the paste-image action bound to the same key.
                var editor = new CustomEditor(tui, Themes.GetEditorTheme(), remapped);
                var pastes = 0;
                editor.OnPasteImage = () => pastes++;
                var submitted = "";
                editor.OnSubmit = text => submitted = text;
                editor.SetAutocompleteProvider(new CommandProvider());
                editor.HandleInput("/");
                Check(editor.IsShowingAutocomplete(), "autocomplete open");
                editor.HandleInput(CtrlV);
                editor.HandleInput(Enter);
                Equal(0, pastes, "no paste while the menu is open");
                Equal("/command-5", submitted, "the menu paged by its visible rows");
                editor.HandleInput(CtrlV);
                Equal(1, pastes, "a closed menu pastes again");
            }
            finally { KeybindingsManager.SetGlobal(previous); }

            // A searchable submenu passes the default PageDown to its list.
            string? theme = null;
            var submenu = new SelectSubmenu("Theme", "", [.. Enumerable.Range(0, 20).Select(index => new SelectItem($"theme-{index}", $"theme-{index}"))],
                "theme-0", value => theme = value, () => { }, null, new SelectSubmenuOptions(Searchable: true));
            submenu.HandleInput("\u001b[6~");
            submenu.HandleInput(Enter);
            Equal("theme-10", theme, "searchable submenu pages");

            // Session and tree selectors capture viewport keys except while their rename or label input is open.
            Equal(true, typeof(SessionSelectorComponent).GetProperty("CapturesViewportInput") is not null, "session selector declares the capture");
            Equal(true, typeof(TreeSelectorComponent).GetProperty("CapturesViewportInput") is not null, "tree selector declares the capture");
        }));

        // model-search.test.ts "getDistinctModelName" (#5916), and the /model and /scoped-models rows that show such a name.
        yield return ("sync150.model-rows-show-distinct-names", Sync(() =>
        {
            Equal(null, ModelSearch.GetDistinctModelName("claude-sonnet-4-5", "Claude Sonnet 4.5"), "restates the id");
            Equal(null, ModelSearch.GetDistinctModelName("openai/gpt-oss-120b", "OpenAI: gpt-oss-120b"), "namespace and separators");
            Equal(null, ModelSearch.GetDistinctModelName("eu.anthropic.claude-sonnet-4-5-20250929-v1:0", "Claude Sonnet 4.5 (EU)"), "dropped suffix");
            Equal(null, ModelSearch.GetDistinctModelName("gpt-5", ""), "empty name");
            Equal(null, ModelSearch.GetDistinctModelName("gpt-5", null), "no name");
            const string Renamed = "MiniMax M3 [rasmus@openrouter, parasail]";
            Equal(Renamed, ModelSearch.GetDistinctModelName("minimax/minimax-m3", Renamed), "renamed override");
            Equal("Nano Banana 2 Lite", ModelSearch.GetDistinctModelName("gemini-3.1-flash-lite-image", "Nano Banana 2 Lite"), "marketing name");
            Equal("Mistral Small 4", ModelSearch.GetDistinctModelName("mistral-small-2603", "Mistral Small 4"), "single characters match whole tokens");
            Equal(null, ModelSearch.GetDistinctModelName("modèle-größe", "Modèle Größe"), "letters outside ASCII are word characters");

            var (tui, loop) = NewTui();
            JsonObject Model(string id, string name) => new()
            {
                ["id"] = id, ["name"] = name, ["provider"] = "faux", ["api"] = "faux", ["reasoning"] = false, ["input"] = new JsonArray("text"),
                ["contextWindow"] = 128000, ["maxTokens"] = 16384
            };
            var models = new List<JsonObject> { Model("minimax/minimax-m3", Renamed), Model("claude-sonnet-4-5", "Claude Sonnet 4.5") };
            var selector = new ModelSelectorComponent(tui, null, new FixedModels(models), [], _ => { }, () => { });
            var rows = Strip(selector.Render(120));
            Check(rows.Any(row => row.Contains($"minimax/minimax-m3 ({Renamed}) [faux]", StringComparison.Ordinal)), "model row shows the name:\n" + string.Join("\n", rows));
            Check(rows.Any(row => row.TrimEnd().EndsWith("claude-sonnet-4-5 [faux]", StringComparison.Ordinal)), "restated name hidden:\n" + string.Join("\n", rows));
            selector.Dispose();
            loop.RunPending();
            var scoped = new ScopedModelsSelectorComponent(new ModelsConfig(models, null), new ModelsCallbacks(_ => { }, _ => { }, () => { }));
            var scopedRows = Strip(scoped.Render(140));
            Check(scopedRows.Any(row => row.Contains($"minimax/minimax-m3 ({Renamed}) [faux]", StringComparison.Ordinal)), "scoped row shows the name:\n" + string.Join("\n", scopedRows));
            Check(scopedRows.Any(row => row.TrimEnd().EndsWith("claude-sonnet-4-5 [faux]", StringComparison.Ordinal)), "scoped restated name hidden");
        }));

        // session-selector-search.test.ts (8c03fef4d): fields match separately, in tiers (name text, then id/message/cwd text, then
        // scattered name or cwd characters); ids and messages match only as exact substrings.
        yield return ("sync150.resume-search-matches-fields-in-tiers", Sync(() =>
        {
            static PiSharp.Cli.Pi.PiSessionInfo Session(string id, string modified, string text, string? name = null) =>
                new($"/tmp/{id}.jsonl", id, "", name, null, DateTimeOffset.UnixEpoch, DateTimeOffset.Parse(modified, System.Globalization.CultureInfo.InvariantCulture),
                    1, "(no messages)", text);
            static string Ids(IEnumerable<PiSharp.Cli.Pi.PiSessionInfo> sessions) => string.Join(",", sessions.Select(session => session.Id));
            static List<PiSharp.Cli.Pi.PiSessionInfo> Find(IReadOnlyList<PiSharp.Cli.Pi.PiSessionInfo> sessions, string query) =>
                SessionSelectorSearch.FilterAndSortSessions(sessions, query, SortMode.Relevance);

            // #11220 "ignores the position of a match in message text"
            var filler = string.Concat(Enumerable.Repeat("lorem ipsum dolor sit amet ", 400));
            Equal("deep,early", Ids(Find([Session("early", "2026-01-01T00:00:00.000Z", $"opus {filler}"),
                Session("deep", "2026-01-02T00:00:00.000Z", $"{filler} we discussed the opus model"),
                Session("scattered", "2026-01-03T00:00:00.000Z", filler)], "opus")), "message position ignored, scattered letters rejected");

            // #11024 "does not match scattered characters in session ids"
            var ids = new[]
            {
                Session("019e9024-aaaa-7bbb-8ccc-dddddddddddd", "2026-01-03T00:00:00.000Z", "unrelated"),
                Session("01a1ffff-aaaa-7bbb-8ccc-dddddddddddd", "2026-01-01T00:00:00.000Z", "unrelated", "#1024 TDD bare env"),
            };
            Equal("#1024 TDD bare env", string.Join(",", Find(ids, "1024").Select(session => session.Name)), "id scatter rejected");
            Equal("019e9024-aaaa-7bbb-8ccc-dddddddddddd", Ids(Find(ids, "9024-aa")), "id substring");

            // "ranks name text, then message text, then scattered name matches"
            Equal("name,message,scattered-name", Ids(Find([
                Session("scattered-name", "2026-01-04T00:00:00.000Z", "unrelated", "c-a-c-h-e notes"),
                Session("message", "2026-01-03T00:00:00.000Z", "A discussion mentioning the cache.", "Other work"),
                Session("name", "2026-01-01T00:00:00.000Z", "Summary", "Cache work")], "cache")), "tiers");

            // "matches each token within a single field"
            var split = new[] { Session("split", "2026-01-01T00:00:00.000Z", "search ranking", "Fuzzy") };
            Equal("split", Ids(Find(split, "fuzzy ranking")), "tokens may match different fields");
            Equal("", Ids(Find(split, "zzysea")), "a token never spans fields");
            Equal("", Ids(Find(split, "re:fuzzy search")), "a regex never spans fields");

            // "ranks regex name matches first"
            Equal("name,message", Ids(Find([Session("message", "2026-01-02T00:00:00.000Z", "brave"),
                Session("name", "2026-01-01T00:00:00.000Z", "xxxx", "Brave")], @"re:\bbrave\b")), "regex tiers");
        }));

        // login-dialog-device-code.test.ts "device-code login" (#9282): the convenience actions need an explicit confirm, run once,
        // follow the provider's overrides, are cleared by the next view, and never interrupt login when they fail.
        yield return ("sync150.login-dialog-device-code-actions", async () =>
        {
            const string Code = "ABCD-EFGH", Verification = "https://example.com/device", Enter = "\r", CtrlX = "\u0018", Escape = "\u001b";
            var (tui, loop) = NewTui();
            var opened = new List<string>();
            var copied = new List<string>();
            (LoginDialogComponent Dialog, List<string?> Completed) Create(Action<string>? open = null, Func<string, Task>? copy = null)
            {
                var completed = new List<string?>();
                var dialog = new LoginDialogComponent(tui, "custom-provider", (success, message) => completed.Add($"{success}:{message}"))
                {
                    OpenBrowser = open ?? opened.Add,
                    CopyToClipboard = copy ?? (text => { copied.Add(text); return Task.CompletedTask; })
                };
                dialog.Focused = true;
                return (dialog, completed);
            }
            string Render(LoginDialogComponent dialog) => string.Join("\n", Strip(dialog.Render(120)));
            void Reset() { opened.Clear(); copied.Clear(); }

            // "opens the verification page and copies the code once after confirmation"
            var (first, _) = Create();
            var domain = first.ShowPrompt("GitHub Enterprise URL/domain (blank for github.com)");
            first.HandleInput(Enter);
            Equal("", await domain.WaitAsync(TimeSpan.FromSeconds(5)), "prompt answered");
            first.ShowDeviceCode(Code, Verification);
            first.ShowWaiting("Waiting for authentication...");
            Contains(Render(first), "to open browser and copy code", "hint row");
            Equal(0, opened.Count + copied.Count, "nothing before confirmation");
            first.HandleInput(Enter);
            first.HandleInput(Enter);
            for (var i = 0; i < 100 && !Render(first).Contains("Code copied to clipboard", StringComparison.Ordinal); i++) { await Task.Delay(10); loop.RunPending(); }
            Equal(Verification, string.Join(",", opened), "opened once");
            Equal(Code, string.Join(",", copied), "copied once");
            var screen = Render(first);
            Contains(screen, "Code copied to clipboard", "status in the hint row");
            Contains(screen, $"Enter code: {Code}", "code still shown");
            Contains(screen, "Waiting for authentication...", "waiting still shown");
            Check(!screen.Contains("to open browser and copy code", StringComparison.Ordinal), "hint replaced");

            // "honors browser / clipboard provider overrides"
            foreach (var (browser, clipboard) in new[] { (false, false), (true, false), (false, true) })
            {
                Reset();
                var (dialog, _) = Create();
                dialog.ShowDeviceCode(Code, Verification, browser, clipboard);
                var label = browser ? "to open browser" : clipboard ? "to copy code" : null;
                if (label is null) Check(!Render(dialog).Contains("(Enter", StringComparison.Ordinal), "no hint without actions");
                else Contains(Render(dialog), label, $"hint {browser}/{clipboard}");
                dialog.HandleInput(Enter);
                loop.RunPending();
                Equal(browser ? 1 : 0, opened.Count, $"browser {browser}/{clipboard}");
                Equal(clipboard ? 1 : 0, copied.Count, $"clipboard {browser}/{clipboard}");
                Equal(false, dialog.Signal.IsCancellationRequested, "not cancelled");
            }

            // "clears the previous auth URL copy action when showing a device code"
            Reset();
            var (auth, _) = Create();
            auth.ShowAuth("https://example.com/authorize");
            opened.Clear();
            auth.ShowDeviceCode(Code, Verification);
            auth.HandleInput(CtrlX);
            Equal(0, opened.Count + copied.Count, "no URL copy after the device code");
            auth.HandleInput(Enter);
            Equal(Code, string.Join(",", copied), "the code is copied");

            // "clears device actions when switching to browser authentication"
            Reset();
            var (toBrowser, _) = Create();
            toBrowser.ShowDeviceCode(Code, Verification);
            toBrowser.ShowAuth("https://example.com/authorize");
            opened.Clear();
            toBrowser.HandleInput(Enter);
            Equal(0, opened.Count + copied.Count, "no device action after showAuth");
            toBrowser.HandleInput(CtrlX);
            Equal("https://example.com/authorize", string.Join(",", copied), "URL copy is back");

            // "clears both action types when showing details"
            Reset();
            var (details, _) = Create();
            details.ShowAuth("https://example.com/authorize");
            details.ShowDetails(["Choose a login method"]);
            details.HandleInput(CtrlX);
            Equal(0, copied.Count, "no URL copy after details");
            details.ShowDeviceCode(Code, Verification);
            details.ShowDetails(["Choose a login method"]);
            opened.Clear();
            details.HandleInput(Enter);
            Equal(0, opened.Count + copied.Count, "no device action after details");

            // "cancels without opening a browser or changing the clipboard"
            Reset();
            var (cancelled, completedOnCancel) = Create();
            cancelled.ShowDeviceCode(Code, Verification);
            cancelled.HandleInput(Escape);
            cancelled.HandleInput(Enter);
            Equal(true, cancelled.Signal.IsCancellationRequested, "cancelled");
            Equal("False:Login cancelled", string.Join(",", completedOnCancel), "completed once as cancelled");
            Equal(0, opened.Count + copied.Count, "no actions after cancel");

            // "keeps manual login available when browser and clipboard access fail"
            Reset();
            var attempts = new List<string>();
            var (failing, completedOnFailure) = Create(url => { attempts.Add(url); throw new InvalidOperationException("Browser unavailable"); },
                text => { attempts.Add(text); return Task.FromException(new InvalidOperationException("Clipboard unavailable")); });
            failing.ShowDeviceCode(Code, Verification);
            failing.ShowWaiting("Waiting for authentication...");
            failing.HandleInput(Enter);
            for (var i = 0; i < 100 && !Render(failing).Contains("Copy code manually", StringComparison.Ordinal); i++) { await Task.Delay(10); loop.RunPending(); }
            Equal($"{Code},{Verification}", string.Join(",", attempts), "both attempted once");
            screen = Render(failing);
            Contains(screen, Verification, "URL still shown");
            Contains(screen, $"Enter code: {Code}", "code still shown");
            Contains(screen, "Copy code manually", "manual copy hint");
            Contains(screen, "Waiting for authentication...", "still waiting");
            Equal(false, failing.Signal.IsCancellationRequested, "not cancelled");
            Equal(0, completedOnFailure.Count, "not completed");
        });

        // login-dialog-device-code.test.ts "login URLs" (89c921d8b): only http(s) URLs are opened or linked, and control characters in
        // sign-in, device-code and info URLs never reach the terminal.
        yield return ("sync150.login-dialog-opens-and-links-only-web-urls", Sync(() =>
        {
            var (tui, _) = NewTui();
            var opened = new List<string>();
            LoginDialogComponent Create() => new(tui, "custom-provider", (_, _) => { }) { OpenBrowser = opened.Add, CopyToClipboard = _ => Task.CompletedTask };
            string Raw(LoginDialogComponent dialog) => string.Join("\n", dialog.Render(120));

            var dialog = Create();
            dialog.ShowAuth("file:///Users/victim/Downloads/payload.app");
            Equal(0, opened.Count, "a file URL is not opened");
            dialog.ShowAuth("https://example.com/authorize");
            Equal("https://example.com/authorize", string.Join(",", opened), "a web URL is opened");

            const string Injection = "\u0007\u001b]52;c;Zm9v\u0007";
            dialog = Create();
            dialog.ShowDeviceCode("ABCD-EFGH", "https://example.com/device" + Injection);
            dialog.ShowInfo("Docs", [new AuthInfoLink("https://example.com/help" + Injection, "Help")]);
            Check(!Raw(dialog).Contains("\u001b]52", StringComparison.Ordinal), "web URLs carry no escape sequences:\n" + Raw(dialog));
            var shown = string.Join("\n", Strip(dialog.Render(120)));
            Contains(shown, "https://example.com/device%07%1B", "device URL percent-encoded");
            Contains(shown, "Help: https://example.com/help%07%1B", "info link percent-encoded");
            Equal("https://example.com/device%07%1B%5D52;c;Zm9v", LoginDialogComponent.WebUrl("https://example.com/device" + Injection)?.Replace("]", "%5D"),
                "normalized like the URL serializer (a ] may stay literal)");

            var injected = "x" + Injection;
            opened.Clear();
            dialog = Create();
            dialog.ShowAuth($"file:///tmp/{injected}");
            Check(!Raw(dialog).Contains("\u001b]52", StringComparison.Ordinal), "a file URL is shown without escape sequences");
            dialog.ShowDeviceCode("ABCD-EFGH", injected);
            Check(!Raw(dialog).Contains("\u001b]52", StringComparison.Ordinal), "a non-URL device link is shown without escape sequences");
            Contains(string.Join("\n", Strip(dialog.Render(120))), "x ]52;c;Zm9v", "control characters replaced");
            dialog.ShowInfo("Docs", [new AuthInfoLink(injected, "Help")]);
            Check(!Raw(dialog).Contains("\u001b]52", StringComparison.Ordinal), "a non-URL info link is shown without escape sequences");
            Contains(string.Join("\n", Strip(dialog.Render(120))), "Help: x ]52;c;Zm9v", "info control characters replaced");
            Equal(0, opened.Count, "nothing opened");
            Equal(null, LoginDialogComponent.WebUrl("javascript:alert(1)"), "scripts are not web URLs");
            Equal("https://example.com/", LoginDialogComponent.WebUrl("  HTTPS://Example.COM  "), "trimmed and normalized");
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

        // agent-session.ts exportToHtml with export-html/tool-renderer.ts: the HTML export pre-renders the tools the page template does
        // not draw (grep, a custom tool) with the transcript's renderers, the theme and the configured keys (keyText and keyHint read
        // the configured keybindings); template tools (bash) are left to the page. Authored from the v1.5.0 sources.
        yield return ("sync150.html-export-pre-renders-tools-with-transcript-renderers", Sync(() =>
        {
            Themes.InitTheme("dark");
            var previous = KeybindingsManager.Global;
            KeybindingsManager.SetGlobal(new KeybindingsManager(AppKeybindings.Definitions(_ => null),
                new Dictionary<string, IReadOnlyList<string>?> { ["app.tools.expand"] = ["ctrl+y"] }));
            try
            {
                var lookup = new ToolRenderers(
                    RenderCall: (args, _, context) => new Text($"lookup {(string?)args?["q"]} ({KeybindingHints.KeyText("app.tools.expand")} to expand) in {context.Cwd}", 0, 0),
                    RenderResult: (result, options, _, context) => new Text(
                        $"{(options.Expanded ? "all" : "some")} of {(string?)result["content"]?[0]?["text"]} {(string?)result["details"]?["k"]} {(string?)context.Args?["q"]}", 0, 0));
                var cwd = Path.Combine(Path.GetTempPath(), "pisharp-export-tools");
                var renderer = ExportToolHtmlRenderer.Create(name => name == "lookup" ? lookup : BuiltInToolRenderers.Resolve(name), Themes.Current, cwd);
                var root = Directory.CreateTempSubdirectory("pisharp-export-tools-").FullName;
                try
                {
                    var session = Path.Combine(root, "session.jsonl");
                    static string Line(JsonObject value) => value.ToJsonString();
                    File.WriteAllText(session, string.Join("\n",
                        Line(new() { ["type"] = "session", ["version"] = 3, ["id"] = "export-tools", ["timestamp"] = "1970-01-01T00:00:00.000Z", ["cwd"] = root }),
                        Line(new()
                        {
                            ["type"] = "message", ["id"] = "a1", ["parentId"] = null, ["timestamp"] = "1970-01-01T00:00:01.000Z",
                            ["message"] = Assistant(new JsonArray(
                                new JsonObject { ["type"] = "toolCall", ["id"] = "call_grep", ["name"] = "grep", ["arguments"] = new JsonObject { ["pattern"] = "needle", ["path"] = "src" } },
                                new JsonObject { ["type"] = "toolCall", ["id"] = "call_lookup", ["name"] = "lookup", ["arguments"] = new JsonObject { ["q"] = "term" } },
                                new JsonObject { ["type"] = "toolCall", ["id"] = "call_bash", ["name"] = "bash", ["arguments"] = new JsonObject { ["command"] = "ls" } }), "toolUse")
                        }),
                        Line(new()
                        {
                            ["type"] = "message", ["id"] = "r1", ["parentId"] = "a1", ["timestamp"] = "1970-01-01T00:00:02.000Z",
                            ["message"] = new JsonObject
                            {
                                ["role"] = "toolResult", ["toolCallId"] = "call_lookup", ["toolName"] = "lookup", ["isError"] = false, ["timestamp"] = 2,
                                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "rows" }), ["details"] = new JsonObject { ["k"] = "v" }
                            }
                        })) + "\n");
                    var data = PiSharp.CodingAgent.Export.SessionHtmlExport.BuildSessionData(PiSharp.CodingAgent.Export.SessionExportSource.Open(session), null, renderer);
                    var rendered = JsonNode.Parse(PiSharp.CodingAgent.Export.Js.Stringify(data["renderedTools"]))!.AsObject();
                    Equal("call_grep,call_lookup", string.Join(",", rendered.Select(pair => pair.Key)), "pre-rendered tools");
                    var grep = (string?)rendered["call_grep"]?["callHtml"] ?? "";
                    Contains(grep, "grep", "grep call");
                    Contains(grep, "needle", "grep pattern");
                    Contains((string?)rendered["call_lookup"]?["callHtml"] ?? "", "lookup term (ctrl+y to expand) in " + System.Net.WebUtility.HtmlEncode(cwd), "custom call");
                    Contains((string?)rendered["call_lookup"]?["resultHtmlCollapsed"] ?? "", "some of rows v term", "collapsed result");
                    Contains((string?)rendered["call_lookup"]?["resultHtmlExpanded"] ?? "", "all of rows v term", "expanded result");
                }
                finally { Directory.Delete(root, true); }
            }
            finally { KeybindingsManager.SetGlobal(previous); }
        }));

        // settings-selector.ts "share-destination" (after Transport) and settings-manager.ts getSessionShareDestination /
        // setSessionShareDestination: the choice cycles auto, gist, radius; anything else in settings.json reads as auto.
        yield return ("sync150.settings-session-share-destination", Sync(() =>
        {
            var changes = new List<string>();
            var list = new SettingsSelectorComponent(new SettingsConfig { SessionShareDestination = "auto" },
                new SettingsCallbacks { OnSessionShareDestinationChange = changes.Add }).GetSettingsList();
            list.SelectItem("share-destination");
            Contains(string.Join("\n", Strip(list.Render(140))), "Session share destination", "item row");
            Contains(string.Join("\n", Strip(list.Render(140))),
                "Where to upload sessions with /share (auto prefers Radius when authenticated, otherwise GitHub Gist)", "item description");
            const string Enter = "\r";
            list.HandleInput(Enter); list.HandleInput(Enter); list.HandleInput(Enter);
            Equal("gist,radius,auto", string.Join(",", changes), "cycled values");

            var settings = new InteractiveSettings(new JsonObject { ["sessionShareDestination"] = "github" });
            Equal("auto", settings.SessionShareDestination, "unknown value");
            settings.SetSessionShareDestination("radius");
            Equal("radius", settings.SessionShareDestination, "saved value");
            Equal("auto", new InteractiveSettings(new JsonObject()).SessionShareDestination, "default");
        }));
    }

    /// <summary>A model runtime with a fixed available snapshot and no refresh errors.</summary>
    private sealed class FixedModels(IReadOnlyList<JsonObject> models) : IModelSelectorRuntime
    {
        public IReadOnlyList<JsonObject> GetAvailableSnapshot() => models;
        public JsonObject? GetModel(string provider, string modelId) =>
            models.FirstOrDefault(model => (string?)model["provider"] == provider && (string?)model["id"] == modelId);
        public string? GetError() => null;
        public Task<ModelsRefreshResult> RefreshAsync(CancellationToken cancellationToken) => Task.FromResult(new ModelsRefreshResult(false, []));
    }

    /// <summary>Twelve slash commands, applied by replacing the line (custom-editor-keybindings.test.ts).</summary>
    private sealed class CommandProvider : IAutocompleteProvider
    {
        public Task<AutocompleteSuggestions?> GetSuggestionsAsync(IReadOnlyList<string> lines, int cursorLine, int cursorCol, bool force, CancellationToken cancellationToken) =>
            Task.FromResult<AutocompleteSuggestions?>(new([.. Enumerable.Range(0, 12).Select(index => new AutocompleteItem($"/command-{index}", $"command-{index}"))], "/"));
        public CompletionResult ApplyCompletion(IReadOnlyList<string> lines, int cursorLine, int cursorCol, AutocompleteItem item, string prefix) =>
            new([.. lines.Select((line, index) => index == cursorLine ? item.Value : line)], cursorLine, item.Value.Length);
    }

    private static JsonObject Block(string type, string key, string value) => new() { ["type"] = type, [key] = value };

    private static JsonObject Assistant(JsonArray content, string stopReason = "stop") => new()
    {
        ["role"] = "assistant", ["content"] = content, ["api"] = "anthropic-messages", ["provider"] = "anthropic", ["model"] = "claude-sonnet-4-5",
        ["stopReason"] = stopReason, ["timestamp"] = 0,
    };

    private static string Trimmed(IComponent component) => string.Join("|", Strip(component.Render(80)).Select(line => line.Trim()));
}
