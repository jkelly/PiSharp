// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/coding-agent/test/oauth-selector.test.ts, scoped-models-selector.test.ts,
// model-selector.test.ts, settings-submenu.test.ts and config-selector.test.ts (the cases Pi v1.6.0 added: #12245, #12246).
// Ported expectations, authored against the native port; not an upstream execution capture. Cases marked "native" have no upstream
// counterpart. The /resume cases are in Sync160UiSessionSelectorCases.cs.
using System.Text.Json.Nodes;
using PiSharp.Cli.Interactive.Mode;
using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Tui.Pi;
using static Expect;

/// <summary>Sync 1.6.0: a search query highlights the top match, and a cursor key in the search field keeps the highlight.</summary>
internal static class Sync160UiSelectorCases
{
    private const string Down = "\u001b[B";
    private const string Left = "\u001b[D";
    private const string Home = "\u001b[H";
    private const string Enter = "\r";
    private const string Provider = "faux";

    private sealed class FakeModelRuntime(IReadOnlyList<JsonObject> models) : IModelSelectorRuntime
    {
        public IReadOnlyList<JsonObject> GetAvailableSnapshot() => models;
        public JsonObject? GetModel(string provider, string modelId) =>
            models.FirstOrDefault(model => (string?)model["provider"] == provider && (string?)model["id"] == modelId);
        public string? GetError() => null;
        public Task<ModelsRefreshResult> RefreshAsync(CancellationToken cancellationToken) => Task.FromResult(new ModelsRefreshResult(false, []));
    }

    private sealed class FakeConfigSettings : IConfigSelectorSettings
    {
        public JsonObject GetGlobalSettings() => [];
        public JsonObject GetProjectSettings() => [];
        public void SetExtensionPaths(IReadOnlyList<string> paths) { }
        public void SetSkillPaths(IReadOnlyList<string> paths) { }
        public void SetPromptTemplatePaths(IReadOnlyList<string> paths) { }
        public void SetThemePaths(IReadOnlyList<string> paths) { }
        public void SetProjectExtensionPaths(IReadOnlyList<string> paths) { }
        public void SetProjectSkillPaths(IReadOnlyList<string> paths) { }
        public void SetProjectPromptTemplatePaths(IReadOnlyList<string> paths) { }
        public void SetProjectThemePaths(IReadOnlyList<string> paths) { }
        public void SetPackages(JsonArray packages) { }
        public void SetProjectPackages(JsonArray packages) { }
    }

    private static JsonObject Model(string id, string? name = null) =>
        new() { ["id"] = id, ["name"] = name ?? id, ["provider"] = Provider, ["api"] = "faux", ["reasoning"] = true, ["input"] = new JsonArray("text"),
            ["contextWindow"] = 128000, ["maxTokens"] = 16384 };

    private static void TypeText(IInputHandler target, string text) { foreach (var ch in text) target.HandleInput(ch.ToString()); }

    /// <summary>oauth-selector.test.ts: <c>name.toLowerCase().replace(" ", "-")</c> replaces the first space only.</summary>
    private static List<AuthSelectorProvider> ApiKeyProviders(params string[] names) => [.. names.Select(name =>
    {
        var lower = name.ToLowerInvariant(); var space = lower.IndexOf(' ');
        return new AuthSelectorProvider(space < 0 ? lower : lower[..space] + "-" + lower[(space + 1)..], name, "api_key");
    })];

    private static string? ArrowRow(IComponent component) =>
        Strip(component.Render(120)).FirstOrDefault(line => line.Contains('→'));

    /// <summary>scoped-models-selector.test.ts createSelector: the enabled flags follow onChange.</summary>
    private static (ScopedModelsSelectorComponent Selector, bool[] Enabled) Scoped(params (string Id, string Name)[] models)
    {
        var enabled = models.Select(_ => true).ToArray();
        var selector = new ScopedModelsSelectorComponent(
            new ModelsConfig([.. models.Select(model => Model(model.Id, model.Name))], [.. models.Select(model => $"{Provider}/{model.Id}")]),
            new ModelsCallbacks(ids =>
            {
                for (var i = 0; i < models.Length; i++) enabled[i] = ids is null || ids.Contains($"{Provider}/{models[i].Id}");
            }, _ => { }, () => { }));
        return (selector, enabled);
    }

    public static IEnumerable<(string Id, Func<Task> Run)> All()
    {
        // ---------------------------------------------------------------- oauth-selector.test.ts
        // Regression test for #12245.
        yield return ("sync160.ui.oauth.selects-the-best-match-after-typing-a-query-even-after-scrolling", Sync(() =>
        {
            var selected = new List<string>();
            var selector = new OAuthSelectorComponent("login",
                ApiKeyProviders("Anthropic", "Google", "Groq", "Mistral", "OpenAI", "OpenCode Go", "OpenCode Zen", "xAI"), (id, _) => selected.Add(id), () => { });
            for (var i = 0; i < 5; i++) selector.HandleInput(Down);
            TypeText(selector, "openai");
            Contains(string.Join("\n", Strip(selector.Render(120))), "→ OpenAI", "the top match is highlighted");
            selector.HandleInput(Enter);
            Equal("openai", string.Join(",", selected), "Enter picks the best match");
        }));
        yield return ("sync160.ui.oauth.keeps-the-selection-when-a-cursor-key-leaves-the-query-unchanged", Sync(() =>
        {
            var selector = new OAuthSelectorComponent("login", ApiKeyProviders("OpenAI", "OpenCode Go", "OpenCode Zen"), (_, _) => { }, () => { });
            TypeText(selector, "open");
            selector.HandleInput(Down);
            selector.HandleInput(Down);
            var before = ArrowRow(selector);
            selector.HandleInput(Left);
            selector.HandleInput(Home);
            Check(before is not null && !before.Contains("OpenAI", StringComparison.Ordinal), "browsed away from the first row: " + before);
            Equal(before, ArrowRow(selector), "selected row after Left and Home");
        }));
        // Native: an emptied query keeps the row (clamped), as before 1.6.0; only a non-empty query moves to the top.
        yield return ("sync160.ui.oauth.native-clearing-the-query-keeps-the-row", Sync(() =>
        {
            var selector = new OAuthSelectorComponent("logout", ApiKeyProviders("OpenAI", "OpenCode Go", "OpenCode Zen"), (_, _) => { }, () => { });
            TypeText(selector, "o");
            selector.HandleInput(Down);
            selector.HandleInput(Down);
            var before = ArrowRow(selector);
            selector.HandleInput("\u007f"); // Backspace: the query is empty again
            Equal(before, ArrowRow(selector), "row kept without a query");
        }));

        // ---------------------------------------------------------------- scoped-models-selector.test.ts
        // Regression test for #12245: the /login fix applied to /scoped-models.
        yield return ("sync160.ui.scoped-models.selects-the-best-match-after-typing-a-query-even-after-scrolling", Sync(() =>
        {
            var (selector, enabled) = Scoped(("gpt-4o", "GPT-4o"), ("gpt-4o-mini", "GPT-4o mini"), ("claude", "Claude"), ("gemini", "Gemini"));
            for (var i = 0; i < 3; i++) selector.HandleInput(Down);
            TypeText(selector, "gpt-4o");
            selector.HandleInput(Enter);
            Equal("False,True,True,True", string.Join(",", enabled), "the top match is toggled");
        }));
        yield return ("sync160.ui.scoped-models.keeps-the-selection-when-a-cursor-key-leaves-the-query-unchanged", Sync(() =>
        {
            var (selector, enabled) = Scoped(("gpt-4o", "GPT-4o"), ("gpt-4o-mini", "GPT-4o mini"), ("claude", "Claude"));
            TypeText(selector, "gpt");
            selector.HandleInput(Down);
            selector.HandleInput(Left);
            selector.HandleInput(Enter);
            Equal("True,False,True", string.Join(",", enabled), "the browsed row is toggled");
        }));

        // ---------------------------------------------------------------- model-selector.test.ts
        yield return ("sync160.ui.model.keeps-the-selection-when-a-cursor-key-leaves-the-query-unchanged", Sync(() =>
        {
            var loop = UiLoop.CreateManual();
            var tui = new TuiMainScreen(new Sync160UiTerminal(), loop, environment: _ => null);
            var models = new[] { Model("alpha-1"), Model("alpha-2"), Model("alpha-3") };
            var selector = new ModelSelectorComponent(tui, null, new FakeModelRuntime(models), [], _ => { }, () => { });
            // model-selector.test.ts selectedModelId: the row that starts with the arrow, without the current-model mark.
            string? SelectedModelId()
            {
                var row = Strip(selector.Render(120)).FirstOrDefault(line => line.StartsWith('→'));
                return row?[1..].TrimStart().TrimStart('✓').TrimStart().Split(' ')[0];
            }
            TypeText(selector, "alpha");
            selector.HandleInput(Down);
            selector.HandleInput(Down);
            var browsed = SelectedModelId();
            selector.HandleInput(Left);
            selector.HandleInput(Home);
            Equal(browsed, SelectedModelId(), "selected model after Left and Home");
            Check(browsed is not null && browsed != "alpha-1", "browsed away from the first row: " + browsed);
            selector.Dispose();
            loop.RunPending();
        }));

        // ---------------------------------------------------------------- settings-submenu.test.ts
        yield return ("sync160.ui.settings-submenu.keeps-the-selection-when-a-cursor-key-leaves-the-query-unchanged", Sync(() =>
        {
            var selected = new List<string>();
            var submenu = new SelectSubmenu("Theme", "", [.. new[] { "dark-blue", "dark-green", "light" }.Select(value => new SelectItem(value, value))], "light",
                selected.Add, () => { }, null, new SelectSubmenuOptions(Searchable: true));
            TypeText(submenu, "dark");
            submenu.HandleInput(Down);
            submenu.HandleInput(Left);
            submenu.HandleInput(Home);
            submenu.HandleInput(Enter);
            Equal("dark-green", string.Join(",", selected), "the browsed option is selected");
        }));

        // ---------------------------------------------------------------- thinking-selector.ts (native: upstream has no case)
        // The list is rebuilt for a new query only; a cursor key leaves the list (and its selection) as it is.
        yield return ("sync160.ui.thinking.native-a-cursor-key-does-not-rebuild-the-list", Sync(() =>
        {
            var selected = new List<string>();
            var selector = new ThinkingSelectorComponent("low", ["off", "minimal", "low", "medium", "high"], selected.Add, () => { });
            TypeText(selector, "m");
            var filtered = selector.GetSelectList();
            selector.HandleInput(Down);
            var browsed = filtered.GetSelectedItem()?.Value;
            selector.HandleInput(Left);
            selector.HandleInput(Home);
            Check(ReferenceEquals(filtered, selector.GetSelectList()), "the list is not rebuilt for Left or Home");
            Equal(browsed, selector.GetSelectList().GetSelectedItem()?.Value, "selection kept");
            TypeText(selector, "e");
            Check(!ReferenceEquals(filtered, selector.GetSelectList()), "a changed query rebuilds the list");
        }));

        // ---------------------------------------------------------------- config-selector.test.ts
        yield return ("sync160.ui.config.keeps-the-selection-when-a-cursor-key-leaves-the-query-unchanged", Sync(() =>
        {
            using var dir = new TempDir();
            var packageRoot = Path.Combine(dir.Path, "pkg");
            var metadata = new PathMetadata("npm:pi-tools", "user", "package", packageRoot);
            var resolved = new ResolvedPaths(
                [.. new[] { "tool-a.ts", "tool-b.ts", "other.ts" }.Select(name => new ResolvedResource(Path.Combine(packageRoot, "extensions", name), true, metadata))],
                [], [], []);
            var selector = new ConfigSelectorComponent(new ScopedResolvedPaths(resolved, resolved), new FakeConfigSettings(), dir.Path,
                Path.Combine(dir.Path, "agent"), () => { }, () => { }, () => { }, homeDir: dir.Path);
            var list = (IInputHandler)selector.GetResourceList();
            string? SelectedRow() => Strip(((IComponent)selector.GetResourceList()).Render(120))
                .FirstOrDefault(line => line.StartsWith('>') && line.Contains(".ts", StringComparison.Ordinal));
            TypeText(list, "tool");
            list.HandleInput(Down);
            Contains(SelectedRow() ?? "", "tool-b", "browsed row");
            list.HandleInput(Left);
            list.HandleInput(Home);
            Contains(SelectedRow() ?? "", "tool-b", "selected row after Left and Home");
        }));
    }
}

/// <summary>A terminal that accepts every call and shows nothing, for components that need a TUI.</summary>
internal sealed class Sync160UiTerminal : ITerminal
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
