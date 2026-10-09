using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.Cli.Interactive.Mode;
using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Cli.Pi;
using PiSharp.Tui.Pi;
using static Expect;

/// <summary>model-search.ts, model-selector.ts, scoped-models-selector.ts, thinking-selector.ts, theme-selector.ts,
/// show-images-selector.ts, user-message-selector.ts, trust-selector.ts, first-time-setup.ts (with startup-ui.ts
/// shouldRunFirstTimeSetup), extension-input.ts, extension-selector.ts, extension-editor.ts, custom-editor.ts and
/// external-editor.ts. Ported from model-selector.test.ts, scoped-models-selector.test.ts, thinking-selector.test.ts,
/// theme-picker.test.ts, trust-selector.test.ts, first-time-setup.test.ts, first-time-setup-fork.test.ts,
/// custom-editor-history-keybindings.test.ts and external-editor.test.ts; render snapshots are authored from the sources.</summary>
internal static class SelectorCases
{
    private const string Provider = "faux";

    private sealed class FakeTerminal : ITerminal
    {
        public void Start(Action<string> onInput, Action onResize) { }
        public void Stop() { }
        public Task DrainInputAsync(int maxMs = 1000, int idleMs = 50) => Task.CompletedTask;
        public void Write(string data) { }
        public int Columns => 120;
        public int Rows => 40;
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

    private static (TuiMainScreen Tui, UiLoop Loop) NewTui()
    {
        var loop = UiLoop.CreateManual();
        return (new TuiMainScreen(new FakeTerminal(), loop, environment: _ => null), loop);
    }

    private static JsonObject Model(string id, string? name = null, string provider = Provider) =>
        new() { ["id"] = id, ["name"] = name ?? id, ["provider"] = provider, ["api"] = "faux", ["reasoning"] = true, ["input"] = new JsonArray("text"),
            ["contextWindow"] = 128000, ["maxTokens"] = 16384 };

    private sealed class FakeModelRuntime(IReadOnlyList<JsonObject> models) : IModelSelectorRuntime
    {
        public Func<CancellationToken, Task<ModelsRefreshResult>> Refresh { get; set; } = _ => Task.FromResult(new ModelsRefreshResult(false, []));
        public int RefreshCalls;
        public string? Error { get; set; }
        public IReadOnlyList<JsonObject> GetAvailableSnapshot() => models;
        public JsonObject? GetModel(string provider, string modelId) =>
            models.FirstOrDefault(model => (string?)model["provider"] == provider && (string?)model["id"] == modelId);
        public string? GetError() => Error;
        public Task<ModelsRefreshResult> RefreshAsync(CancellationToken cancellationToken) { Interlocked.Increment(ref RefreshCalls); return Refresh(cancellationToken); }
    }

    private static KeybindingsManager AppManager(Dictionary<string, IReadOnlyList<string>?>? user = null) =>
        new(AppKeybindings.Definitions(_ => null), user);

    private static void UseBindings(Dictionary<string, IReadOnlyList<string>?> user) => KeybindingsManager.SetGlobal(AppManager(user));

    private static string Joined(IComponent component, int width) => string.Join("\n", Strip(component.Render(width)));

    private static string? Row(IComponent component, int width, string contains) =>
        Strip(component.Render(width)).FirstOrDefault(line => line.Contains(contains, StringComparison.Ordinal))?.TrimEnd();

    private static string Rule(int width) => new('─', width);

    private static bool[] MarkerStates(ScopedModelsSelectorComponent selector, IEnumerable<(string Id, bool Enabled)> models)
    {
        var lines = Strip(selector.Render(120));
        return models.Select(model =>
        {
            var line = lines.FirstOrDefault(candidate => candidate.Contains($"{model.Id} [", StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"Expected rendered row for {model.Id}");
            return line[2..].StartsWith("✓ ", StringComparison.Ordinal);
        }).ToArray();
    }

    private sealed class ScopedHarness
    {
        public readonly (string Id, string Name, bool Enabled)[] Initial;
        public readonly bool[] Enabled;
        public readonly ScopedModelsSelectorComponent Selector;
        public ScopedHarness(params (string Id, string Name, bool Enabled)[] models)
        {
            Initial = models;
            Enabled = models.Select(model => model.Enabled).ToArray();
            var all = models.Select(model => Model(model.Id, model.Name)).ToList();
            Selector = new ScopedModelsSelectorComponent(
                new ModelsConfig(all, models.Where(model => model.Enabled).Select(model => $"{Provider}/{model.Id}").ToList()),
                new ModelsCallbacks(
                    enabledIds =>
                    {
                        for (var i = 0; i < models.Length; i++)
                            Enabled[i] = enabledIds is null || enabledIds.Contains($"{Provider}/{models[i].Id}");
                    },
                    _ => { },
                    () => { }));
        }
        public bool[] Markers() => MarkerStates(Selector, Initial.Select(model => (model.Id, model.Enabled)));
    }

    private static string Seq(IEnumerable<bool> values) => string.Join(",", values);

    private static string FixturePath(string path) => Path.GetFullPath(path);

    public static IEnumerable<(string Id, Func<Task> Run)> All()
    {
        // ---------------------------------------------------------------- model-search.ts
        yield return ("sel.model-search.text", Sync(() =>
        {
            Equal("gpt-5 openai openai/gpt-5 openai gpt-5 GPT-5", ModelSearch.GetModelSearchText(new ModelSearchItem("gpt-5", "openai", "GPT-5")), "search text");
            Equal("openai openai/gpt-5 openai gpt-5 GPT-5", ModelSearch.GetModelSelectorSearchText(new ModelSearchItem("gpt-5", "openai", "GPT-5")), "selector text");
            Equal("openai openai/gpt-5 openai gpt-5", ModelSearch.GetModelSelectorSearchText(new ModelSearchItem("gpt-5", "openai")), "no name");
        }));

        // ---------------------------------------------------------------- model-selector.test.ts
        yield return ("sel.model.keeps-current-marked-while-browsing", Sync(() =>
        {
            var (tui, loop) = NewTui();
            var models = new[] { Model("current-model", "Current Model"), Model("browsed-model", "Browsed Model") };
            var selector = new ModelSelectorComponent(tui, models[0], new FakeModelRuntime(models), [], _ => { }, () => { });
            Equal($"→ ✓ current-model [{Provider}]", Row(selector, 120, "current-model ["), "current row");
            selector.HandleInput("\u001b[B");
            Equal($"  ✓ current-model [{Provider}]", Row(selector, 120, "current-model ["), "current row after browse");
            Equal($"→   browsed-model [{Provider}]", Row(selector, 120, "browsed-model ["), "browsed row");
            selector.Dispose();
            loop.RunPending();
        }));
        yield return ("sel.model.uses-configured-save-binding", Sync(() =>
        {
            UseBindings(new() { ["app.models.save"] = ["ctrl+r"] });
            var (tui, _) = NewTui();
            var model = Model("faux-1", "Faux 1");
            JsonObject? saved = null;
            var selector = new ModelSelectorComponent(tui, model, new FakeModelRuntime([model]), [], _ => { }, () => { }, null, m => saved = m);
            Contains(Joined(selector, 120), "Ctrl+R to set as default", "hint");
            selector.HandleInput("\u0013");
            Check(saved is null, "ctrl+s does not save");
            selector.HandleInput("\u0012");
            Check(ReferenceEquals(saved, model), "ctrl+r saves the current model");
        }));
        yield return ("sel.model.lists-every-failed-catalog", async () =>
        {
            var (tui, loop) = NewTui();
            var model = Model("faux-1", "Faux 1");
            var runtime = new FakeModelRuntime([model])
            {
                Refresh = _ => Task.FromResult(new ModelsRefreshResult(false,
                    [new("openai", "unavailable"), new("anthropic", "unavailable")])),
            };
            var selector = new ModelSelectorComponent(tui, model, runtime, [], _ => { }, () => { });
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!Joined(selector, 120).Contains("Could not refresh 2 model catalogs (openai, anthropic); showing cached models.", StringComparison.Ordinal))
            {
                if (DateTime.UtcNow > deadline) throw new InvalidOperationException("refresh error not rendered:\n" + Joined(selector, 120));
                loop.RunPending();
                await Task.Delay(10);
            }
            selector.Dispose();
        });
        yield return ("sel.model.single-failed-catalog-and-config-error", async () =>
        {
            var (tui, loop) = NewTui();
            var model = Model("faux-1", "Faux 1");
            var runtime = new FakeModelRuntime([model]) { Refresh = _ => Task.FromResult(new ModelsRefreshResult(false, [new("openai", "x")])) };
            var selector = new ModelSelectorComponent(tui, model, runtime, [], _ => { }, () => { });
            await Task.Delay(20); loop.RunPending();
            Contains(Joined(selector, 120), "Could not refresh openai; showing cached models.", "single error");
            var runtime2 = new FakeModelRuntime([model]) { Error = "bad models.json\nline two" };
            var selector2 = new ModelSelectorComponent(tui, model, runtime2, [], _ => { }, () => { });
            await Task.Delay(20); loop.RunPending();
            var text = Joined(selector2, 120);
            Check(Row(selector2, 120, "bad models.json") == "bad models.json" && Row(selector2, 120, "line two") == "line two", "config error lines");
            Check(!text.Contains("Model Name:", StringComparison.Ordinal), "error replaces the model name");
        });
        yield return ("sel.model.render-snapshot", async () =>
        {
            var (tui, loop) = NewTui();
            var models = new[] { Model("beta", "Beta Model", "zeta"), Model("alpha", "Alpha Model", "acme"), Model("gamma", "Gamma Model", "acme") };
            var selector = new ModelSelectorComponent(tui, models[2], new FakeModelRuntime(models), [], _ => { }, () => { }, null, _ => { }, new DefaultModelReference("zeta", "beta"));
            // Before the refresh settles: current first, default second, then by provider.
            Lines([
                Rule(60), "",
                "Only showing models from configured providers. Use /login to",
                "add providers.", "",
                "> ", "",
                "→ ✓ gamma [acme]",
                "    beta [zeta] · default",
                "    alpha [acme]", "",
                "  Model Name: Gamma Model", "",
                "  Refreshing model catalogs…", "",
                "  Enter to select · Ctrl+S to set as default · Escape/Ctrl+C",
                "to cancel",
                Rule(60),
            ], selector.Render(60), "model selector before refresh");
            await Task.Delay(20); loop.RunPending();
            Contains(Joined(selector, 60), "  Model catalogs refreshed.", "refreshed status");
            // A query moves the selection to the best match; "def" ranks the default model first.
            foreach (var ch in "def") selector.HandleInput(ch.ToString());
            Equal("→   beta [zeta] · default", Row(selector, 60, "beta ["), "default search");
            selector.Dispose();
        });
        yield return ("sel.model.scoped-scope-toggle", async () =>
        {
            var (tui, loop) = NewTui();
            var models = new[] { Model("a"), Model("b"), Model("c") };
            var selector = new ModelSelectorComponent(tui, models[1], new FakeModelRuntime(models), [new ScopedModelItem(models[1]), new ScopedModelItem(models[2])], _ => { }, () => { });
            await Task.Delay(20); loop.RunPending();
            var lines = Strip(selector.Render(80)).Select(line => line.TrimEnd()).ToList();
            Equal("Scope: all | scoped", lines[2], "scope line");
            Equal("tab scope (all/scoped)", lines[3], "scope hint");
            Check(Row(selector, 80, "a [") is null, "scoped list hides a");
            selector.HandleInput("\t");
            Equal("    a [faux]", Row(selector, 80, "a ["), "all scope shows a");
            Equal("→ ✓ b [faux]", Row(selector, 80, "b ["), "current stays selected");
            selector.Dispose();
        });
        yield return ("sel.model.shares-concurrent-refreshes", async () =>
        {
            var gate = new TaskCompletionSource<ModelsRefreshResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var runtime = new FakeModelRuntime([Model("a")]) { Refresh = _ => gate.Task };
            using var first = new CancellationTokenSource();
            var one = ModelCatalogRefresh.RefreshModelCatalogs(runtime, first.Token);
            var two = ModelCatalogRefresh.RefreshModelCatalogs(runtime, CancellationToken.None);
            Equal(1, runtime.RefreshCalls, "one shared refresh");
            first.Cancel();
            try { await one; throw new InvalidOperationException("cancelled waiter should reject"); } catch (OperationCanceledException) { }
            gate.SetResult(new ModelsRefreshResult(false, []));
            Equal(false, (await two).Aborted, "the other waiter still gets the result");
            await Task.Delay(10);
            _ = ModelCatalogRefresh.RefreshModelCatalogs(runtime, CancellationToken.None);
            Equal(2, runtime.RefreshCalls, "a settled refresh is not reused");
        });

        // ---------------------------------------------------------------- scoped-models-selector.test.ts
        yield return ("sel.scoped.marks-every-model-after-enabling-all", Sync(() =>
        {
            var h = new ScopedHarness(("model-a", "Model A", true), ("model-b", "Model B", false), ("model-c", "Model C", false));
            h.Selector.HandleInput("\u0001");
            Equal("True,True,True", Seq(h.Enabled), "enabled");
            Equal("True,True,True", Seq(h.Markers()), "markers");
            Contains(Joined(h.Selector, 120), "all enabled", "footer");
        }));
        yield return ("sel.scoped.disables-only-selected-after-enabling-all", Sync(() =>
        {
            var h = new ScopedHarness(("model-a", "Model A", true), ("model-b", "Model B", false), ("model-c", "Model C", false));
            h.Selector.HandleInput("\u0001");
            h.Selector.HandleInput("\r");
            Equal("False,True,True", Seq(h.Enabled), "enabled");
            Equal("False,True,True", Seq(h.Markers()), "markers");
        }));
        yield return ("sel.scoped.enables-only-selected-after-clearing-all", Sync(() =>
        {
            var h = new ScopedHarness(("model-a", "Model A", true), ("model-b", "Model B", true), ("model-c", "Model C", true));
            h.Selector.HandleInput("\u0018");
            Equal("False,False,False", Seq(h.Enabled), "enabled after clear");
            Equal("False,False,False", Seq(h.Markers()), "markers after clear");
            h.Selector.HandleInput("\r");
            Equal("True,False,False", Seq(h.Enabled), "enabled after toggle");
            Equal("True,False,False", Seq(h.Markers()), "markers after toggle");
        }));
        yield return ("sel.scoped.restores-all-enabled-after-reenabling-last", Sync(() =>
        {
            var h = new ScopedHarness(("model-a", "Model A", true), ("model-b", "Model B", false), ("model-c", "Model C", false));
            h.Selector.HandleInput("\u0001"); // enable all -> null
            h.Selector.HandleInput("\r"); // disable model-a; enabled models re-sort first: [b, c, a]
            h.Selector.HandleInput("\u001b[B");
            h.Selector.HandleInput("\u001b[B"); // move selection back to model-a
            h.Selector.HandleInput("\r"); // re-enable model-a
            Equal("True,True,True", Seq(h.Enabled), "enabled");
            Equal("True,True,True", Seq(h.Markers()), "markers");
            Contains(Joined(h.Selector, 120), "all enabled", "footer");
        }));
        yield return ("sel.scoped.render-snapshot", Sync(() =>
        {
            var models = new[] { Model("model-a", "Model A"), Model("model-b", "Model B") };
            var selector = new ScopedModelsSelectorComponent(new ModelsConfig(models, ["faux/model-b", "gone/old"], "Refreshing model catalogs…"),
                new ModelsCallbacks(_ => { }, _ => { }, () => { }));
            var reorder = $"{KeybindingHints.KeyDisplayText("app.models.reorderUp")}/{KeybindingHints.KeyDisplayText("app.models.reorderDown")}";
            Lines([
                Rule(140), "",
                "Model Configuration",
                "Session-only. Ctrl+S to save to settings.", "",
                "> ", "",
                "→ ✓ model-b [faux]",
                "    gone/old [unavailable]",
                "    model-a [faux]", "",
                "  Model Name: Model B", "",
                "  Refreshing model catalogs…",
                $"  Enter toggle · Ctrl+A all · Ctrl+X clear · Ctrl+P provider · {reorder} reorder · Ctrl+S save · 1/2 enabled · 1 unavailable",
                Rule(140),
            ], selector.Render(140), "scoped selector");
            selector.HandleInput("\u001b[B");
            Contains(Joined(selector, 120), "  Model unavailable", "unavailable model name");
            selector.HandleInput("\u001b[B");
            selector.HandleInput("\r");
            Contains(Joined(selector, 140), "· 2/2 enabled · 1 unavailable (unsaved)", "dirty footer");
            selector.SetRefreshStatus("Model catalogs refreshed.", "success");
            Equal("  Model catalogs refreshed.", Row(selector, 140, "Model catalogs"), "refresh status");
        }));
        yield return ("sel.scoped.reorder-search-and-cancel", Sync(() =>
        {
            IReadOnlyList<string>? last = null; var cancelled = 0; IReadOnlyList<string>? persisted = ["x"];
            var models = new[] { Model("model-a", "Model A"), Model("model-b", "Model B"), Model("other", "Other", "zeta") };
            var selector = new ScopedModelsSelectorComponent(new ModelsConfig(models, ["faux/model-a", "faux/model-b"]),
                new ModelsCallbacks(ids => last = ids, ids => persisted = ids, () => cancelled++));
            selector.HandleInput("\u001b[1;3B"); // alt+down moves model-a after model-b
            Equal("faux/model-b,faux/model-a", string.Join(",", last!), "reordered");
            selector.HandleInput("\u0010"); // ctrl+p toggles the provider of model-a (all enabled -> cleared)
            Equal("", string.Join(",", last!), "provider cleared");
            selector.HandleInput("\u0013");
            Equal("", string.Join(",", persisted!), "persisted");
            foreach (var ch in "oth") selector.HandleInput(ch.ToString());
            Equal("→   other [zeta]", Row(selector, 80, "other ["), "search");
            selector.HandleInput("\u0003"); // ctrl+c clears the search first
            Equal(0, cancelled, "not cancelled while searching");
            Equal(">", Strip(selector.Render(80))[5].TrimEnd(), "search cleared");
            selector.HandleInput("\u0003");
            Equal(1, cancelled, "ctrl+c cancels");
            selector.HandleInput("\u001b");
            Equal(2, cancelled, "escape cancels");
        }));

        // ---------------------------------------------------------------- thinking-selector.test.ts
        yield return ("sel.thinking.keeps-current-marked-while-browsing", Sync(() =>
        {
            var selector = new ThinkingSelectorComponent("medium", ["medium", "high"], _ => { }, () => { });
            string? LevelRow(string level) => Strip(selector.GetSelectList().Render(80)).FirstOrDefault(line => line.Contains(level, StringComparison.Ordinal));
            Equal("✓ medium", selector.GetSelectList().GetSelectedItem()?.Label, "selected label");
            Check(LevelRow("medium")!.StartsWith("→ ✓ medium", StringComparison.Ordinal), "medium selected");
            selector.HandleInput("\u001b[B");
            Check(LevelRow("medium")!.StartsWith("  ✓ medium", StringComparison.Ordinal), "medium kept marked");
            Check(LevelRow("high")!.StartsWith("→   high", StringComparison.Ordinal), "high selected");
        }));
        yield return ("sel.thinking.uses-configured-save-binding", Sync(() =>
        {
            UseBindings(new() { ["app.thinking.save"] = ["ctrl+r"] });
            string? saved = null;
            var selector = new ThinkingSelectorComponent("medium", ["medium", "high"], _ => { }, () => { }, level => saved = level);
            Contains(Joined(selector, 80), "Ctrl+R to set as default", "hint");
            selector.HandleInput("\u0013");
            Equal(null, saved, "ctrl+s ignored");
            selector.HandleInput("\u0012");
            Equal("medium", saved, "ctrl+r saves");
        }));
        yield return ("sel.thinking.render-snapshot", Sync(() =>
        {
            string? selected = null;
            var selector = new ThinkingSelectorComponent("low", ["off", "low", "high"], level => selected = level, () => { }, null, "high");
            Lines([
                Rule(80), "",
                "Thinking Level", "",
                "Shift+Tab cycles thinking levels in-session", "",
                "> ", "",
                "    off       No reasoning",
                "→ ✓ low       Light reasoning (~2k tokens)",
                "    high      Deep reasoning (~16k tokens) · default", "",
                "  Enter to select · Ctrl+S to set as default · Escape/Ctrl+C to cancel",
                Rule(80),
            ], selector.Render(80), "thinking selector");
            foreach (var ch in "deep") selector.HandleInput(ch.ToString());
            Equal("→   high      Deep reasoning (~16k tokens) · default", Strip(selector.Render(80))[8].TrimEnd(), "filtered by description");
            selector.HandleInput("\r");
            Equal("high", selected, "enter selects the filtered level");
        }));

        // ---------------------------------------------------------------- theme-picker.test.ts and theme-selector.ts
        yield return ("sel.theme-picker.uses-content-names", Sync(() =>
        {
            using var dir = new TempDir();
            var agentDir = Path.Combine(dir.Path, "agent");
            Directory.CreateDirectory(Path.Combine(agentDir, "themes"));
            Themes.Environment = name => name == "PI_CODING_AGENT_DIR" ? agentDir : null;
            Themes.SetRegisteredThemes(Array.Empty<PiSharp.Cli.Interactive.Mode.Theme>());
            var dark = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(PiSharp.CodingAgent.Export.SessionHtmlExport.ReadAssetBytes("dark.json")).TrimStart((char)0xFEFF))!.AsObject();
            dark["name"] = "bar";
            var themePath = Path.Combine(agentDir, "themes", "foo.json");
            File.WriteAllText(themePath, dark.ToJsonString());
            Check(Themes.GetAvailableThemes().Contains("bar"), "content name listed");
            Check(!Themes.GetAvailableThemes().Contains("foo"), "file name not listed");
            Check(Themes.GetAvailableThemesWithPaths().Any(info => info.Name == "bar" && info.Path == themePath), "path of bar");
            Check(!Themes.GetAvailableThemesWithPaths().Any(info => info.Name == "foo"), "no foo");
            var selector = new ThemeSelectorComponent("dark", _ => { }, () => { }, _ => { });
            Check(Strip(selector.Render(80)).Any(line => line.TrimEnd() == "  bar"), "selector lists bar");
        }));
        yield return ("sel.theme-selector.render-and-preview", Sync(() =>
        {
            using var dir = new TempDir();
            Themes.Environment = name => name == "PI_CODING_AGENT_DIR" ? dir.Path : null;
            var previews = new List<string>(); string? selected = null; var cancelled = false;
            var selector = new ThemeSelectorComponent("dark", name => selected = name, () => cancelled = true, previews.Add);
            Lines([
                Rule(80),
                "  system",
                "→ dark        (current)",
                "  light",
                Rule(80),
            ], selector.Render(80), "theme selector");
            selector.GetSelectList().HandleInput("\u001b[B");
            Equal("light", string.Join(",", previews), "preview on move");
            selector.GetSelectList().HandleInput("\r");
            Equal("light", selected, "selected");
            selector.GetSelectList().HandleInput("\u001b");
            Check(cancelled, "cancel");
        }));

        // ---------------------------------------------------------------- show-images-selector.ts
        yield return ("sel.show-images.render-and-select", Sync(() =>
        {
            bool? selected = null;
            var selector = new ShowImagesSelectorComponent(false, value => selected = value, () => { });
            Lines([
                Rule(80),
                "  Yes         Show images inline in terminal",
                "→ No          Show text placeholder instead",
                Rule(80),
            ], selector.Render(80), "show images selector");
            selector.GetSelectList().HandleInput("\u001b[A");
            selector.GetSelectList().HandleInput("\r");
            Equal(true, selected, "yes");
        }));

        // ---------------------------------------------------------------- user-message-selector.ts
        yield return ("sel.user-message.render-and-select", Sync(() =>
        {
            string? selected = null;
            var selector = new UserMessageSelectorComponent(
                [new("e1", "first message\nwith two lines"), new("e2", "  second  "), new("e3", new string('x', 100))],
                id => selected = id, () => { }, "e2");
            Lines([
                "",
                " Fork from Message",
                " Select a user message to copy the active path up to that point into a new session",
                "",
                Rule(90), "",
                "  first message with two lines",
                "  Message 1 of 3", "",
                "› second",
                "  Message 2 of 3", "",
                "  " + new string('x', 85) + "...",
                "  Message 3 of 3", "",
                "",
                Rule(90),
            ], selector.Render(90), "user message selector");
            selector.GetMessageList().HandleInput("\u001b[A");
            selector.GetMessageList().HandleInput("\u001b[A"); // wraps to the newest
            selector.GetMessageList().HandleInput("\r");
            Equal("e3", selected, "selected");
        }));
        yield return ("sel.user-message.empty-auto-cancels", Sync(() =>
        {
            var cancelled = 0; Action? scheduled = null; double delay = 0;
            var selector = new UserMessageSelectorComponent([], _ => { }, () => cancelled++, null, (action, ms) => { scheduled = action; delay = ms; return new CancellationTokenSource(); });
            Equal("  No user messages found", Strip(selector.GetMessageList().Render(80))[0], "empty list");
            Equal(100.0, delay, "timeout");
            Equal(0, cancelled, "not yet");
            scheduled!();
            Equal(1, cancelled, "auto-cancelled");
        }));

        // ---------------------------------------------------------------- trust-selector.test.ts
        var root = Path.GetPathRoot(Path.GetFullPath("/"))!;
        string P(params string[] parts) => Path.Join([root, .. parts]);
        yield return ("sel.trust.keeps-saved-decision-marked", Sync(() =>
        {
            var project = P("project");
            var selector = new TrustSelectorComponent(new TrustSelectorOptions(project, new ProjectTrustStoreEntry(project, true), true, _ => { }, () => { }));
            var output = Joined(selector, 120);
            Contains(output, $"Saved decision: trusted ({project})", "saved");
            Contains(output, "Current session: trusted", "session");
            Contains(output, "→ ✓ Trust", "selected saved");
            selector.HandleInput("\u001b[B");
            output = Joined(selector, 120);
            Contains(output, "✓ Trust", "still marked");
            Contains(output, $"→   Trust parent folder ({root})", "parent selected");
            Check(!output.Contains("✓ Do not trust", StringComparison.Ordinal), "do not trust unmarked");
        }));
        yield return ("sel.trust.selects-a-decision", Sync(() =>
        {
            var project = P("project");
            TrustSelection? selection = null;
            var selector = new TrustSelectorComponent(new TrustSelectorOptions(project, null, false, s => selection = s, () => { }));
            selector.HandleInput("\n");
            Equal(true, selection!.Trusted, "trusted");
            Equal($"{project}=True", string.Join(";", selection.Updates.Select(u => $"{u.Path}={u.Decision}")), "updates");
        }));
        yield return ("sel.trust.labels-inherited-decisions", Sync(() =>
        {
            var selector = new TrustSelectorComponent(new TrustSelectorOptions(P("parent", "project", "nested"), new ProjectTrustStoreEntry(P("parent"), true), true, _ => { }, () => { }));
            Contains(Joined(selector, 120), $"Saved decision: trusted (inherited from {P("parent")})", "inherited");
        }));
        yield return ("sel.trust.adds-a-trust-parent-option", Sync(() =>
        {
            TrustSelection? selection = null;
            var selector = new TrustSelectorComponent(new TrustSelectorOptions(P("parent", "project"), new ProjectTrustStoreEntry(P("parent"), true), true, s => selection = s, () => { }));
            var output = Joined(selector, 120);
            Contains(output, $"Saved decision: trusted (inherited from {P("parent")})", "inherited");
            Contains(output, $"✓ Trust parent folder ({P("parent")})", "parent marked");
            selector.HandleInput("\n");
            Equal(true, selection!.Trusted, "trusted");
            Equal($"{P("parent")}=True;{P("parent", "project")}=", string.Join(";", selection.Updates.Select(u => $"{u.Path}={u.Decision}")), "updates");
        }));
        yield return ("sel.trust.render-snapshot", Sync(() =>
        {
            var cancelled = false;
            var options = ImmutableArray.Create(
                new ProjectTrustOption("Trust", true, [new("/w/p", true)], "/w/p"),
                new ProjectTrustOption("Trust parent folder (/w)", true, [new("/w", true), new("/w/p", null)], "/w"),
                new ProjectTrustOption("Do not trust", false, [new("/w/p", false)], "/w/p"));
            var selector = new TrustSelectorComponent(new TrustSelectorOptions("/w/p", new ProjectTrustStoreEntry("/w/p", false), false, _ => { }, () => cancelled = true, _ => options));
            Lines([
                Rule(60), "",
                " Project trust",
                " /w/p", "",
                " Saved decision: untrusted (/w/p)",
                " Current session: untrusted", "",
                "     Trust",
                "     Trust parent folder (/w)",
                " → ✓ Do not trust", "",
                " ↑↓ navigate  enter save  escape/ctrl+c cancel", "",
                Rule(60),
            ], selector.Render(60), "trust selector");
            selector.HandleInput("k");
            Equal(" →   Trust parent folder (/w)", Row(selector, 60, "parent"), "k moves up");
            selector.HandleInput("\u001b");
            Check(cancelled, "escape cancels");
        }));

        // ---------------------------------------------------------------- first-time-setup.test.ts / first-time-setup-fork.test.ts
        Func<string, string?> Env(params (string Key, string Value)[] values) => name => values.FirstOrDefault(v => v.Key == name).Value;
        yield return ("sel.first-time-setup.runs-when-experimental-default-dir-no-settings", Sync(() =>
        {
            using var dir = new TempDir();
            Check(FirstTimeSetupComponent.ShouldRunFirstTimeSetup(Path.Combine(dir.Path, "settings.json"), Env(("PI_EXPERIMENTAL", "1"))), "runs");
        }));
        yield return ("sel.first-time-setup.not-without-experimental", Sync(() =>
        {
            using var dir = new TempDir();
            Check(!FirstTimeSetupComponent.ShouldRunFirstTimeSetup(Path.Combine(dir.Path, "settings.json"), Env()), "no experimental");
        }));
        yield return ("sel.first-time-setup.not-with-custom-agent-dir", Sync(() =>
        {
            using var dir = new TempDir();
            Check(!FirstTimeSetupComponent.ShouldRunFirstTimeSetup(Path.Combine(dir.Path, "settings.json"), Env(("PI_EXPERIMENTAL", "1"), ("PI_CODING_AGENT_DIR", dir.Path))), "custom agent dir");
        }));
        yield return ("sel.first-time-setup.not-when-settings-exist", Sync(() =>
        {
            using var dir = new TempDir();
            var settings = Path.Combine(dir.Path, "settings.json");
            File.WriteAllText(settings, "{}");
            Check(!FirstTimeSetupComponent.ShouldRunFirstTimeSetup(settings, Env(("PI_EXPERIMENTAL", "1"))), "settings exist");
        }));
        yield return ("sel.first-time-setup.not-for-forked-package", Sync(() =>
        {
            using var dir = new TempDir();
            Check(!FirstTimeSetupComponent.ShouldRunFirstTimeSetup(Path.Combine(dir.Path, "settings.json"), Env(("PI_EXPERIMENTAL", "1")),
                FirstTimeSetupComponent.Distribution with { PackageName = "@example/pi-coding-agent" }), "fork");
        }));
        yield return ("sel.first-time-setup.default-settings-path", Sync(() =>
        {
            using var dir = new TempDir();
            Check(FirstTimeSetupComponent.ShouldRunFirstTimeSetup(null, Env(("PI_EXPERIMENTAL", "1")), home: dir.Path), "no ~/.pi/agent/settings.json");
            Directory.CreateDirectory(Path.Combine(dir.Path, ".pi", "agent"));
            File.WriteAllText(Path.Combine(dir.Path, ".pi", "agent", "settings.json"), "{}");
            Check(!FirstTimeSetupComponent.ShouldRunFirstTimeSetup(null, Env(("PI_EXPERIMENTAL", "1")), home: dir.Path), "settings in the default agent dir");
        }));
        yield return ("sel.first-time-setup.render-steps-and-submit", Sync(() =>
        {
            var previews = new List<string>(); FirstTimeSetupResult? result = null;
            var setup = new FirstTimeSetupComponent(new FirstTimeSetupOptions(previews.Add, r => result = r, () => { }));
            Lines([
                Rule(90), "",
                " ██████",
                " ██  ██",
                " ████  ██",
                " ██    ██", "",
                " Welcome to pi, the minimal coding agent.", "",
                " Pick a theme.", "",
                " → System (matches your terminal colors)",
                "   Dark",
                "   Light", "",
                " ↑↓ navigate  enter continue  escape/ctrl+c skip setup", "",
                Rule(90),
            ], setup.Render(90), "theme step");
            setup.HandleInput("j");
            setup.HandleInput("j");
            setup.HandleInput("j");
            Equal("dark,light", string.Join(",", previews), "previews only on change");
            setup.HandleInput("\r");
            Lines([
                Rule(90), "",
                " ██████",
                " ██  ██",
                " ████  ██",
                " ██    ██", "",
                " Welcome to pi, the minimal coding agent.", "",
                " Opt-in to anonymous usage data sharing?",
                " Opting in stores a tracking identifier in settings.json and enables anonymous",
                " usage analytics. This helps us to better debug, reproduce, and resolve issues",
                " and bugs within Pi. You can observe what is shared using /privacy and make",
                " changes anytime in settings.json.", "",
                " → Share anonymous usage data",
                "   Don't share", "",
                " ↑↓ navigate  enter finish  escape/ctrl+c skip setup", "",
                Rule(90),
            ], setup.Render(90), "analytics step");
            setup.HandleInput("\u001b[B");
            setup.HandleInput("\n");
            Equal("light/False", $"{result!.Theme}/{result.ShareAnalytics}", "result");
        }));

        // ---------------------------------------------------------------- extension-input.ts
        yield return ("sel.extension-input.render-submit-and-countdown", Sync(() =>
        {
            var (tui, _) = NewTui();
            string? submitted = null; var cancelled = 0; Action? tick = null;
            var input = new ExtensionInputComponent("Your name?", "ignored", value => submitted = value, () => cancelled++,
                new ExtensionInputOptions(tui, 2000, "pi", "Used for greetings.", (action, _) => { tick = action; return new CancellationTokenSource(); }));
            Lines([
                Rule(60), "",
                " Your name? (2s)", "",
                " Used for greetings.", "",
                "> pi", "",
                " enter submit  escape/ctrl+c cancel", "",
                Rule(60),
            ], input.Render(60), "extension input");
            tick!();
            Contains(Joined(input, 60), " Your name? (1s)", "countdown");
            tick!();
            Equal(1, cancelled, "expired");
            input.HandleInput("!");
            input.HandleInput("\r");
            Equal("!pi", submitted, "submitted (setValue keeps the cursor at 0)");
            input.Dispose();
        }));

        // ---------------------------------------------------------------- extension-selector.ts
        yield return ("sel.extension-selector.render-and-select", Sync(() =>
        {
            string? selected = null; var toggled = 0;
            var selector = new ExtensionSelectorComponent("Pick one", ["Alpha", "Beta"], value => selected = value, () => { },
                new ExtensionSelectorOptions(OnToggleToolsExpanded: () => toggled++));
            Lines([
                Rule(60), "",
                " Pick one", "",
                " → Alpha",
                "   Beta", "",
                " ↑↓ navigate  enter select  escape/ctrl+c cancel", "",
                Rule(60),
            ], selector.Render(60), "extension selector");
            selector.HandleInput("\u000f");
            Equal(1, toggled, "ctrl+o toggles tool output");
            selector.HandleInput("j");
            selector.HandleInput("j");
            selector.HandleInput("\r");
            Equal("Beta", selected, "selected (clamped)");
        }));

        // ---------------------------------------------------------------- extension-editor.ts + external-editor.ts
        yield return ("sel.extension-editor.render-and-external-editor", Sync(() =>
        {
            var (tui, loop) = NewTui();
            var keybindings = AppManager();
            string? submitted = null; string? editedPath = null; string? seenContent = null;
            var output = new StringWriter();
            var editor = new ExtensionEditorComponent(tui, keybindings, "Edit prompt", "hello", value => submitted = value, () => { },
                new ExtensionEditorOptions(Description: "Change the text."), null, name => name == "EDITOR" ? "my-editor --wait" : null,
                (file, args, _) =>
                {
                    Equal("my-editor", file, "editor");
                    Equal("--wait", args[0], "argument");
                    editedPath = args[1];
                    seenContent = File.ReadAllText(args[1]);
                    File.WriteAllText(args[1], "﻿from editor\n");
                    return Task.FromResult<int?>(0);
                }, output);
            Equal("my-editor --wait", editor.ExternalEditorCommand, "command from EDITOR");
            var hint = $" enter submit  {KeybindingHints.KeyText("tui.input.newLine")} newline  escape/ctrl+c cancel  ctrl+g external editor";
            var rendered = Strip(editor.Render(100)).Select(line => line.TrimEnd()).ToList();
            Equal(Rule(100), rendered[0], "top border");
            Equal(" Edit prompt", rendered[2], "title");
            Equal(" Change the text.", rendered[4], "description");
            Equal(Rule(100), rendered[6], "editor top border");
            Equal("hello", rendered[7], "prefill");
            Equal(hint, rendered[^3], "hint");
            Equal(Rule(100), rendered[^1], "bottom border");
            loop.Post(() => editor.HandleInput("\u0007"));
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (editor.GetEditor().GetText() != "from editor" && DateTime.UtcNow < deadline) { loop.RunPending(); Thread.Sleep(5); }
            Equal("hello", seenContent, "editor saw the content");
            Equal("from editor", editor.GetEditor().GetText(), "edited text");
            Check(!Directory.Exists(Path.GetDirectoryName(editedPath!)), "temp directory removed");
            Contains(output.ToString(), "Launching external editor: my-editor --wait\nPi will resume when the editor exits.\n", "launch notice");
            editor.HandleInput("\r");
            Equal("from editor", submitted, "submit");
        }));
        yield return ("sel.extension-editor.default-command", Sync(() =>
        {
            var (tui, _) = NewTui();
            var editor = new ExtensionEditorComponent(tui, AppManager(), "t", null, _ => { }, () => { }, null, null, name => name == "VISUAL" ? "vis" : name == "EDITOR" ? "ed" : null);
            Equal("vis", editor.ExternalEditorCommand, "VISUAL first");
            var fallback = new ExtensionEditorComponent(tui, AppManager(), "t", null, _ => { }, () => { }, null, null, _ => null);
            Equal(OperatingSystem.IsWindows() ? "notepad" : "nano", fallback.ExternalEditorCommand, "platform fallback");
            var configured = new ExtensionEditorComponent(tui, AppManager(), "t", null, _ => { }, () => { }, null, "code -w", _ => "vis");
            Equal("code -w", configured.ExternalEditorCommand, "configured command wins");
        }));
        yield return ("sel.external-editor.edits-in-private-temp-directory", async () =>
        {
            string? filePath = null; string? content = null; string[]? entries = null; UnixFileMode mode = default; bool shell = false;
            var result = await ExternalEditor.EditInExternalEditorAsync(new ExternalEditorOptions("fake-editor", "original"), (_, args, useShell) =>
            {
                shell = useShell;
                filePath = args[^1];
                content = File.ReadAllText(filePath);
                entries = Directory.GetFileSystemEntries(Path.GetDirectoryName(filePath)!).Select(Path.GetFileName).OfType<string>().ToArray();
                if (!OperatingSystem.IsWindows()) mode = File.GetUnixFileMode(Path.GetDirectoryName(filePath)!);
                File.WriteAllText(filePath, "edited");
                return Task.FromResult<int?>(0);
            }, TextWriter.Null);
            var directory = Path.GetDirectoryName(filePath!)!;
            Equal("complete:edited", $"{result.Status}:{result.Content}", "result");
            Equal(Path.TrimEndingDirectorySeparator(Path.GetTempPath()), Path.GetDirectoryName(directory), "inside tmpdir");
            Check(Path.GetFileName(directory).StartsWith("pi-editor-", StringComparison.Ordinal) && Path.GetFileName(directory).Length > "pi-editor-".Length, "pi-editor- prefix");
            Equal("prompt.md", Path.GetFileName(filePath!), "file name");
            Equal("prompt.md", string.Join(",", entries!), "only the prompt file");
            Equal("original", content, "original content");
            Equal(OperatingSystem.IsWindows(), shell, "shell on Windows");
            if (!OperatingSystem.IsWindows())
                Equal(0, (int)mode & 0b000_111_111, "directory is private");
            Check(!Directory.Exists(directory), "directory removed");
        });
        yield return ("sel.external-editor.keeps-original-on-failure", async () =>
        {
            string? filePath = null;
            var result = await ExternalEditor.EditInExternalEditorAsync(new ExternalEditorOptions("fake-editor --fail", "original"), (_, args, _) =>
            {
                filePath = args[^1];
                File.WriteAllText(filePath, "changed");
                return Task.FromResult<int?>(1);
            }, TextWriter.Null);
            Equal("failed:", $"{result.Status}:{result.Content}", "result");
            Check(!Directory.Exists(Path.GetDirectoryName(filePath!)), "directory removed");
            var noStart = await ExternalEditor.EditInExternalEditorAsync(new ExternalEditorOptions("x", "y"), (_, _, _) => Task.FromResult<int?>(null), TextWriter.Null);
            Equal("failed", noStart.Status, "spawn error");
            var throws = await ExternalEditor.EditInExternalEditorAsync(new ExternalEditorOptions("x", "y"), (_, _, _) => throw new InvalidOperationException("boom"), TextWriter.Null);
            Equal("failed", throws.Status, "runner exception");
        });
        yield return ("sel.external-editor.empty-content", async () =>
        {
            var result = await ExternalEditor.EditInExternalEditorAsync(new ExternalEditorOptions("fake-editor --empty", "original"), (_, args, _) =>
            {
                File.WriteAllText(args[^1], "");
                return Task.FromResult<int?>(0);
            }, TextWriter.Null);
            Equal("complete:", $"{result.Status}:{result.Content}", "result");
            var newline = await ExternalEditor.EditInExternalEditorAsync(new ExternalEditorOptions("e", "x"), (_, args, _) =>
            {
                File.WriteAllText(args[^1], "a\n\n");
                return Task.FromResult<int?>(0);
            }, TextWriter.Null);
            Equal("a\n", newline.Content, "only one trailing newline removed");
        });
        yield return ("sel.external-editor.real-process", async () =>
        {
            var ok = await ExternalEditor.EditInExternalEditorAsync(new ExternalEditorOptions(OperatingSystem.IsWindows() ? "cmd /c rem" : "true", "keep"), null, TextWriter.Null);
            Equal("complete:keep", $"{ok.Status}:{ok.Content}", "editor exiting 0 keeps the file");
            var missing = await ExternalEditor.EditInExternalEditorAsync(new ExternalEditorOptions("pisharp-no-such-editor-7f3a", "keep"), null, TextWriter.Null);
            Equal("failed", missing.Status, "missing editor");
        });

        // ---------------------------------------------------------------- custom-editor-history-keybindings.test.ts
        yield return ("sel.custom-editor.history-binding-precedes-model-cycling", Sync(() =>
        {
            var keybindings = AppManager(new() { ["tui.editor.historyPrevious"] = ["ctrl+p"], ["tui.editor.historyNext"] = ["ctrl+n"] });
            KeybindingsManager.SetGlobal(keybindings);
            var (tui, _) = NewTui();
            var editor = new CustomEditor(tui, Themes.GetEditorTheme(), keybindings);
            var modelCycles = 0;
            editor.OnAction("app.model.cycleForward", () => modelCycles++);
            editor.AddToHistory("previous prompt");
            editor.SetText("draft");
            editor.HandleInput("\u0010"); // Ctrl+P
            Equal("previous prompt", editor.GetText(), "history previous");
            Equal(0, modelCycles, "no model cycle");
            editor.HandleInput("\u000e"); // Ctrl+N
            Equal("draft", editor.GetText(), "history next");
        }));
        yield return ("sel.custom-editor.app-actions", Sync(() =>
        {
            var keybindings = AppManager();
            var (tui, _) = NewTui();
            var editor = new CustomEditor(tui, Themes.GetEditorTheme(), keybindings);
            var log = new List<string>();
            editor.OnAction("app.model.cycleForward", () => log.Add("cycle"));
            editor.OnAction("app.interrupt", () => log.Add("interrupt"));
            editor.OnAction("app.exit", () => log.Add("exit"));
            editor.OnExtensionShortcut = data => data == "\u0019" && log.Count >= 0 && Add(log, "ext");
            editor.HandleInput("\u0010");
            editor.HandleInput("\u001b");
            editor.OnEscape = () => log.Add("escape");
            editor.HandleInput("\u001b");
            editor.HandleInput("\u0004");
            editor.HandleInput("\u0019");
            editor.SetText("ab");
            editor.HandleInput("\u0004"); // not empty: the editor handles ctrl+d
            Equal("cycle,interrupt,escape,exit,ext", string.Join(",", log), "dispatch order");
            Equal("ab", editor.GetText(), "ctrl+d at end of text deletes nothing");
        }));
        yield return ("sel.custom-editor.render-snapshot", Sync(() =>
        {
            var (tui, _) = NewTui();
            var editor = new CustomEditor(tui, Themes.GetEditorTheme(), AppManager(), new CustomEditorOptions(PaddingX: 1, EmbedWorkingStatus: true));
            editor.SetText("hello");
            Lines([Rule(30), " hello", Rule(30)], editor.Render(30), "custom editor without status");
            Equal(true, editor.EmbedWorkingStatus, "embed flag");
        }));
    }

    private static bool Add(List<string> log, string value) { log.Add(value); return true; }
}
