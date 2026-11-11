// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/coding-agent/src/modes/interactive/interactive-mode.ts.
// Authored native regression; not an upstream execution capture.
using System.Text.Json.Nodes;
using PiSharp.Cli.Interactive.Mode;
using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Tui.Pi;
using PiSharp.Cli.Llama;
using static Expect;

internal static class Sync152SmallUiCases
{
    private sealed class Runtime : IModelSelectorRuntime
    {
        public List<JsonObject> Models = [];
        public TaskCompletionSource<ModelsRefreshResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<JsonObject> GetAvailableSnapshot() => Models;
        public JsonObject? GetModel(string provider, string id) => Models.Find(model => ModelJson.Provider(model) == provider && ModelJson.Id(model) == id);
        public string? GetError() => null;
        public Task<ModelsRefreshResult> RefreshAsync(CancellationToken token) => Completion.Task;
    }

    private static JsonObject Model(string id, string provider = "test") => new() { ["id"] = id, ["name"] = id, ["provider"] = provider };
    private static string Text(IComponent component) => string.Join("\n", Strip(component.Render(100)));

    public static IEnumerable<(string Id, Func<Task> Run)> All()
    {
        yield return ("sync152.ui.extension-window", Sync(() =>
        {
            var selected = "";
            var selector = new ExtensionSelectorComponent("Choose", Enumerable.Range(0, 25).Select(i => "item-" + i.ToString("D2")).ToList(), value => selected = value, () => { });
            for (var i = 0; i < 13; i++) selector.HandleInput("\u001b[B");
            var rows = Strip(selector.Render(100));
            Equal(10, rows.Count(row => row.Contains("item-", StringComparison.Ordinal)), "window size");
            Contains(string.Join("\n", rows), "(14/25)", "scroll position");
            Check(!rows.Any(row => row.Contains("item-07", StringComparison.Ordinal)), "window follows selection");
            selector.HandleInput("\r"); Equal("item-13", selected, "correct absolute row");
            var shortList = new ExtensionSelectorComponent("Choose", ["one"], _ => { }, () => { });
            Check(!Text(shortList).Contains("(1/1)", StringComparison.Ordinal), "short list has no scroll info");
            new ExtensionSelectorComponent("Choose", [], _ => throw new Exception("empty selected"), () => { }).HandleInput("\r");
        }));
        foreach (var query in new[] { "", "model" })
        foreach (var removed in new[] { false, true })
        {
            var activeQuery = query; var remove = removed;
            yield return ($"sync152.ui.model-refresh-{(query.Length == 0 ? "all" : "query")}-{removed}", async () =>
            {
                var tui = new FakeTui();
                var runtime = new Runtime { Models = [Model("model-a"), Model("model-b"), Model("model-c")] };
                using var loop = tui.Loop;
                var selected = "";
                var selector = new ModelSelectorComponent(tui, runtime.Models[0], runtime, [], model => selected = ModelJson.Id(model), () => { }, activeQuery);
                // Navigate while the request is outstanding; the completed snapshot adds a row before this one.
                selector.HandleInput("\u001b[B"); selector.HandleInput("\u001b[B");
                runtime.Models = [Model("model-0"), Model("model-a"), Model("model-b")];
                if (!remove) runtime.Models.Add(Model("model-c"));
                runtime.Completion.SetResult(new(false, []));
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!Text(selector).Contains("Model catalogs refreshed.", StringComparison.Ordinal))
                {
                    if (DateTime.UtcNow > deadline) throw new Exception("Refresh did not complete");
                    await Task.Delay(10); loop.RunPending();
                }
                selector.HandleInput("\r");
                Equal(remove ? "model-a" : "model-c", selected, "preserved identity or first matching fallback");
                selector.Dispose();
            });
        }
        yield return ("sync152.ui.tree-focus-and-width", Sync(() =>
        {
            var entry = new JsonObject { ["id"] = "user", ["type"] = "message", ["message"] = new JsonObject { ["role"] = "user", ["content"] = "hello" } };
            var selector = new TreeSelectorComponent([new SessionTreeNode(entry)], "user", 40, _ => { }, () => { }, (_, _) => { });
            var search = selector.Children.Single(child => child.GetType().Name == "SearchLine");
            Equal("", search.Render(0).Single(), "zero width");
            selector.Focused = true;
            Equal(TuiBase.CursorMarker + TuiBase.RenderFakeCursor(" "), search.Render(1).Single(), "one column cursor");
            selector.HandleInput("hello");
            Check(search.Render(40).Single().IndexOf(TuiBase.CursorMarker, StringComparison.Ordinal) > search.Render(40).Single().IndexOf("hello", StringComparison.Ordinal), "cursor follows query");
            selector.HandleInput("L");
            Check(!search.Render(40).Single().Contains(TuiBase.CursorMarker, StringComparison.Ordinal), "label edit owns focus");
            selector.HandleInput("\u001b");
            Check(search.Render(40).Single().Contains(TuiBase.CursorMarker, StringComparison.Ordinal), "cancel restores search cursor");
            selector.HandleInput("L"); selector.HandleInput("\r");
            Check(search.Render(40).Single().Contains(TuiBase.CursorMarker, StringComparison.Ordinal), "save restores search cursor");
            selector.Focused = false;
            Check(!search.Render(40).Single().Contains(TuiBase.CursorMarker, StringComparison.Ordinal), "unfocused has no cursor");
        }));
        yield return ("sync152.ui.provider-order-brand", Sync(() =>
        {
            var providers = new List<AuthSelectorProvider> { new("z", "Zed", "oauth"), new("openrouter", "OpenRouter", "oauth"), new("radius", "Radius", "oauth"), new("radius", "Radius", "api_key"), new("a", "Alpha", "api_key") };
            Equal("radius/oauth,radius/api_key,openrouter/oauth,a/api_key,z/oauth", string.Join(",", PreferredProviders.Sort(providers, item => item.Id, item => item.Name).Select(item => item.Id + "/" + item.AuthType)), "preferred and stable order");
            using var selector = new OAuthSelectorComponent("login", providers, (_, _) => { }, () => { });
            var text = Text(selector);
            Contains(text, "OpenRouter ✦", "partner marker");
            Check(!text.Contains("Zed ✦", StringComparison.Ordinal), "ordinary provider has no marker");
            Equal(1, TextUtils.VisibleWidth("✦"), "single column marker");
            foreach (var appearance in new[] { "dark", "light" })
            {
                Themes.InitTheme(appearance);
                var expected = Colors.ParseColor(appearance == "dark" ? "#c6f243" : "#5d32e1");
                Equal(Colors.ForegroundAnsi(expected, TerminalColorMode.TrueColor) + "name\u001b[39m", ProviderBrand.OpenRouterGlow("name", 0), "base color " + appearance);
                Check(ProviderBrand.OpenRouterGlow("name", 1200) != ProviderBrand.OpenRouterGlow("name", 0), "glow pulse " + appearance);
            }
            Equal(RadiusLoginMenuComponent.RadiusShimmer("Radius", 400), ProviderBrand.GetStyle("radius")!("Radius", 400), "shared Radius style");
            Check(ProviderBrand.GetStyle("ordinary") is null, "ordinary provider has no style");
        }));
        yield return ("sync152.ui.provider-animation-disposes", async () =>
        {
            var tui = new FakeTui(); using var loop = tui.Loop;
            using var selector = new OAuthSelectorComponent("login", [new("radius", "Radius", "oauth"), new("other", "Other", "api_key")], (_, _) => { }, () => { }, tui: tui, now: () => 0);
            Contains(string.Join("\n", selector.Render(100)), ProviderBrand.RadiusShimmer("Radius", 0), "selected brand style");
            await Task.Delay(110); loop.RunPending(); Check(tui.RenderRequests > 0, "selected partner redraws");
            selector.HandleInput("\u001b[B");
            await Task.Delay(70); loop.RunPending(); var idle = tui.RenderRequests;
            await Task.Delay(70); loop.RunPending(); Equal(idle, tui.RenderRequests, "ordinary selection does not redraw");
            selector.HandleInput("\u001b[A"); selector.Dispose();
            await Task.Delay(70); loop.RunPending(); var stopped = tui.RenderRequests;
            await Task.Delay(70); loop.RunPending(); Equal(stopped, tui.RenderRequests, "disposed timer stops");
        });
        yield return ("sync152.ui.paste-shortcuts", Sync(() =>
        {
            var defs = AppKeybindings.Definitions(name => name == "WSL_DISTRO_NAME" ? "test" : null);
            var keys = new KeybindingsManager(defs);
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            {
                Equal("alt+v,ctrl+v", string.Join(",", keys.GetKeys("app.clipboard.pasteImage")), "Windows or WSL paste keys");
                var tui = new FakeTui(); using var loop = tui.Loop;
                var editor = new CustomEditor(tui, Themes.GetEditorTheme(), keys);
                var pastes = 0; editor.OnPasteImage = () => pastes++;
                editor.HandleInput("\u001bv"); editor.HandleInput("\u0016");
                Equal(2, pastes, "both shortcuts dispatch paste");
            }
            keys.SetUserBindings(new Dictionary<string, IReadOnlyList<string>?> { ["app.clipboard.pasteImage"] = ["ctrl+x"] });
            Equal("ctrl+x", string.Join(",", keys.GetKeys("app.clipboard.pasteImage")), "explicit override wins");
        }));
        yield return ("sync152.ui.diff-keeps-shared-indent", Sync(() =>
        {
            foreach (var indent in new[] { "    ", "\t" })
            {
                var result = Diff.RenderDiff("-1 " + indent + "value\n+1 " + indent + "new value");
                var space = indent.Replace("\t", "   ");
                Equal("-1 " + space + "value\n+1 " + space + "new value", Strip(result), "both line prefixes survive");
                Contains(result, space + "\u001b[7mnew", "insertion begins after indentation");
            }
            Equal("-1   old\n+1     new", Strip(Diff.RenderDiff("-1   old\n+1     new")), "changed indentation preserved");
            Equal("-1 \n+1 x", Strip(Diff.RenderDiff("-1 \n+1 x")), "empty line");
        }));
        yield return ("sync152.ui.hugging-face-pending-selection", async () =>
        {
            await using var pi = new InteractiveHarness("sync152-hf-selection");
            UiLoop? loop = null;
            pi.Configure = context => { loop = context.Loop; return context; };
            pi.Start("--provider", "anthropic", "--model", "claude-sonnet-4-5", "--tui-mode", "regular");
            await pi.WaitFor("escape interrupt");
            foreach (var scenario in new[] { "touched", "missing", "untouched", "stale" })
            {
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var completion = new TaskCompletionSource<IReadOnlyList<HuggingFaceModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
                var old = new HuggingFaceModel[] { new("owner/qwen-a", 20), new("owner/qwen-b", 10) };
                var cache = new Dictionary<string, IReadOnlyList<HuggingFaceModel>> { ["qw"] = old };
                string? selected = null;
                var search = await loop!.InvokeAsync(() => new InteractiveMode.HuggingFaceSearch(pi.Mode!, (_, _) => { started.TrySetResult(); return completion.Task; }, cache, id => selected = id));
                await loop.InvokeAsync(() => { search.HandleInput("qw"); search.HandleInput("e"); });
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (scenario != "untouched") await loop.InvokeAsync(() => search.HandleInput("\u001b[B"));
                if (scenario == "stale") await loop.InvokeAsync(() => search.HandleInput("x"));
                completion.SetResult(scenario == "missing" ? [new("owner/qwen-c", 40), old[0]] : [new("owner/qwen-c", 40), old[1], old[0]]);
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!await loop.InvokeAsync(() => cache.ContainsKey("qwe")))
                {
                    if (DateTime.UtcNow > deadline) throw new Exception("Hugging Face search did not complete");
                    await Task.Delay(10);
                }
                // Awaiting one posted action lets the request's continuation finish applying before selection.
                await loop.InvokeAsync(() => search.HandleInput("\r"));
                Equal<string?>(scenario == "touched" ? "owner/qwen-b" : scenario == "stale" ? null : "owner/qwen-c", selected, scenario + " selection");
                await loop.InvokeAsync(() => search.Close(null));
            }
            Equal(0, await pi.Quit(), "exit code");
        });
        yield return ("sync152.ui.editor-swap-expanded-paste", async () =>
        {
            await using var pi = new InteractiveHarness("sync152-editor-paste");
            UiLoop? loop = null;
            pi.Configure = context => { loop = context.Loop; return context; };
            pi.Start("--provider", "anthropic", "--model", "claude-sonnet-4-5", "--tui-mode", "regular");
            await pi.WaitFor("escape interrupt");
            CustomEditor? editor = null;
            var draft = string.Join("\n", Enumerable.Range(1, 20).Select(i => "pasted line " + i));
            await loop!.InvokeAsync(() =>
            {
                pi.Mode!.SetCustomEditorComponent((tui, theme, keys) => editor = new CustomEditor(tui, theme, keys));
                editor!.HandleInput("\u001b[200~" + draft + "\u001b[201~");
                Check(editor.GetText().Contains("[paste #", StringComparison.Ordinal), "paste is collapsed before swap");
                pi.Mode.SetCustomEditorComponent((tui, theme, keys) => editor = new CustomEditor(tui, theme, keys));
                Equal(draft, editor!.GetExpandedText(), "custom to custom expanded text");
                editor.SetText(""); editor.HandleInput("\u001b[200~" + draft + "\u001b[201~");
                pi.Mode.SetCustomEditorComponent(null);
                pi.Mode.SetCustomEditorComponent((tui, theme, keys) => editor = new CustomEditor(tui, theme, keys));
                Equal(draft, editor!.GetExpandedText(), "custom through default expanded text");
                editor.SetText("");
            });
            Equal(0, await pi.Quit(), "exit code");
        });
        yield return ("sync152.ui.syntax-new-file-types", Sync(() =>
        {
            foreach (var (extension, language, code) in new[] { ("dart", "dart", "void main() { return; }"), ("nix", "nix", "let x = true; in x"), ("groovy", "groovy", "class Example { def x = true }"), ("gradle", "groovy", "class Example { def x = true }") })
            {
                Equal(language, Themes.GetLanguageFromPath("file." + extension), "mapped " + extension);
                var output = string.Join("\n", Themes.HighlightCode(code, language));
                Equal(code, Strip(output), "text preserved " + extension);
                Check(output != Themes.Current.Fg("mdCodeBlock", code), "loaded grammar " + extension);
            }
        }));
    }

    private sealed class FakeTui : ITui
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
}
