using System.Text.Json.Nodes;
using PiSharp.Cli.Interactive.Mode;
using static Expect;

/// <summary>theme.ts, theme-json.ts, settings-manager.ts (interactive settings), keybindings.ts and keybinding-hints.ts.</summary>
internal static class FoundationCases
{
    public static IEnumerable<(string Id, Func<Task> Run)> All()
    {
        yield return ("theme.dark-fg-truecolor", Sync(() =>
        {
            // dark.json accent is the "violet" var, okhsl(295 50% 67%).
            var accent = PiSharp.Tui.Pi.Colors.ToRgb(PiSharp.Tui.Pi.Colors.ParseColor("okhsl(295 50% 67%)"));
            Equal($"\u001b[38;2;{(int)Math.Round(accent.R)};{(int)Math.Round(accent.G)};{(int)Math.Round(accent.B)}mhi\u001b[39m", Themes.Current.Fg("accent", "hi"), "accent foreground");
            // Every token agrees with the HTML exporter's resolution of the same theme.
            var exported = new PiSharp.CodingAgent.Export.PiThemeHost().GetResolvedThemeColors("dark");
            foreach (var (token, hex) in exported)
                Equal(hex, PiSharp.Tui.Pi.Colors.ToHex(Themes.Current.TokenColors[token]), "token " + token);
            Equal("\u001b[1mx\u001b[22m", Themes.Current.Bold("x"), "bold");
        }));
        yield return ("theme.unknown-token-throws", Sync(() => Throws<ArgumentException>(() => Themes.Current.Fg("nope", "x"), "unknown token")));
        yield return ("theme.available-system-first", Sync(() =>
        {
            var names = Themes.GetAvailableThemes();
            Equal("system", names[0], "first theme");
            Check(names.Contains("dark") && names.Contains("light"), "builtins listed");
        }));
        yield return ("theme.invalid-falls-back-to-system", Sync(() =>
        {
            var result = Themes.SetTheme("does-not-exist");
            Check(!result.Success, "invalid theme fails");
            Equal("system", Themes.CurrentThemeName, "fallback name");
            Contains(result.Error ?? "", "Theme not found: does-not-exist", "error");
        }));
        yield return ("theme.custom-json-validation", Sync(() =>
        {
            using var dir = new TempDir();
            var path = Path.Combine(dir.Path, "bad.json");
            File.WriteAllText(path, "{\"name\":\"bad\",\"colors\":{\"accent\":\"#ffffff\"}}");
            var error = Throws<ArgumentException>(() => Themes.LoadThemeFromPath(path), "invalid custom theme");
            Contains(error.Message, "Missing required color tokens:", "validation message");
            Contains(error.Message, "  - bashMode", "missing token listed");
        }));
        yield return ("theme.custom-json-loads", Sync(() =>
        {
            using var dir = new TempDir();
            var dark = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Encoding.UTF8.GetString(PiSharp.CodingAgent.Export.SessionHtmlExport.ReadAssetBytes("dark.json")).TrimStart((char)0xFEFF))!.AsObject();
            dark["name"] = "mine";
            var path = Path.Combine(dir.Path, "mine.json");
            File.WriteAllText(path, dark.ToJsonString());
            var theme = Themes.LoadThemeFromPath(path);
            Equal("mine", theme.Name, "name");
            Equal("dark", theme.Appearance, "appearance");
        }));
        yield return ("theme.auto-setting", Sync(() =>
        {
            Equal("light", Themes.ResolveThemeSetting("light/dark", "light"), "light side");
            Equal("dark", Themes.ResolveThemeSetting("light/dark", "dark"), "dark side");
            Equal(null, Themes.ResolveThemeSetting("a/b/c", "dark"), "invalid auto");
        }));
        yield return ("theme.highlight-typescript", Sync(() =>
        {
            var lines = Themes.HighlightCode("const x = 1; // hi", "ts");
            Equal(1, lines.Count, "line count");
            Contains(lines[0], Themes.Current.Fg("syntaxKeyword", "const"), "keyword colored");
            Contains(lines[0], Themes.Current.Fg("syntaxComment", "// hi"), "comment colored");
            Equal("const x = 1; // hi", Strip(lines[0]), "text preserved");
            var unknown = Themes.HighlightCode("a\nb", "not-a-language");
            Equal(Themes.Current.Fg("mdCodeBlock", "b"), unknown[1], "unknown language uses mdCodeBlock");
        }));
        yield return ("settings.defaults", Sync(() =>
        {
            var settings = new InteractiveSettings(new JsonObject(), _ => null);
            Equal("fullscreen", settings.TuiMode, "tuiMode");
            Equal(1, settings.OutputPad, "outputPad");
            Equal(5, settings.AutocompleteMaxVisible, "autocompleteMaxVisible");
            Equal("tree", settings.DoubleEscapeAction, "doubleEscapeAction");
            Equal(false, settings.QuietStartup, "quietStartup");
            Equal("streaming", settings.CacheWarmingMode, "cacheWarming");
            Equal(true, settings.ShowImages, "showImages");
            Equal(60, settings.ImageWidthCells, "imageWidthCells");
            Equal("one-at-a-time", settings.SteeringMode, "steeringMode");
            Equal(OperatingSystem.IsWindows() ? "notepad" : "nano", settings.ExternalEditorCommand, "external editor fallback");
        }));
        yield return ("settings.quiet-startup-header", Sync(() =>
        {
            Equal("header", new InteractiveSettings(new JsonObject { ["quietStartup"] = "header" }, _ => null).QuietStartup, "header");
            Equal(true, new InteractiveSettings(new JsonObject { ["quietStartup"] = true }, _ => null).QuietStartup, "true");
        }));
        yield return ("settings.persist-merges-modified-fields", async () =>
        {
            using var dir = new TempDir();
            var agent = Path.Combine(dir.Path, "agent"); Directory.CreateDirectory(agent);
            var cwd = Path.Combine(dir.Path, "cwd"); Directory.CreateDirectory(cwd);
            File.WriteAllText(Path.Combine(agent, "settings.json"), "{\"theme\":\"dark\",\"terminal\":{\"showImages\":false}}");
            var settings = new InteractiveSettings(cwd, agent, projectTrusted: false, _ => null);
            // Another process changes an unrelated field after load.
            File.WriteAllText(Path.Combine(agent, "settings.json"), "{\"theme\":\"dark\",\"terminal\":{\"showImages\":false},\"defaultModel\":\"m\"}");
            settings.SetOutputPad(0);
            settings.SetImageWidthCells(80);
            await settings.FlushAsync();
            var written = JsonNode.Parse(File.ReadAllText(Path.Combine(agent, "settings.json")))!.AsObject();
            Equal("m", (string?)written["defaultModel"], "external field kept");
            Equal(0, (int?)written["outputPad"], "outputPad written");
            Equal(80, (int?)written["terminal"]!["imageWidthCells"], "nested field written");
            Equal(false, (bool?)written["terminal"]!["showImages"], "nested sibling kept");
            Check(!Directory.Exists(Path.Combine(agent, "settings.json.lock")), "lock released");
        });
        yield return ("keybindings.windows-defaults", Sync(() =>
        {
            var defs = AppKeybindings.Definitions(_ => null).ToDictionary(pair => pair.Key, pair => pair.Value);
            if (OperatingSystem.IsWindows())
            {
                Equal("ctrl+z", defs["tui.editor.undo"].DefaultKeys[0], "undo");
                Equal("alt+v", defs["app.clipboard.pasteImage"].DefaultKeys[0], "paste image");
                Equal(0, defs["app.suspend"].DefaultKeys.Count, "no suspend");
            }
            else Equal("ctrl+z", defs["app.suspend"].DefaultKeys[0], "suspend");
            Equal("escape", defs["app.interrupt"].DefaultKeys[0], "interrupt");
        }));
        yield return ("keybindings.migration", Sync(() =>
        {
            var (config, migrated) = AppKeybindings.MigrateKeybindingsConfig(new JsonObject { ["cursorUp"] = "ctrl+k", ["app.exit"] = "ctrl+q", ["zzz"] = "x" });
            Check(migrated, "migrated");
            Equal("tui.editor.cursorUp,app.exit,zzz", string.Join(",", config.Select(pair => pair.Key)), "renamed and ordered");
            var (dup, _) = AppKeybindings.MigrateKeybindingsConfig(new JsonObject { ["exit"] = "a", ["app.exit"] = "b" });
            Equal("b", (string?)dup["app.exit"], "new name wins");
        }));
        yield return ("keybindings.hints", Sync(() =>
        {
            Equal("ctrl+c", KeybindingHints.KeyText("app.clear"), "keyText");
            Equal("Ctrl+C", KeybindingHints.KeyDisplayText("app.clear"), "keyDisplayText");
            Equal("ctrl+c clear", Strip(KeybindingHints.KeyHint("app.clear", "clear")), "keyHint");
            Equal("Shift+Tab", KeybindingHints.FormatKeyText("shift+tab", capitalize: true), "format");
        }));
    }
}
